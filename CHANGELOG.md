# Changelog

Every version keeps the game identical to the base game, unless the player picks an option. Everything can be switched
off subsystem by subsystem (KSP's settings, the Hyperdrive window, or `PluginData/Hyperdrive.user.cfg`).

## 1.0.0 (first public release)
- **ModuleManager keeps its cache** even when a patch of the modpack has an error; otherwise it redoes all its patches at
  every start (744 s → 427 s on a 295-mod RSS/RP-1 modpack). The errors are still shown while loading, and comments in
  `.cfg` files no longer force a new patch run.
- **Loads that used to get stuck go through**: a file held for a moment by the antivirus, bundle files locked by Windows
  (including on the very first start without KSPCF), incomplete launch pad terrain with Parallax.
- **Compatibility**: an optimization steps back on its own when another mod changes the same part of the game.
- **Intro** in the style of the game's own launch, smooth even when loading is uneven; **new logo** and a settings window
  in the mod's style (the other styles remain available).
- Expansion copies: no temporary file left behind when the game is closed while they are written.
- Dynamic resolution (option): no longer lowers the resolution for no reason on a CPU-bound PC.
- For measuring: AutoBench `menuOnly`, `tools/menu_time.ps1` (time to main menu, with or without Hyperdrive),
  `tools/package.ps1`.

## 0.22.1
- Part values applied faster while loading (reflection lookups cached; identical parts, checked on 1,575 parts). Leaves
  the job to KSPCommunityFixes when it does the same.

## 0.22.0
- DDS textures and cached PNG textures written straight into their memory by worker threads, with no zero fill and no
  copy on the main thread: texture phase 4.0 → 2.4 s on a big modpack, smooth loading screen. Checked byte for byte
  (3,973 / 3,973 identical textures).
- Event and action delegates of parts and modules created without a lookup by name for every part (loading and vessels
  loaded in flight).

## 0.21.x
- Part compilation in a single pass, texture index, Unity loading settings during the first load, automatic and
  self-cleaning PNG cache, bundle definitions read 64 ahead, loading timeline in `KSP.log`.

## 0.19 – 0.20
- Loading screens (random draw, first start in hyperspace), start-up intro, Hyperdrive section in KSP's settings, window
  and toolbar logo, 9 languages.
- Validation on Windows, Parallax compatibility, automatic choice of the better version when another optimization mod
  does the same job (learned per install).

## 0.16 – 0.18
- Part highlighting skipped when nothing is highlighted, HUD texts formatted only when they change.
- Loading freezes covered by the animation, careful start after a crash, game version guard, regression battery.

## 0.9 – 0.15
- PNG texture cache, textures read on several cores, models read ahead, localized texts kept during part compilation,
  editor part search, performance window.
- Expansion bundles opened without freezing the screen, sounds requested ahead, launch pad terrain built once, terrain
  update leak fixed, crashes less frozen, optional FSR 1 upscaling.

## 0.1 – 0.8
- Base: switchable subsystems with full rollback, better tuned garbage collector (fewer stutters), measurements (PerfHUD,
  frame timeline, mod profiler), fast texture loading, animated loading screen.
