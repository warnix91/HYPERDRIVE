using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Hyperdrive.Core;
using Hyperdrive.Patching;
using UnityEngine;
using UnityEngine.Rendering;

namespace Hyperdrive.Diagnostics
{
    /// <summary>
    /// Developer tool for the rendering side of the frame (off by default).
    /// - Inventory, once per flight scene: quality settings, cameras, lights, and for the active vessel the renderers,
    ///   materials, shaders, triangles and shadow modes per part; scene-wide renderer counts per layer.
    /// - Per-camera main-thread time (Camera.onPreCull to Camera.onPostRender), logged per interval.
    /// - Optional experiments (diagnostic only, every change restored): timed windows with shadows disabled, the active
    ///   vessel hidden, a camera disabled, or per-camera shadow settings (applied in Camera.onPreCull, restored in
    ///   onPostRender), each surrounded by baseline windows, to price each element.
    /// Steady-state per-frame work is allocation-free; inventory and reports allocate once.
    /// </summary>
    internal sealed class RenderProbe : Subsystem
    {
        private sealed class CamStat
        {
            public Camera Camera;
            public string Name;
            public long Start = -1;
            public long Ticks;
            public int Renders;
        }

        private float _inventoryDelay = 12f;
        private float _intervalSeconds = 15f;
        private bool _runExperiments;
        private float _experimentStart = 20f;
        private float _experimentWindow = 10f;
        private float _experimentSettle = 2f;

        private readonly Dictionary<int, CamStat> _cams = new Dictionary<int, CamStat>();
        private readonly List<CamStat> _camList = new List<CamStat>();
        private Camera.CameraCallback _onPreCull, _onPostRender;
        private readonly StringBuilder _sb = new StringBuilder(1024);

        private float _flightStart = -1f;
        private bool _inventoryDone;
        private float _nextReport;
        private int _reportFrames;

        // Experiments
        private readonly List<string> _steps = new List<string>();
        private int _step = -1;
        private bool _globalShadowsChanged, _globalQualityChanged;
        private int _gSavedCascades;
        private ShadowResolution _gSavedResolution;
        private float _gSavedDistance;
        private string _camOverrideName, _camOverrideKind;
        private float _camOverrideValue;
        private bool _camOverrideApplied;
        private ShadowQuality _savedQShadows;
        private int _savedQCascades;
        private float _savedQDistance;
        private float _stepStart;
        private int _stepFrames, _stepUnfocused;
        private double _stepFrameTime;
        private bool _experimentsDone;
        private ShadowQuality _savedShadows;
        private readonly List<Renderer> _changedRenderers = new List<Renderer>();
        private readonly List<ShadowCastingMode> _savedModes = new List<ShadowCastingMode>();
        private Camera _disabledCamera;
        // viewport:<scale> — every camera drawing to the screen draws a smaller rectangle: is the frame bound by pixels?
        private readonly List<Camera> _viewportCameras = new List<Camera>();
        private bool _upscalerChanged;      // upscaler:on|off — restored to on
        private readonly List<Rect> _viewportRects = new List<Rect>();
        private readonly List<string> _results = new List<string>();

        public override string Name => "RenderProbe";
        public override bool ReadOnly => true;

        public override bool EnabledByDefault => false;

        protected internal override void LoadSettings(SettingsSection settings)
        {
            _inventoryDelay = Mathf.Clamp(settings.GetFloat("inventoryDelaySeconds", _inventoryDelay), 1f, 600f);
            _intervalSeconds = Mathf.Clamp(settings.GetFloat("logIntervalSeconds", _intervalSeconds), 2f, 600f);
            _runExperiments = settings.GetBool("experiments", false);
            _experimentStart = Mathf.Clamp(settings.GetFloat("experimentStartSeconds", _experimentStart), 5f, 600f);
            _experimentWindow = Mathf.Clamp(settings.GetFloat("experimentWindowSeconds", _experimentWindow), 4f, 120f);
            BuildSteps(settings.GetString("experimentList", "shadows:off;camshadows:Camera 01:off;camshadows:Camera 00:off;camcascades:Camera 01:2;camdistance:Camera 01:2500;camcascades:Camera 00:2"));
        }

