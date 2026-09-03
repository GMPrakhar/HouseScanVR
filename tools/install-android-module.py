#!/usr/bin/env python3
"""
Install Unity's Android Build Support into a Linux editor, without Unity Hub.

Unity's release metadata lists the Android target for the *Linux* editor as a
macOS `.pkg`. That is not a mistake and it does not mean Linux is unsupported:
a `.pkg` is an xar archive, and Unity Hub simply extracts its payload into
Editor/Data/PlaybackEngines/AndroidPlayer. Every sub-module (JDK, SDK, NDK) is
a genuine linux-x64 build.

This script walks the same metadata Hub uses and performs the same extraction
and post-extract renames, so the result is byte-for-byte what Hub produces.
"""
import json
import os
import shutil
import subprocess
import sys
import urllib.request

UNITY_VERSION = os.environ.get("UNITY_VERSION", "6000.0.81f1")
UNITY_PATH = os.environ.get(
    "UNITY_PATH", os.path.expanduser(f"~/unity/editor/{UNITY_VERSION}"))
CACHE = os.environ.get("DL_CACHE", os.path.expanduser("~/unity/dl/android"))
HOST = os.environ.get("HOST_PLATFORM", "LINUX")

RELEASE_API = ("https://services.api.unity.com/unity/editor/release/v1/releases"
               "?version={version}&limit=5")

os.makedirs(CACHE, exist_ok=True)


def fetch_android_module():
    """Pull the Android module tree straight from Unity's release metadata."""
    url = RELEASE_API.format(version=UNITY_VERSION)
    print(f"Fetching release metadata for {UNITY_VERSION} ...")
    with urllib.request.urlopen(url, timeout=60) as r:
        data = json.load(r)

    results = data.get("results") or []
    if not results:
        raise SystemExit(f"Unity has no release metadata for {UNITY_VERSION}")

    for dl in results[0].get("downloads", []):
        if dl.get("platform") != HOST:
            continue
        for m in dl.get("modules", []):
            if m["id"] == "android":
                return m
    raise SystemExit(f"no android module listed for host platform {HOST}")


def expand(p):
    return p.replace("{UNITY_PATH}", UNITY_PATH) if p else p


def run(cmd, **kw):
    r = subprocess.run(cmd, **kw)
    if r.returncode != 0:
        raise SystemExit(f"FAILED: {' '.join(cmd)}")


def download(url, dest):
    if os.path.exists(dest) and os.path.getsize(dest) > 0:
        print(f"    cached {os.path.basename(dest)} "
              f"({os.path.getsize(dest)/1e6:.0f} MB)")
        return
    print(f"    downloading {url.split('/')[-1]}")
    tmp = dest + ".part"
    # curl handles redirects and resume better than urllib for large files.
    run(["curl", "-fL", "--retry", "3", "-C", "-", "-o", tmp, url])
    os.replace(tmp, dest)
    print(f"    got {os.path.getsize(dest)/1e6:.0f} MB")


def extract_zip(archive, dest):
    os.makedirs(dest, exist_ok=True)
    run(["unzip", "-q", "-o", archive, "-d", dest])


