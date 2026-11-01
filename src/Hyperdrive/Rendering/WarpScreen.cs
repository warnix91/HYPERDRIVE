using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using Hyperdrive.Core;
using Hyperdrive.Patching;
using UnityEngine;

namespace Hyperdrive.Rendering
{
    /// <summary>
    /// Loading-screen animation: the game "jumps to hyperspace" while it loads and drops out at the main menu.
    /// A 3D starfield (power-law brightness, star colour temperatures, a denser deep field near the axis) is flown
    /// through along the view axis; a streak is the projection of a star's motion during a short exposure, so its length
    /// comes from perspective (long near the edges, short near the vanishing point), with a slight Doppler tint at speed
    /// and a faint halo around the brightest stars. Phases: ignition (still sky, then acceleration and a brief exposure
    /// lift at the jump), cruise (speed breathing slowly, vanishing point drifting like a hand-held camera), arrival when
    /// the last loading system is ready (the scene load is held for that second so the deceleration plays, and the
    /// frozen frames of the final scene loads show a still sky), then a fade to the main menu.
    /// A discreet lower third shows KSP's own loading tip, the current stage and the progress.
    /// One mesh of soft-edged quads drawn additively in OnGUI; nothing is allocated per frame.
    /// </summary>
    internal sealed class WarpScreen : Subsystem
    {
        internal int StarCount = 2600;
        internal bool CaptureFrames, ExportArt;
        internal bool CaptureClean;          // developer: drawn effects captured alone, without the game's UI over them
        internal bool AsyncSceneLoads = true;
        internal int MaxRefreshRate = 144;
        internal bool HoldGcAtEnd = false;   // measured: removes the GCs but not the 2.1 s frame, heap +1.2 GB
        internal string Style = "hyperspace";
        internal bool PreviewStyles;
        internal float PreviewSeconds = 9f;
        internal bool EasterEgg = true;
        private GameObject _host;
        private static WarpScreenView s_view;
        private static readonly MethodInfo LoadSceneByIndex = AccessTools.Method(typeof(UnityEngine.SceneManagement.SceneManager), "LoadScene", new[] { typeof(int) });
        private static readonly MethodInfo LoadSceneHelper = AccessTools.Method(typeof(WarpScreen), nameof(LoadScene));
        private static int s_replaced;

        public override string Name => "WarpScreen";

        // Loading Screen Manager replaces the loading screens with the player's own: those are kept.
        public override string[] IncompatibleAssemblies => new[] { "LoadingScreenManager" };

        protected internal override void LoadSettings(SettingsSection settings)
        {
            StarCount = Mathf.Clamp(settings.GetInt("stars", StarCount), 300, 6000);
            CaptureFrames = settings.GetBool("captureFrames", false);
            ExportArt = settings.GetBool("exportArt", false);
            CaptureClean = settings.GetBool("captureClean", false);
            AsyncSceneLoads = settings.GetBool("asyncSceneLoads", true);
            MaxRefreshRate = Mathf.Clamp(settings.GetInt("maxRefreshRate", MaxRefreshRate), 30, 360);
            HoldGcAtEnd = settings.GetBool("holdGcDuringFinalScenes", false);
            Style = settings.GetString("style", Style).ToLowerInvariant();
            PreviewStyles = settings.GetBool("previewStyles", false);
            PreviewSeconds = Mathf.Clamp(settings.GetFloat("previewSeconds", PreviewSeconds), 2f, 60f);
            EasterEgg = settings.GetBool("easterEgg", true);
            CoverFreezes = settings.GetBool("coverFreezes", true);
            Intro = settings.GetBool("intro", true);
        }

        // A title sequence as KSP opens with: the game's name, then the mod's, over the loading screen before the animation
        // starts; the author is only credited in small print.
        internal bool Intro = true;

        // Frozen frames of a first load fall at the same places from one start to the next (same files, same order): where
        // they fell (loading progress) is kept in PluginData, and the animation calms down just before them next time.
        internal bool CoverFreezes = true;

        protected internal override void OnEnable()
        {
            LoadingScreen screen = LoadingScreen.Instance;
            if (screen == null || HighLogic.LoadedScene != GameScenes.LOADING)
                throw new SubsystemUnavailableException("not during the game's first load");

            // The two scene loads that end the first load (planetary system, then main menu) are synchronous in stock and
            // freeze the screen for ~4 s and ~2 s. While the animation runs they use SceneManager.LoadSceneAsync, the call
            // KSP itself uses for its other scene changes.
            if (AsyncSceneLoads && LoadSceneByIndex != null && LoadSceneHelper != null)
            {
                s_replaced = 0;
                Type loadSystems = AccessTools.FirstInner(typeof(LoadingScreen), t => t.Name.Contains("<LoadSystems>"));
                MethodInfo moveNext = loadSystems != null ? AccessTools.Method(loadSystems, "MoveNext") : null;
                if (moveNext != null)
                    Patch(moveNext, transpiler: nameof(SceneLoadTranspiler));
                Patch(AccessTools.Method(typeof(HighLogic), nameof(HighLogic.LoadScene), new[] { typeof(GameScenes) }), transpiler: nameof(SceneLoadTranspiler));
                Log.Info("WarpScreen: " + s_replaced + " scene loads of the first load made asynchronous");
            }

            _host = new GameObject("Hyperdrive.WarpScreen");
            UnityEngine.Object.DontDestroyOnLoad(_host);
            s_view = _host.AddComponent<WarpScreenView>();
            s_view.Init(this, screen);
            Log.Info("WarpScreen: loading animation '" + s_view.StyleName + "' (" + StarCount + " stars" + (PreviewStyles ? ", cycling through every style" : "") + ")");
        }

        protected internal override void OnDisable() => Finished();

        protected internal override void OnSceneReady(GameScenes scene)
        {
            if (s_view != null && scene == GameScenes.MAINMENU)
                s_view.RequestExit();

        }

        internal void Finished()
        {
            s_view = null;
            Loading.FastLoader.FrameBudgetOverride = -1f;
            if (Application.backgroundLoadingPriority == UnityEngine.ThreadPriority.High && HighLogic.LoadedScene == GameScenes.MAINMENU)
                Application.backgroundLoadingPriority = UnityEngine.ThreadPriority.Normal;   // KSP's value after its loads
            if (_host != null)
                UnityEngine.Object.Destroy(_host);
            _host = null;
        }

        // IL: "call SceneManager::LoadScene(int32)" consumes [index] and returns nothing; the helper has the same shape.
        private static IEnumerable<CodeInstruction> SceneLoadTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            foreach (CodeInstruction instruction in instructions)
            {
                if ((instruction.opcode == System.Reflection.Emit.OpCodes.Call || instruction.opcode == System.Reflection.Emit.OpCodes.Callvirt)
                    && instruction.operand is MethodInfo m && m == LoadSceneByIndex)
                {
                    instruction.opcode = System.Reflection.Emit.OpCodes.Call;
                    instruction.operand = LoadSceneHelper;
                    s_replaced++;
                }
                yield return instruction;
            }
        }

