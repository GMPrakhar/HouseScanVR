#!/usr/bin/env bash
# Runs a real round of the game headlessly.
#
# Where nav-probe.sh tests the pathfinding in isolation, this drives the actual
# MonoBehaviour wiring: HouseScanLoader.Load(), the onScanReady handshake,
# GameDirector spawn selection, and the per-frame round loop at 72 Hz. No GPU is
# needed - nothing is rendered.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
UNITY="${UNITY:-$HOME/unity/editor/6000.0.81f1/Editor/Unity}"
SCANS="${GAME_SCAN_DIR:-$HOME/vr-work/scans}"
OUT="${NAV_OUT:-$ROOT/Build/Nav}"

[ -x "$UNITY" ] || { echo "Unity not found at $UNITY (set UNITY=...)" >&2; exit 1; }
for f in house_doors.ply house_sealed.ply; do
  [ -f "$SCANS/$f" ] || { echo "Scan not found: $SCANS/$f" >&2; exit 1; }
done

mkdir -p "$OUT"
LOG="$OUT/game-probe.log"

set +e
env BEE_BUILD_THREADS=1 \
  "$UNITY" \
    -batchmode -nographics \
    -projectPath "$ROOT" \
    -executeMethod HouseScan.EditorTools.GameProbe.Run \
    -scanDir "$SCANS" \
    -logFile "$LOG"
STATUS=$?
set -e

[ -f "$OUT/game_report.txt" ] && { echo; cat "$OUT/game_report.txt"; }

if [ "$STATUS" -ne 0 ]; then
  echo
  echo "Game probe FAILED (exit $STATUS). Full log: $LOG" >&2
  grep -E "error CS|Exception|Out of memory|pthread_create" "$LOG" | head -20 >&2 || true
  exit "$STATUS"
fi

echo
echo "Game probe passed. Report: $OUT/game_report.txt"