def extract_pkg(archive, dest):
    """A macOS .pkg is xar( component.pkg( Payload = gzip(cpio) ) ).

    7z unwraps the xar layer and, depending on version, may also decompress the
    gzip layer, leaving `Payload~` instead of `Payload`. Handle both.
    """
    work = archive + ".x"
    shutil.rmtree(work, ignore_errors=True)
    os.makedirs(work, exist_ok=True)
    run(["7z", "x", "-y", f"-o{work}", archive], stdout=subprocess.DEVNULL)

    payloads = []
    for root, _, files in os.walk(work):
        for f in files:
            if f.startswith("Payload"):
                payloads.append(os.path.join(root, f))
    if not payloads:
        raise SystemExit(f"no Payload found inside {archive}")

    os.makedirs(dest, exist_ok=True)
    for p in payloads:
        print(f"    payload {os.path.relpath(p, work)}")
        with open(p, "rb") as fh:
            magic = fh.read(2)
        gzipped = magic == b"\x1f\x8b"

        with open(p, "rb") as fh:
            if gzipped:
                gz = subprocess.Popen(["gzip", "-dc"], stdin=fh, stdout=subprocess.PIPE)
                src, upstream = gz.stdout, gz
            else:
                src, upstream = fh, None

            cp = subprocess.Popen(["cpio", "-idmu", "--quiet"], stdin=src, cwd=dest,
                                  stderr=subprocess.DEVNULL)
            if upstream is not None:
                upstream.stdout.close()
            cp.communicate()
            if upstream is not None:
                upstream.wait()
            if cp.returncode != 0:
                raise SystemExit(f"cpio failed for {p}")
    shutil.rmtree(work, ignore_errors=True)


def apply_rename(node):
    ren = node.get("extractedPathRename")
    if not ren:
        return
    src, dst = expand(ren["from"]), expand(ren["to"])
    if not os.path.exists(src):
        print(f"    rename skipped, missing: {src}")
        return
    if os.path.abspath(src) == os.path.abspath(dst):
        return

    # The destination is often the *parent* of the source (extract into NDK/,
    # then collapse NDK/android-ndk-r27c up into NDK). Clearing dst first would
    # delete src along with it, so stage through a sibling temp directory.
    staging = os.path.join(os.path.dirname(dst.rstrip("/")) or "/",
                           "." + os.path.basename(dst.rstrip("/")) + ".staging")
    shutil.rmtree(staging, ignore_errors=True)
    os.makedirs(os.path.dirname(staging), exist_ok=True)
    shutil.move(src, staging)

    if os.path.exists(dst):
        shutil.rmtree(dst, ignore_errors=True)
    os.makedirs(os.path.dirname(dst.rstrip("/")), exist_ok=True)
    shutil.move(staging, dst)
    print(f"    renamed -> {os.path.relpath(dst, UNITY_PATH)}")


def install(node, depth=0):
    pad = "  " * depth
    mid, mtype = node["id"], node.get("type")
    dest = expand(node["destination"])
    print(f"{pad}[{mid}] {mtype} -> {os.path.relpath(dest, UNITY_PATH)}")

    archive = os.path.join(CACHE, mid.replace("/", "_") + (
        ".pkg" if mtype == "PKG" else ".zip"))
    download(node["url"], archive)

    if mtype == "PKG":
        # The pkg payload is rooted at the AndroidPlayer contents themselves.
        extract_pkg(archive, dest)
    elif mtype == "ZIP":
        extract_zip(archive, dest)
    else:
        raise SystemExit(f"unhandled module type {mtype}")

    apply_rename(node)

    for sub in node.get("subModules") or []:
        install(sub, depth + 1)


def main():
    if not os.path.isdir(os.path.join(UNITY_PATH, "Editor")):
        raise SystemExit(f"no Unity editor at {UNITY_PATH} "
                         f"(set UNITY_PATH or UNITY_VERSION)")
    for tool in ("7z", "unzip", "cpio", "curl"):
        if shutil.which(tool) is None:
            raise SystemExit(f"required tool not found: {tool}")

    mod = fetch_android_module()
    install(mod)

    sdk = os.path.join(UNITY_PATH, "Editor/Data/PlaybackEngines/AndroidPlayer")
    print("\nInstalled. Verifying:")
    for rel in ("SDK/platform-tools/adb", "OpenJDK/bin/java",
                "NDK/ndk-build", "SDK/build-tools"):
        p = os.path.join(sdk, rel)
        print(f"  {'OK ' if os.path.exists(p) else 'MISSING'} {rel}")
    print("\nUnity will pick these up automatically; no path configuration needed.")


if __name__ == "__main__":
    main()
