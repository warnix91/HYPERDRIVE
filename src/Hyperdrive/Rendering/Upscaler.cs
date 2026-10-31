using System;
using System.IO;
using Hyperdrive.Core;
using Hyperdrive.Patching;
using UnityEngine;

namespace Hyperdrive.Rendering
{
    /// <summary>
    /// Opt-in upscaler (AMD FSR 1: EASU + RCAS, Shaders/hyperdrive-*.bundle). The scene cameras (depth up to maxSceneDepth:
    /// galaxy, scaled space, far and near cameras) draw into a render texture of scale x scale the screen; a camera of our
    /// own, right after them, upscales it to the screen and sharpens it; the cameras after it (markers, UI) draw at full
    /// resolution on top. Measured on a Mac M4 (pad, heavy craft): 56 % of the pixels took the frame from 25 to 17 ms.
    ///
    /// A camera's target is our texture only while it culls and renders (Camera.onPreCull to onPostRender): the rest of
    /// the frame its target is the screen, so ScreenPointToRay, WorldToScreenPoint and pixelWidth, called by KSP and mods
    /// from Update, keep their full-screen meaning (part picking, markers). It changes the image (fewer pixels rendered),
    /// hence off by default; Alt+F9 switches it live.
    ///
    /// dynamic = true (opt-in): the rendered size follows the load, as Lossless Scaling's resolution scaling does but
    /// inside the game. Frames slower than the target frame rate for 1.5 s lower it one step (0.1 per axis, down to
    /// minScale); a step that does not make frames faster (the frame is waiting on the CPU, not the GPU) is undone and
    /// steps down pause for a while. Spare speed raises it again, up to maxScale; at 1 nothing is redirected (native
    /// image, no upscaling pass). With vsync or a frame limit, spare speed is not visible in frame times: a step up is
    /// tried from time to time and undone at once if frames become slow.
    /// supersample = true (opt-in, with dynamic): when the CPU is the limit (a smaller size made frames no faster), the
    /// graphics card has time to spare; the scene is then rendered above 100 % (steps of 25 %, up to maxSupersample) and
    /// scaled down to the screen (bilinear: a 2 x 2 average at 200 %), a sharper image for free. A step that makes frames
    /// over 4 % slower is undone; frames getting 8 % slower while above 100 % bring it back to 100 % at once.
    /// </summary>
    internal sealed class Upscaler : Subsystem
    {
        private float _scale = 0.75f;
        private float _sharpnessStops = 0.2f;
        private float _maxSceneDepth = 0f;
        private string _scenes = "FLIGHT,SPACECENTER,TRACKSTATION";
        private KeyCode _toggleKey = KeyCode.F9;
        private bool _dynamic;
        private float _minScale = 0.6f, _maxScale = 1f, _targetFps;
        private bool _supersample, _assumeCpuBound;
        private float _maxSupersample = 1.5f;
        private AssetBundle _bundle;
        private Material _material;
        private UpscalerView _view;

        public override string Name => "Upscaler";

        public override bool EnabledByDefault => false;

        internal static Upscaler Active { get; private set; }

        private string _bundleOverride = "";

        protected internal override void LoadSettings(SettingsSection settings)
        {
            _scale = Mathf.Clamp(settings.GetFloat("scale", _scale), 0.5f, 1f);
            _sharpnessStops = Mathf.Clamp(settings.GetFloat("sharpness", _sharpnessStops), 0f, 2f);
            _maxSceneDepth = settings.GetFloat("maxSceneDepth", _maxSceneDepth);
            _scenes = settings.GetString("scenes", _scenes);
            _dynamic = settings.GetBool("dynamic", false);
            _minScale = Mathf.Clamp(settings.GetFloat("minScale", _minScale), 0.5f, 1f);
            _maxScale = Mathf.Clamp(settings.GetFloat("maxScale", _maxScale), _minScale, 1f);
            _targetFps = Mathf.Clamp(settings.GetFloat("targetFps", 0f), 0f, 360f);
            _supersample = settings.GetBool("supersample", false);
            _bundleOverride = settings.GetString("bundlePlatform", "").Trim().ToLowerInvariant();   // developer: test another platform's bundle
            _maxSupersample = Mathf.Clamp(settings.GetFloat("maxSupersample", _maxSupersample), 1.25f, 2f);
            _assumeCpuBound = settings.GetBool("assumeCpuBound", false);   // developer: test the supersampling steps on any machine
            string key = settings.GetString("toggleKey", "F9");
            try { _toggleKey = (KeyCode)Enum.Parse(typeof(KeyCode), key, true); }
            catch (ArgumentException) { Log.Warn("Upscaler: unknown toggleKey '" + key + "', using F9"); }
        }