        // Asynchronous only while the loading animation is on screen (the first load); otherwise exactly stock.
        private static void LoadScene(int index)
        {
            if (s_view != null)
            {
                s_view.BeginApproach();
                Application.backgroundLoadingPriority = UnityEngine.ThreadPriority.High;   // as KSP's own async scene loads
                UnityEngine.SceneManagement.SceneManager.LoadSceneAsync(index);
            }
            else
                UnityEngine.SceneManagement.SceneManager.LoadScene(index);
        }
    }

    internal sealed class WarpScreenView : MonoBehaviour
    {
        private enum Phase { Ignition, Cruise, Approach, Exit }

        // Hyperspace: the jump through a field of streaking stars. Vortex: a spiralling tunnel of cold light.
        // Nebula: a slow drift through coloured clouds. Orbit: sunrise over a planet's limb, the sun climbing with progress.
        internal enum Style { Hyperspace, Vortex, Nebula, Orbit }
        private static readonly string[] StyleNames = { "hyperspace", "vortex", "nebula", "orbit" };

        // Effects drawn by their own code (LoadingEffects): style indices from StyleNames.Length on.
        private LoadingEffect[] _effects;
        private LoadingEffect _effect;
        private int _styleIndex;
        private readonly EffectContext _ctx = new EffectContext();
        private GUIStyle _effectStyle;
        private float _eggStart = -1f;

        private const float ZNear = 0.045f, ZFar = 1f;
        private const float FieldRadius = 1.6f;
        private const float CruiseSpeed = 1.15f;      // depth units per second
        private const float Exposure = 0.15f;         // seconds of motion captured in a streak
        private const float IgnitionHold = 1.1f, IgnitionRamp = 2.2f;
        private const float BrakeTime = 1.1f, FadeStart = 0.2f, FadeTime = 1.2f;   // drop out: brake and cross-fade together
        private const float ApproachTime = 2.6f, ApproachSpeed = 0.07f;          // while the final scenes load
        private const int VertsPerStreak = 9;         // three rows (tail, shoulder, tip) of left/centre/right
        private const double BrightFraction = 0.18;   // the stars bright enough for a halo (same overall distribution)

        private WarpScreen _owner;
        private LoadingScreen _screen;
        private Phase _phase = Phase.Ignition;
        private float _phaseStart, _start, _lastTime;
        private float _introClock;   // seconds of titles shown: advances by the bounded frame time, not through frozen frames
        private float _introFrameEma = 1f / 60f;   // the intro's usual frame time: a longer frame advances it no more than 1.25x this
        private float _introSmooth;  // smooth frames in a row before the intro starts (the load's first seconds stall)
        private int _introFrames, _introSlow;
        private float _introWorst, _introSpan;
        private bool _introReported;
        private bool _introRolling;
        private float _speed, _warp, _overlay = 1f, _flash, _exitSpeed, _approachWarp;
        private float _drawSpeed, _drawWarp, _starDim = 1f, _sunUp;   // what the current style makes of speed and warp
        private Style _style;
        private float _nextPreview;

        // Vortex: each star's angle and radius around the axis. Nebula: soft clouds (position, size, colour, drift).
        private float[] _angle, _radius;
        private const int CloudCount = 9;
        private readonly float[] _cloudX = new float[CloudCount], _cloudY = new float[CloudCount], _cloudR = new float[CloudCount];
        private readonly float[] _cloudVx = new float[CloudCount], _cloudVy = new float[CloudCount], _cloudPhase = new float[CloudCount];
        private readonly Color32[] _cloudColor = new Color32[CloudCount];
        private bool _exitRequested;

        // Frame pacing: main-thread time left to the loaders each frame, adjusted to hold the display refresh rate.
        private float _targetFrame, _budget, _frameEma, _overheadEma = 0.004f;
        private long _costTicks;
        private int _costFrames;

        // KSP's own loading screen is fully covered: its canvases are not rendered meanwhile.
        private readonly List<Canvas> _hiddenCanvases = new List<Canvas>();

        // Stars
        private int _count, _bright, _haloBase;
        private float[] _x, _y, _z, _mag, _twinkle;
        private bool[] _deep;
        private Color32[] _tint, _tintHead, _tintTail;   // base colour, and fully Doppler-tinted head/tail colours
        private readonly float[] _depthGain = new float[DepthLutSize + 1];
        private const int DepthLutSize = 512;
        private System.Random _rng;

        // Geometry
        private Mesh _mesh, _quad;
        private Vector3[] _vertices;
        private Color32[] _colors;
        private readonly Vector3[] _quadVerts = new Vector3[4];
        private readonly Color32[] _quadColors = new Color32[4];
        private Material _additive, _blend;

        // Text
        private GUIStyle _tipStyle, _labelStyle, _percentStyle, _markStyle;
        private Font _lightFont, _regularFont;
        private readonly GUIContent _content = new GUIContent();
        private string _tip = "", _stage = "", _percentText = "0 %";
        private int _percent = -1;
        private float _progress, _shownProgress, _nextTextRefresh;
        private LoadingSystem _loggedLoader;

        // Animation clock: advanced by the clamped frame time, so a frozen frame pauses the animation instead of making it
        // jump ahead when the game comes back. Slowed down (time scale) just before a remembered freeze.
        private float _anim, _timeScale = 1f, _prevProgress, _progressRate;
        private const float CalmScale = 0.08f, CalmLeadSeconds = 0.6f, FreezeMin = 0.25f, PassedMargin = 0.004f;
        private const int FreezeCapacity = 32;
        private readonly float[] _known = new float[FreezeCapacity];
        private readonly bool[] _knownPassed = new bool[FreezeCapacity];
        private readonly int[] _knownMisses = new int[FreezeCapacity];   // starts in a row without that freeze (dropped after MaxMisses)
        private const int MaxMisses = 2;
        private int _knownCount, _calmedFreezes, _newFreezes;
        private bool _calming;
        private readonly float[] _seenAt = new float[FreezeCapacity];
        private readonly float[] _seenMs = new float[FreezeCapacity];
        private int _seenCount;
        private bool _loadCompleted;
        private string _modpack;
        private Component _tipsComponent;
        private PropertyInfo _tipsTextProperty;

        private float _lastFrameForPace;
        private int _captureIndex;
        private float _nextCapture;

        // Frame trace: every frame time, the long ones with the last log line before them (summary at the end).
        private const int TraceCapacity = 30000, SpikeCapacity = 64;
        private readonly float[] _frameDt = new float[TraceCapacity];
        private readonly float[] _frameAt = new float[TraceCapacity];
        private int _frames;
        private readonly float[] _spikeDt = new float[SpikeCapacity], _spikeAt = new float[SpikeCapacity];
        private readonly string[] _spikeLog = new string[SpikeCapacity];
        private readonly string[] _spikePhase = new string[SpikeCapacity];
        private readonly long[] _spikeGc = new long[SpikeCapacity];
        private long _lastGcNumber;
        private int _spikes;
        private string _lastLog = "";
        private Application.LogCallback _logCallback;
        private bool _traceReported;

        private static readonly Color32 Clear = new Color32(0, 0, 0, 0);

        internal void Init(WarpScreen owner, LoadingScreen screen)
        {
            _owner = owner;
            _screen = screen;
            FindPosterArt(screen);
            _count = owner.StarCount;
            _bright = (int)(_count * BrightFraction);
            _rng = new System.Random(1225);
            _x = new float[_count]; _y = new float[_count]; _z = new float[_count];
            _mag = new float[_count]; _twinkle = new float[_count];
            _deep = new bool[_count];
            _tint = new Color32[_count];
            _tintHead = new Color32[_count];
            _tintTail = new Color32[_count];
            _angle = new float[_count];
            _radius = new float[_count];
            Lang.Ensure();   // the language chosen in KSP's settings (English on a first start)
            _effects = LoadingEffectCatalog.Create();
            SwitchStyle(PickStyle(owner.Style), respawn: false);
            PlanEasterEgg(owner);
            for (int k = 0; k <= DepthLutSize; k++)
            {
                float z = ZNear + (ZFar - ZNear) * k / DepthLutSize;
                _depthGain[k] = Mathf.Pow(Mathf.Clamp(0.10f / z, 0.30f, 1f), 0.6f);
            }
            for (int i = 0; i < _count; i++)
                Spawn(i, (float)(ZNear + (ZFar - ZNear) * _rng.NextDouble()));

            // Core streaks for every star, then halos for the bright class only (indices below _bright).
            _haloBase = _count * VertsPerStreak;
            _vertices = new Vector3[(_count + _bright) * VertsPerStreak];
            _colors = new Color32[_vertices.Length];
            var triangles = new int[(_count + _bright) * 24];
            for (int i = 0; i < _bright; i++)
                AddStreak(triangles, i * 24, _haloBase + i * VertsPerStreak);          // halos first, under the cores
            for (int i = 0; i < _count; i++)
                AddStreak(triangles, (_bright + i) * 24, i * VertsPerStreak);
            _mesh = NewMesh("Hyperdrive.WarpStars", _vertices, _colors, triangles);
            _quad = NewMesh("Hyperdrive.WarpQuad", _quadVerts, _quadColors, new[] { 0, 1, 2, 0, 2, 3 });

            Shader shader = Shader.Find("Hidden/Internal-Colored");
            _additive = MakeMaterial(shader, UnityEngine.Rendering.BlendMode.One, UnityEngine.Rendering.BlendMode.One);
            _blend = MakeMaterial(shader, UnityEngine.Rendering.BlendMode.SrcAlpha, UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);

            FieldInfo tips = AccessTools.Field(typeof(LoadingScreen), "tipsText");
            _tipsComponent = tips != null ? tips.GetValue(screen) as Component : null;
            _tipsTextProperty = _tipsComponent != null ? _tipsComponent.GetType().GetProperty("text", BindingFlags.Public | BindingFlags.Instance) : null;

            _start = _phaseStart = _lastTime = Time.realtimeSinceStartup;
            if (_owner.Intro)
            {
                _phaseStart += RevealStart;   // the animation's ignition starts as the loading screen comes out of the black
                IntroPlaying = true;
            }
            LoadFreezes();
            _nextCapture = _start + 0.5f;
            _logCallback = OnLog;
            Application.logMessageReceived += _logCallback;
            UnityEngine.SceneManagement.SceneManager.sceneLoaded += OnUnitySceneLoaded;
            UnityEngine.SceneManagement.SceneManager.sceneUnloaded += OnUnitySceneUnloaded;
            _lastFrameForPace = _start;
            InitPacing(owner.MaxRefreshRate);
            HideKspLoadingScreen();
        }

        // Rows r0 (tail), r1 (shoulder), r2 (tip), each L,C,R: four quads, eight triangles.
        private static void AddStreak(int[] t, int at, int v)
        {
            for (int row = 0; row < 2; row++)
            {
                int a = v + row * 3, b = a + 3;
                for (int side = 0; side < 2; side++)
                {
                    int k = at + row * 12 + side * 6;
                    t[k] = a + side; t[k + 1] = b + side; t[k + 2] = b + side + 1;
                    t[k + 3] = a + side; t[k + 4] = b + side + 1; t[k + 5] = a + side + 1;
                }
            }
        }

        private static Mesh NewMesh(string name, Vector3[] vertices, Color32[] colors, int[] triangles)
        {
            var mesh = new Mesh { name = name };
            mesh.MarkDynamic();
            mesh.vertices = vertices;
            mesh.colors32 = colors;
            mesh.triangles = triangles;
            mesh.bounds = new Bounds(Vector3.zero, new Vector3(1e6f, 1e6f, 10f));
            return mesh;
        }

        private static Material MakeMaterial(Shader shader, UnityEngine.Rendering.BlendMode src, UnityEngine.Rendering.BlendMode dst)
        {
            var m = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            m.SetInt("_SrcBlend", (int)src);
            m.SetInt("_DstBlend", (int)dst);
            m.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
            m.SetInt("_ZWrite", 0);
            m.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
            return m;
        }

        // Mostly uniform over the disc, a third of the stars packed closer to the axis (the deep field that fills the
        // vanishing point); brightness from a steep power law; colour from a rough distribution of stellar temperatures.
        private void Spawn(int i, float z)
        {
            double u = _rng.NextDouble();
            _deep[i] = _rng.NextDouble() < 0.3;
            double r = _deep[i] ? FieldRadius * 0.30 * u : FieldRadius * Math.Sqrt(0.002 + 0.998 * u);
            if (_style == Style.Vortex && !_deep[i])
                r = FieldRadius * (0.42 + 0.16 * u * u);                      // the tunnel wall, thicker towards the inside
            double a = _rng.NextDouble() * Math.PI * 2;
            _angle[i] = (float)a;
            _radius[i] = (float)r;
            _x[i] = (float)(r * Math.Cos(a));
            _y[i] = (float)(r * Math.Sin(a));
            _z[i] = z;
            // Steep power law, split at the halo threshold (u = 0.818 gives 0.62) so the bright class keeps its share.
            double uMag = i < _bright ? 0.818 + 0.182 * _rng.NextDouble() : 0.818 * _rng.NextDouble();
            _mag[i] = (float)(0.16 + 0.84 * Math.Pow(uMag, 3.0));
            _twinkle[i] = (float)(_rng.NextDouble() * 100.0);
            double c = _rng.NextDouble();
            if (c < 0.60) _tint[i] = new Color32(255, 255, 255, 255);
            else if (c < 0.76) _tint[i] = new Color32(255, 246, 236, 255);
            else if (c < 0.89) _tint[i] = new Color32(220, 232, 255, 255);
            else if (c < 0.96) _tint[i] = new Color32(255, 222, 188, 255);
            else _tint[i] = new Color32(194, 212, 255, 255);
            if (_style == Style.Vortex)
            {
                double v = _rng.NextDouble();
                _tint[i] = v < 0.45 ? new Color32(150, 225, 255, 255) : v < 0.80 ? new Color32(185, 160, 255, 255) : new Color32(240, 246, 255, 255);
            }
            _tintHead[i] = Mix(_tint[i], new Color32(205, 222, 255, 255), 70);
            _tintTail[i] = Mix(_tint[i], _style == Style.Vortex ? new Color32(120, 110, 255, 255) : new Color32(255, 214, 184, 255), 60);
        }

        // ------------------------------------------------------------------ styles

        internal string StyleName => _effect != null ? _effect.Name : StyleNames[(int)_style];

        private int StyleCount => StyleNames.Length + _effects.Length;

        private int PickStyle(string setting)
        {
            // Random or the main menu window's choice, without repeats across starts (LoadingScreenChoice).
            var names = new string[StyleCount];
            for (int i = 0; i < StyleNames.Length; i++)
                names[i] = StyleNames[i];
            for (int i = 0; i < _effects.Length; i++)
                names[StyleNames.Length + i] = _effects[i].Name;
            string pick = LoadingScreenChoice.Pick(names, setting, out string how);
            Log.Info("WarpScreen: loading screen '" + pick + "' (" + how + ")");
            int index = Array.IndexOf(names, pick);
            return index >= 0 ? index : 0;
        }

        // The Nyan Kerbal crosses the screen on about one load in twenty, always on April 1st, or on demand.
        private void PlanEasterEgg(WarpScreen owner)
        {
            var dice = new System.Random(Environment.TickCount ^ 0x5eed);
            DateTime today = DateTime.Now;
            bool due = owner.Style == "nyankerbal" || (owner.EasterEgg && (dice.Next(20) == 0 || (today.Month == 4 && today.Day == 1)));
            _eggStart = due ? 14f + 20f * (float)dice.NextDouble() : -1f;   // seconds after the animation starts
        }

        // How each style uses the shared motion: the phases drive speed and warp, the style scales what is drawn.
        private void StyleScales(out float speedScale, out float warpScale)
        {
            switch (_style)
            {
                case Style.Nebula: speedScale = 0.045f; warpScale = 0.06f; break;
                case Style.Orbit: speedScale = 0.008f; warpScale = 0f; break;
                case Style.Vortex: speedScale = 0.85f; warpScale = 1f; break;
                default: speedScale = 1f; warpScale = 1f; break;
            }
        }

        private float FlashScale => _effect == null && (_style == Style.Hyperspace || _style == Style.Vortex) ? 1f : 0.25f;

        private void SwitchStyle(int index, bool respawn = true)
        {
            _styleIndex = index;
            _effect = index >= StyleNames.Length ? _effects[index - StyleNames.Length] : null;
            _style = _effect == null ? (Style)index : Style.Hyperspace;
            if (_effect != null)
            {
                FillContext(0f);
                _effect.Init(_ctx, _rng);
                return;
            }
            if (respawn)
                for (int i = 0; i < _count; i++)
                    Spawn(i, (float)(ZNear + (ZFar - ZNear) * _rng.NextDouble()));
            InitClouds();
        }

        private void FillContext(float dt)
        {
            _ctx.W = Screen.width;
            _ctx.H = Screen.height;
            _ctx.T = _anim;
            _ctx.Dt = dt;
            _ctx.Energy = _warp;
            _ctx.Progress = _shownProgress;
            _ctx.Overlay = _overlay;
            _ctx.Flash = _flash;
            _ctx.Exiting = _phase == Phase.Exit;
            _ctx.BlendMaterial = _blend;
            _ctx.AdditiveMaterial = _additive;
        }

        private void InitClouds()
        {
            Color32[] palette =
            {
                new Color32(120, 70, 190, 255), new Color32(40, 120, 170, 255), new Color32(190, 70, 120, 255),
                new Color32(60, 80, 200, 255), new Color32(30, 150, 140, 255), new Color32(170, 110, 60, 255)
            };
            for (int c = 0; c < CloudCount; c++)
            {
                _cloudX[c] = (float)_rng.NextDouble() * 1.2f - 0.1f;
                _cloudY[c] = (float)_rng.NextDouble();
                _cloudR[c] = 0.22f + 0.38f * (float)_rng.NextDouble();
                double a = _rng.NextDouble() * Math.PI * 2;
                float v = 0.004f + 0.006f * (float)_rng.NextDouble();
                _cloudVx[c] = v * (float)Math.Cos(a);
                _cloudVy[c] = v * (float)Math.Sin(a) * 0.5f;
                _cloudPhase[c] = (float)_rng.NextDouble() * 10f;
                _cloudColor[c] = palette[c % palette.Length];
            }
        }

        // ------------------------------------------------------------------ phases

        internal void RequestExit() => _exitRequested = true;

        // The final scene loads have started (they stutter): ease out of warp so the stutter falls on slow stars.
        internal void BeginApproach()
        {
            if (_phase == Phase.Ignition || _phase == Phase.Cruise)
            {
                SetPhase(Phase.Approach);
                HoldGc();
            }
        }

        // While the planetary system and the menu are built, the game allocates in bursts and the collector stops the
        // world several times in a row (measured: 6 full collections in one 2.1 s frame). Automatic collections are
        // suspended for that stretch (explicit GC.Collect still runs), with a cap on heap growth and on duration;
        // they resume once the menu is on screen, where the incremental collector catches up in small slices.
        private bool _gcHeld;
        private float _gcHeldSince;
        private long _heapAtHold;
        private const float MaxGcHoldSeconds = 20f;
        private const long MaxGcHoldGrowth = 1536L * 1024 * 1024;

        private void HoldGc()
        {
            if (_gcHeld || !_owner.HoldGcAtEnd || UnityEngine.Scripting.GarbageCollector.GCMode != UnityEngine.Scripting.GarbageCollector.Mode.Enabled)
                return;
            _heapAtHold = UnityEngine.Profiling.Profiler.GetMonoHeapSizeLong();
            _gcHeldSince = Time.realtimeSinceStartup;
            UnityEngine.Scripting.GarbageCollector.GCMode = UnityEngine.Scripting.GarbageCollector.Mode.Disabled;
            _gcHeld = true;
        }

        private void ReleaseGc(string why)
        {
            if (!_gcHeld)
                return;
            _gcHeld = false;
            UnityEngine.Scripting.GarbageCollector.GCMode = UnityEngine.Scripting.GarbageCollector.Mode.Enabled;
            long grown = UnityEngine.Profiling.Profiler.GetMonoHeapSizeLong() - _heapAtHold;
            Log.Info("WarpScreen: automatic GC resumed (" + why + ") after " + (Time.realtimeSinceStartup - _gcHeldSince).ToString("F1") + " s, heap grew "
                     + (grown / (1024 * 1024)) + " MB");
        }

        private void CheckGcHold()
        {
            if (!_gcHeld)
                return;
            if (Time.realtimeSinceStartup - _gcHeldSince > MaxGcHoldSeconds)
                ReleaseGc("time limit");
            else if (UnityEngine.Profiling.Profiler.GetMonoHeapSizeLong() - _heapAtHold > MaxGcHoldGrowth)
                ReleaseGc("heap growth limit");
        }

        private void SetPhase(Phase phase)
        {
            _phase = phase;
            _phaseStart = Time.realtimeSinceStartup;
            if (phase == Phase.Approach)
            {
                _exitSpeed = _speed;
                _approachWarp = _warp;
                RefreshText();
            }
            if (phase == Phase.Exit)
            {
                _flash = 0.6f * FlashScale;
                _exitSpeed = _speed;
                RestoreCanvases();
                RefreshText();
            }
            if (_owner.CaptureFrames)
                _nextCapture = _phaseStart + 0.2f;
        }

        private void Update()
        {
            long costStart = System.Diagnostics.Stopwatch.GetTimestamp();
            try { Step(); }
            finally { _costTicks += System.Diagnostics.Stopwatch.GetTimestamp() - costStart; }
        }

        private void Step()
        {
            float now = Time.realtimeSinceStartup;
            float rawDt = now - _lastTime;
            Trace(now, rawDt);
            float dt = Mathf.Clamp(rawDt, 0f, 0.1f);   // a long frozen frame must not teleport the field
            _lastTime = now;
            UpdateCalm(dt);
            float adt = dt * _timeScale;   // animation time: slowed before a remembered freeze
            _anim += adt;
            if (_owner.Intro && _introClock < IntroHold + 1f)
            {
                // The cards pause during a frozen frame, as the animation does, instead of playing unseen; a key or a
                // click skips to the reveal; ignition starts as the loading screen comes out of the black.
                // The load's first seconds can stall frame after frame (PC, big modpack: several 0.5-2 s frames, a few
                // frames a second in between): the intro waits on black, as the game would show, until frames flow
                // (half a second of frames under 70 ms), so it plays whole and smooth; 20 s at most. It also waits for the
                // freezes remembered from the last start that would fall while it plays (measured, KSP DEV: two freezes of
                // 0.3-1 s in the load's first percent, then none for 30 s): up to 35 s.
                if (!_introRolling)
                {
                    _introSmooth = rawDt < 0.07f ? _introSmooth + rawDt : 0f;
                    bool flowing = _introSmooth >= 0.5f || now - _start > 20f;
                    if (flowing && (!FreezeDuringIntro() || now - _start > 35f))
                    {
                        _introRolling = true;
                        Log.Info("WarpScreen: intro starts " + (now - _start).ToString("F1") + " s into the load at "
                                 + (_progress * 100f).ToString("F1") + " % (frames flowing" + (_knownCount > 0 ? ", past the remembered freezes)" : ")"));
                    }
                }
                else
                {
                    // Loading frames come unevenly while the titles play (KSPCF's loader hands back a frame every 1/30 s at
                    // best, some frames take far longer): advancing by each frame's own length made the slow moves (the
                    // game's logo rising) jump. Each frame advances by the usual frame time at most: the moves stay even, a
                    // long frame is a short pause instead of a jump.
                    if (rawDt < 0.2f)
                        _introFrameEma += (rawDt - _introFrameEma) * 0.08f;
                    _introClock += Mathf.Min(dt, Mathf.Max(_introFrameEma, 1f / 120f) * 1.25f);
                    if (_introClock <= IntroHold)
                    {
                        _introFrames++;
                        _introSpan += rawDt;
                        if (rawDt > _introWorst) _introWorst = rawDt;
                        if (rawDt > 0.05f) _introSlow++;
                    }
                    else if (!_introReported)
                    {
                        _introReported = true;
                        Log.Info("WarpScreen: intro played over " + _introFrames + " frames in " + _introSpan.ToString("F1") + " s (avg "
                                 + (_introFrames > 0 ? _introSpan / _introFrames * 1000f : 0f).ToString("F1") + " ms, worst "
                                 + (_introWorst * 1000f).ToString("F0") + " ms, " + _introSlow + " frames over 50 ms)");
                    }
                }
                // A key only once the titles show: a click on the black before them (focusing the game's window) skipped
                // the whole intro.
                if (_introClock < RevealStart && ((_introRolling && _introClock > 0.5f && Input.anyKeyDown) || Loading.PngCachePrompt.Shown))
                {
                    _introClock = RevealStart;   // also when the PNG cache question comes up: the titles give way to it
                    _introRolling = true;
                }
                if (_phase == Phase.Ignition && _introClock < RevealStart)
                    _phaseStart = now + (RevealStart - _introClock);
            }
            float inPhase = now - _phaseStart;

            Pace(now - _lastFrameForPace);
            _lastFrameForPace = now;
            CheckGcHold();
            if (_phase != Phase.Exit && _exitRequested)
            {
                SetPhase(Phase.Exit);
                inPhase = 0f;
            }

            switch (_phase)
            {
                case Phase.Ignition:
                {
                    float k = Mathf.Clamp01((inPhase - IgnitionHold) / IgnitionRamp);
                    k = k * k * k * (k * (k * 6f - 15f) + 10f);       // smootherstep: gentle start, firm end
                    _speed = Mathf.Lerp(0.01f, CruiseSpeed, k);
                    _warp = k;
                    if (inPhase > IgnitionHold + IgnitionRamp)
                    {
                        SetPhase(Phase.Cruise);
                        _flash = 0.45f * FlashScale;                    // the jump itself
                    }
                    break;
                }
                case Phase.Cruise:
                {
                    float breathe = 1f + 0.07f * Mathf.Sin(inPhase * 0.83f) + 0.04f * Mathf.Sin(inPhase * 2.1f + 1.3f);
                    _speed = CruiseSpeed * breathe * (1f + 0.12f * _progress);
                    _warp = 1f;
                    break;
                }
                case Phase.Approach:
                {
                    float a = Mathf.Clamp01(inPhase / ApproachTime);
                    float e = 1f - (1f - a) * (1f - a);                // ease-out quadratic
                    _speed = Mathf.Lerp(_exitSpeed, ApproachSpeed, e);
                    _warp = Mathf.Lerp(_approachWarp, 0.12f, e);
                    break;
                }
                default:
                {
                    // Drop out of hyperspace straight into the menu: brake (ease-out) while cross-fading.
                    float b = Mathf.Clamp01(inPhase / BrakeTime);
                    float e = 1f - (1f - b) * (1f - b) * (1f - b);
                    _speed = Mathf.Lerp(_exitSpeed, 0.004f, e);
                    _warp = Mathf.Min(_warp, 1f - e);
                    float k = Mathf.Clamp01((inPhase - FadeStart) / FadeTime);
                    _overlay = 1f - k * k * (3f - 2f * k);
                    if (k >= 1f)
                    {
                        ReleaseGc("menu on screen");
                        _loadCompleted = true;
                        ReportTrace();
                        _owner.Finished();
                        return;
                    }
                    break;
                }
            }
            _flash = Mathf.Max(0f, _flash - dt * 2.2f);

            if (_owner.PreviewStyles && _phase == Phase.Cruise && now >= _nextPreview)
            {
                if (_nextPreview > 0f)
                    SwitchStyle((_styleIndex + 1) % StyleCount);
                _nextPreview = now + _owner.PreviewSeconds;
            }
            StyleScales(out float speedScale, out float warpScale);
            _drawSpeed = _speed * speedScale;
            _drawWarp = _warp * warpScale * Mathf.Lerp(0.3f, 1f, _timeScale);   // calm: streaks settle into a still sky
            // Orbit: the sun climbs over the limb with the loading progress and outshines the faint stars.
            _sunUp = _style == Style.Orbit ? (_phase >= Phase.Approach ? Mathf.MoveTowards(_sunUp, 1f, dt * 0.5f) : Mathf.SmoothStep(0f, 0.85f, _shownProgress)) : 0f;
            _starDim = 1f - 0.55f * _sunUp;

            FillContext(adt);
            if (_effect != null)
            {
                _effect.Step(_ctx);
                goto Text;
            }
            float spin = _style == Style.Vortex ? (0.25f + 0.75f * _warp) * adt : 0f;
            for (int i = 0; i < _count; i++)
            {
                float z = _z[i] - _drawSpeed * adt;
                if (z < ZNear)
                    Spawn(i, ZFar - (ZNear - z));
                else
                    _z[i] = z;
                if (spin != 0f)
                {
                    // Nearer stars turn faster: the tunnel twists towards the viewer.
                    float a = _angle[i] + spin * (0.6f + 0.25f / _z[i]);
                    _angle[i] = a;
                    _x[i] = _radius[i] * Mathf.Cos(a);
                    _y[i] = _radius[i] * Mathf.Sin(a);
                }
            }

            Text:
            if (now >= _nextTextRefresh)
            {
                _nextTextRefresh = now + 0.25f;
                RefreshText();
            }
            _shownProgress = Mathf.MoveTowards(_shownProgress, _progress, dt * 0.6f);
        }

        // Same overall fraction as KSP's own progress bar (every frame, nothing allocated); returns the loader at work.
        private LoadingSystem ComputeProgress(List<LoadingSystem> loaders)
        {
            float total = 0f, done = 0f;
            LoadingSystem current = null;
            for (int i = 0; i < loaders.Count; i++)
            {
                LoadingSystem l = loaders[i];
                if (l == null)
                    continue;
                float w = l.LoadWeight();
                total += w;
                if (l.IsReady())
                    done += w;
                else if (current == null)
                {
                    current = l;
                    done += w * Mathf.Clamp01(l.ProgressFraction());
                }
            }
            _progress = total > 0f ? Mathf.Clamp01(done / total) : 0f;
            return current;
        }

        // Before a remembered freeze (progress about CalmLeadSeconds ahead at the current pace) the animation time eases
        // down to a near stillness, so the frozen frame reads as a pause; it eases back once the progress is past it.
        private void UpdateCalm(float dt)
        {
            List<LoadingSystem> loaders = _screen != null ? _screen.loaders : null;
            if (loaders != null && loaders.Count > 0 && _phase <= Phase.Cruise)
            {
                try { ComputeProgress(loaders); }
                catch (Exception) { }   // a loader in a bad state: the text refresh reports it
            }
            if (dt > 0f && _progress > _prevProgress)
                _progressRate = Mathf.Lerp(_progressRate, Mathf.Min(1f, (_progress - _prevProgress) / dt), Mathf.Clamp01(dt * 2f));
            _prevProgress = _progress;

            bool calm = false;
            if (_owner.CoverFreezes && _phase <= Phase.Cruise)
            {
                float lead = Mathf.Clamp(_progressRate * CalmLeadSeconds, 0.0015f, 0.04f);
                for (int i = 0; i < _knownCount; i++)
                {
                    if (_knownPassed[i])
                        continue;
                    if (_progress > _known[i] + PassedMargin)
                        _knownPassed[i] = true;
                    else if (_progress >= _known[i] - lead)
                        calm = true;
                }
            }
            if (calm && !_calming)
                _calmedFreezes++;
            _calming = calm;
            float target = calm ? CalmScale : 1f;
            _timeScale = Mathf.MoveTowards(_timeScale, target, dt * (calm ? 5f : 1.6f));
        }

        // A remembered freeze still ahead within the progress the titles would cover at the current pace (1-5 % of the load).
        private bool FreezeDuringIntro()
        {
            float span = Mathf.Clamp(_progressRate * IntroHold, 0.01f, 0.05f);
            for (int i = 0; i < _knownCount; i++)
                if (_progress <= _known[i] + PassedMargin && _known[i] <= _progress + span)
                    return true;
            return false;
        }

        private static string FreezeFile => System.IO.Path.Combine(System.IO.Path.Combine(HyperdriveRuntime.ModDirectory, "PluginData"), "LoadingFreezes.txt");

        private void LoadFreezes()
        {
            _knownCount = 0;
            if (!_owner.CoverFreezes)
                return;
            try
            {
                _modpack = Modpack.Fingerprint;   // this install's mods and machine: another set is learned again
                if (!System.IO.File.Exists(FreezeFile))
                    return;
                string[] lines = System.IO.File.ReadAllLines(FreezeFile);
                string stored = null;
                foreach (string line in lines)
                    if (line.StartsWith("modpack ", StringComparison.Ordinal))
                        stored = line.Substring(8).Trim();
                if (stored != _modpack)
                {
                    Log.Info("WarpScreen: mods changed since the freezes were remembered (" + (stored ?? "none") + " -> " + _modpack + "): learned again during this load");
                    return;
                }
                foreach (string line in lines)
                {
                    if (_knownCount >= FreezeCapacity || line.Length == 0 || line[0] == '/' || line[0] == 'm')
                        continue;
                    string[] parts = line.Split(' ');
                    if (float.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float at) && at > 0f && at < 1f)
                    {
                        int misses = 0;
                        if (parts.Length > 2)
                            int.TryParse(parts[2], out misses);
                        _knownMisses[_knownCount] = misses;
                        _known[_knownCount++] = at;
                    }
                }
            }
            catch (Exception e)
            {
                Log.Debug("WarpScreen: freeze memory not read (" + e.Message + ")");
                _knownCount = 0;
            }
        }

        // Only after a load that reached the menu: the freezes of this start replace the remembered ones.
        private void SaveFreezes()
        {
            if (!_owner.CoverFreezes || !_loadCompleted)
                return;
            try
            {
                var sb = new System.Text.StringBuilder(512);
                sb.Append("// Hyperdrive: where the last first load of this install froze (loading progress 0-1, then ms), for these mods.\n")
                  .Append("// Rewritten at each start, ignored when the mods change; safe to delete.\n")
                  .Append("modpack ").Append(_modpack ?? Modpack.Fingerprint).Append('\n');
                for (int i = 0; i < _seenCount; i++)
                    sb.Append(_seenAt[i].ToString("F4", System.Globalization.CultureInfo.InvariantCulture)).Append(' ')
                      .Append(_seenMs[i].ToString("F0", System.Globalization.CultureInfo.InvariantCulture)).Append(" 0\n");
                // A remembered freeze that did not come back this time (a GC does not fall at every start) is kept a little longer.
                for (int k = 0; k < _knownCount; k++)
                {
                    bool seen = false;
                    for (int i = 0; i < _seenCount; i++)
                        if (Mathf.Abs(_seenAt[i] - _known[k]) <= PassedMargin)
                            seen = true;
                    if (!seen && _knownMisses[k] < MaxMisses)
                        sb.Append(_known[k].ToString("F4", System.Globalization.CultureInfo.InvariantCulture)).Append(" 0 ").Append(_knownMisses[k] + 1).Append('\n');
                }
                System.IO.File.WriteAllText(FreezeFile, sb.ToString());
                Log.Info("WarpScreen: " + _knownCount + " remembered freezes, calmed before " + _calmedFreezes + "; " + _seenCount + " freezes of "
                         + (FreezeMin * 1000f).ToString("F0") + " ms or more kept for the next start (" + _newFreezes + " not remembered before)");
            }
            catch (Exception e)
            {
                Log.Debug("WarpScreen: freeze memory not written (" + e.Message + ")");
            }
        }

        // Called from Trace with the frame that just ended; _progress is still the value from before it.
        private void NoteFreeze(float dt)
        {
            if (dt < FreezeMin || _phase > Phase.Cruise || _progress <= 0f || _progress >= 1f)
                return;
            bool known = false;
            for (int i = 0; i < _knownCount; i++)
                if (Mathf.Abs(_known[i] - _progress) <= PassedMargin)
                    known = true;
            if (!known)
                _newFreezes++;
            Log.Info("WarpScreen: frozen frame of " + (dt * 1000f).ToString("F0") + " ms at " + (_progress * 100f).ToString("F1") + " % of the load, animation at "
                     + (_timeScale * 100f).ToString("F0") + " % speed" + (known ? " (remembered)" : ""));
            for (int i = 0; i < _seenCount; i++)
            {
                if (Mathf.Abs(_seenAt[i] - _progress) <= 0.002f)
                {
                    _seenMs[i] += dt * 1000f;   // several freezes at one place
                    return;
                }
            }
            if (_seenCount < FreezeCapacity)
            {
                _seenAt[_seenCount] = _progress;
                _seenMs[_seenCount++] = dt * 1000f;
            }
        }

        // Same overall fraction as KSP's own progress bar; text refreshed four times a second (allocates then only).
        private void RefreshText()
        {
            List<LoadingSystem> loaders = _screen != null ? _screen.loaders : null;
            if (loaders != null && loaders.Count > 0 && _phase <= Phase.Cruise)
            {
                LoadingSystem current = ComputeProgress(loaders);
                _stage = StageName(current);
                if (!ReferenceEquals(current, _loggedLoader))
                {
                    // One line per loading system: where the load spends its time (LoadReport and benches read it).
                    _loggedLoader = current;
                    Log.Info("WarpScreen: loading system " + (current != null ? current.GetType().Name : "none") + " from "
                             + (Time.realtimeSinceStartup - _start).ToString("F2") + " s (" + (_progress * 100f).ToString("F0") + " %)");
                }
            }
            else if (_phase == Phase.Exit || _phase == Phase.Approach)
            {
                _progress = 1f;
                _stage = ExitText();
            }
            else if (loaders == null || loaders.Count == 0 || _screen == null)
            {
                _progress = 1f;   // loading systems done, scenes on their way
                _stage = Lang.T("#HD_Stage_Menu");
            }
            int percent = Mathf.RoundToInt(_progress * 100f);
            if (percent != _percent)
            {
                _percent = percent;
                _percentText = percent + " %";
            }
            if (_tipsComponent != null && _tipsTextProperty != null)
            {
                try
                {
                    // A tip KSP has not translated yet (its localization loads later) shows as its raw key: left out.
                    string tip = _tipsTextProperty.GetValue(_tipsComponent, null) as string;
                    if (tip != null && !tip.StartsWith("#autoLOC", StringComparison.Ordinal))
                        _tip = tip;
                }
                catch (Exception) { _tipsTextProperty = null; }
            }
        }

        private string ExitText()
        {
            if (_effect != null)
                return _effect.ExitText;
            switch (_style)
            {
                case Style.Vortex: return Lang.T("#HD_Exit_vortex");
                case Style.Nebula: return Lang.T("#HD_Exit_nebula");
                case Style.Orbit: return Lang.T("#HD_Exit_orbit");
                default: return Lang.T("#HD_Exit_hyperspace");
            }
        }

        private string StageName(LoadingSystem l)
        {
            if (l == null)
                return Lang.T("#HD_Stage_Prepare");
            string type = l.GetType().Name;
            if (type == "GameDatabase") return Lang.T("#HD_Stage_GameData");
            if (type == "PartLoader") return Lang.T("#HD_Stage_Parts");
            if (type == "ExpansionsLoader") return Lang.T("#HD_Stage_Expansions");
            string title = l.ProgressTitle() ?? "";
            int slash = title.LastIndexOf('/');
            if (slash >= 0 && slash < title.Length - 1)
                title = title.Substring(slash + 1);
            return title.Length > 48 ? title.Substring(0, 48) : title;
        }

        // ------------------------------------------------------------------ pacing

        private void InitPacing(int maxRefresh)
        {
            int refresh = Screen.currentResolution.refreshRate;
            if (refresh < 30)
                refresh = 60;
            refresh = Mathf.Clamp(refresh, Mathf.Min(60, maxRefresh), maxRefresh);
            _targetFrame = 1f / refresh;
            _budget = Mathf.Max(0.004f, _targetFrame - 0.005f);
            _frameEma = _targetFrame;
            Loading.FastLoader.FrameBudgetOverride = _budget;
            // No frame-rate cap here: KSP's loaders spend a lot of time waiting "one frame" (web requests, coroutines),
            // and under a cap every such wait costs a full refresh period (measured: +12 s). The loader budget keeps the
            // frames under the refresh period instead; AsyncExpansions caps the rate only while bundles open.
            Log.Info("WarpScreen: pacing for " + refresh + " Hz (initial loader budget " + (_budget * 1000f).ToString("F1") + " ms per frame)");
        }

        // Loader budget = target frame time minus what the rest of the frame costs (rendering, the animation, other
        // loading code). The rest is measured as frame time minus the loading work FastLoader did in that frame, so a
        // frame made long by one indivisible step (a heavy part, a big texture) does not shrink the budget.
        private void Pace(float frame)
        {
            if (_phase >= Phase.Approach || frame <= 0f)
                return;
            // While something else caps the frame rate (AsyncExpansions opening bundles), frames cannot get shorter than
            // the cap: nothing to learn from them.
            int cap = Application.targetFrameRate;
            if (cap > 0 && 1f / cap > _targetFrame * 0.9f)
                return;
            float rest = frame - Loading.FastLoader.WorkSecondsInFrame(Time.frameCount - 1);
            if (rest > 0f && rest < _targetFrame * 2f)
                _overheadEma += (rest - _overheadEma) * 0.1f;
            _budget = Mathf.Clamp(_targetFrame - _overheadEma - 0.0015f, 0.003f, Mathf.Max(0.004f, _targetFrame - 0.002f));
            Loading.FastLoader.FrameBudgetOverride = _budget;
        }

        private void HideKspLoadingScreen()
        {
            foreach (Canvas canvas in UnityEngine.Object.FindObjectsOfType<Canvas>())
            {
                if (canvas != null && canvas.enabled && canvas.isRootCanvas && canvas.gameObject.scene.name != "DontDestroyOnLoad")
                {
                    canvas.enabled = false;
                    _hiddenCanvases.Add(canvas);
                }
            }
        }

        private void RestoreCanvases()
        {
            for (int i = 0; i < _hiddenCanvases.Count; i++)
                if (_hiddenCanvases[i] != null)
                    _hiddenCanvases[i].enabled = true;
            _hiddenCanvases.Clear();
        }

        // ------------------------------------------------------------------ frame trace

        private void OnLog(string message, string stackTrace, LogType type) => _lastLog = message;

        private void OnUnitySceneLoaded(UnityEngine.SceneManagement.Scene scene, UnityEngine.SceneManagement.LoadSceneMode mode) =>
            Log.Info("WarpScreen: Unity scene loaded '" + scene.name + "' (" + mode + ") at " + (Time.realtimeSinceStartup - _start).ToString("F2") + " s");

        private void OnUnitySceneUnloaded(UnityEngine.SceneManagement.Scene scene) =>
            Log.Info("WarpScreen: Unity scene unloaded '" + scene.name + "' at " + (Time.realtimeSinceStartup - _start).ToString("F2") + " s");

        private void Trace(float now, float dt)
        {
            long gc = Memory.BoehmGC.CollectionNumber;
            long gcDelta = _lastGcNumber > 0 && gc >= _lastGcNumber ? gc - _lastGcNumber : 0;
            _lastGcNumber = gc;
            if (_frames < TraceCapacity)
            {
                _frameDt[_frames] = dt;
                _frameAt[_frames] = now - _start;
                _frames++;
            }
            NoteFreeze(dt);
            if (dt > 0.034f)
            {
                int slot = _spikes;
                if (slot >= SpikeCapacity)
                {
                    // keep the longest ones
                    slot = 0;
                    for (int i = 1; i < SpikeCapacity; i++)
                        if (_spikeDt[i] < _spikeDt[slot]) slot = i;
                    if (_spikeDt[slot] >= dt)
                        return;
                }
                else
                    _spikes++;
                _spikeDt[slot] = dt;
                _spikeAt[slot] = now - _start;
                _spikeLog[slot] = _lastLog;
                _spikePhase[slot] = _phase.ToString();
                _spikeGc[slot] = gcDelta;
            }
        }

        private void OnDestroy()
        {
            ReleaseGc("animation closed");
            IntroPlaying = false;
            UnityEngine.SceneManagement.SceneManager.sceneLoaded -= OnUnitySceneLoaded;
            UnityEngine.SceneManagement.SceneManager.sceneUnloaded -= OnUnitySceneUnloaded;
            RestoreCanvases();
            Loading.FastLoader.FrameBudgetOverride = -1f;
            if (_logCallback != null)
                Application.logMessageReceived -= _logCallback;
            ReportTrace();
        }

        private void ReportTrace()
        {
            if (_traceReported || _frames < 2)
                return;
            _traceReported = true;
            SaveFreezes();
            int over17 = 0, over20 = 0, over34 = 0, over100 = 0;
            double total = 0;
            for (int i = 1; i < _frames; i++)
            {
                float d = _frameDt[i];
                total += d;
                if (d > 0.0172f) over17++;
                if (d > 0.020f) over20++;
                if (d > 0.034f) over34++;
                if (d > 0.100f) over100++;
            }
            int n = _frames - 1;
            var sb = new System.Text.StringBuilder(4096);
            sb.Append(Log.Prefix).Append("WarpScreen frame trace: ").Append(n).Append(" frames in ").Append(total.ToString("F1"))
              .Append(" s (").Append((n / total).ToString("F0")).Append(" fps average, started ").Append(_start.ToString("F1")).Append(" s after launch)")
              .Append(" | animation cost ").Append((_costFrames > 0 ? _costTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency / _costFrames : 0).ToString("F2")).Append(" ms/frame, loader budget ").Append((_budget * 1000f).ToString("F1")).Append(" ms")
              .Append(" | over 17 ms: ").Append(over17).Append(" | over 20 ms: ").Append(over20).Append(" | over 34 ms: ").Append(over34).Append(" | over 100 ms: ").Append(over100).Append('\n');
            sb.Append("  per 2 s window (fps / longest frame ms):");
            int w = 0, wFrames = 0; float wMax = 0f;
            for (int i = 1; i < _frames; i++)
            {
                int window = (int)(_frameAt[i] / 2f);
                if (window != w)
                {
                    sb.Append(' ').Append((w * 2).ToString()).Append("s:").Append((wFrames / 2f).ToString("F0")).Append('/').Append((wMax * 1000f).ToString("F0"));
                    w = window; wFrames = 0; wMax = 0f;
                }
                wFrames++;
                if (_frameDt[i] > wMax) wMax = _frameDt[i];
            }
            sb.Append('\n').Append("  longest frames (s since start, ms, phase, last log line):\n");
            var order = new List<int>();
            for (int i = 0; i < _spikes; i++) order.Add(i);
            order.Sort((a, b) => _spikeDt[b].CompareTo(_spikeDt[a]));
            for (int k = 0; k < order.Count && k < 30; k++)
            {
                int i = order[k];
                string line = _spikeLog[i] ?? "";
                if (line.Length > 110) line = line.Substring(0, 110);
                sb.Append("    ").Append(_spikeAt[i].ToString("F1")).Append(" s  ").Append((_spikeDt[i] * 1000f).ToString("F0")).Append(" ms  ")
                  .Append(_spikePhase[i]).Append(_spikeGc[i] > 0 ? "  GC x" + _spikeGc[i] : "").Append("  ").Append(line).Append('\n');
            }
            UnityEngine.Debug.Log(sb.ToString());
        }

        // ------------------------------------------------------------------ drawing

        private void OnGUI()
        {
            GUI.depth = -10000;
            if (Event.current.type != EventType.Repaint)
                return;
            long costStart = System.Diagnostics.Stopwatch.GetTimestamp();
            _costFrames++;
            try
            {
                Draw();
                if (_owner.Intro)
                    DrawIntro(Screen.width, Screen.height);
            }
            finally { _costTicks += System.Diagnostics.Stopwatch.GetTimestamp() - costStart; }
        }

        // ------------------------------------------------------------------ title sequence

        // A game's opening titles, in the mod's look (deep-space black, cold light): letterbox bars close in, a thin
        // anamorphic streak of light opens from the centre, the game's name assembles letter by letter while its tracking
        // slowly tightens, a gleam crosses it (the streak flares with it), the version follows; the titles drift up and
        // fade, and the bars open on the loading screen rising out of the black. Glyphs are GL quads (IMGUI lays text out on
        // whole pixels and made the slow push-in tremble). The mod's name is not shown. Any key or click skips to the
        // reveal. Times are on the intro clock, which frozen frames pause.
        private const float CardStart = 0.6f, StreakStart = 0.9f, TitleStart = 1.3f, TitleIn = 3.2f, LetterFade = 1.1f, LetterStagger = 0.055f;
        private const float SweepStart = 4.4f, SweepTime = 1.4f, CardOutStart = 9.4f, CardOut = 1.6f;
        private const float RevealStart = CardOutStart + CardOut, RevealTime = 2.2f, IntroHold = RevealStart + RevealTime;
        private const string GameTitle = "KERBAL SPACE PROGRAM";
        private static readonly Color TitleSilver = new Color(0.80f, 0.84f, 0.90f), StreakBlue = new Color(0.55f, 0.72f, 1f);

        // True while the card is up: the load problems notice waits for the reveal.
        internal static bool IntroPlaying;

        private static float Ramp(float t, float start, float fadeIn, float end, float fadeOut)
        {
            if (t <= start || t >= end + fadeOut)
                return 0f;
            float a = t < start + fadeIn ? (t - start) / fadeIn : t > end ? 1f - (t - end) / fadeOut : 1f;
            return a * a * (3f - 2f * a);
        }

        private static float Smooth(float x)
        {
            x = Mathf.Clamp01(x);
            return x * x * (3f - 2f * x);
        }

        private static float EaseOut(float x)
        {
            x = Mathf.Clamp01(x);
            return 1f - (1f - x) * (1f - x) * (1f - x);
        }

        // 0 while the card plays, 1 once the loading screen is out of the black (the loading text fades in with it).
        private float IntroDone()
        {
            if (!_owner.Intro)
                return 1f;
            return Mathf.Clamp01((_introClock - RevealStart - 0.6f) / 1.2f);
        }

        // The game's own launch art, as its loading screens hold it: the KSP logo (second screen of the stock sequence,
        // "logo1") and a key art. Missing (another mod replaced the screens): the titles are drawn as text instead.
        private Texture2D _posterLogo, _posterArt;
        // Cut-out copies made once on the intro's first frame: the art's near-black background becomes transparent (no
        // more lighter box around the logo), mipmapped and trilinear so the slow moves stay smooth when scaled.
        private Texture2D _cutLogo, _cutArt;
        private bool _cutDone;
        // The game's logo screen ("legal-loading": the KSP logo above the legal notice): only its top half (the logo) is
        // shown, large. Measured on the 1920x1080 texture: logo within x 0.19-0.86, y 0.05-0.46 from the top.
        private static readonly Rect PosterCrop = new Rect(0.14f, 0.50f, 0.76f, 0.50f);   // UV, from the bottom

        private void FindPosterArt(LoadingScreen screen)
        {
            try
            {
                var states = AccessTools.Field(typeof(LoadingScreen), "screens")?.GetValue(screen) as System.Collections.Generic.List<LoadingScreen.LoadingScreenState>;
                if (states == null)
                    return;
                foreach (LoadingScreen.LoadingScreenState state in states)
                {
                    if (state?.screens == null)
                        continue;
                    foreach (UnityEngine.Object o in state.screens)
                    {
                        if (!(o is Texture2D tex) || tex == null)
                            continue;
                        if (_owner.ExportArt)
                            _exportList.Add(tex);
                        if (_posterLogo == null && tex.name == "logo1")
                            _posterLogo = tex;
                        if (_posterArt == null && tex.name == "legal-loading")
                            _posterArt = tex;
                    }
                }
            }
            catch (Exception e)
            {
                Log.Info("WarpScreen: the game's launch art was not found (" + e.Message + "), titles drawn as text");
                _posterLogo = _posterArt = null;
            }
            try { if (_exportList.Count > 0) ExportSheet(_exportList); }
            catch (Exception e) { Log.Info("WarpScreen: art sheet failed (" + e.Message + ")"); }
        }

        // Developer (exportArt = true): one contact sheet of every loading screen texture (6 per row, in log order) in
        // PluginData/art_sheet.png, to pick the art.
        private readonly System.Collections.Generic.List<Texture2D> _exportList = new System.Collections.Generic.List<Texture2D>();

        private static void ExportSheet(System.Collections.Generic.List<Texture2D> list)
        {
            const int cw = 320, ch = 180, cols = 6;
            int rows = (list.Count + cols - 1) / cols;
            RenderTexture rt = RenderTexture.GetTemporary(cw * cols, ch * rows, 0, RenderTextureFormat.ARGB32);
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = rt;
            GL.Clear(true, true, Color.black);
            GL.PushMatrix();
            GL.LoadPixelMatrix(0, cw * cols, ch * rows, 0);
            var names = new System.Text.StringBuilder("WarpScreen: art sheet:");
            for (int i = 0; i < list.Count; i++)
            {
                Graphics.DrawTexture(new Rect((i % cols) * cw, (i / cols) * ch, cw - 4, ch - 4), list[i]);
                names.Append(' ').Append(i).Append('=').Append(list[i].name);
            }
            GL.PopMatrix();
            var copy = new Texture2D(cw * cols, ch * rows, TextureFormat.RGB24, false);
            copy.ReadPixels(new Rect(0, 0, cw * cols, ch * rows), 0, 0);
            copy.Apply();
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(rt);
            string dir = System.IO.Path.Combine(HyperdriveRuntime.ModDirectory, "PluginData");
            System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, "art_sheet.png"), ImageConversion.EncodeToPNG(copy));
            UnityEngine.Object.Destroy(copy);
            Log.Info(names.ToString());
        }

        // Poster opening, on the intro clock, as slow camera moves (nothing bounces): the studio's logo out of the black,
        // backlit; then the game's logo rising into place among drifting motes, a light crossing it and the anamorphic
        // streak beneath, a slow push-in and a dive forward; the letterbox bars open on the loading screen.
        private const float LogoIn = 0.9f, LogoInTime = 1.8f, LogoOut = 3.3f, LogoOutTime = 1.2f;
        private const float ArtIn = 4.2f, ArtInTime = 1.4f, PosterTitle = 5.2f, PosterSweep = 6.9f;

        private void DrawPoster(float w, float h, float t, float black, float bars)
        {
            Color old = GUI.color;
            float outK = Smooth((t - CardOutStart) / CardOut);
            // The studio's logo, as the game itself opens: it comes out of the black, lit from behind by a soft halo that
            // swells and dies away, on a very slow dolly-in; it goes back into the black as the light fades.
            float studio = _posterLogo != null ? Ramp(t, LogoIn, LogoInTime, LogoOut, LogoOutTime) : 0f;
            float studioLife = Mathf.Clamp01((t - LogoIn) / (LogoOut + LogoOutTime - LogoIn));
            float studioSize = h * 0.60f * (0.965f + 0.05f * Smoother(studioLife));
            if (studio > 0f)
            {
                Texture2D tex = _cutLogo != null ? _cutLogo : _posterLogo;
                float aspect = tex.height > 0 ? (float)tex.width / tex.height : 1f;   // fitted in the square, as ScaleToFit
                float sw = aspect >= 1f ? studioSize : studioSize * aspect, sh = aspect >= 1f ? studioSize / aspect : studioSize;
                DrawSmooth(new Rect((w - sw) * 0.5f, (h - sh) * 0.5f, sw, sh), tex, new Rect(0f, 0f, 1f, 1f), studio * black);
            }
            // Then the game's logo: it rises slowly into place while it appears, holds on a slow push-in, and dives forward
            // as it leaves.
            float logo = Ramp(t, ArtIn, ArtInTime, CardOutStart, CardOut);
            float rise = Smoother((t - ArtIn) / 2.8f);
            float cy = h * 0.47f + h * 0.035f * (1f - rise);
            Rect r = default;
            if (logo > 0f)
            {
                float z = 1f + 0.045f * Smoother((t - ArtIn) / (CardOutStart - ArtIn)) + 0.40f * Smoother(outK);
                float lw = Mathf.Min(w * 0.80f, h * 1.9f) * z;
                float lh = lw * (PosterCrop.height * _posterArt.height) / (PosterCrop.width * _posterArt.width);
                r = new Rect((w - lw) * 0.5f, cy - lh * 0.5f, lw, lh);
                if (_cutArt != null)
                    DrawSmooth(r, _cutArt, new Rect(0f, 0f, 1f, 1f), logo * black);
                else
                    DrawSmooth(r, _posterArt, PosterCrop, logo * black);
            }
            GUI.color = old;

            GL.PushMatrix();
            GL.LoadPixelMatrix(0, w, h, 0);   // GUI orientation (y down), like the rects above
            // The crop's edges fade into the black (the art's background is not quite black).
            if (logo > 0f && _cutArt == null && _blend != null && _blend.SetPass(0))
            {
                GL.Begin(GL.QUADS);
                Color solid = new Color(0f, 0f, 0f, black), clear = new Color(0f, 0f, 0f, 0f);
                float fx = r.width * 0.16f, fy = r.height * 0.22f;
                FadeQuad(r.xMin - 1f, r.yMin, r.xMin + fx, r.yMax, solid, clear, true);
                FadeQuad(r.xMax - fx, r.yMin, r.xMax + 1f, r.yMax, clear, solid, true);
                FadeQuad(r.xMin, r.yMin - 1f, r.xMax, r.yMin + fy, solid, clear, false);
                FadeQuad(r.xMin, r.yMax - fy, r.xMax, r.yMax + 1f, clear, solid, false);
                GL.End();
            }
            if (t < RevealStart && _additive != null && _additive.SetPass(0))
            {
                GL.Begin(GL.QUADS);
                // Studio: the backlight, a cool soft halo behind the logo, swelling then dying away before the logo goes.
                float halo = studio * Mathf.Sin(Mathf.Clamp01(studioLife * 1.15f) * Mathf.PI);
                if (halo > 0.001f)
                {
                    DrawGlow(w * 0.5f, h * 0.47f, studioSize * 0.75f, studioSize * 0.62f, new Color(0.55f, 0.65f, 0.85f) * (0.20f * halo));
                    DrawGlow(w * 0.5f, h * 0.47f, studioSize * 0.32f, studioSize * 0.30f, new Color(0.8f, 0.86f, 1f) * (0.10f * halo));
                }
                if (logo > 0f)
                {
                    // Dust motes drifting slowly outward around the game's logo, fading in with it.
                    DrawMotes(w, h, cy, t - ArtIn, logo * (1f - outK));
                    // A soft light crossing the logo, and the anamorphic streak beneath it.
                    float sweep = (t - PosterSweep) / SweepTime;
                    float flash = sweep > 0f && sweep < 1f ? Mathf.Sin(sweep * Mathf.PI) : 0f;
                    if (flash > 0f)
                    {
                        float gx = r.xMin + r.width * (0.10f + 0.80f * Smoother(sweep));
                        DrawGlow(gx, cy, r.height * 0.26f, r.height * 0.60f, new Color(0.85f, 0.92f, 1f) * (0.15f * flash));
                    }
                    float streak = (0.45f * Ramp(t, ArtIn + 0.6f, 0.8f, ArtIn + 1.8f, 2.2f) + 0.14f * logo + 0.35f * flash) * (1f - outK)
                                   + 0.75f * Mathf.Sin(Mathf.Clamp01(outK * 1.3f) * Mathf.PI);
                    float reach = w * (0.30f + 0.22f * Smoother((t - ArtIn - 0.6f) / 2f));
                    float sy = r.yMax - r.height * 0.12f;
                    if (streak > 0.001f)
                    {
                        DrawGlow(w * 0.5f, sy, reach, Mathf.Max(1.5f, h * 0.0024f), StreakBlue * streak);
                        DrawGlow(w * 0.5f, sy, reach * 0.75f, h * 0.03f, StreakBlue * (streak * 0.22f));
                    }
                }
                GL.End();
            }
            GL.PopMatrix();

            // No version under the logo: the poster alone.
        }

        // A copy of the uv part of src whose background level (the darkest tenth of its border, a little above) becomes
        // transparent; what is above keeps its colour, its coverage goes to alpha. Read through a render texture (the
        // game's screens are not readable). One-time, on the first load only. Null on any failure (the original is drawn).
        private static Texture2D CutOut(Texture2D src, Rect uv)
        {
            if (src == null)
                return null;
            RenderTexture previous = RenderTexture.active;
            RenderTexture rt = null;
            try
            {
                int w = Mathf.Max(8, Mathf.RoundToInt(src.width * uv.width)), h = Mathf.Max(8, Mathf.RoundToInt(src.height * uv.height));
                rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
                Graphics.Blit(src, rt, new Vector2(uv.width, uv.height), new Vector2(uv.x, uv.y));
                RenderTexture.active = rt;
                var tex = new Texture2D(w, h, TextureFormat.RGBA32, true);
                tex.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
                Color32[] px = tex.GetPixels32();
                // Art that already has a transparent background (the studio logo: white, its shape in alpha) is drawn as it
                // is: brightness alone would turn it into a white square.
                int clearBorder = 0, borderCount = 0;
                for (int x = 0; x < w; x += 4) { borderCount += 2; if (px[x].a < 16) clearBorder++; if (px[(h - 1) * w + x].a < 16) clearBorder++; }
                if (clearBorder * 2 > borderCount)
                {
                    UnityEngine.Object.Destroy(tex);
                    return null;
                }
                // Background: the border's pixels, brightest channel; a level just above most of them.
                var border = new System.Collections.Generic.List<int>(2 * (w + h));
                for (int x = 0; x < w; x++) { border.Add(Max3(px[x])); border.Add(Max3(px[(h - 1) * w + x])); }
                for (int y = 0; y < h; y++) { border.Add(Max3(px[y * w])); border.Add(Max3(px[y * w + w - 1])); }
                border.Sort();
                float bg = Mathf.Min(80f, border[(int)(border.Count * 0.9f)] + 6f);
                float range = 255f - bg;
                for (int i = 0; i < px.Length; i++)
                {
                    Color32 c = px[i];
                    float m = Max3(c);
                    float v = (m - bg) / range;
                    if (v <= 0f)
                    {
                        px[i] = new Color32(0, 0, 0, 0);
                        continue;
                    }
                    v = Mathf.Clamp01(v);
                    float k = 1f / Mathf.Max(v, 0.05f);   // colour as if fully covered; coverage in alpha
                    px[i] = new Color32((byte)Mathf.Clamp((c.r - bg) / range * k * 255f, 0f, 255f),
                                        (byte)Mathf.Clamp((c.g - bg) / range * k * 255f, 0f, 255f),
                                        (byte)Mathf.Clamp((c.b - bg) / range * k * 255f, 0f, 255f), (byte)(v * 255f));
                }
                tex.SetPixels32(px);
                tex.wrapMode = TextureWrapMode.Clamp;
                tex.filterMode = FilterMode.Trilinear;
                tex.anisoLevel = 4;
                tex.Apply(true, true);
                tex.name = "Hyperdrive.Intro." + src.name;
                return tex;
            }
            catch (Exception e)
            {
                Log.Info("WarpScreen: intro art cut-out failed (" + e.Message + "), original drawn");
                return null;
            }
            finally
            {
                RenderTexture.active = previous;
                if (rt != null)
                    RenderTexture.ReleaseTemporary(rt);
            }
        }

        private static int Max3(Color32 c) => Math.Max(c.r, Math.Max(c.g, c.b));

        // Sub-pixel textured quad (GUI space): Graphics.DrawTexture places the rect exactly, where GUI.DrawTexture snaps it
        // to whole pixels (a slow zoom made the logo's edges step and tremble). 0.5 is the neutral tint of its material.
        private static void DrawSmooth(Rect r, Texture tex, Rect uv, float alpha)
        {
            if (tex == null || alpha <= 0f)
                return;
            Graphics.DrawTexture(r, tex, uv, 0, 0, 0, 0, new Color(0.5f, 0.5f, 0.5f, 0.5f * Mathf.Clamp01(alpha)));
        }

        private static float Smoother(float x)
        {
            x = Mathf.Clamp01(x);
            return x * x * x * (x * (x * 6f - 15f) + 10f);
        }

        // Faint motes of light drifting slowly away from the logo's centre (fixed per index, no allocation), each fading in
        // and out along its path. Additive, GUI orientation, inside GL.Begin(QUADS).
        private const int Motes = 70;

        private static void DrawMotes(float w, float h, float cy, float time, float alpha)
        {
            if (alpha <= 0.001f || time < 0f)
                return;
            for (int i = 0; i < Motes; i++)
            {
                uint seed = (uint)(i * 2654435761u + 977u);
                float a = (seed & 0xffff) / 65535f * Mathf.PI * 2f;
                float r0 = 0.18f + ((seed >> 16) & 0xff) / 255f * 0.55f;
                float speed = 0.006f + ((seed >> 24) & 0x3f) / 63f * 0.012f;
                float r = r0 + speed * time;
                float x = w * 0.5f + Mathf.Cos(a) * r * w * 0.55f;
                float y = cy + Mathf.Sin(a) * r * h * 0.42f;
                float life = Mathf.Sin(Mathf.Clamp01((r - r0) / (speed * 9f) + 0.15f) * Mathf.PI);
                float k = alpha * life * (0.25f + 0.75f * ((seed >> 8) & 0xff) / 255f) * 0.35f;
                if (k <= 0.002f)
                    continue;
                float size = 0.6f + ((seed >> 4) & 0x3) * 0.45f;
                Color c = new Color(0.75f * k, 0.85f * k, k, 0f);
                GL.Color(c); GL.Vertex3(x - size, y - size, 0f);
                GL.Color(c); GL.Vertex3(x + size, y - size, 0f);
                GL.Color(c); GL.Vertex3(x + size, y + size, 0f);
                GL.Color(c); GL.Vertex3(x - size, y + size, 0f);
            }
        }

        // A rectangle whose colour goes from a to b, left to right (horizontal) or top to bottom. Inside GL.Begin(QUADS).
        private static void FadeQuad(float x0, float y0, float x1, float y1, Color a, Color b, bool horizontal)
        {
            GL.Color(a); GL.Vertex3(x0, y0, 0f);
            GL.Color(horizontal ? b : a); GL.Vertex3(x1, y0, 0f);
            GL.Color(b); GL.Vertex3(x1, y1, 0f);
            GL.Color(horizontal ? a : b); GL.Vertex3(x0, y1, 0f);
        }

        private void DrawIntro(float w, float h)
        {
            float t = _introClock;
            if (t > IntroHold || w < 16f || h < 16f || _phase == Phase.Exit)
            {
                IntroPlaying = false;
                return;
            }
            if (_posterArt != null)
            {
                if (!_cutDone)
                {
                    _cutDone = true;
                    _cutArt = CutOut(_posterArt, PosterCrop);
                    _cutLogo = CutOut(_posterLogo, new Rect(0f, 0f, 1f, 1f));
                }
                EnsureStyles(h);
                Color previous = GUI.color;
                float dark = t < RevealStart ? 1f : 1f - Smooth((t - RevealStart) / RevealTime);
                GUI.color = new Color(0f, 0f, 0f, dark);
                GUI.DrawTexture(new Rect(0f, 0f, w, h), Texture2D.whiteTexture);
                GUI.color = previous;
                float barsH = h * 0.115f * EaseOut((t - CardStart) / 1.2f) * (1f - Smooth((t - RevealStart - 0.3f) / 1.7f));
                DrawPoster(w, h, t, dark, barsH);
                DrawBars(w, h, barsH);
                return;
            }
            EnsureStyles(h);
            Color old = GUI.color;
            float black = t < RevealStart ? 1f : 1f - Smooth((t - RevealStart) / RevealTime);
            GUI.color = new Color(0f, 0f, 0f, black);
            GUI.DrawTexture(new Rect(0f, 0f, w, h), Texture2D.whiteTexture);
            GUI.color = old;

            // Bars height first: the card's backdrop (a hint of deep blue) stops at them, so they read as letterbox bars.
            float bars = h * 0.115f * EaseOut((t - CardStart) / 1.2f) * (1f - Smooth((t - RevealStart - 0.3f) / 1.7f));
            if (t < RevealStart && bars > 0.5f && _blend != null)
            {
                GL.PushMatrix();
                GL.LoadPixelMatrix(0, w, 0, h);
                if (_blend.SetPass(0))
                    DrawRect(0f, bars, w, h - bars, new Color32(6, 8, 13, (byte)(255f * black)));
                GL.PopMatrix();
            }
            float outK = Smooth((t - CardOutStart) / CardOut);
            float cy = h * 0.53f + h * 0.012f * outK;   // GL pixel space: y from the bottom; the titles drift up as they go
            float push = 1f + 0.05f * EaseOut((t - TitleStart) / (CardOutStart + CardOut - TitleStart));
            float sweep = (t - SweepStart) / SweepTime;
            float flash = sweep > 0f && sweep < 1f ? Mathf.Sin(sweep * Mathf.PI) : 0f;

            GL.PushMatrix();
            GL.LoadPixelMatrix();
            if (t < RevealStart && _additive != null && _additive.SetPass(0))
            {
                GL.Begin(GL.QUADS);
                // A faint cold glow behind the name, and the anamorphic streak through it.
                float ambient = 0.07f * Ramp(t, CardStart, 1.6f, CardOutStart, CardOut);
                DrawGlow(w * 0.5f, cy, w * 0.55f, h * 0.22f, StreakBlue * ambient);
                float streak = 0.6f * Ramp(t, StreakStart, 0.5f, StreakStart + 0.9f, 1.8f)
                               + 0.13f * Ramp(t, StreakStart, 1.2f, CardOutStart, CardOut) + 0.35f * flash * (1f - outK);
                float reach = w * (0.30f + 0.20f * EaseOut((t - StreakStart) / 1.4f)) * (1f + 0.25f * outK);
                if (t > StreakStart)
                {
                    DrawGlow(w * 0.5f, cy, reach, Mathf.Max(1.5f, h * 0.0022f), StreakBlue * streak);
                    DrawGlow(w * 0.5f, cy, reach * 0.75f, h * 0.026f, StreakBlue * (streak * 0.22f));
                }
                GL.End();
            }
            GL.PopMatrix();

            Font font = _lightFont ?? _regularFont;
            if (font != null && t < RevealStart)
            {
                float tracking = 0.24f + 0.55f * (1f - EaseOut((t - TitleStart) / TitleIn)) + 0.12f * outK;
                DrawGlTitle(GameTitle, font, Mathf.RoundToInt(h * 0.044f), w * 0.5f, cy, push, tracking, t, 1f - outK, sweep);
            }

            DrawBars(w, h, bars);
        }

        // Letterbox bars: close in with the titles, open as the loading screen rises.
        private void DrawBars(float w, float h, float bars)
        {
            if (bars <= 0.5f || _blend == null)
                return;
            GL.PushMatrix();
            GL.LoadPixelMatrix(0, w, 0, h);
            if (_blend.SetPass(0))
            {
                DrawRect(0f, 0f, w, bars, new Color32(0, 0, 0, 255));
                DrawRect(0f, h - bars, w, h, new Color32(0, 0, 0, 255));
            }
            GL.PopMatrix();
        }

        // A soft elliptical light: full colour at the centre, smoothly to nothing at (cx ± halfW, cy ± halfH), on a 6x6
        // grid of quads so the falloff has no straight edges. Additive (premultiplied colour), inside GL.Begin(QUADS).
        private const int GlowGrid = 6;

        private static void DrawGlow(float cx, float cy, float halfW, float halfH, Color c)
        {
            for (int j = 0; j < GlowGrid; j++)
                for (int i = 0; i < GlowGrid; i++)
                {
                    GlowVertex(cx, cy, halfW, halfH, c, i, j);
                    GlowVertex(cx, cy, halfW, halfH, c, i, j + 1);
                    GlowVertex(cx, cy, halfW, halfH, c, i + 1, j + 1);
                    GlowVertex(cx, cy, halfW, halfH, c, i + 1, j);
                }
        }

        private static void GlowVertex(float cx, float cy, float halfW, float halfH, Color c, int i, int j)
        {
            float u = i * (2f / GlowGrid) - 1f, v = j * (2f / GlowGrid) - 1f;
            float r = Mathf.Clamp01(u * u + v * v);
            float k = (1f - r) * (1f - r);
            GL.Color(new Color(c.r * k, c.g * k, c.b * k, 0f));
            GL.Vertex3(cx + u * halfW, cy + v * halfH, 0f);
        }

        // The game's name: letters fade and rise in one after the other, extra tracking (in em) closing up, and a gleam
        // (sweep 0..1 crosses from left to right) brightening the silver to white where it passes. No allocation.
        private static void DrawGlTitle(string text, Font font, int size, float cx, float cy, float scale, float tracking, float t, float fade, float sweep, float start = TitleStart)
        {
            font.RequestCharactersInTexture(text, size, FontStyle.Normal);
            float extra = tracking * size;
            float width = 0f;
            CharacterInfo ci;
            for (int i = 0; i < text.Length; i++)
                if (font.GetCharacterInfo(text[i], out ci, size, FontStyle.Normal))
                    width += ci.advance + (i < text.Length - 1 ? extra : 0f);
            float left = cx - width * 0.5f * scale;
            float x = left;
            float baseline = cy - size * 0.33f * scale;
            float band = size * 2.2f * scale;
            float gleamX = left - band + (width * scale + 2f * band) * sweep;
            bool gleam = sweep > 0f && sweep < 1f;
            Material material = font.material;
            if (material == null || !material.SetPass(0))
                return;
            GL.PushMatrix();
            GL.LoadPixelMatrix();
            GL.Begin(GL.QUADS);
            for (int i = 0; i < text.Length; i++)
            {
                if (!font.GetCharacterInfo(text[i], out ci, size, FontStyle.Normal))
                    continue;
                float a = Smooth((t - start - i * LetterStagger) / LetterFade) * fade;
                if (a > 0.002f)
                {
                    float rise = (1f - a) * size * 0.10f * scale;
                    float x0 = x + ci.minX * scale, x1 = x + ci.maxX * scale;
                    float y0 = baseline + ci.minY * scale - rise, y1 = baseline + ci.maxY * scale - rise;
                    Color c0 = Lit(x0, gleamX, band, gleam, a), c1 = Lit(x1, gleamX, band, gleam, a);
                    GL.Color(c0); GL.TexCoord(ci.uvBottomLeft); GL.Vertex3(x0, y0, 0f);
                    GL.Color(c0); GL.TexCoord(ci.uvTopLeft); GL.Vertex3(x0, y1, 0f);
                    GL.Color(c1); GL.TexCoord(ci.uvTopRight); GL.Vertex3(x1, y1, 0f);
                    GL.Color(c1); GL.TexCoord(ci.uvBottomRight); GL.Vertex3(x1, y0, 0f);
                }
                x += (ci.advance + extra) * scale;
            }
            GL.End();
            GL.PopMatrix();
        }

        private static Color Lit(float x, float gleamX, float band, bool gleam, float alpha)
        {
            float g = 0f;
            if (gleam)
            {
                float d = (x - gleamX) / band;
                g = Mathf.Exp(-d * d * 3f);
            }
            Color c = Color.Lerp(TitleSilver, Color.white, g);
            c.a = alpha * (0.92f + 0.08f * g);
            return c;
        }

        // Centered text from the font's glyph quads, scaled around its centre by the GPU (smooth at any scale). Pixel space,
        // y up; cy is the middle of the capitals. No allocation: glyphs are read from the font texture every frame.
        private static void DrawGlText(string text, Font font, int size, float cx, float cy, float scale, Color color)
        {
            font.RequestCharactersInTexture(text, size, FontStyle.Normal);
            float width = 0f;
            CharacterInfo ci;
            for (int i = 0; i < text.Length; i++)
                if (font.GetCharacterInfo(text[i], out ci, size, FontStyle.Normal))
                    width += ci.advance;
            float x = cx - width * 0.5f * scale;
            float baseline = cy - size * 0.33f * scale;
            Material material = font.material;
            if (material == null || !material.SetPass(0))
                return;
            GL.PushMatrix();
            GL.LoadPixelMatrix();
            GL.Begin(GL.QUADS);
            GL.Color(color);
            for (int i = 0; i < text.Length; i++)
            {
                if (!font.GetCharacterInfo(text[i], out ci, size, FontStyle.Normal))
                    continue;
                float x0 = x + ci.minX * scale, x1 = x + ci.maxX * scale;
                float y0 = baseline + ci.minY * scale, y1 = baseline + ci.maxY * scale;
                GL.TexCoord(ci.uvBottomLeft); GL.Vertex3(x0, y0, 0f);
                GL.TexCoord(ci.uvTopLeft); GL.Vertex3(x0, y1, 0f);
                GL.TexCoord(ci.uvTopRight); GL.Vertex3(x1, y1, 0f);
                GL.TexCoord(ci.uvBottomRight); GL.Vertex3(x1, y0, 0f);
                x += ci.advance * scale;
            }
            GL.End();
            GL.PopMatrix();
        }

        private void Draw()
        {
            float w = Screen.width, h = Screen.height;
            if (w < 16f || h < 16f)
                return;   // minimized or zero-sized window: nothing to draw (and the effects' loops need a real size)
            if (_effect != null)
            {
                DrawEffect(w, h);
                return;
            }
            BuildStars(w, h);

            GL.PushMatrix();
            GL.LoadPixelMatrix(0, w, 0, h);
            if (_blend != null && _blend.SetPass(0))
            {
                // Flat deep-space black (gradients band visibly at these levels); a uniform lift for the exposure flashes.
                byte lift = (byte)(2f + 30f * _flash);
                DrawRect(0, 0, w, h, new Color32(lift, lift, (byte)Mathf.Min(255, lift + 2), (byte)(255f * _overlay)));
            }
            if (_style == Style.Nebula || _style == Style.Vortex)
                DrawBackdrop(w, h);
            if (_additive != null && _additive.SetPass(0))
                Graphics.DrawMeshNow(_mesh, Matrix4x4.identity);
            if (_style == Style.Orbit)
                DrawOrbit(w, h);
            if (_style != Style.Hyperspace)
            {
                FillContext(0f);
                Finishes.Apply(_ctx, Finish.Film);
            }
            DrawEasterEgg();
            DrawProgressLine(w, h);
            GL.PopMatrix();

            DrawText(w, h);
            MaybeCapture();
        }

        private void BuildStars(float w, float h)
        {
            float t = _anim;
            float scale = h / 1080f;
            float focal = 0.72f * h;
            float cx = w * 0.5f + w * (0.018f + 0.010f * Mathf.Sin(t * 0.11f)) * (0.35f + 0.65f * _drawWarp);
            float cy = h * 0.52f + h * 0.012f * Mathf.Sin(t * 0.153f + 1.1f) * _drawWarp;
            float shutter = _drawSpeed * Exposure;
            float still = 1f - _drawWarp;
            byte warpMix = (byte)(255f * _drawWarp);
            bool twinkle = still > 0.01f;
            float lutScale = DepthLutSize / (ZFar - ZNear);
            float marginX = w * 1.2f, marginY = h * 1.2f;

            for (int i = 0; i < _count; i++)
            {
                int v = i * VertsPerStreak;
                float z = _z[i], zTail = z + shutter;
                float hx = cx + _x[i] * focal / z, hy = cy + _y[i] * focal / z;
                float tx = cx + _x[i] * focal / zTail, ty = cy + _y[i] * focal / zTail;
                if (hx < -marginX || hx > w + marginX || hy < -marginY || hy > h + marginY)
                {
                    Collapse(v, i);
                    continue;
                }

                float mag = _mag[i];
                float near = Mathf.Clamp(0.18f / z, 0.75f, 2.2f);
                float thick = Mathf.Max(1.5f * scale, (0.9f + 1.3f * mag) * near * scale) * (1f + 1.3f * still);   // points need more area than lines

                int lut = (int)((z - ZNear) * lutScale);
                float bright = 1.2f * (0.28f + 0.72f * mag) * _depthGain[lut < 0 ? 0 : lut > DepthLutSize ? DepthLutSize : lut];
                if (z > 0.97f)
                    bright *= (ZFar - z) / (ZFar - 0.97f);                         // born invisible in the distance
                if (twinkle)
                    bright *= 1f + still * (1.3f - 0.4f * (0.5f + 0.5f * Mathf.Sin(t * 1.9f + _twinkle[i])));  // a still sky reads brighter, and twinkles
                if (_deep[i])
                    bright *= 0.3f + 0.7f * _drawWarp;                              // the deep field fills the vanishing point in warp only
                bright = Mathf.Clamp01(bright) * _overlay * _starDim;

                Color32 baseTint = _tint[i];
                Color32 head = warpMix == 255 ? _tintHead[i] : warpMix == 0 ? baseTint : Mix(baseTint, _tintHead[i], warpMix);
                Color32 tail = warpMix == 255 ? _tintTail[i] : warpMix == 0 ? baseTint : Mix(baseTint, _tintTail[i], warpMix);
                byte hb = (byte)(bright * 255f);
                byte tb = (byte)(bright * 255f * (0.30f + 0.70f * still));        // streaks taper towards the vanishing point

                WriteStreak(v, hx, hy, tx, ty, thick, Scale(head, hb), Scale(tail, tb));

                // Halo for the bright class: wider, faint, same streak.
                if (i < _bright)
                {
                    float glow = Mathf.Clamp01((mag - 0.62f) / 0.38f);
                    WriteStreak(_haloBase + i * VertsPerStreak, hx, hy, tx, ty, thick * (4f - 2.2f * still), Scale(head, (byte)(hb * 0.18f * glow)), Scale(tail, (byte)(tb * 0.12f * glow)));
                }
            }
            _mesh.vertices = _vertices;
            _mesh.colors32 = _colors;
        }

        // Three rows from tail to tip, bright along the centre line and black on the edges (soft, anti-aliased):
        // the shoulder sits one width behind the tip, so a moving star has a rounded head and fades towards its tail,
        // and a star at rest (streak no longer than twice its width) is a soft diamond that reads as a round point.
        private void WriteStreak(int v, float hx, float hy, float tx, float ty, float thick, Color32 head, Color32 tail)
        {
            float dx = hx - tx, dy = hy - ty;
            float len = Mathf.Sqrt(dx * dx + dy * dy);
            float ux = 0f, uy = 1f;
            if (len > 1e-4f) { ux = dx / len; uy = dy / len; }
            float minLen = thick;   // at rest: a diamond as long as it is wide, which reads as a round point
            if (len < minLen)
            {
                float pad = (minLen - len) * 0.5f;
                hx += ux * pad; hy += uy * pad; tx -= ux * pad; ty -= uy * pad;
                len = minLen;
            }
            float cap = Mathf.Min(thick, len * 0.5f);
            float sx = hx - ux * cap, sy = hy - uy * cap;
            float nx = -uy * thick * 0.5f, ny = ux * thick * 0.5f;
            // Tail brightness only for real streaks; a point fades on both sides of its shoulder.
            float streak = Mathf.Clamp01((len - 2f * thick) / (2f * thick));
            Color32 tailColor = Scale(tail, (byte)(255f * streak));

            _vertices[v] = new Vector3(tx + nx, ty + ny, 0f);
            _vertices[v + 1] = new Vector3(tx, ty, 0f);
            _vertices[v + 2] = new Vector3(tx - nx, ty - ny, 0f);
            _vertices[v + 3] = new Vector3(sx + nx, sy + ny, 0f);
            _vertices[v + 4] = new Vector3(sx, sy, 0f);
            _vertices[v + 5] = new Vector3(sx - nx, sy - ny, 0f);
            _vertices[v + 6] = new Vector3(hx + nx * 0.4f, hy + ny * 0.4f, 0f);
            _vertices[v + 7] = new Vector3(hx, hy, 0f);
            _vertices[v + 8] = new Vector3(hx - nx * 0.4f, hy - ny * 0.4f, 0f);
            _colors[v] = Clear; _colors[v + 2] = Clear;
            _colors[v + 3] = Clear; _colors[v + 5] = Clear;
            _colors[v + 6] = Clear; _colors[v + 8] = Clear;
            _colors[v + 1] = tailColor;
            _colors[v + 4] = head;
            _colors[v + 7] = Clear;
        }

        private void Collapse(int v, int star)
        {
            for (int k = 0; k < VertsPerStreak; k++)
            {
                _vertices[v + k] = Vector3.zero;
                _colors[v + k] = Clear;
            }
            if (star < _bright)
            {
                int h = _haloBase + star * VertsPerStreak;
                for (int k = 0; k < VertsPerStreak; k++)
                {
                    _vertices[h + k] = Vector3.zero;
                    _colors[h + k] = Clear;
                }
            }
        }

        private static Color32 Mix(Color32 a, Color32 b, byte t)
        {
            int it = 255 - t;
            return new Color32((byte)((a.r * it + b.r * t) / 255), (byte)((a.g * it + b.g * t) / 255), (byte)((a.b * it + b.b * t) / 255), 255);
        }

        private static Color32 Scale(Color32 c, byte k) =>
            new Color32((byte)(c.r * k / 255), (byte)(c.g * k / 255), (byte)(c.b * k / 255), 255);

        // An effect draws everything itself over its own background; the progress line and the texts stay the same.
        private void DrawEffect(float w, float h)
        {
            FillContext(0f);
            GL.PushMatrix();
            GL.LoadPixelMatrix(0, w, 0, h);
            if (_blend != null && _blend.SetPass(0))
            {
                Color bg = _effect.Background;
                DrawRect(0, 0, w, h, new Color32((byte)(255f * bg.r), (byte)(255f * bg.g), (byte)(255f * bg.b), (byte)(255f * _overlay)));
            }
            Gfx.Boil = _effect.Boil * h / 1080f;
            Gfx.BoilStep = (int)(_ctx.T * 8f);
            _effect.Draw(_ctx);
            Gfx.Boil = 0f;
            Finishes.Apply(_ctx, _effect.Finish);
            DrawEasterEgg();
            DrawProgressLine(w, h);
            GL.PopMatrix();

            DrawText(w, h);
            if (_effectStyle == null && _labelStyle != null)
                _effectStyle = new GUIStyle(_labelStyle);
            _ctx.Text = _effectStyle;
            Color old = GUI.color;
            _effect.DrawGui(_ctx);
            GUI.color = old;
            MaybeCapture();
        }

        private void DrawEasterEgg()
        {
            if (_eggStart < 0f)
                return;
            float t = Time.realtimeSinceStartup - _start - _eggStart;
            if (t < 0f || t > NyanKerbal.Duration || _phase > Phase.Cruise)
                return;
            FillContext(0f);
            if (_blend != null && _blend.SetPass(0))
                NyanKerbal.Draw(_ctx, t);
        }

        // ------------------------------------------------------------------ style layers (GL immediate: a few hundred triangles)

        // Nebula clouds and the vortex's glowing throat, behind the stars (additive).
        private void DrawBackdrop(float w, float h)
        {
            if (_additive == null || !_additive.SetPass(0))
                return;
            float t = _anim;
            if (_style == Style.Vortex)
            {
                float cx = w * 0.5f, cy = h * 0.52f;
                float pulse = 0.85f + 0.15f * Mathf.Sin(t * 1.7f);
                byte a = (byte)(70f * _drawWarp * _overlay * pulse);
                GlFan(cx, cy, h * 0.30f, h * 0.30f, 48, new Color32((byte)(a * 0.45f), (byte)(a * 0.55f), a, 255), Clear);
                byte c = (byte)(120f * _drawWarp * _overlay * pulse);
                GlFan(cx, cy, h * 0.05f, h * 0.05f, 32, new Color32((byte)(c * 0.8f), (byte)(c * 0.9f), c, 255), Clear);
                return;
            }
            float glow = (0.55f + 0.45f * _shownProgress) * _overlay;
            for (int c = 0; c < CloudCount; c++)
            {
                float x = Mathf.Repeat(_cloudX[c] + _cloudVx[c] * t + 0.2f, 1.4f) - 0.2f;
                float y = Mathf.Repeat(_cloudY[c] + _cloudVy[c] * t + 0.2f, 1.4f) - 0.2f;
                float breathe = 0.8f + 0.2f * Mathf.Sin(t * 0.21f + _cloudPhase[c]);
                float r = _cloudR[c] * h * (0.9f + 0.1f * breathe);
                float k = 0.27f * glow * breathe;
                Color32 col = _cloudColor[c];
                GlFan(x * w, y * h, r * 1.35f, r, 40, new Color32((byte)(col.r * k), (byte)(col.g * k), (byte)(col.b * k), 255), Clear);
                // A brighter, tighter core makes the cloud read as gas rather than a flat disc.
                float kc = k * 0.9f;
                GlFan(x * w + r * 0.12f, y * h - r * 0.08f, r * 0.45f, r * 0.32f, 32,
                    new Color32((byte)Mathf.Min(255f, col.r * kc + 18f * kc), (byte)(col.g * kc), (byte)Mathf.Min(255f, col.b * kc + 10f * kc), 255), Clear);
            }
        }

        // A planet seen from orbit, its night side over the bottom of the screen; the sun rises over the limb.
        private void DrawOrbit(float w, float h)
        {
            float R = h * 1.75f, pcx = w * 0.5f, pcy = -h * 1.36f;    // limb top at 0.39 h
            const float Arc = 0.62f;                                  // half the visible arc (radians from the top)
            float top = Mathf.PI * 0.5f;
            float sunA = top - 0.10f;                                 // a little right of the top
            float lit = _sunUp;
            byte ov = (byte)(255f * _overlay);

            // The disc (opaque, hides the stars behind it), then the thin bright limb and the airglow, additive.
            if (_blend != null && _blend.SetPass(0))
                GlFan(pcx, pcy, R, R, 96, new Color32(1, 2, 6, ov), new Color32(5, 10, 24, ov), top - Arc, top + Arc);
            if (_additive == null || !_additive.SetPass(0))
                return;
            float o = _overlay;
            GlRingLit(pcx, pcy, R - h * 0.003f, R + h * 0.004f, top - Arc, top + Arc, 128, sunA, 0.9f,
                new Color32(30, 60, 120, 255), new Color32(170, 210, 255, 255), lit, o, true, false);
            GlRingLit(pcx, pcy, R, R + h * 0.07f, top - Arc, top + Arc, 128, sunA, 1.2f,
                new Color32(10, 22, 60, 255), new Color32(60, 120, 230, 255), lit, o, false, false);
            // Lit crescent just inside the limb, and the warm band where the sun touches it.
            GlRingLit(pcx, pcy, R - h * 0.035f, R, sunA - 0.5f, sunA + 0.5f, 64, sunA, 0.5f,
                new Color32(0, 0, 0, 255), new Color32(70, 110, 170, 255), lit, o, false, true);
            GlRingLit(pcx, pcy, R - h * 0.004f, R + h * 0.03f, sunA - 0.22f, sunA + 0.22f, 48, sunA, 0.22f,
                new Color32(0, 0, 0, 255), new Color32(255, 170, 90, 255), lit, o, true, false);

            // The sun: below the limb at the start of loading, a little above it at the end.
            float limbX = pcx + R * Mathf.Cos(sunA), limbY = pcy + R * Mathf.Sin(sunA);
            float sx = limbX, sy = limbY + h * (-0.025f + 0.075f * lit);
            float above = Mathf.Clamp01((sy - limbY) / (h * 0.012f) + 0.5f);
            float sun = above * o;
            GlFan(sx, sy, h * 0.30f, h * 0.24f, 48, Scale(new Color32(255, 196, 140, 255), (byte)(60f * lit * o)), Clear);
            GlFan(sx, sy, w * 0.36f, h * 0.004f, 32, Scale(new Color32(255, 232, 205, 255), (byte)(140f * sun)), Clear);
            GlFan(sx, sy, h * 0.05f, h * 0.05f, 32, Scale(new Color32(255, 236, 210, 255), (byte)(170f * sun)), Clear);
            GlFan(sx, sy, h * 0.011f, h * 0.011f, 24, Scale(new Color32(255, 252, 245, 255), (byte)(255f * sun)), Scale(new Color32(255, 240, 220, 255), (byte)(200f * sun)));
        }

        // Filled ellipse (or a sector of it, angles in radians) with a radial gradient from centre to edge.
        private static void GlFan(float cx, float cy, float rx, float ry, int segments, Color32 centre, Color32 edge, float a0 = 0f, float a1 = Mathf.PI * 2f)
        {
            GL.Begin(GL.TRIANGLES);
            float step = (a1 - a0) / segments;
            float px = cx + rx * Mathf.Cos(a0), py = cy + ry * Mathf.Sin(a0);
            for (int s = 1; s <= segments; s++)
            {
                float a = a0 + step * s;
                float nx = cx + rx * Mathf.Cos(a), ny = cy + ry * Mathf.Sin(a);
                GL.Color(centre); GL.Vertex3(cx, cy, 0f);
                GL.Color(edge); GL.Vertex3(px, py, 0f);
                GL.Vertex3(nx, ny, 0f);
                px = nx; py = ny;
            }
            GL.End();
        }

        // Band between two radii, lit near the sun (falloff width in radians) as the light comes (lit); bright on its inner
        // edge and fading to nothing on the outer one, or the other way round when inward.
        private static void GlRingLit(float cx, float cy, float r0, float r1, float a0, float a1, int segments, float sunA, float width,
            Color32 dark, Color32 bright, float lit, float overlay, bool sharp, bool inward)
        {
            GL.Begin(GL.TRIANGLES);
            float step = (a1 - a0) / segments;
            Color clear = Color.clear;
            for (int s = 0; s < segments; s++)
            {
                float aA = a0 + step * s, aB = aA + step;
                Color cA = RingColor(aA, a0, a1, sunA, width, dark, bright, lit, overlay, sharp);
                Color cB = RingColor(aB, a0, a1, sunA, width, dark, bright, lit, overlay, sharp);
                float cosA = Mathf.Cos(aA), sinA = Mathf.Sin(aA), cosB = Mathf.Cos(aB), sinB = Mathf.Sin(aB);
                Color inA = inward ? clear : cA, inB = inward ? clear : cB, outA = inward ? cA : clear, outB = inward ? cB : clear;
                GL.Color(inA); GL.Vertex3(cx + r0 * cosA, cy + r0 * sinA, 0f);
                GL.Color(outA); GL.Vertex3(cx + r1 * cosA, cy + r1 * sinA, 0f);
                GL.Color(outB); GL.Vertex3(cx + r1 * cosB, cy + r1 * sinB, 0f);
                GL.Color(inA); GL.Vertex3(cx + r0 * cosA, cy + r0 * sinA, 0f);
                GL.Color(outB); GL.Vertex3(cx + r1 * cosB, cy + r1 * sinB, 0f);
                GL.Color(inB); GL.Vertex3(cx + r0 * cosB, cy + r0 * sinB, 0f);
            }
            GL.End();
        }

        private static Color RingColor(float a, float a0, float a1, float sunA, float width, Color32 dark, Color32 bright, float lit, float overlay, bool sharp)
        {
            float edge = Mathf.Clamp01(Mathf.Min(a - a0, a1 - a) / ((a1 - a0) * 0.15f));      // fade at the ends of the arc
            float near = Mathf.Clamp01(1f - Mathf.Abs(a - sunA) / width);
            if (sharp)
                near *= near;
            float k = Mathf.Lerp(0.25f, 1f, near) * (0.35f + 0.65f * lit);
            Color c = Color.Lerp(dark, bright, near * lit);
            float m = k * edge * overlay;
            return new Color(c.r * m, c.g * m, c.b * m, 1f);
        }

        private void DrawProgressLine(float w, float h)
        {
            if (_blend == null || !_blend.SetPass(0))
                return;
            float scale = h / 1080f;
            float x0 = w * 0.075f, x1 = w * 0.36f;
            float y = h * 0.085f, th = Mathf.Max(1f, 1.2f * scale);
            DrawRect(x0, y, x1, y + th, new Color32(255, 255, 255, (byte)(28 * _overlay)));
            float xf = Mathf.Lerp(x0, x1, _shownProgress);
            DrawRect(x0, y, xf, y + th, new Color32(236, 240, 246, (byte)(190 * _overlay)));
            if (_shownProgress > 0.001f)
                DrawRect(xf - th * 1.5f, y - th, xf + th * 1.5f, y + th * 2f, new Color32(255, 255, 255, (byte)(230 * _overlay)));
        }

        private void DrawRect(float x0, float y0, float x1, float y1, Color32 c)
        {
            _quadVerts[0] = new Vector3(x0, y0, 0); _quadVerts[1] = new Vector3(x0, y1, 0);
            _quadVerts[2] = new Vector3(x1, y1, 0); _quadVerts[3] = new Vector3(x1, y0, 0);
            _quadColors[0] = c; _quadColors[1] = c; _quadColors[2] = c; _quadColors[3] = c;
            _quad.vertices = _quadVerts;
            _quad.colors32 = _quadColors;
            Graphics.DrawMeshNow(_quad, Matrix4x4.identity);
        }

        private void EnsureStyles(float h)
        {
            int tipSize = Mathf.RoundToInt(h * 0.0185f), labelSize = Mathf.RoundToInt(h * 0.0125f);
            if (_tipStyle != null && _tipStyle.fontSize == tipSize)
                return;
            if (_lightFont == null)
            {
                _lightFont = PickFont("calibril");
                _regularFont = PickFont("calibri");
            }
            _tipStyle = new GUIStyle { font = _lightFont, fontSize = tipSize, alignment = TextAnchor.LowerLeft, wordWrap = false, richText = false };
            _tipStyle.normal.textColor = Color.white;
            _labelStyle = new GUIStyle(_tipStyle) { font = _regularFont ?? _lightFont, fontSize = labelSize };
            _percentStyle = new GUIStyle(_labelStyle) { alignment = TextAnchor.LowerRight };
            _markStyle = new GUIStyle(_labelStyle) { alignment = TextAnchor.UpperLeft };
        }

        // Calibri (light and regular) ships with KSP; Unity's default font otherwise.
        private static Font PickFont(string name)
        {
            foreach (Font f in Resources.FindObjectsOfTypeAll<Font>())
                if (f != null && f.dynamic && string.Equals(f.name, name, StringComparison.OrdinalIgnoreCase))
                    return f;
            return null;
        }

        private void DrawText(float w, float h)
        {
            EnsureStyles(h);
            float a = _overlay * IntroDone();
            float left = w * 0.075f, right = w * 0.36f;
            float lineY = h - h * 0.085f;   // GUI space: y from the top

            Color old = GUI.color;
            GUI.color = new Color(0.93f, 0.95f, 0.98f, 0.42f * a);
            _content.text = _stage;
            GUI.Label(new Rect(left, lineY - h * 0.034f, right - left, h * 0.03f), _content, _labelStyle);
            _content.text = _percentText;
            GUI.Label(new Rect(left, lineY - h * 0.034f, right - left, h * 0.03f), _content, _percentStyle);

            if (_tip.Length > 0)
            {
                GUI.color = new Color(0.95f, 0.96f, 0.99f, 0.74f * a);
                _content.text = _tip;
                GUI.Label(new Rect(left, lineY - h * 0.082f, w * 0.8f, h * 0.045f), _content, _tipStyle);
            }

            // No mod name on the loading screens: the game's name only, in the intro.
            GUI.color = old;
        }

        private void MaybeCapture()
        {
            if (!_owner.CaptureFrames || _captureIndex >= (_owner.PreviewStyles ? 90 : 30))
                return;
            float now = Time.realtimeSinceStartup;
            if (_owner.Intro && !_introRolling)
            {
                _nextCapture = now + 0.3f;   // the intro waits for frames to flow: capture it from its start
                return;
            }
            if (now < _nextCapture)
                return;
            string dir = Path.Combine(HyperdriveRuntime.ModDirectory, "PluginData");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "warp_" + _captureIndex.ToString("00") + "_" + StyleName + "_" + _phase + ".png");
            if (_owner.CaptureClean && _effect != null)
                CaptureEffectAlone(file);
            else
                ScreenCapture.CaptureScreenshot(file);
            _captureIndex++;
            bool fast = _captureIndex < (_owner.Intro ? 18 : 6) || _phase == Phase.Exit;
            _nextCapture = now + (fast ? (_owner.Intro && _phase != Phase.Exit ? 0.9f : 0.45f) : _owner.PreviewStyles ? _owner.PreviewSeconds * 0.5f : 6f);
        }

        // Developer captures only: draws the effect again into a render texture, so popups of other mods can't hide it.
        private void CaptureEffectAlone(string file)
        {
            int w = Screen.width, h = Screen.height;
            RenderTexture rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
            RenderTexture previous = RenderTexture.active;
            var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
            try
            {
                RenderTexture.active = rt;
                GL.Clear(true, true, Color.black);
                GL.PushMatrix();
                GL.LoadPixelMatrix(0, w, 0, h);
                if (_blend != null && _blend.SetPass(0))
                {
                    Color bg = _effect.Background;
                    DrawRect(0, 0, w, h, new Color32((byte)(255f * bg.r), (byte)(255f * bg.g), (byte)(255f * bg.b), 255));
                }
                Gfx.Boil = _effect.Boil * h / 1080f;
                Gfx.BoilStep = (int)(_ctx.T * 8f);
                _effect.Draw(_ctx);
                Gfx.Boil = 0f;
                Finishes.Apply(_ctx, _effect.Finish);
                DrawProgressLine(w, h);
                GL.PopMatrix();
                Color old = GUI.color;
                _effect.DrawGui(_ctx);
                GUI.color = old;
                tex.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
                tex.Apply(false);
                File.WriteAllBytes(file, tex.EncodeToPNG());
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(rt);
                UnityEngine.Object.Destroy(tex);
            }
        }
    }
}