        protected internal override void OnEnable()
        {
            _onPreCull = OnPreCull;
            _onPostRender = OnPostRender;
            Camera.onPreCull += _onPreCull;
            Camera.onPostRender += _onPostRender;
            Log.Info("RenderProbe: armed (inventory after " + _inventoryDelay + " s of flight, camera times every " + _intervalSeconds + " s"
                     + (_runExperiments ? ", experiments from " + _experimentStart + " s" : "") + ")");
        }

        protected internal override void OnDisable()
        {
            RestoreExperiment();
            if (_onPreCull != null) Camera.onPreCull -= _onPreCull;
            if (_onPostRender != null) Camera.onPostRender -= _onPostRender;
        }

        protected internal override void OnSceneReady(GameScenes scene)
        {
            RestoreExperiment();
            _flightStart = scene == GameScenes.FLIGHT ? Time.unscaledTime : -1f;
            _inventoryDone = false;
            _step = -1;
            _experimentsDone = false;
            ResetCameraStats();
        }

        private void OnPreCull(Camera cam)
        {
            int id = cam.GetInstanceID();
            if (!_cams.TryGetValue(id, out CamStat s))
            {
                s = new CamStat { Camera = cam, Name = cam.name };
                _cams[id] = s;
                _camList.Add(s);
            }
            ApplyCameraOverride(s);
            s.Start = Stopwatch.GetTimestamp();
        }

        private void OnPostRender(Camera cam)
        {
            if (_cams.TryGetValue(cam.GetInstanceID(), out CamStat s) && s.Start >= 0)
            {
                s.Ticks += Stopwatch.GetTimestamp() - s.Start;
                s.Renders++;
                s.Start = -1;
            }
            RestoreCameraOverride();
        }

        protected internal override void OnUpdate()
        {
            if (_flightStart < 0f || HighLogic.LoadedScene != GameScenes.FLIGHT || !FlightGlobals.ready)
                return;
            float now = Time.unscaledTime;
            float t = now - _flightStart;
            _reportFrames++;

            if (!_inventoryDone && t >= _inventoryDelay)
            {
                _inventoryDone = true;
                Inventory();
            }

            if (_runExperiments && !_experimentsDone && t >= _experimentStart)
                StepExperiments(now);

            if (now >= _nextReport)
            {
                _nextReport = now + _intervalSeconds;
                if (_step < 0 || _experimentsDone)
                    ReportCameras("Cameras");
                ResetCameraStats();
            }
        }

        private void ResetCameraStats()
        {
            for (int i = 0; i < _camList.Count; i++)
            {
                _camList[i].Ticks = 0;
                _camList[i].Renders = 0;
            }
            _reportFrames = 0;
        }

        private void ReportCameras(string label)
        {
            if (_reportFrames == 0)
                return;
            StringBuilder sb = _sb;
            sb.Length = 0;
            sb.Append(Log.Prefix).Append("RenderProbe ").Append(label).Append(" (main-thread ms/frame, ").Append(_reportFrames).Append(" frames):");
            double toMs = 1000.0 / Stopwatch.Frequency / _reportFrames;
            for (int i = 0; i < _camList.Count; i++)
            {
                CamStat s = _camList[i];
                if (s.Renders == 0)
                    continue;
                sb.Append(" | ").Append(s.Name).Append(' ').AppendFixed(s.Ticks * toMs, 2);
            }
            UnityEngine.Debug.Log(sb.ToString());
        }

        // ---------------------------------------------------------------- inventory (one-off, allocates)

