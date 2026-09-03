#!/usr/bin/env bash
# Install HouseScanVR on a Meta Quest 3 over adb, and optionally push a scan.
#
#   tools/install-quest.sh                     # install the APK only
#   tools/install-quest.sh my-house.ply        # install and copy a scan
#   APK=/path/to/other.apk tools/install-quest.sh
#
# The headset must have Developer Mode enabled (Meta Horizon phone app >
# Devices > your headset > Headset settings > Developer Mode) and must be
# plugged in over USB-C with the "Allow USB debugging" prompt accepted.
set -euo pipefail

PROJECT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PACKAGE="com.gmprakhar.housescanvr"
APK="${APK:-$PROJECT_DIR/Build/Quest/HouseScanVR.apk}"
SCAN="${1:-}"
UNITY_VERSION="${UNITY_VERSION:-6000.0.81f1}"

# Prefer a system adb, but fall back to the one Unity's Android module ships,
# which is present on any machine that can build this APK.
find_adb() {
    if [[ -n "${ADB:-}" ]]; then echo "$ADB"; return 0; fi
    if command -v adb >/dev/null 2>&1; then command -v adb; return 0; fi
    local roots=(
        "${UNITY:+$(dirname "$UNITY")/Data}"
        "$HOME/unity/editor/$UNITY_VERSION/Editor/Data"
        "$HOME/Unity/Hub/Editor/$UNITY_VERSION/Editor/Data"
        "/opt/unity/editor/$UNITY_VERSION/Editor/Data"
        "/Applications/Unity/Hub/Editor/$UNITY_VERSION/Unity.app/Contents"
    )
    for r in "${roots[@]}"; do
        [[ -z "$r" ]] && continue
        local c="$r/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb"
        [[ -x "$c" ]] && { echo "$c"; return 0; }
    done
    return 1
}

if ! ADB_BIN="$(find_adb)"; then
    echo "ERROR: adb not found on PATH." >&2
    echo "Install Android platform-tools:" >&2
    echo "  Linux  : sudo apt install android-sdk-platform-tools" >&2
    echo "  macOS  : brew install --cask android-platform-tools" >&2
    echo "  Windows: https://developer.android.com/tools/releases/platform-tools" >&2
    echo "Unity also ships one at" >&2
    echo "  <Unity>/Editor/Data/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb" >&2
    echo "Set ADB=/path/to/adb to point at it directly." >&2
    exit 1
fi
adb() { "$ADB_BIN" "$@"; }

if [[ ! -f "$APK" ]]; then
    echo "ERROR: APK not found: $APK" >&2
    echo "Build it first with: tools/build-quest.sh" >&2
    exit 1
fi

adb start-server >/dev/null 2>&1 || true

# `adb devices` always exits 0, so the state has to be parsed.
DEVICES="$(adb devices | tail -n +2 | grep -v '^$' || true)"
if [[ -z "$DEVICES" ]]; then
    echo "ERROR: no device detected." >&2
    echo "  1. Connect the Quest 3 by USB-C." >&2
    echo "  2. Put the headset on and accept 'Allow USB debugging'." >&2
    echo "  3. Confirm Developer Mode is on in the Meta Horizon app." >&2
    exit 1
fi

if echo "$DEVICES" | grep -q 'unauthorized'; then
    echo "ERROR: device is unauthorized." >&2
    echo "Put the headset on and accept the 'Allow USB debugging' prompt," >&2
    echo "then re-run. If no prompt appears: adb kill-server && adb start-server" >&2
    exit 1
fi

if ! echo "$DEVICES" | grep -qw 'device'; then
    echo "ERROR: device is not ready:" >&2
    echo "$DEVICES" >&2
    exit 1
fi

COUNT="$(echo "$DEVICES" | grep -cw 'device')"
if [[ "$COUNT" -gt 1 ]]; then
    echo "ERROR: $COUNT devices connected. Set ANDROID_SERIAL=<serial> to choose one:" >&2
    echo "$DEVICES" >&2
    exit 1
fi

MODEL="$(adb shell getprop ro.product.model 2>/dev/null | tr -d '\r')"
echo "Device : ${MODEL:-unknown}"
echo "APK    : $APK ($(du -h "$APK" | cut -f1))"

echo "Installing..."
# -r reinstalls over an existing copy; -g pre-grants runtime permissions.
if ! adb install -r -g "$APK"; then
    echo >&2
    echo "Install failed. If the error mentions INSTALL_FAILED_UPDATE_INCOMPATIBLE," >&2
    echo "the existing build was signed with a different key. Remove it first:" >&2
    echo "  adb uninstall $PACKAGE" >&2
    exit 1
fi

DEST="/sdcard/Android/data/$PACKAGE/files/Scans"
adb shell "mkdir -p '$DEST'" >/dev/null 2>&1 || true

if [[ -n "$SCAN" ]]; then
    if [[ ! -f "$SCAN" ]]; then
        echo "ERROR: scan not found: $SCAN" >&2
        exit 1
    fi
    case "$SCAN" in
        *.ply) ;;
        *) echo "ERROR: expected a .ply file, got: $SCAN" >&2; exit 1 ;;
    esac
    echo "Pushing $(basename "$SCAN") ($(du -h "$SCAN" | cut -f1)) to $DEST ..."
    adb push "$SCAN" "$DEST/"
else
    echo
    echo "No scan given. Copy one in later with:"
    echo "  adb push my-house.ply $DEST/"
    echo "The app loads the first .ply it finds there."
fi

echo "Launching..."
adb shell monkey -p "$PACKAGE" -c android.intent.category.LAUNCHER 1 >/dev/null 2>&1 || \
    echo "Could not auto-launch; start it from Library > Unknown Sources on the headset."

echo
echo "Done. To watch the log:"
echo "  adb logcat -s Unity:V HouseScanLoader:V"
