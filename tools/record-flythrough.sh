#!/usr/bin/env bash
# Records a flythrough of a scan and encodes it to MP4.
#
# The capture runs the real GaussianSplatRenderer under Xvfb with Vulkan, so
# the footage comes from the same renderer the Quest build ships -- but it is
# desktop, mono footage and says nothing about headset frame rate.
#
# Each feature gets its own short video, selected by FLY_SHOT:
#   tour   cutaway orbit + eye-height walkthrough of the scan  (default)
#   hunt   a real round: hunters spawn, path and chase, seen from overhead
#   map    mapping a house by walking it, then playing the map that results
#
# Usage: tools/record-flythrough.sh [scan.ply] [output.mp4]
#        FLY_SHOT=hunt tools/record-flythrough.sh
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
UNITY="${UNITY:-$HOME/unity/editor/6000.0.81f1/Editor/Unity}"
SCAN="${1:-$HOME/vr-work/scans/house_doors.ply}"
CUTAWAY="${FLY_SCAN_CUTAWAY:-$HOME/vr-work/scans/house_cutaway.ply}"
SHOT="${FLY_SHOT:-tour}"
OUT_MP4="${2:-$ROOT/Build/Media/housescan-$SHOT.mp4}"

FRAMES_DIR="${FLY_OUT:-/tmp/housescan-$SHOT}"
FPS="${FLY_FPS:-30}"

[ -x "$UNITY" ] || { echo "Unity not found at $UNITY (set UNITY=...)" >&2; exit 1; }
[ -f "$SCAN" ]  || { echo "Scan not found: $SCAN" >&2; exit 1; }
command -v ffmpeg >/dev/null || { echo "ffmpeg not installed" >&2; exit 1; }

rm -rf "$FRAMES_DIR"
mkdir -p "$FRAMES_DIR" "$(dirname "$OUT_MP4")"

LOG="$ROOT/Build/Media/$SHOT.log"
echo "Rendering '$SHOT' frames (this takes a few minutes)..."

# Xvfb + Vulkan: the splat renderer needs real compute, which the null device
# used by plain -batchmode does not provide.
xvfb-run -a --server-args="-screen 0 1920x1080x24" \
  env BEE_BUILD_THREADS=1 \
      FLY_SCAN="$SCAN" \
      FLY_SCAN_CUTAWAY="$CUTAWAY" \
      FLY_OUT="$FRAMES_DIR" \
      FLY_FPS="$FPS" \
      FLY_SHOT="$SHOT" \
  "$UNITY" \
    -batchmode -nographics=false -force-vulkan \
    -projectPath "$ROOT" \
    -executeMethod HouseScan.EditorTools.FlythroughRecorder.Run \
    -logFile "$LOG" \
  || { echo "Unity exited non-zero; see $LOG" >&2; tail -40 "$LOG" >&2; exit 1; }

COUNT=$(find "$FRAMES_DIR" -name 'frame_*.png' | wc -l)
if [ "$COUNT" -eq 0 ]; then
  echo "No frames were produced; see $LOG" >&2
  exit 1
fi
echo "Captured $COUNT frames."
cat "$FRAMES_DIR/flythrough.txt" 2>/dev/null || true

ffmpeg -y -loglevel error \
  -framerate "$FPS" -i "$FRAMES_DIR/frame_%05d.png" \
  -c:v libx264 -pix_fmt yuv420p -crf 20 -preset slow \
  -movflags +faststart \
  "$OUT_MP4"

# Poster frame for the web page. The hunt shot needs a later frame, since the
# trails that make it legible have not been drawn yet at the start.
POSTER="${OUT_MP4%.mp4}.jpg"
POSTER_AT=2
[ "$SHOT" = "hunt" ] && POSTER_AT=9
# The map shot starts on bare floor, so its poster has to come from late on,
# once enough of the house has been walked to be recognisable.
[ "$SHOT" = "map" ] && POSTER_AT=12
ffmpeg -y -loglevel error -i "$OUT_MP4" -ss "$POSTER_AT" -frames:v 1 -q:v 3 "$POSTER"

echo
echo "Video:  $OUT_MP4"
echo "Poster: $POSTER"
ffprobe -v error -show_entries format=duration,size \
        -show_entries stream=codec_name,width,height,avg_frame_rate \
        -of default=noprint_wrappers=1 "$OUT_MP4"