        private void Inventory()
        {
            var sb = new StringBuilder(4096);
            sb.Append(Log.Prefix).Append("RenderProbe inventory\n");
            sb.Append("  Device: ").Append(SystemInfo.graphicsDeviceType).Append(" '").Append(SystemInfo.graphicsDeviceName).Append("' ")
              .Append(SystemInfo.graphicsDeviceVersion).Append(", multithreaded rendering ").Append(SystemInfo.graphicsMultiThreaded)
              .Append(", screen ").Append(Screen.width).Append('x').Append(Screen.height).Append(" target fps ").Append(Application.targetFrameRate).Append('\n');
            sb.Append("  Quality '").Append(QualitySettings.names[QualitySettings.GetQualityLevel()]).Append("': shadows ").Append(QualitySettings.shadows)
              .Append(" res ").Append(QualitySettings.shadowResolution).Append(" distance ").Append(QualitySettings.shadowDistance)
              .Append(" cascades ").Append(QualitySettings.shadowCascades).Append(" projection ").Append(QualitySettings.shadowProjection)
              .Append(", pixel lights ").Append(QualitySettings.pixelLightCount).Append(", AA ").Append(QualitySettings.antiAliasing)
              .Append(", vsync ").Append(QualitySettings.vSyncCount).Append(", texture limit ").Append(QualitySettings.masterTextureLimit)
              .Append(", aniso ").Append(QualitySettings.anisotropicFiltering).Append(", lodBias ").Append(QualitySettings.lodBias)
              .Append(", soft particles ").Append(QualitySettings.softParticles).Append('\n');

            foreach (Camera c in Camera.allCameras)
            {
                sb.Append("  Camera '").Append(c.name).Append("' depth ").Append(c.depth).Append(" path ").Append(c.actualRenderingPath)
                  .Append(" clear ").Append(c.clearFlags).Append(" near/far ").Append(c.nearClipPlane).Append('/').Append(c.farClipPlane)
                  .Append(" mask 0x").Append(c.cullingMask.ToString("X8")).Append(" HDR ").Append(c.allowHDR).Append(" MSAA ").Append(c.allowMSAA)
                  .Append(" occlusion ").Append(c.useOcclusionCulling).Append(" target ").Append(c.targetTexture != null ? c.targetTexture.width + "x" + c.targetTexture.height : "screen")
                  .Append(" cmdbufs ").Append(c.commandBufferCount).Append(" depthTex ").Append(c.depthTextureMode).Append('\n');
                // Scripts on the camera with render messages (an enabled OnRenderImage costs a full-screen copy at least).
                foreach (MonoBehaviour b in c.GetComponents<MonoBehaviour>())
                {
                    if (b == null)
                        continue;
                    var messages = new StringBuilder();
                    foreach (string m in new[] { "OnPreCull", "OnPreRender", "OnPostRender", "OnRenderImage", "OnRenderObject" })
                        if (b.GetType().GetMethod(m, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic) != null)
                            messages.Append(' ').Append(m);
                    if (messages.Length > 0)
                        sb.Append("    ").Append(b.GetType().FullName).Append(" (").Append(b.GetType().Assembly.GetName().Name).Append(b.enabled ? ", on" : ", off").Append("):").Append(messages).Append('\n');
                }
            }

            foreach (Light l in Object.FindObjectsOfType<Light>())
            {
                if (!l.isActiveAndEnabled)
                    continue;
                sb.Append("  Light '").Append(l.name).Append("' ").Append(l.type).Append(" shadows ").Append(l.shadows).Append(" mode ").Append(l.renderMode)
                  .Append(" intensity ").Append(l.intensity.ToString("F2")).Append(" range ").Append(l.range.ToString("F0"))
                  .Append(" mask 0x").Append(l.cullingMask.ToString("X8")).Append('\n');
            }

            // Scene-wide renderers per layer
            var perLayer = new int[32];
            var visiblePerLayer = new int[32];
            int sceneRenderers = 0;
            foreach (Renderer r in Object.FindObjectsOfType<Renderer>())
            {
                if (!r.enabled || !r.gameObject.activeInHierarchy)
                    continue;
                sceneRenderers++;
                perLayer[r.gameObject.layer]++;
                if (r.isVisible)
                    visiblePerLayer[r.gameObject.layer]++;
            }
            sb.Append("  Scene: ").Append(sceneRenderers).Append(" active renderers; per layer (active/visible):");
            for (int i = 0; i < 32; i++)
            {
                if (perLayer[i] > 0)
                    sb.Append(' ').Append(i).Append('=').Append(perLayer[i]).Append('/').Append(visiblePerLayer[i]);
            }
            sb.Append('\n');

            Vessel v = FlightGlobals.ActiveVessel;
            if (v != null)
                VesselInventory(v, sb);
            UnityEngine.Debug.Log(sb.ToString());
        }