        protected internal override void OnEnable()
        {
            string platform;
            switch (Application.platform)
            {
                case RuntimePlatform.OSXPlayer: platform = "mac"; break;
                case RuntimePlatform.WindowsPlayer: platform = "windows"; break;
                case RuntimePlatform.LinuxPlayer: platform = "linux"; break;   // bundle built with tools/build_shaders.sh linux
                default: throw new SubsystemUnavailableException("no shader bundle for " + Application.platform);
            }
            if (_bundleOverride.Length > 0)
                platform = _bundleOverride;
            string path = Path.Combine(Path.Combine(HyperdriveRuntime.ModDirectory, "Shaders"), "hyperdrive-" + platform + ".bundle");
            if (!File.Exists(path))
                throw new SubsystemUnavailableException("shader bundle missing: " + path);
            _bundle = AssetBundle.LoadFromFile(path);
            Shader shader = _bundle != null ? _bundle.LoadAsset<Shader>("Assets/Shaders/Upscale.shader") : null;
            if (shader == null || !shader.isSupported)
            {
                OnDisable();
                throw new SubsystemUnavailableException("upscale shader not supported on " + SystemInfo.graphicsDeviceType);
            }
            _material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            _material.SetFloat("_Sharpness", Mathf.Pow(2f, -_sharpnessStops));

            var host = new GameObject("Hyperdrive.Upscaler");
            UnityEngine.Object.DontDestroyOnLoad(host);
            _view = host.AddComponent<UpscalerView>();
            _view.Setup(_material, _scale, _maxSceneDepth, _toggleKey);
            _view.Sharpen = _sharpnessStops < 2f;
            if (_dynamic)
                _view.SetupDynamic(_minScale, _maxScale, _targetFps, _supersample ? _maxSupersample : 1f, _assumeCpuBound);
            Active = this;
            Log.Info("Upscaler: FSR 1 at " + (_scale * 100f).ToString("F0") + " % per axis (" + (_scale * _scale * 100f).ToString("F0")
                     + " % of the pixels), sharpness " + _sharpnessStops.ToString("F2") + " stops, scenes " + _scenes + ", Alt+" + _toggleKey + " switches it"
                     + (_dynamic ? "; dynamic between " + (_minScale * 100f).ToString("F0") + " and " + (_maxScale * 100f).ToString("F0") + " %"
                        + (_supersample ? ", up to " + (_maxSupersample * 100f).ToString("F0") + " % when the CPU is the limit" : "") : ""));
        }

        protected internal override void OnDisable()
        {
            Active = null;
            if (_view != null)
            {
                _view.Teardown();
                UnityEngine.Object.Destroy(_view.gameObject);
                _view = null;
            }
            if (_material != null)
                UnityEngine.Object.Destroy(_material);
            _material = null;
            if (_bundle != null)
                _bundle.Unload(true);
            _bundle = null;
        }

        protected internal override void OnSceneReady(GameScenes scene)
        {
            if (_view != null)
            {
                _view.SceneAllowed = _scenes.IndexOf(scene.ToString(), StringComparison.OrdinalIgnoreCase) >= 0;
                _view.ResetDynamic();
            }
        }

        /// <summary>For RenderProbe experiments: switches the upscaling without unloading anything.</summary>
        internal void SetRunning(bool running)
        {
            if (_view != null)
                _view.Running = running;
        }

