# Developer settings

These settings are not in `Hyperdrive.cfg` (the players' file): their defaults are in the code. To use one, write it in
`GameData/Hyperdrive/PluginData/Hyperdrive.user.cfg`, inside `HYPERDRIVE { ... }`, in the subsystem's section (and turn
the subsystem on in `SUBSYSTEMS` if it is a tool that is off by default).

## "Identical to the game" checks
| Section | Key | Effect |
|---|---|---|
| FastLoader | `ddsVerify = true` | every DDS / cached PNG texture rebuilt the game's way and compared byte for byte (`direct path: ... verify:`) |
| FastLoader | `png = verify` | every PNG rebuilt after the stock load and compared (and every cache file) |
| FastLoader | `ddsReader = unity`, `ddsFrameWait = 2`, `ddsInFlightMB = 384`, `ddsDirect = false` | direct path settings (Unity's reader is slower; 0 frames of wait = Unity copies the buffers) |
| DelegateCache | `verify = true` | every delegate rebuilt by name and compared |
| PartValueCache | `fingerprint = true` (+ `bypass = true` for the stock reference) | fingerprint of every public field of every part at the main menu |
| LaunchTerrain | `fingerprint = true` | terrain fingerprint 6 s into each flight |
| LaunchSitePacing | `fingerprint = false` | turns off the terrain and launch site fingerprint (written by default) |

## Captures and previews
| Section | Key | Effect |
|---|---|---|
| WarpScreen | `previewStyles = true`, `previewSeconds = 9` | changes the animation style every N seconds |
| WarpScreen, SceneLoadScreen | `captureFrames = true` | screenshots of the animation in PluginData |
| WarpScreen | `exportArt = true` | contact sheet of every loading screen image of the game (`PluginData/art_sheet.png`, names in the log) |
| HyperdriveWindow | `captureAtMenu = true` | screenshots of the settings window (in the editor) |
| FastLoader | `pngCachePromptTest = true` | shows the PNG cache question even during a benchmark |
| Upscaler | `bundlePlatform = linux` | loads another platform's shader bundle (test of the Linux bundle on a Mac) |

## Tools (original sections, to copy as they are)
```
	RenderProbe
{
	inventoryDelaySeconds = 12
	logIntervalSeconds = 15
	// Diagnostic windows (shadows off, vessel hidden, cameras off...), each change restored. For benchmarks only.
	experiments = false
	experimentStartSeconds = 20
	experimentWindowSeconds = 10
	// ';'-separated, a baseline window is inserted before each one: shadows:off | cascades:<0|2|4> |
	// shadowres:<Low|Medium|High|VeryHigh> | shadowdist:<meters> | vessel:hidden | vessel:noshadows |
	// camoff:<camera> | camshadows:<camera>:off | camcascades:<camera>:<n> | camdistance:<camera>:<meters>
	experimentList = shadows:off;camshadows:Camera 01:off;camshadows:Camera 00:off;camcascades:Camera 01:2;camdistance:Camera 01:2500;camcascades:Camera 00:2
}

PSystemTrace
{
	// Frames longer than this (ms) are split into the traced steps they contain.
	longFrameMs = 50
	// Lines per ranking.
	reportTop = 40
	// Extra methods to time, one node each, written on several lines (KSP's config parser needs one value per line):
	//   TARGET
	//   {
	//       type = PQSMod_VertexHeightMap
	//       method = OnSetup
	//   }
}

DelegateCache
{
	// Developer check: every cached delegate is also made the stock way and compared (count in KSP.log at the main menu).
	verify = false
}
```