        private static void VesselInventory(Vessel v, StringBuilder sb)
        {
            int renderers = 0, enabled = 0, visible = 0, skinned = 0, materialSlots = 0, castShadows = 0, receiveShadows = 0;
            long triangles = 0;
            var materials = new HashSet<Material>();
            var shaders = new Dictionary<string, int>();
            var perPartType = new Dictionary<string, int[]>();   // name -> parts, renderers, material slots, triangles/100
            foreach (Part p in v.parts)
            {
                if (p == null)
                    continue;
                Renderer[] rs = p.GetComponentsInChildren<Renderer>(true);
                string partName = p.partInfo != null ? p.partInfo.name : p.name;
                if (!perPartType.TryGetValue(partName, out int[] row))
                    perPartType[partName] = row = new int[4];
                row[0]++;
                foreach (Renderer r in rs)
                {
                    renderers++;
                    row[1]++;
                    if (!r.enabled || !r.gameObject.activeInHierarchy)
                        continue;
                    enabled++;
                    if (r.isVisible) visible++;
                    if (r is SkinnedMeshRenderer) skinned++;
                    if (r.shadowCastingMode != ShadowCastingMode.Off) castShadows++;
                    if (r.receiveShadows) receiveShadows++;
                    Material[] mats = r.sharedMaterials;
                    materialSlots += mats.Length;
                    row[2] += mats.Length;
                    foreach (Material m in mats)
                    {
                        if (m == null)
                            continue;
                        materials.Add(m);
                        string sh = m.shader != null ? m.shader.name : "?";
                        shaders.TryGetValue(sh, out int n);
                        shaders[sh] = n + 1;
                    }
                    Mesh mesh = null;
                    if (r is MeshRenderer)
                    {
                        MeshFilter mf = r.GetComponent<MeshFilter>();
                        if (mf != null) mesh = mf.sharedMesh;
                    }
                    else if (r is SkinnedMeshRenderer smr)
                        mesh = smr.sharedMesh;
                    if (mesh != null)
                    {
                        long tri = 0;
                        for (int s = 0; s < mesh.subMeshCount; s++)
                            tri += mesh.GetIndexCount(s) / 3;
                        triangles += tri;
                        row[3] += (int)(tri / 100);
                    }
                }
            }
            sb.Append("  Vessel '").Append(v.vesselName).Append("': ").Append(v.parts.Count).Append(" parts, ").Append(renderers).Append(" renderers (")
              .Append(enabled).Append(" active, ").Append(visible).Append(" visible, ").Append(skinned).Append(" skinned), ")
              .Append(materialSlots).Append(" material slots, ").Append(materials.Count).Append(" distinct materials, ")
              .Append(castShadows).Append(" casting shadows, ").Append(receiveShadows).Append(" receiving, ").Append(triangles).Append(" triangles\n");
            sb.Append("  Shaders (material slots):");
            foreach (KeyValuePair<string, int> kv in shaders)
                sb.Append(" '").Append(kv.Key).Append("' ").Append(kv.Value);
            sb.Append('\n');
            sb.Append("  Per part type (parts, renderers, material slots, triangles):");
            foreach (KeyValuePair<string, int[]> kv in perPartType)
                sb.Append(" | ").Append(kv.Key).Append(' ').Append(kv.Value[0]).Append(", ").Append(kv.Value[1]).Append(", ").Append(kv.Value[2]).Append(", ").Append(kv.Value[3] * 100L);
            sb.Append('\n');
        }

