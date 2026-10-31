# Benchmarks

Machines: a PC (i5-14600KF, 32 GB DDR4-3200, RX 9070 XT, Windows 11, DX11) and a MacBook Air M4 (16 GB, KSP under
Rosetta 2, OpenGL 4.1). KSP 1.12.5 with both expansions.

## Time to main menu, PC (2026-10-31, Hyperdrive 1.0.0)
Measured from outside the game with `tools/menu_time.ps1`: from the process start to `Scene Change : From LOADING to
MAINMENU` in Player.log, the same way with or without Hyperdrive installed.

**Base game + both expansions**, no other mod (KSPCF with ModuleManager 4.2.3 and HarmonyKSP; Hyperdrive with
HarmonyKSP):

| | First start | Next starts | Mean |
|---|---|---|---|
| Base game (after a warm-up start) | — | 47.0 / 45.4 / 45.8 s | 46.1 s |
| KSPCommunityFixes | 44.0 s | 25.4 / 25.1 s | 25.3 s |
| Hyperdrive | 21.4 s | 14.7 / 16.0 / 15.7 s | 15.5 s |
| KSPCommunityFixes + Hyperdrive | 18.2 s | 16.5 / 16.1 s | 16.3 s |

Most of Hyperdrive's gain here is the expansions: an LZ4 copy of their bundles, written once at the main menu and opened
in a fraction of a second at the next starts.

**~135 mods** (Kopernicus, Parallax, Scatterer, EVE, FAR, Waterfall, ReStock, B9PartSwitch, kOS, MechJeb...):

| | First start after a mod change (ModuleManager patches again) | Next start |
|---|---|---|
| No optimization mod | 271.0 s | 162.2 s |
| KSPCommunityFixes | 101.9 s | 95.7 s |
| Hyperdrive | 133.4 s | 88.0 s |
| KSPCommunityFixes + Hyperdrive | 103.4 s | 81.7 s |

One start after the first per configuration: a few seconds of noise are possible.

## Big modpack, PC
RSS/RP-1, 295 mods, with KSPCommunityFixes. One of the modpack's ModuleManager patches has an error, so ModuleManager
writes no cache and patches everything at every start.

| | Time to main menu |
|---|---|
| Without Hyperdrive, every start | 744 s |
| Hyperdrive, first start (cache written) | 768 s |
| Hyperdrive, next starts | 427 s |

Same game: on a test install, the configs read from the cache were compared byte for byte with the patched ones (7,056
configs); the only two differences are ModuleManager's own cache format, the same as with its normal cache.

## Memory, PC
Same scripted flight of a 310-part craft, ~135 mods, with KSPCF: managed heap 5.3 GB without Hyperdrive, 3.0 GB with it.
Frame rate in flight the same (~72 fps): the CPU is the limit there.

## Mac, base game + both expansions
Measured 2026-10-28/29 (Hyperdrive 0.10 to 0.13), mean of the starts after the first.

| | Time to main menu |
|---|---|
| Base game | 69.5 s |
| KSPCommunityFixes + ModuleManager | 36.9 s |
| Hyperdrive | 22.9 s |
| KSPCommunityFixes + Hyperdrive | 22.9 s |

## Other measurements (Mac)
| | Without | With Hyperdrive |
|---|---|---|
| Loading the expansions, with KSPCF | 12.6 s | 0.3 s |
| Longest freeze while the launch sites' terrain is built | 2,248 ms | 85 ms (same terrain) |
| Space center to flight, with KSPCF | 5.7 s | 4.6 s |
| Terrain cost per frame after 12 scene changes, without KSPCF | 9.3 ms | 1.1 ms |
| Launching from the space center after several scene changes, without KSPCF | 5.37 s | 3.50 s |
| Part highlighting at rest, 310 parts (0 pixel changed) | 26.18 ms/frame | 23.60 ms/frame |
| Memory garbage per frame, scripts in flight | 14.67 KB | 10.86 KB |

## Same game
- Textures: every DDS and cached PNG compared byte for byte with the game's (3,973/3,973 identical); 631/631 PNGs.
- Parts: fingerprint of every public field of every part identical to the game (1,575 parts, 327,664 values).
- Physics: final temperatures of a 310-part craft identical (304.284 K without, 304.283 K with, after the same flight).
- Highlighting: 0 pixel changed.

## Regression battery
Before each release: `tools/regress.ps1` (PC) / `tools/regress.sh` (Mac) runs four automatic sessions (first load and
flight, scene cycles, crash of the 310-part craft, editor searches) and checks every log against a baseline: unfinished
run, failed subsystem, exception in Hyperdrive code, new exception, temperatures moved.
