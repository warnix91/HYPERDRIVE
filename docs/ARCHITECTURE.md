# Hyperdrive architecture

## Goals
1. **Fewer stutters**: heap padding and fine control of Mono's garbage collector (Boehm, non-generational).
2. **Fast loading**: textures read ahead on worker threads (no Unity API off the main thread), the game's loading loops
   carried on within the same frame. Steps back where KSPCommunityFixes already does the job better.
3. **Throttling guard**: lowers the rate of heavy `Update`s of other mods (opt-in list) when the frame rate drops.
4. **API** (pooling, GPU instancing, GC hold, light budget) for other mods.

## Start-up
```
KSP AssemblyLoader
  └─ HyperdriveBootstrap  [KSPAddon Instantly, once]   ← no Harmony type here
       ├─ checks 0Harmony >= 2.2 (otherwise logs an error and stops cleanly)
       └─ AddComponent<HyperdriveRuntime>  (DontDestroyOnLoad)
            ├─ HyperdriveSettings.Load  (Hyperdrive.cfg + PluginData/Hyperdrive.user.cfg)
            ├─ SubsystemHost.Register(...) then EnableAll()
            │    for each subsystem: cfg switch → incompatible mods → LoadSettings → OnEnable
            │    exception ⇒ OnDisable + UnpatchAll(own id) + Failed state
            ├─ GameEvents.onLevelWasLoadedGUIReady → SubsystemHost.SceneReady
            ├─ Update → SubsystemHost.Update (no allocation)
            └─ HyperdriveAPI.Register(RuntimeBridge)
```

## Other optimization mods (`Patching/OtherOptimizers`)
Rule: keep the other mods' effects, and for a job done on both sides, keep the better version.
- **KSPCF's loader** (always on: FastLoader, GameDatabasePerf, `translateLoadedNodes`): `FastLoader`, `ConfigPrefetch`,
  `StartupFixes`, `DatabaseIndex` step back (`IncompatibleAssemblies`).
- **KSPCF's optional patches**: each subsystem declares its equivalents (`KspcfEquivalents`). KSPCF reads its settings
  after ModuleManager and applies its patches one by one (`BasePatch.Patch(Type)`); a Hyperdrive prefix, on the first
  call where `enabledPatches` is filled, removes from that set the patches listed in `Compatibility { keepOurs }` (as if
  the player had set them to `false`), stops the subsystems whose equivalent stays on, and starts those that were waiting
  on a guess. Before that, the guess comes from the .cfg files of KSPCF's folder. Afterwards, a subsystem whose KSPCF
  equivalent was applied anyway (`patchInstances`) steps back.
- **Shared methods** (after KSPCF, then at the main menu): a `YieldOnSharedTargets` subsystem steps back when a mod of
  the `optimizerAssemblies` list *replaces* one of its methods (transpiler or boolean prefix); a mere addition (postfix,
  void prefix) coexists and is only reported.
- If KSPCF's hooks change, every subsystem with a KSPCF equivalent steps back (caution).

## Assemblies
| DLL | Role | Depends on |
|---|---|---|
| `Hyperdrive.API.dll` | Small stable public contract (`HyperdriveAPI`) | Assembly-CSharp (KSPAssembly attribute), UnityEngine |
| `Hyperdrive.dll` | Everything else, each part switchable | Hyperdrive.API, 0Harmony, the game |

Load order guaranteed by `KSPAssemblyDependency("Hyperdrive.API", 0, 1)`.