        /// <summary>For RenderProbe experiments: rendered size per axis, and the sharpening pass.</summary>
        internal void SetScale(float scale)
        {
            if (_view != null)
                _view.Scale = Mathf.Clamp(scale, 0.5f, 1f);
        }

        internal void SetSharpening(bool on)
        {
            if (_view != null)
                _view.Sharpen = on;
        }

        internal float ConfiguredScale => _scale;
    }

    [DefaultExecutionOrder(32000)]
    internal sealed class UpscalerView : MonoBehaviour
    {
        private const int MaxScaled = 16;

        private Material _material;
        internal float Scale;
        internal bool Sharpen = true;
        private float _maxSceneDepth;
        private KeyCode _toggleKey;
        private Camera _blitCamera;
        private RenderTexture _low, _mid, _spareLow;   // spare: the previous reduced size, kept for a quick way back
        private Camera[] _all = new Camera[32];
        private readonly Camera[] _scaled = new Camera[MaxScaled];
        private int _scaledCount;
        private Camera.CameraCallback _onPreCull, _onPostRender;
        private bool _active;          // this frame: scene cameras redirected, blit camera on
        internal bool Running = true;  // Alt+key / experiments
        internal bool SceneAllowed;

        // Dynamic resolution: frame times averaged over half-second windows.
        private const float Window = 0.5f;
        private bool _dynamic;
        private float _minScale, _maxScale, _targetFps;
        private float _target;              // target frame time (s), from targetFps, the frame limit or the screen
        private bool _capped;               // vsync or frame limit: spare speed does not show in frame times
        private float _winTime;
        private int _winFrames;
        private int _slowWindows, _fastWindows, _steadyWindows;
        private float _beforeDown;          // average frame time before the last step down, while it is judged
        private float _beforeDownScale;
        private bool _probingUp;            // capped: last step up is a try, undone if the next window is slow
        private float _downPausedUntil, _upPausedUntil;
        private float _probeInterval = 8f;
        private int _settleWindows;          // windows ignored after a change (new texture, new load)
        // Supersampling (above 100 %) when the CPU is the limit.
        private float _superMax = 1f;
        private bool _cpuBound, _superJudge;
        private float _superBefore, _superPrev, _superBaseline, _superNextAt, _superInterval = 10f;
        private int _superSlowWindows;

        internal void Setup(Material material, float scale, float maxSceneDepth, KeyCode toggleKey)
        {
            _material = material;
            Scale = scale;
            _maxSceneDepth = maxSceneDepth;
            _toggleKey = toggleKey;
            var blit = new GameObject("Hyperdrive.Upscaler.Blit");
            blit.transform.SetParent(transform, false);
            _blitCamera = blit.AddComponent<Camera>();
            _blitCamera.clearFlags = CameraClearFlags.Nothing;
            _blitCamera.cullingMask = 0;
            _blitCamera.useOcclusionCulling = false;
            _blitCamera.allowHDR = false;
            _blitCamera.allowMSAA = false;
            _blitCamera.enabled = false;
            blit.AddComponent<UpscalerBlit>().View = this;
            _onPreCull = OnAnyPreCull;
            _onPostRender = OnAnyPostRender;
            Camera.onPreCull += _onPreCull;
            Camera.onPostRender += _onPostRender;
        }

        internal void SetupDynamic(float minScale, float maxScale, float targetFps, float superMax, bool assumeCpuBound)
        {
            _dynamic = true;
            _superMax = superMax;
            _cpuBound = assumeCpuBound;
            _minScale = minScale;
            _maxScale = maxScale;
            _targetFps = targetFps;
            Scale = Mathf.Clamp(Snap(Scale), _minScale, _maxScale);   // steps of 0.1 per axis
            ResetDynamic();
        }

