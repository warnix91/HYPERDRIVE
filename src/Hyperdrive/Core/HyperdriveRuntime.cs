using System;
using System.IO;
using System.Reflection;
using Hyperdrive.API;
using Hyperdrive.Diagnostics;
using Hyperdrive.Fixes;
using Hyperdrive.Loading;
using Hyperdrive.Memory;
using Hyperdrive.Patching;
using Hyperdrive.Physics;
using Hyperdrive.Rendering;
using Hyperdrive.Throttle;
using UnityEngine;

namespace Hyperdrive.Core
{
    /// <summary>
    /// Lives for the whole game session (DontDestroyOnLoad host created by <see cref="HyperdriveBootstrap"/>).
    /// Loads the config, starts the subsystems and forwards Unity/KSP callbacks to them.
    /// </summary>
    internal sealed class HyperdriveRuntime : MonoBehaviour
    {
        public static readonly string Version = typeof(HyperdriveRuntime).Assembly.GetName().Version.ToString(3);

        public static string ModDirectory { get; private set; }

        private SubsystemHost _host;

        /// <summary>The subsystems and their states (the Hyperdrive window lists them).</summary>
        internal static SubsystemHost Host { get; private set; }

        /// <summary>The settings read at start (Hyperdrive.cfg and the player's overrides).</summary>
        internal static HyperdriveSettings Settings { get; private set; }

        /// <summary>PluginData/Hyperdrive.user.cfg: the player's overrides (the window writes its switches there).</summary>
        internal static string UserConfigPath { get; private set; }

        private void Awake()
        {
            _loadReviewFrame = Time.frameCount + 3;
            try
            {
                ModDirectory = ResolveModDirectory();
                HyperdriveSettings settings = HyperdriveSettings.Load(ModDirectory);
                Settings = settings;
                Log.DebugEnabled = settings.Global.GetBool("debugLogging", false);
                Brand.Load(settings.Global);
                Log.Info("v" + Version + " starting. Config: " + (settings.DefaultsFound ? settings.DefaultsPath : "MISSING (using built-in defaults)")
                         + (settings.UserFound ? " + user overrides " + settings.UserPath : ""));
                Log.Info("Graphics: " + SystemInfo.graphicsDeviceType + ", rendering " + SystemInfo.renderingThreadingMode
                         + " (KSP's boot.config turns graphics jobs on: LegacyJobified), " + SystemInfo.processorCount + " threads");

                SubsystemHost.s_allowOtherVersions = settings.Section("Compatibility").GetBool("allowOtherGameVersions", false);
                PrudentStart.Begin(ModDirectory, settings.Section("Compatibility").GetBool("prudentStart", true));
                _host = new SubsystemHost(settings);
                Host = _host;
                UserConfigPath = settings.UserPath;
                // Order matters: the log guard goes first so that it also covers spam emitted during loading,
                // memory tuning next so that loading benefits, Probe last so that it reports the applied state.
                _host.Register(new LogSpamGuard());
                _host.Register(new LoadProblems());
                // Starts parsing GameData configs on worker threads right away: KSP needs them a moment later.
                _host.Register(new ConfigPrefetch());
                _host.Register(new StartupFixes());
                // Before the Instantly add-ons created after Hyperdrive get their Awake.
                _host.Register(new StartPacing());
                _host.Register(new QuietLoading());
                _host.Register(new DatabaseIndex());
                _host.Register(new HeapPadder());
                _host.Register(new GCWindows());
                _host.Register(new PerfHUD());
                _host.Register(new FrameTimeline());
                _host.Register(new RenderProbe());
                _host.Register(new PSystemTrace());
                _host.Register(new ModProfiler());
                _host.Register(new ThrottleGuard());
                _host.Register(new LightBudget());
                _host.Register(new HighlightIdle());
                _host.Register(new HudText());
                _host.Register(new WarpScreen());
                _host.Register(new Upscaler());
                _host.Register(new SceneLoadScreen());
                _host.Register(new ShaderWarmup());
                _host.Register(new LoadReport());
                _host.Register(new JitPrewarm());
                _host.Register(new FastLoader());
                _host.Register(new AsyncExpansions());
                _host.Register(new BundleDefinitions());
                _host.Register(new LaunchSitePacing());
                _host.Register(new SavePrefetch());
                _host.Register(new ModelPrefetch());
                _host.Register(new LocalizerCache());
                _host.Register(new DelegateCache());
                _host.Register(new PartValueCache());
                _host.Register(new PartCompilePacing());
                _host.Register(new LoadingBoost());
                _host.Register(new ModuleManagerCache());
                _host.Register(new Editor.PartListPerf());
                _host.Register(new Editor.IconPrewarm());
                _host.Register(new UI.HyperdriveWindow());
                _host.Register(new BuoyancyGuard());
                _host.Register(new AeroIdleThrottle());
                _host.Register(new MassUpdateGuard());
                _host.Register(new ThermalCadence());
                _host.Register(new OcclusionCache());
                _host.Register(new ZeroForceGuard());
                _host.Register(new StockFixes());
                _host.Register(new LeakFixes());
                _host.Register(new TerrainCoroutines());
                _host.Register(new LaunchTerrain());
                _host.Register(new CrashFixes());
                _host.Register(new SaveRetry());
                _host.Register(new Probe(_host));
                _host.Register(new AutoBench());
                OtherOptimizers.Init(_host, settings.Section("Compatibility"));
                _host.EnableAll();

                GameEvents.onLevelWasLoadedGUIReady.Add(OnLevelReady);
                try
                {
                    // KSP's GameEvents need instance methods (a static handler throws in EventData.Add).
                    GameEvents.onGameSceneLoadRequested.Add(OnSceneRequested);
                    Application.quitting += Overlaps.Quitting;
                    if (!SubsystemHost.GameVersionSupported)
                    {
                        Log.Warn("KSP " + SubsystemHost.GameVersion + ": Hyperdrive is made for KSP 1.12, its optimizations stay off (diagnostics only)");
                        LoadProblems.Notice(Lang.F("#HD_LP_GameVersion", SubsystemHost.GameVersion));
                    }
                    if (PrudentStart.Active)
                    {
                        int off = 0;   // the ones actually held off (some may be off in the settings anyway)
                        foreach (Subsystem sub in _host.All)
                            if (sub.State == SubsystemState.StoodDown && sub.StateReason != null && sub.StateReason.StartsWith("prudent start", StringComparison.Ordinal))
                                off++;
                        LoadProblems.Notice(Lang.F("#HD_LP_Prudent", PlaceName(PrudentStart.StoppedIn), off));
                    }
                }
                catch (Exception e)
                {
                    Log.Exception("Session notices failed (the subsystems run normally)", e);
                }
                HyperdriveAPI.Register(new RuntimeBridge(_host));
            }
            catch (Exception e)
            {
                Log.Exception("Runtime failed to start", e);
                _host?.DisableAll();
                _host = null;
            }
        }