        // ---------------------------------------------------------------- experiments (diagnostic, restored)
        // Spec list, a baseline window is inserted before each experiment and at the end:
        //   shadows:off | cascades:<0|2|4> | shadowres:<Low|Medium|High|VeryHigh> | shadowdist:<meters>
        //   vessel:hidden | vessel:noshadows | camoff:<camera>
        //   camshadows:<camera>:off | camcascades:<camera>:<n> | camdistance:<camera>:<meters>
        //   upscaler:<on|off|nosharpen|scale> (Upscaler switched, unsharpened or rescaled for the window, restored after)
        //   viewport:<scale> (every camera drawing to the screen draws scale x scale of its rectangle: pixel-bound test)

        private void BuildSteps(string specs)
        {
            _steps.Clear();
            foreach (string raw in specs.Split(';'))
            {
                string spec = raw.Trim();
                if (spec.Length == 0)
                    continue;
                _steps.Add("baseline");
                _steps.Add(spec);
            }
            _steps.Add("baseline");
        }

        private void StepExperiments(float now)
        {
            if (_step < 0)
            {
                _step = 0;
                BeginStep(now);
                return;
            }

            float inStep = now - _stepStart;
            if (inStep >= _experimentSettle)
            {
                _stepFrames++;
                _stepFrameTime += Time.unscaledDeltaTime;
                if (!Application.isFocused)
                    _stepUnfocused++;
            }
            if (inStep < _experimentWindow)
                return;

            double avgMs = _stepFrames > 0 ? _stepFrameTime * 1000.0 / _stepFrames : 0;
            ReportCameras("during " + _steps[_step]);
            _results.Add(_steps[_step] + ": " + avgMs.ToString("F2") + " ms (" + (avgMs > 0 ? 1000.0 / avgMs : 0).ToString("F1") + " fps)"
                         + (_stepUnfocused > 0 ? ", KSP not focused for " + _stepUnfocused + " of " + _stepFrames + " frames" : ""));
            RestoreExperiment();
            ResetCameraStats();

            _step++;
            if (_step >= _steps.Count)
            {
                _experimentsDone = true;
                Log.Info("RenderProbe experiments (frame time per window, diagnostic only, all changes restored):\n  " + string.Join("\n  ", _results.ToArray()));
                return;
            }
            BeginStep(now);
        }

