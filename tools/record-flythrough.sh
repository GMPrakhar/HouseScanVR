#!/usr/bin/env bash
# Records a flythrough of a scan and encodes it to MP4.
#
# The capture runs the real GaussianSplatRenderer under Xvfb with Vulkan, so
# the footage comes from the same renderer the Quest build ships -- but it is
# desktop, mono footage and says nothing about headset frame rate.
#
# Usage: tools/record-flythrough.sh [scan.ply] [output.mp4]
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
UNITY="${UNITY:-$HOME/unity/editor/6000.0.81f1/Editor/Unity}"
SCAN="${1:-$HOME/vr-work/scans/house_doors.ply}"
CUTAWAY="${FLY_SCAN_CUTAWAY:-$HOME/vr-work/scans/house_cutaway.ply}"
OUT_MP4="${2:-$ROOT/Build/Media/housescan-flythrough.mp4}"

FRAMES_DIR="${FLY_OUT:-/tmp/housescan-flythrough}"
FPS="${FLY_FPS:-30}"

[ -x "$UNITY" ] || { echo "Unity not found at $UNITY (set UNITY=...)" >&2; exit 1; }
[ -f "$SCAN" ]  || { echo "Scan not found: $SCAN" >&2; exit 1; }
command -v ffmpeg >/dev/null || { echo "ffmpeg not installed" >&2; exit 1; }

rm -rf "$FRAMES_DIR"
mkdir -p "$FRAMES_DIR" "$(dirname "$OUT_MP4")"

LOG="$ROOT/Build/Media/flythrough.log"
echo "Rendering frames (this takes a few minutes)..."

# Xvfb + Vulkan: the splat renderer needs real compute, which the null device
# used by plain -batchmode does not provide.
xvfb-run -a --server-args="-screen 0 1920x1080x24" \
  env BEE_BUILD_THREADS=1 \
      FLY_SCAN="$SCAN" \
      FLY_SCAN_CUTAWAY="$CUTAWAY" \
      FLY_OUT="$FRAMES_DIR" \
      FLY_FPS="$FPS" \
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

# Poster frame for the web page, taken from partway through the orbit.
POSTER="${OUT_MP4%.mp4}.jpg"
ffmpeg -y -loglevel error -i "$OUT_MP4" -ss 2 -frames:v 1 -q:v 3 "$POSTER"

echo
echo "Video:  $OUT_MP4"
echo "Poster: $POSTER"
ffprobe -v error -show_entries format=duration,size \
        -show_entries stream=codec_name,width,height,avg_frame_rate \
        -of default=noprint_wrappers=1 "$OUT_MP4"
