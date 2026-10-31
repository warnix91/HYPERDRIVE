#!/bin/zsh
# Hyperdrive AutoBench runner (macOS): arms the AutoBench, launches KSP, waits for "AutoBench DONE" in Player.log
# (or KSP exit / timeout), copies the logs and the 10 s screenshot, then disarms.
# KSP may be shared with other projects: it never starts while a KSP is running and only stops the instance it started.
#
# usage: tools/bench.sh <outdir> [timeout-seconds] [user-override.cfg] [autobench.cfg]
#   user-override.cfg  copied to GameData/Hyperdrive/PluginData/Hyperdrive.user.cfg for this run (e.g. SUBSYSTEMS switches)
#   autobench.cfg      AUTOBENCH { craft = ...  flightSeconds = ... }; default: Kerbal X, 120 s, staging every 10 s
# env: KSP_ROOT (default: Steam install), KSP_PLAYER_LOG (default: ~/Library/Logs/Squad/KSP/Player.log)
if [ "$(uname)" = "Linux" ]; then
  K="${KSP_ROOT:-$HOME/.local/share/Steam/steamapps/common/Kerbal Space Program}"
  P="${KSP_PLAYER_LOG:-$HOME/.config/unity3d/Squad/Kerbal Space Program/Player.log}"
else
  K="${KSP_ROOT:-$HOME/Library/Application Support/Steam/steamapps/common/Kerbal Space Program}"
  P="${KSP_PLAYER_LOG:-$HOME/Library/Logs/Squad/KSP/Player.log}"
fi
OUT="$1"; TIMEOUT=${2:-1200}; USERCFG="$3"; BENCHCFG="$4"
[ -z "$OUT" ] && { sed -n '2,9p' "$0"; exit 1; }
mkdir -p "$OUT"
W=0
while pgrep -x KSP >/dev/null || pgrep -f KSP.x86_64 >/dev/null; do
  sleep 10; W=$((W+10))
  if [ $W -gt 1800 ]; then echo "SKIPPED: another KSP is running"; exit 3; fi
done
PD="$K/GameData/Hyperdrive/PluginData"; mkdir -p "$PD"
if [ -n "$BENCHCFG" ]; then cp "$BENCHCFG" "$PD/autobench.cfg"
else printf 'AUTOBENCH\n{\n\tcraft = Kerbal X\n\tflightSeconds = 120\n\tstageIntervalSeconds = 10\n\tpauseAtSeconds = 60\n\tpauseDurationSeconds = 4\n\tquitWhenDone = true\n}\n' > "$PD/autobench.cfg"; fi
# The player's own overrides are set aside for the run (benchmarks use known settings) and put back after.
[ -f "$PD/Hyperdrive.user.cfg" ] && [ ! -f "$PD/Hyperdrive.user.cfg.player" ] && mv "$PD/Hyperdrive.user.cfg" "$PD/Hyperdrive.user.cfg.player"
[ -n "$USERCFG" ] && cp "$USERCFG" "$PD/Hyperdrive.user.cfg"
# A run killed by its timeout would make the next start a prudent one (PrudentStart): benches always start normally.
[ -z "$HD_KEEP_SESSION" ] && rm -f "$PD/Session.txt"
START=$(date +%s)
if [ "$(uname)" = "Linux" ]; then
  (cd "$K" && nohup ./KSP.x86_64 >/dev/null 2>&1 &)
  sleep 5
  KPID=$(pgrep -n -f KSP.x86_64)
else
  open -n "$K/KSP.app"
  sleep 5
  KPID=$(pgrep -n -x KSP)
fi
sleep 15
while true; do
  sleep 5
  ELAPSED=$(( $(date +%s) - START ))
  if grep -q "AutoBench DONE" "$P" 2>/dev/null; then echo "DONE after ${ELAPSED}s"; sleep 8; break; fi
  if ! kill -0 "$KPID" 2>/dev/null; then echo "KSP_EXITED after ${ELAPSED}s"; break; fi
  if [ $ELAPSED -gt $TIMEOUT ]; then echo "TIMEOUT after ${ELAPSED}s"; break; fi
done
kill -TERM "$KPID" 2>/dev/null; sleep 3; kill -KILL "$KPID" 2>/dev/null
cp "$K/KSP.log" "$OUT/KSP.log" 2>/dev/null; cp "$P" "$OUT/Player.log" 2>/dev/null
cp "$PD"/*.png "$OUT"/ 2>/dev/null; rm -f "$PD"/*.png
rm -f "$PD/autobench.cfg" "$PD/Hyperdrive.user.cfg"
[ -f "$PD/Hyperdrive.user.cfg.player" ] && mv "$PD/Hyperdrive.user.cfg.player" "$PD/Hyperdrive.user.cfg"
rmdir "$PD" 2>/dev/null
grep -aE "AutoBench REPORT|===== LoadReport|By category|FastLoader: [0-9]" "$OUT/Player.log" | cut -c1-240
