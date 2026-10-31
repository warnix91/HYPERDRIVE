#!/bin/zsh
# Face-to-face runs of a job done both by Hyperdrive and KSPCommunityFixes (needs KSPCF installed): the same short
# AutoBench run with Compatibility PREFER <job> = ours, then = theirs, and the numbers side by side. Use it for jobs
# without a neutral measure in play (FasterEditorPartList) or to check what an install learned.
#
# usage: tools/overlaps.sh <job> [runs per side, default 1]
#   jobs: FlightIntegratorPerf, OnDemandPartBuoyancy (heavy craft, 40 s on the pad), FasterEditorPartList (VAB searches),
#         SceneLoadSpeedBoost (first load + flight: game load time)
cd "${0:A:h}/.."
JOB="$1"; RUNS=${2:-1}
[ -z "$JOB" ] && { sed -n '2,9p' "$0"; exit 1; }
OUT="${TMPDIR:-/tmp}/hyperdrive-overlaps/$JOB"; rm -rf "$OUT"; mkdir -p "$OUT"
case "$JOB" in
  FasterEditorPartList) BENCH=tools/bench/editor_search.cfg; PATTERN="AutoBench EDITOR|AutoBench DONE" ;;
  SceneLoadSpeedBoost) BENCH=tools/bench/quick.cfg; PATTERN="AutoBench: scene SPACECENTER ready|Overlaps:" ;;
  *) BENCH=tools/regress/flight40.cfg; PATTERN="AutoBench REPORT|Timeline ms/frame" ;;
esac
for i in $(seq 1 $RUNS); do
  for side in ours theirs; do
    printf 'HYPERDRIVE\n{\n\tCompatibility\n\t{\n\t\tlearnOverlaps = false\n\t\tPREFER\n\t\t{\n\t\t\t%s = %s\n\t\t}\n\t}\n}\n' "$JOB" "$side" > "$OUT/$side.cfg"
    zsh tools/bench.sh "$OUT/$side$i" 900 "$OUT/$side.cfg" "$BENCH" > /dev/null 2>&1
    echo "== $JOB = $side (run $i)"
    grep -aE "$PATTERN" "$OUT/$side$i/Player.log" | grep -av "Filename" | cut -c1-200
  done
done
