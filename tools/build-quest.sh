#!/usr/bin/env bash
# Build the Meta Quest APK.
#
# Requires Unity 6000.0.81f1 WITH the Android Build Support module (plus OpenJDK
# and Android SDK & NDK Tools). Install it from Unity Hub > Installs > gear icon
# > Add Modules. Without it this script stops with a clear message instead of an
# opaque Unity error.
set -euo pipefail

PROJECT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
UNITY_VERSION="${UNITY_VERSION:-6000.0.81f1}"

find_unity() {
    if [[ -n "${UNITY:-}" ]]; then echo "$UNITY"; return; fi
    local candidates=(
        "$HOME/unity/editor/$UNITY_VERSION/Editor/Unity"
        "$HOME/Unity/Hub/Editor/$UNITY_VERSION/Editor/Unity"
        "/opt/unity/editor/$UNITY_VERSION/Editor/Unity"
        "/Applications/Unity/Hub/Editor/$UNITY_VERSION/Unity.app/Contents/MacOS/Unity"
        "/c/Program Files/Unity/Hub/Editor/$UNITY_VERSION/Editor/Unity.exe"
    )
    for c in "${candidates[@]}"; do
        [[ -x "$c" ]] && { echo "$c"; return; }
    done
    return 1
}

if ! UNITY_BIN="$(find_unity)"; then
    echo "ERROR: Unity $UNITY_VERSION not found." >&2
    echo "Set UNITY=/path/to/Unity (or UNITY_VERSION) and re-run." >&2
    exit 1
fi

export QUEST_BUILD_DIR="${QUEST_BUILD_DIR:-$PROJECT_DIR/Build/Quest}"
export QUEST_DEV_BUILD="${QUEST_DEV_BUILD:-0}"

# Unity's Bee backend spawns a worker per core. On machines with a constrained
# pid limit this deadlocks the linker rather than failing, so cap it.
export BEE_BUILD_THREADS="${BEE_BUILD_THREADS:-4}"

LOG="${LOG:-$PROJECT_DIR/Build/quest-build.log}"
mkdir -p "$(dirname "$LOG")" "$QUEST_BUILD_DIR"

echo "Unity   : $UNITY_BIN"
echo "Project : $PROJECT_DIR"
echo "Output  : $QUEST_BUILD_DIR/HouseScanVR.apk"
echo "Log     : $LOG"
echo "Building (this takes several minutes on a first run)..."

set +e
"$UNITY_BIN" \
    -batchmode -nographics -quit \
    -disable-assembly-updater \
    -projectPath "$PROJECT_DIR" \
    -buildTarget Android \
    -executeMethod HouseScan.EditorTools.QuestBuild.BuildQuestApk \
    -logFile "$LOG"
STATUS=$?
set -e

if [[ $STATUS -eq 2 ]]; then
    echo >&2
    echo "ERROR: Android Build Support is not installed for Unity $UNITY_VERSION." >&2
    echo "Unity Hub > Installs > (gear on $UNITY_VERSION) > Add Modules >" >&2
    echo "  [x] Android Build Support" >&2
    echo "      [x] OpenJDK" >&2
    echo "      [x] Android SDK & NDK Tools" >&2
    exit 2
fi

if [[ $STATUS -ne 0 ]]; then
    echo >&2
    echo "ERROR: build failed (exit $STATUS)." >&2

    # Unity reports almost every build failure as "Scripts have compiler errors",
    # including out-of-memory and process-limit failures that have nothing to do
    # with the code. Surface the real cause first.
    if grep -q "Out of memory" "$LOG"; then
        echo >&2
        echo "CAUSE: a build process ran out of memory." >&2
        echo "  Free some RAM and retry. Unity leaves Roslyn compiler servers" >&2
        echo "  (VBCSCompiler) running between builds; they can hold several GB." >&2
    fi
    if grep -qE "pthread_create failed \(EAGAIN\)|posix_spawn failed|Resource temporarily unavailable" "$LOG"; then
        echo >&2
        echo "CAUSE: the process/thread limit was exhausted." >&2
        echo "  Lower BEE_BUILD_THREADS (try 1), and check your shell's cgroup:" >&2
        echo "    cat /sys/fs/cgroup/\$(cut -d: -f3 /proc/self/cgroup)/pids.{current,max}" >&2
        echo "  Assets/Plugins/Android/gradleTemplate.properties already forces" >&2
        echo "  Gradle to run serially without a daemon." >&2
    fi
    if grep -q "Android JNI" "$LOG"; then
        echo >&2
        echo "CAUSE: the built-in Android JNI module is disabled." >&2
        echo "  Add \"com.unity.modules.androidjni\": \"1.0.0\" to Packages/manifest.json." >&2
    fi

    echo >&2
    echo "Errors from the log:" >&2
    grep -E "error CS|BuildFailedException|CommandInvokationFailure|Out of memory|EAGAIN|posix_spawn|^Error" "$LOG" \
        | sort -u | tail -20 >&2 || true
    echo >&2
    echo "Full log: $LOG" >&2
    exit $STATUS
fi

APK="$QUEST_BUILD_DIR/HouseScanVR.apk"
if [[ ! -f "$APK" ]]; then
    echo "ERROR: Unity reported success but $APK is missing. See $LOG" >&2
    exit 1
fi

grep -E "\[QuestBuild\]" "$LOG" || true
echo
echo "OK: $APK ($(du -h "$APK" | cut -f1))"
echo "Next: tools/install-quest.sh [path/to/scan.ply]"