        // New scene: new load, new frame limit; the window starts over (the scale is kept).
        internal void ResetDynamic()
        {
            if (!_dynamic)
                return;
            float fps = _targetFps;
            _capped = QualitySettings.vSyncCount > 0 || Application.targetFrameRate > 0;
            if (fps <= 0f)
            {
                int refresh = Screen.currentResolution.refreshRate > 0 ? Screen.currentResolution.refreshRate : 60;
                if (QualitySettings.vSyncCount > 0)
                    fps = refresh / (float)QualitySettings.vSyncCount;
                else if (Application.targetFrameRate > 0)
                    fps = Mathf.Min(Application.targetFrameRate, refresh);
                else
                    fps = refresh;
            }
            _target = 1f / Mathf.Clamp(fps, 20f, 360f);
            _winTime = 0f;
            _winFrames = 0;
            _slowWindows = _fastWindows = _steadyWindows = 0;
            _beforeDown = 0f;
            _probingUp = false;
            _downPausedUntil = _upPausedUntil = Time.unscaledTime + 3f;   // the scene settles first
            _superJudge = false;
            _superSlowWindows = 0;
            _superNextAt = Time.unscaledTime + 10f;
            if (Scale > 1f)
                Scale = 1f;   // a new scene: back to native until the CPU is found to be the limit again
        }

        private void UpdateDynamic()
        {
            _winTime += Time.unscaledDeltaTime;
            _winFrames++;
            if (_winTime < Window)
                return;
            float avg = _winTime / _winFrames;
            _winTime = 0f;
            _winFrames = 0;
            float now = Time.unscaledTime;
            bool slow = avg > _target * 1.08f;
            if (_settleWindows > 0)
            {
                _settleWindows--;
                return;
            }
            if (_superMax > 1f && Supersampling(avg, now))
                return;

            if (_probingUp)
            {
                _probingUp = false;
                if (slow)
                {
                    SetScale(Scale - 0.1f, "the step up made frames slow");
                    _probeInterval = Mathf.Min(_probeInterval * 2f, 64f);
                    _upPausedUntil = now + _probeInterval;
                    return;
                }
                _probeInterval = 8f;
                Log.Info("Upscaler: " + (Scale * 100f).ToString("F0") + " % per axis (step up kept: frames still on target)");
            }
            if (_beforeDown > 0f)
            {
                // Judged one window after the step: a step that brings nothing means the GPU was not the limit. 0.1 less per
                // axis drops ~20 % of the pixels: a GPU-bound frame gets at least ~8 % faster, a 4 % gain is window noise
                // (CPU-bound PC, heavy craft: four noise steps down to 60 % for no frame rate at all).
                float before = _beforeDown;
                _beforeDown = 0f;
                if (avg > before * 0.92f)
                {
                    // The earlier steps down were window noise too: back to the full size (and above it, when supersampling).
                    SetScale(_maxScale, "no faster at a smaller size (the CPU is the limit)");
                    _cpuBound = true;
                    _downPausedUntil = now + (_superMax > 1f ? 60f : 20f);
                    return;
                }
            }

            if (slow)
            {
                _slowWindows++;
                _fastWindows = _steadyWindows = 0;
            }
            else if (!_capped && avg < _target * 0.8f)
            {
                _fastWindows++;
                _slowWindows = _steadyWindows = 0;
            }
            else
            {
                _steadyWindows++;
                _slowWindows = _fastWindows = 0;
            }

            if (_slowWindows >= 3 && now >= _downPausedUntil && Scale > _minScale + 0.001f)
            {
                _beforeDown = avg;
                _beforeDownScale = Scale;
                SetScale(Scale - 0.1f, "frames slower than the target");
                _slowWindows = 0;
                _upPausedUntil = now + 15f;
            }
            else if (Scale < _maxScale - 0.001f && now >= _upPausedUntil)
            {
                if (_fastWindows >= 6)
                {
                    SetScale(Scale + 0.1f, "spare speed");
                    _fastWindows = 0;
                }
                else if (_capped && _steadyWindows * Window >= _probeInterval)
                {
                    _probingUp = true;
                    SetScale(Scale + 0.1f, null);
                    _steadyWindows = 0;
                }
            }
        }