        private void Update()
        {
            _host?.Update();
            Overlaps.Update();
            // A few frames into the first load every add-on has run its Awake (where most mods patch): methods shared with
            // another mod's replacing patch are reviewed then, before the loading subsystems start their work, with or
            // without KSPCommunityFixes (whose own review comes after it applies its patches).
            if (!_reviewedLoad && HighLogic.LoadedScene == GameScenes.LOADING && Time.frameCount >= _loadReviewFrame)
            {
                _reviewedLoad = true;
                _host?.ReviewSharedTargets("start of the load");
            }
        }

        private bool _reviewedLoad;
        private int _loadReviewFrame = int.MaxValue;

        private void OnSceneRequested(GameScenes scene)
        {
            PrudentStart.SceneRequested(scene);
            Overlaps.SceneRequested(scene);
        }

        // Where the game stopped, in the player's language.
        private static string PlaceName(string place)
        {
            if (place == null)
                return Lang.T("#HD_Place_LOADING");
            string key = "#HD_Place_" + (place.StartsWith("SCENECHANGE", StringComparison.Ordinal) ? "SCENECHANGE" : place);
            string text = Lang.T(key);
            return text == key ? place : text;
        }

        private void OnLevelReady(GameScenes scene)
        {
            PrudentStart.SceneReady(scene);
            Overlaps.SceneReady(scene);
            if (scene == GameScenes.MAINMENU && !_sharedTargetsReviewed)
            {
                _sharedTargetsReviewed = true;
                _host?.ReviewSharedTargets("main menu");
            }
            // Some mods patch only once a game is loaded or a flight starts: shared methods reviewed again then, once.
            if (scene == GameScenes.SPACECENTER && !_reviewedSpaceCentre)
            {
                _reviewedSpaceCentre = true;
                _host?.ReviewSharedTargets("space centre");
            }
            if (scene == GameScenes.FLIGHT && !_reviewedFlight)
            {
                _reviewedFlight = true;
                _host?.ReviewSharedTargets("first flight");
            }
            _host?.SceneReady(scene);
        }

        private bool _sharedTargetsReviewed, _reviewedSpaceCentre, _reviewedFlight;

        private void OnDestroy()
        {
            GameEvents.onLevelWasLoadedGUIReady.Remove(OnLevelReady);
            GameEvents.onGameSceneLoadRequested.Remove(OnSceneRequested);
            _host?.DisableAll();
            OtherOptimizers.Shutdown();
        }

        /// <summary>GameData/Hyperdrive, found from where KSP loaded this DLL (survives a renamed folder).</summary>
        private static string ResolveModDirectory()
        {
            Assembly self = typeof(HyperdriveRuntime).Assembly;
            foreach (AssemblyLoader.LoadedAssembly loaded in AssemblyLoader.loadedAssemblies)
            {
                if (loaded.assembly != self || string.IsNullOrEmpty(loaded.path))
                    continue;

                // .../GameData/Hyperdrive/Plugins/Hyperdrive.dll -> .../GameData/Hyperdrive
                string pluginsDirectory = Path.GetDirectoryName(loaded.path);
                string modDirectory = pluginsDirectory != null ? Path.GetDirectoryName(pluginsDirectory) : null;
                if (!string.IsNullOrEmpty(modDirectory))
                    return modDirectory;
            }
            return Path.Combine(Path.Combine(KSPUtil.ApplicationRootPath, "GameData"), "Hyperdrive");
        }

        private sealed class RuntimeBridge : IHyperdriveRuntime
        {
            private readonly SubsystemHost _host;

            public RuntimeBridge(SubsystemHost host) => _host = host;

            public string Version => HyperdriveRuntime.Version;

            public bool IsSubsystemActive(string name) => _host.IsActive(name);

            public bool RequestGcHold(float seconds) => GCWindows.Active != null && GCWindows.Active.RequestHold(seconds, "API");

            public bool RegisterLight(Light light, float priority) => LightBudget.Active != null && LightBudget.Active.Register(light, priority);

            public bool UnregisterLight(Light light) => LightBudget.Active != null && LightBudget.Active.Unregister(light);
        }
    }
}
