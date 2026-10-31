# KSP with Lossless Scaling (Windows)

Hyperdrive cannot generate in-between frames from inside the game: Unity shows a frame only at the end of a whole turn
of the game loop (scripts, physics, rendering). The **Lossless Scaling** app (LSFG) does it at the Windows display level
and works with KSP. Hyperdrive takes care of the rest (freezes, loading, 3D render resolution).

## Recommended settings
1. **KSP in a borderless window**: Steam launch option `-popupwindow` (or windowed fullscreen in the graphics settings),
   resolution = the screen's. Lossless Scaling captures a window, not exclusive fullscreen.
2. **Steady, capped base frame rate**: in KSP, *Settings → Graphics → Max Frame Rate* at **half** the screen's refresh
   rate (72 for 144 Hz, 60 for 120 Hz) with **KSP's VSync off**. Frame generation works much better when the game holds
   its rate; if KSP can't (big craft), lower the cap.
3. **Lossless Scaling**: *Frame Generation* = LSFG 3 (or newer), **X2** mode (X3 only with a high base frame rate);
   *Scaling type* = **Off** (Hyperdrive's Upscaler renders the 3D view smaller and keeps the interface sharp, which
   Lossless Scaling's scaling can't); default *Sync mode*; turn it on with its shortcut (Ctrl+Alt+S by default) in KSP.
4. **Hyperdrive's Upscaler** (optional, only useful when the GPU is the limit): `Hyperdrive.user.cfg` →
   `SUBSYSTEMS { Upscaler = true }` and `Upscaler { dynamic = true }`. On a CPU-bound PC the dynamic resolution notices
   it and stays native.

## Good to know
- Frame generation adds a little latency (one frame): it doesn't matter in KSP.
- Interface text (navball, windows, tooltips) can show slight artefacts while it moves.
- Freezes (crashes, scene changes) stay freezes: Lossless Scaling can't make up frames when the game stops producing
  them. That part is Hyperdrive's job.