        // Above 100 %: judged against the frame time before each step, not against the target (the CPU sets the pace).
        private bool Supersampling(float avg, float now)
        {
            if (_superJudge)
            {
                _superJudge = false;
                if (avg > _superBefore * 1.04f)
                {
                    SetScaleAny(_superPrev, "the graphics card has no time to spare at a larger size");
                    _superInterval = Mathf.Min(_superInterval * 2f, 80f);
                    _superNextAt = now + _superInterval;
                }
                else
                {
                    _superBaseline = avg;
                    _superInterval = 10f;
                    _superNextAt = now + _superInterval;
                    Log.Info("Upscaler: " + (Scale * 100f).ToString("F0") + " % per axis kept (the CPU sets the pace, the graphics card renders more pixels for free)");
                }
                return true;
            }
            if (Scale > 1.001f)
            {
                _superSlowWindows = avg > _superBaseline * 1.08f ? _superSlowWindows + 1 : 0;
                if (_superSlowWindows >= 3)
                {
                    _superSlowWindows = 0;
                    SetScaleAny(1f, "frames slower: back to native");
                    _superNextAt = now + 30f;
                    return true;
                }
            }
            if (_cpuBound && Scale >= 0.999f && Scale < _superMax - 0.001f && now >= _superNextAt)
            {
                _superPrev = Scale;
                _superBefore = avg;
                _superJudge = true;
                SetScaleAny(Mathf.Min(_superMax, Mathf.Floor(Scale * 4f + 0.5f) / 4f + 0.25f), null);
                return true;
            }
            return Scale > 1.001f;   // while above 100 %, the steps below 100 % wait
        }

        private void SetScaleAny(float scale, string reason)
        {
            float next = Mathf.Clamp(scale, _minScale, Mathf.Max(_maxScale, _superMax));
            if (Mathf.Abs(next - Scale) < 0.001f)
                return;
            Scale = next;
            _settleWindows = 1;
            if (reason != null)
                Log.Info("Upscaler: " + (next * 100f).ToString("F0") + " % per axis (" + reason + ")");
        }

        private void SetScale(float scale, string reason)
        {
            float next = Mathf.Clamp(Snap(scale), _minScale, _maxScale);
            if (Mathf.Abs(next - Scale) < 0.001f)
                return;
            Scale = next;
            _settleWindows = 1;
            if (reason != null)
                Log.Info("Upscaler: " + (next * 100f).ToString("F0") + " % per axis (" + reason + ")");
        }

        private static float Snap(float scale) => Mathf.Floor(scale * 10f + 0.5f) / 10f;

        internal void Teardown()
        {
            Camera.onPreCull -= _onPreCull;
            Camera.onPostRender -= _onPostRender;
            Release();
        }

        private void Release()
        {
            if (_low != null) { _low.Release(); Destroy(_low); _low = null; }
            if (_spareLow != null) { _spareLow.Release(); Destroy(_spareLow); _spareLow = null; }
            if (_mid != null) { _mid.Release(); Destroy(_mid); _mid = null; }
        }

        private void Update()
        {
            if (Input.GetKeyDown(_toggleKey) && (Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt)))
            {
                Running = !Running;
                Log.Info("Upscaler: " + (Running ? "on" : "off"));
            }
        }

