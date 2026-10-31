#!/bin/zsh
# Hyperdrive regression battery (macOS), one command, ~8 minutes: builds, then four short AutoBench runs with the
# default settings (first load + flight, 3 scene cycles, crash of the heavy craft, VAB searches), then checks every
# Player.log against tools/regress/baseline_mac.txt (tools/regress/Check): unfinished run, failed subsystem, exception in
# Hyperdrive code, exception not seen before, temperatures moved. Exit code 0 = PASS.
#
# usage: tools/regress.sh [--update]     --update: (re)write the baseline from this run (only on a build known to be good)
# env: REGRESS_OUT (default: $TMPDIR/hyperdrive-regress), plus tools/bench.sh's KSP_ROOT / KSP_PLAYER_LOG
setopt errexit
cd "${0:A:h}/.."
OUT="${REGRESS_OUT:-${TMPDIR:-/tmp}/hyperdrive-regress}"
UPDATE=""; [ "$1" = "--update" ] && UPDATE="--update"
rm -rf "$OUT"; mkdir -p "$OUT"

echo "Building..."
if ! dotnet build Hyperdrive.sln -c Release > "$OUT/build.log" 2>&1; then
  grep -E "error" "$OUT/build.log" | sort -u | head -20; echo "RESULT: FAIL (build)"; exit 1
fi
dotnet build tools/regress/Check/Check.csproj -c Release > "$OUT/check-build.log" 2>&1 || { tail -20 "$OUT/check-build.log"; exit 1; }

unsetopt errexit
for run in load:tools/bench/quick.cfg cycles:tools/regress/cycles3.cfg crash:tools/bench/crash.cfg editor:tools/bench/editor_search.cfg; do
  name="${run%%:*}"; cfg="${run#*:}"
  echo "Run $name ($cfg)..."
  zsh tools/bench.sh "$OUT/$name" 900 tools/regress/defaults.cfg "$cfg" > "$OUT/$name.bench.txt" 2>&1
  tail -1 "$OUT/$name.bench.txt"
done

dotnet tools/regress/Check/bin/Release/net6.0/Check.dll tools/regress/baseline_mac.txt $UPDATE \
  load="$OUT/load" cycles="$OUT/cycles" crash="$OUT/crash" editor="$OUT/editor"
