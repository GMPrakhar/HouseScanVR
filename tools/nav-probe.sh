#!/usr/bin/env bash
# Verifies the navigation and AI layer headlessly.
#
# The key test is an A/B between two scans that differ only in whether the rooms
# have doorways, so a pathfinder that ignored walls would fail. No GPU is needed:
# this reasons about the grid derived from the splats, not about pixels.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
UNITY="${UNITY:-$HOME/unity/editor/6000.0.81f1/Editor/Unity}"
DOORS="${NAV_SCAN_DOORS:-$HOME/vr-work/scans/house_doors.ply}"
SEALED="${NAV_SCAN_SEALED:-$HOME/vr-work/scans/house_sealed.ply}"
OUT="${NAV_OUT:-$ROOT/Build/Nav}"

[ -x "$UNITY" ]  || { echo "Unity not found at $UNITY (set UNITY=...)" >&2; exit 1; }
[ -f "$DOORS" ]  || { echo "Scan not found: $DOORS" >&2; exit 1; }
[ -f "$SEALED" ] || { echo "Control scan not found: $SEALED" >&2; exit 1; }

mkdir -p "$OUT"
LOG="$OUT/nav-probe.log"

set +e
env BEE_BUILD_THREADS=1 \
    NAV_SCAN_DOORS="$DOORS" \
    NAV_SCAN_SEALED="$SEALED" \
    NAV_OUT="$OUT" \
  "$UNITY" \
    -batchmode -nographics \
    -projectPath "$ROOT" \
    -executeMethod HouseScan.EditorTools.NavProbe.Run \
    -logFile "$LOG"
STATUS=$?
set -e

if [ -f "$OUT/nav_report.txt" ]; then
  echo
  cat "$OUT/nav_report.txt"
fi

if [ "$STATUS" -ne 0 ]; then
  echo
  echo "Nav probe FAILED (exit $STATUS). Full log: $LOG" >&2
  grep -E "error CS|Exception|Out of memory|pthread_create" "$LOG" | head -20 >&2 || true
  exit "$STATUS"
fi

echo
echo "Nav probe passed. Report: $OUT/nav_report.txt"