        // After every script's LateUpdate has chosen its cameras: which cameras draw the scene this frame.
        private void LateUpdate()
        {
            _active = false;
            _scaledCount = 0;
            if (!Running || !SceneAllowed || _material == null)
            {
                _blitCamera.enabled = false;
                return;
            }
            if (_dynamic)
            {
                UpdateDynamic();
                if (Mathf.Abs(Scale - 1f) < 0.001f)
                {
                    _blitCamera.enabled = false;   // native size: nothing to redirect or scale
                    return;
                }
            }
            int count = Camera.allCamerasCount;
            if (count > _all.Length)
                _all = new Camera[count * 2];
            count = Camera.GetAllCameras(_all);

            // Scene cameras: drawing to the screen, depth up to maxSceneDepth (KSP: galaxy -4, scaled space -3, far -1,
            // near 0; markers 1 and UI 90+ stay at full resolution). The blit goes between them and the next camera.
            float top = float.NegativeInfinity, next = float.PositiveInfinity;
            for (int i = 0; i < count; i++)
            {
                Camera c = _all[i];
                if (c == null || c == _blitCamera || c.targetTexture != null)
                    continue;
                if (c.depth <= _maxSceneDepth)
                {
                    if (_scaledCount < MaxScaled)
                        _scaled[_scaledCount++] = c;
                    if (c.depth > top)
                        top = c.depth;
                }
                else if (c.depth < next)
                    next = c.depth;
            }
            for (int i = 0; i < count; i++)
                _all[i] = null;
            if (_scaledCount == 0 || _scaledCount >= MaxScaled)
            {
                _blitCamera.enabled = false;
                return;
            }

            int w = Screen.width, h = Screen.height;
            int lw = Mathf.Max(16, Mathf.RoundToInt(w * Scale)), lh = Mathf.Max(16, Mathf.RoundToInt(h * Scale));
            int aa = Mathf.Max(1, QualitySettings.antiAliasing);
            if (_mid == null || _mid.width != w || _mid.height != h || (_low != null && _low.antiAliasing != aa))
            {
                Release();
                _mid = new RenderTexture(w, h, 0, RenderTextureFormat.Default) { name = "Hyperdrive.Upscaler.Mid", filterMode = FilterMode.Point };
                _mid.Create();
                _material.SetVector("_OutputSize", new Vector4(w, h, 1f / w, 1f / h));
            }
            if (_low == null || _low.width != lw || _low.height != lh)
            {
                // A new reduced size (dynamic steps): the previous one is kept as the spare, so stepping back is free.
                RenderTexture previous = _low;
                if (_spareLow != null && _spareLow.width == lw && _spareLow.height == lh)
                {
                    _low = _spareLow;
                }
                else
                {
                    if (_spareLow != null) { _spareLow.Release(); Destroy(_spareLow); }
                    _low = new RenderTexture(lw, lh, 24, RenderTextureFormat.Default) { name = "Hyperdrive.Upscaler.Low", antiAliasing = aa, filterMode = FilterMode.Bilinear };
                    _low.Create();
                }
                _spareLow = previous;
            }
            _blitCamera.depth = next == float.PositiveInfinity ? top + 1f : top + (next - top) * 0.5f;
            _blitCamera.enabled = true;
            _active = true;
        }

        private bool IsScaled(Camera c)
        {
            for (int i = 0; i < _scaledCount; i++)
                if (_scaled[i] == c)
                    return true;
            return false;
        }

        private void OnAnyPreCull(Camera c)
        {
            if (_active && c.targetTexture == null && IsScaled(c))
                c.targetTexture = _low;
        }

        private void OnAnyPostRender(Camera c)
        {
            if (_active && c.targetTexture == _low && _low != null)
                c.targetTexture = null;
        }

        // Called after the scene cameras and before the markers and UI.
        internal void Blit()
        {
            if (!_active || _low == null || _mid == null)
                return;
            if (Scale > 1f)
            {
                Graphics.Blit(_low, (RenderTexture)null);   // supersampled: scaled down (bilinear), no upscaling pass
                return;
            }
            if (Sharpen)
            {
                Graphics.Blit(_low, _mid, _material, 0);
                Graphics.Blit(_mid, (RenderTexture)null, _material, 1);
            }
            else
                Graphics.Blit(_low, (RenderTexture)null, _material, 0);
        }

        private void OnDestroy()
        {
            Camera.onPreCull -= _onPreCull;
            Camera.onPostRender -= _onPostRender;
            Release();
        }
    }

    internal sealed class UpscalerBlit : MonoBehaviour
    {
        internal UpscalerView View;

        private void OnPostRender()
        {
            if (View != null)
                View.Blit();
        }
    }
}
