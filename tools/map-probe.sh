#!/usr/bin/env bash
# Verifies the in-app "map your house by walking" feature without a headset.
#
# A simulated player walks a route through the synthetic house; the poses they
# would generate are fed to RoomMapper, and the resulting map is compared
# against the splat scan of the same house, which acts as independent ground
# truth. Also runs a full round of the game on the mapped level, to prove a
# walked map is playable and not just plausible-looking.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
UNITY="${UNITY:-$HOME/unity/editor/6000.0.81f1/Editor/Unity}"
SCANS="${GAME_SCAN_DIR:-$HOME/vr-work/scans}"
OUT="${NAV_OUT:-$ROOT/Build/Nav}"

[ -x "$UNITY" ] || { echo "Unity not found at $UNITY (set UNITY=...)" >&2; exit 1; }
[ -f "$SCANS/house_doors.ply" ] || { echo "Scan not found: $SCANS/house_doors.ply" >&2; exit 1; }

mkdir -p "$OUT"
LOG="$OUT/map-probe.log"

set +e
env BEE_BUILD_THREADS=1 \
  "$UNITY" \
    -batchmode -nographics \
    -projectPath "$ROOT" \
    -executeMethod HouseScan.EditorTools.MapProbe.Run \
    -scanDir "$SCANS" \
    -logFile "$LOG"
STATUS=$?
set -e

[ -f "$OUT/map_report.txt" ] && { echo; cat "$OUT/map_report.txt"; }

if [ "$STATUS" -ne 0 ]; then
  echo
  echo "Map probe FAILED (exit $STATUS). Full log: $LOG" >&2
  grep -E "error CS|Exception|Out of memory|pthread_create" "$LOG" | head -20 >&2 || true
  exit "$STATUS"
fi

echo
echo "Map probe passed. Report: $OUT/map_report.txt  Map: $OUT/map_walked.png"
