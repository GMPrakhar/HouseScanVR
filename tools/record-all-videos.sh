#!/usr/bin/env bash
# Records one short video per feature, so the page keeps a running record
# rather than a single snapshot that goes stale.
#
# Add a shot here whenever a feature lands. Each shot is a case in
# FlythroughRecorder and asserts something about its own footage, so a clip
# that renders nothing fails the run instead of quietly shipping.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SHOTS="${*:-tour hunt map}"

for shot in $SHOTS; do
  echo "=== $shot ==="
  FLY_SHOT="$shot" "$ROOT/tools/record-flythrough.sh"
  echo
done

echo "Videos in $ROOT/Build/Media:"
ls -lh "$ROOT/Build/Media"/*.mp4