## Subsystems
| Name | Folder | What it does |
|---|---|---|
| `LogSpamGuard` | Diagnostics | Wraps `Debug.unityLogger.logHandler`: identical messages beyond N per window become a summary. No Harmony. |
| `LoadProblems` | Diagnostics | First-load problems (errors of any mod or of the game, DLLs KSP didn't load, types that don't load = missing dependency), each attributed to its GameData folder: a box over the loading screen, a window at the main menu, a list in KSP.log. Read-only. |
| `Probe` | Diagnostics | Machine, graphics API, GC mode, memory, load time, Harmony audit. Read-only. |
| `HeapPadder` | Memory | Lowers the Boehm GC *free space divisor* (native API) and grows the heap ahead (`GC_expand_hp`); safety valve by RAM. |
| `GCWindows` | Memory | Stronger incremental collection at calm moments (`CollectIncremental`); short, capped `GCMode.Disabled` during staging, explosions and crashes or on API request. |
| `PerfHUD` | Diagnostics | Alt+F10 overlay and a one-line summary per minute in KSP.log (fps, spikes, GC cycles, heap). Allocation-free. |
| `ModProfiler` | Throttle | Alt+F11: temporary timing patches on every mod's `Update`/`LateUpdate`/`FixedUpdate`, ranking in KSP.log, patches removed. |
| `ThrottleGuard` | Throttle | Opt-in (`TARGET` in the config): in flight, under 40 fps, the listed `Update`/`LateUpdate` run at ~15 Hz, instances spread over frames. Refuses `FixedUpdate`/`OnGUI`. |
| `LightBudget` | Rendering | At most N dynamic lights active among those registered by mods (API). |
| `HighlightIdle` | Rendering | Frames with no highlighted part: HighlightingSystem's command buffer is emptied in place and OnRenderImage copies the image instead of compositing empty buffers. Checked to the pixel (`verify`). |
| `HudText` | Rendering | SpeedDisplay.LateUpdate skipped when the rounded speed, its statics and the displayed text haven't changed. |
| `ShaderWarmup` | Rendering | `Shader.WarmupAllShaders()` once at the main menu (off on Metal/Vulkan/DX12; off by default). |
| `LoadReport` | Loading | Load time per GameData folder and per category (with frame counts), from KSP's log lines; longest pauses. |
| `FastLoader` | Loading | First load only. DDS textures read ahead on a thread and built exactly as the game does; PNGs decoded by the game's own web request, 48 at a time; normal maps converted in one array copy; the game's loading loop and part compilation carried on within the same frame; a direct path writes file bytes straight into the texture (`ddsVerify` compares everything with the game). |
| `DelegateCache` | Loading | Part and module event/action delegates: the method found by the first stock call per (delegate type, target type, name) is reused; `verify` compares with stock. |
| `PartValueCache` | Loading | `PartLoader.ApplyPartValue`: field, `Parse` and set lookups cached per type and name (same results; parts fingerprint identical to the game). Leaves the job to KSPCF PartParsingPerf. |
| `AsyncExpansions` | Loading | Expansion bundles (Making History, Breaking Ground) opened with `LoadFromFileAsync` instead of a blocking `LoadFromMemory`, MD5 computed in the background. An LZ4 copy (Unity's `RecompressAssetBundleAsync`) is written once at the main menu and opened at the next starts when the original's size and date match, MD5 checked again. |
| `TerrainCoroutines` | Fixes | Stock leak: `StartCoroutine(UpdateSphere())` at every sphere start, never really stopped. Only the newest coroutine keeps running. Left to KSPCF `PQSCoroutineLeak` when on. Idea and measurements credited to Smooth Running (Ishiaki, CC BY 4.0). |
| `LaunchTerrain` | Fixes | New launch: the second `PSystemSetup.SetPQSActive()` (which throws away and rebuilds the terrain just built) is skipped when `SetPQSActive(sphere)` already ran in the same launch, as for a loaded flight. Left to KSPCF `PQSOnlyStartOnce` when on. |
| `Upscaler` | Rendering | Opt-in. AMD FSR 1 (EASU + RCAS, MIT) as a Unity shader, bundles built by `tools/build_shaders.sh` from `unity/HyperdriveShaders`. Scene cameras render to a reduced texture only between `onPreCull` and `onPostRender`, so screen/world conversions keep their meaning; a camera of ours scales and sharpens; markers and UI are drawn at full resolution. Alt+F9. |
| `WarpScreen` | Rendering | Loading animations (35), drawn in `OnGUI` without per-frame allocation; paces the loaders to hold the screen's rate; loads the two final scenes asynchronously; covers frozen frames (the animation pauses instead of jumping, and slows down before freezes remembered per modpack). Intro in the style of the game's own launch. |
| `SceneLoadScreen` | Rendering | During KSP's `LoadingBufferMask` (scene changes after the first load), a loading animation drawn on top, fading out with the mask. |
| `LaunchSitePacing` | Loading | First load: the forced build of Kerbin's terrain to place the launch sites (`PQS.ForceStart`, 2.1-2.3 s in one frame) runs through the same stock calls, in the same order, in slices during the 2 s wait that follows. |
| `BundleDefinitions` | Loading | First load: KSPAssets' bundle definitions read with the same requests sent ahead and without waiting a frame for what is ready. A request still running is aborted before the game rewrites its file. |
| `SavePrefetch` | Loading | Scene changes: right after a save is written, a thread reads and parses it the way `ConfigNode.Load` does; `LoadSFSFile` takes the result when the file's size and date haven't changed. Left to KSPCF `SceneLoadSpeedBoost` when on. |
| `CrashFixes` | Fixes | Around the outermost `Part.Die`: the vessel's resource sets are rebuilt once instead of once per lost part, exactly as staging does. |
| `StockFixes` | Fixes | `FXGroup.VolumeFade`: an engine's sound fade stops cleanly when its audio source was destroyed. |
| `JitPrewarm` | Loading | At the main menu, compiles every mod method ahead on a background thread (no hitch on first use), skipping any method that could run a static constructor off the main thread. |
| `BuoyancyGuard` | Physics | Skips `PartBuoyancy.FixedUpdate` for dry parts of vessels surely far from water (same conditions as the game, plus margins). Left to KSPCF `OnDemandPartBuoyancy` when on. |
| `AeroIdleThrottle` | Physics | Under 0.5 m/s, a part's aerodynamics is recomputed one physics step in 5; the previous state is kept. Water and EVA excluded. Steps back with FAR. |
| `MassUpdateGuard` | Physics | Off by default. `Rigidbody.mass`/`centerOfMass` set only when the value changes. |
| `ThermalCadence` | Physics | Thermally calm vessel: `UpdateThermodynamics` integrates once every 3 steps through the game's own time accumulation. Works with ModularFlightIntegrator unless a mod took the step. |
| `OcclusionCache` | Physics | Sun/body shading of parts reused while the direction seen from the vessel moves by less than 0.5°; recomputed at least every second. Left to KSPCF `FlightIntegratorPerf`. |
| `ZeroForceGuard` | Physics | `AddForce`/`AddTorque` skipped when the vector is exactly zero (no effect, per Unity). |
| `FrameTimeline` | Diagnostics | Splits the frame: FixedUpdate scripts, physics, Update scripts, coroutines, LateUpdate, rendering + wait. |
| `RenderProbe` | Diagnostics | Off by default. Rendering inventory in flight and timed diagnostic experiments, always restored. |
| `AutoBench` | Diagnostics | Automatic in-game benchmark (dedicated save, scripted flight, report). Idle without `PluginData/autobench.cfg`. |

## Platform notes
- macOS: x86_64 under Rosetta 2, OpenGL 4.1 Core ⇒ **no compute shaders** ⇒ no `DrawMeshInstancedIndirect`; fallback to
  `DrawMeshInstanced` (1023 instances per call).
- Windows: DX11, compute available. Linux: OpenGL Core / Vulkan shader bundle.
- Unity 2019.4: `GarbageCollector.GCMode` is `Enabled`/`Disabled` only; KSP 1.12.5 runs with the incremental GC on
  (3 ms time slice).

## Garbage collector
- KSP's Mono runtime exports Boehm's C API on macOS (Linux: expected, not yet checked). `Memory/BoehmGC.cs` binds to it **without loading a second
  copy** and checks that the heap size it reads matches Unity's before using it. On Windows these functions are not
  exported: `HeapPadder` steps back and `GCWindows` falls back to a time interval.
- Boehm starts a cycle when the bytes allocated since the last GC exceed about `(2 × live data with pointers + live data
  without pointers / 4 + roots) / divisor`: lowering the *divisor* spaces the cycles out; the heap grows in proportion.

## Configuration
- `GameData/Hyperdrive/Hyperdrive.cfg`: shipped values, `HYPERDRIVE` node, switches in `SUBSYSTEMS`.
- `GameData/Hyperdrive/PluginData/Hyperdrive.user.cfg`: local overrides (take precedence, never overwritten).
- Read at `Instantly`, before GameDatabase: ModuleManager patches don't apply to these settings.

## Languages
`Core/Lang`: Hyperdrive's texts in KSP's language (read from `buildID.txt` as KSP does, English by default), from
`GameData/Hyperdrive/Localization/<language>.cfg` (KSP's Localization format, `#HD_…` keys). 9 languages: en-us, fr-fr,
de-de, es-es, it-it, pt-br, ru, zh-cn, ja.

## Safety
- `Core/PrudentStart`: `PluginData/Session.txt` records where the game is; after a crash, the next start leaves out the
  optimizations of the place where it crashed, once.
- `Subsystem.ReadOnly`: diagnostics, window, notices. Outside KSP 1.12.x only those run.
- `tools/regress.sh|ps1` + `tools/regress/Check`: regression battery (load, scene changes, crash, editor) compared with a
  baseline.
- `Patching/Overlaps`: jobs shared with KSPCF (default, measure, evidence), decided ours/theirs/auto and learned per
  install.

## Window
`UI/Panel/SettingsPanel` (uGUI built at run time, six styles, three layouts), `UI/PanelModel` (pages),
`UI/HyperdriveWindow` (toolbar button, main menu icon), `UI/KspSettingsSection` (section in KSP's settings),
`Core/UserConfig` (writes Hyperdrive.user.cfg).