        private void BeginStep(float now)
        {
            _stepStart = now;
            _stepFrames = 0;
            _stepFrameTime = 0;
            _stepUnfocused = 0;
            ResetCameraStats();
            string[] parts = _steps[_step].Split(':');
            string kind = parts[0];
            Vessel v = FlightGlobals.ActiveVessel;
            switch (kind)
            {
                case "shadows":
                    _savedShadows = QualitySettings.shadows;
                    _globalShadowsChanged = true;
                    QualitySettings.shadows = ShadowQuality.Disable;
                    break;
                case "cascades":
                case "shadowres":
                case "shadowdist":
                    if (parts.Length < 2)
                        break;
                    _gSavedCascades = QualitySettings.shadowCascades;
                    _gSavedResolution = QualitySettings.shadowResolution;
                    _gSavedDistance = QualitySettings.shadowDistance;
                    _globalQualityChanged = true;
                    if (kind == "cascades")
                        QualitySettings.shadowCascades = int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
                    else if (kind == "shadowres")
                        QualitySettings.shadowResolution = (ShadowResolution)System.Enum.Parse(typeof(ShadowResolution), parts[1], true);
                    else
                        QualitySettings.shadowDistance = float.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "vessel":
                    if (v == null)
                        break;
                    bool hide = parts.Length > 1 && parts[1] == "hidden";
                    foreach (Part p in v.parts)
                        foreach (Renderer r in p.GetComponentsInChildren<Renderer>())
                        {
                            if (hide && r.enabled) { r.enabled = false; _changedRenderers.Add(r); }
                            else if (!hide && r.shadowCastingMode != ShadowCastingMode.Off)
                            {
                                _changedRenderers.Add(r);
                                _savedModes.Add(r.shadowCastingMode);
                                r.shadowCastingMode = ShadowCastingMode.Off;
                            }
                        }
                    break;
                case "camoff":
                    if (parts.Length > 1)
                        foreach (Camera c in Camera.allCameras)
                            if (c.name == parts[1] && c.enabled) { c.enabled = false; _disabledCamera = c; break; }
                    break;
                case "upscaler":
                    if (Rendering.Upscaler.Active != null && parts.Length > 1)
                    {
                        _upscalerChanged = true;
                        if (parts[1] == "on" || parts[1] == "off")
                            Rendering.Upscaler.Active.SetRunning(parts[1] == "on");
                        else if (parts[1] == "nosharpen")
                            Rendering.Upscaler.Active.SetSharpening(false);
                        else if (float.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float upscale))
                            Rendering.Upscaler.Active.SetScale(upscale);
                    }
                    break;
                case "viewport":
                    if (parts.Length > 1 && float.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float scale))
                        foreach (Camera c in Camera.allCameras)
                            if (c.targetTexture == null)
                            {
                                Rect r = c.rect;
                                _viewportCameras.Add(c);
                                _viewportRects.Add(r);
                                c.rect = new Rect(r.x * scale, r.y * scale, r.width * scale, r.height * scale);
                            }
                    break;
                case "camshadows":
                case "camcascades":
                case "camdistance":
                    if (parts.Length > 2)
                    {
                        _camOverrideName = parts[1];
                        _camOverrideKind = kind;
                        float.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _camOverrideValue);
                    }
                    break;
            }
        }

        // Per-camera override: applied right before the target camera culls, restored right after it renders.
        private void ApplyCameraOverride(CamStat s)
        {
            RestoreCameraOverride();
            if (_camOverrideName == null || s.Name != _camOverrideName)
                return;
            _savedQShadows = QualitySettings.shadows;
            _savedQCascades = QualitySettings.shadowCascades;
            _savedQDistance = QualitySettings.shadowDistance;
            _camOverrideApplied = true;
            switch (_camOverrideKind)
            {
                case "camshadows": QualitySettings.shadows = ShadowQuality.Disable; break;
                case "camcascades": QualitySettings.shadowCascades = (int)_camOverrideValue; break;
                case "camdistance": QualitySettings.shadowDistance = _camOverrideValue; break;
            }
        }

        private void RestoreCameraOverride()
        {
            if (!_camOverrideApplied)
                return;
            QualitySettings.shadows = _savedQShadows;
            QualitySettings.shadowCascades = _savedQCascades;
            QualitySettings.shadowDistance = _savedQDistance;
            _camOverrideApplied = false;
        }

        private void RestoreExperiment()
        {
            RestoreCameraOverride();
            _camOverrideName = null;
            if (_globalShadowsChanged)
                QualitySettings.shadows = _savedShadows;
            _globalShadowsChanged = false;
            if (_globalQualityChanged)
            {
                QualitySettings.shadowCascades = _gSavedCascades;
                QualitySettings.shadowResolution = _gSavedResolution;
                QualitySettings.shadowDistance = _gSavedDistance;
            }
            _globalQualityChanged = false;
            for (int i = 0; i < _changedRenderers.Count; i++)
            {
                Renderer r = _changedRenderers[i];
                if (r == null)
                    continue;
                if (i < _savedModes.Count)
                    r.shadowCastingMode = _savedModes[i];
                else
                    r.enabled = true;
            }
            _changedRenderers.Clear();
            _savedModes.Clear();
            if (_disabledCamera != null)
                _disabledCamera.enabled = true;
            _disabledCamera = null;
            for (int i = 0; i < _viewportCameras.Count; i++)
                if (_viewportCameras[i] != null)
                    _viewportCameras[i].rect = _viewportRects[i];
            _viewportCameras.Clear();
            _viewportRects.Clear();
            if (_upscalerChanged && Rendering.Upscaler.Active != null)
            {
                Rendering.Upscaler.Active.SetRunning(true);
                Rendering.Upscaler.Active.SetSharpening(true);
                Rendering.Upscaler.Active.SetScale(Rendering.Upscaler.Active.ConfiguredScale);
            }
            _upscalerChanged = false;
        }
    }
}
