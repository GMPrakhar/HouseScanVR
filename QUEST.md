# Installing HouseScanVR on a Meta Quest 3

Two scripts do the work:

```bash
tools/build-quest.sh                        # produces Build/Quest/HouseScanVR.apk
tools/install-quest.sh path/to/my-house.ply # installs it and copies a scan in
```

Everything below explains the prerequisites and what to do when a step fails.

> **Not verified on hardware.** The APK could not be built or run in the
> environment this project was developed in (no Android module, no adb, no
> headset). The desktop rendering, level analysis and stereo rendering *are*
> verified — see [README.md](README.md). Treat the Quest path as reviewed and
> configured, but unproven, and expect to iterate on frame rate.

---

## 1. One-time setup

### Enable Developer Mode on the headset

1. Install the **Meta Horizon** app on your phone and sign in with the same
   account as the headset.
2. Create an organisation at <https://developers.meta.com/horizon/manage/> if
   you have never done so. Meta requires this before Developer Mode appears.
3. In the phone app: **Devices → your Quest 3 → Headset settings →
   Developer Mode → On**.
4. Reboot the headset.

### Install adb

`adb` talks to the headset over USB.

| Platform | Command |
|---|---|
| Linux | `sudo apt install android-sdk-platform-tools` |
| macOS | `brew install --cask android-platform-tools` |
| Windows | [platform-tools zip](https://developer.android.com/tools/releases/platform-tools), add to `PATH` |

Unity also ships one at
`<Unity>/Editor/Data/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb`.

### Install Unity's Android Build Support

The APK cannot be built without it. In **Unity Hub → Installs**, click the gear
on **6000.0.81f1 → Add Modules**, and tick:

- [x] Android Build Support
  - [x] OpenJDK
  - [x] Android SDK & NDK Tools

`build-quest.sh` exits with code `2` and this exact remedy if the module is
missing, so it is safe to just run it and find out.

---

## 2. Build the APK

```bash
cd HouseScanVR
tools/build-quest.sh
```

Useful environment variables:

| Variable | Purpose |
|---|---|
| `UNITY` | Full path to the Unity binary, if auto-detection fails |
| `UNITY_VERSION` | Editor version to look for (default `6000.0.81f1`) |
| `QUEST_BUILD_DIR` | Output directory (default `Build/Quest`) |
| `QUEST_DEV_BUILD=1` | Development build with the profiler and debugging enabled |
| `BEE_BUILD_THREADS` | Cap Unity's build workers on pid-constrained machines |

The first build takes several minutes because IL2CPP compiles the whole engine.
On failure the script prints the compiler errors from the log rather than making
you dig through it.

### What the build configures

`Assets/Editor/QuestBuild.cs` applies all of this, so nothing needs setting by
hand in the Editor:

- IL2CPP, ARM64, minimum SDK 32 (Quest 3 runs Android 12L)
- **Vulkan only** — the splat renderer is compute-based and has no GLES path
- **Linear colour space** (project-wide, not per-platform)
- OpenXR with the Meta Quest feature and the Touch / Touch Plus controller profiles
- Application ID `com.gmprakhar.housescanvr`

> **Render mode must stay MultiPass.** Unity's OpenXR defaults to
> `SinglePassInstanced`. The Gaussian splat renderer only consults
> `XRSettings.eyeTextureWidth` and has no instancing-aware path, so single-pass
> renders incorrectly. `QuestBuild` forces `MultiPass`; don't change it back
> unless the renderer gains instancing support.

---

## 3. Install and run

Connect the headset by USB-C, put it on, and accept **Allow USB debugging**.

```bash
tools/install-quest.sh ~/scans/my-house.ply
```

This verifies the device is present and authorised, installs with `-r` (upgrade
in place) and `-g` (pre-grant permissions), pushes the scan, and launches the app.

Run it without an argument to install the APK only. To add a scan later:

```bash
adb push my-house.ply /sdcard/Android/data/com.gmprakhar.housescanvr/files/Scans/
```

The app loads the first `.ply` it finds in that folder, so the file name does not
matter.

If it does not auto-launch, start it from **Library → Unknown Sources** in the
headset.

### Watch the logs

```bash
adb logcat -s Unity:V HouseScanLoader:V
```

---

## 4. Getting a scan

Capture your house with any Gaussian-splat scanner that exports `.ply`:

- **Polycam** (Gaussian splatting mode) — export `.ply`
- **Scaniverse** — free splat export
- **Luma AI** — export `.ply`

Walk the whole house slowly, keeping surfaces overlapping between frames, and
capture each room from several heights. The loader takes the raw export; no
editor import step is needed.

If a scan comes in rotated (floor on a wall), set `m_Convention` on the
`HouseScanLoader` component to `ThreeDGS_YDown`.

---

## 5. Performance

The Quest 3's GPU sorts far fewer Gaussians per frame than a desktop card, and
there are two eyes to sort for. `HouseScanLoader` therefore caps loading at
**400,000 splats on mobile** (`m_MaxSplatsMobile`); desktop is uncapped. A
722,786-splat whole-house scan loads and renders fine on a GTX 1650 at
0.5–3.3 ms/frame, but is very unlikely to hold 72 fps on-device.

Tuning:

- `m_MaxSplats` — explicit cap, overrides the mobile default on every platform.
- `m_MaxSplatsMobile` — the mobile default. Lower it to 250k if frame rate is
  poor, raise it if there is headroom.

Memory is not the binding constraint: 722k splats is ~163 MB of GPU payload
(236 B/splat) against 8 GB of shared memory. **Frame time is.** Measure with
`adb logcat` or OVR Metrics Tool before assuming a cap is needed.

---

## Troubleshooting

| Symptom | Cause and fix |
|---|---|
| `no device detected` | Headset asleep, cable is charge-only, or Developer Mode off. Put the headset on and re-check. |
| `device is unauthorized` | The USB debugging prompt was not accepted. Accept it in-headset, or `adb kill-server && adb start-server`. |
| `INSTALL_FAILED_UPDATE_INCOMPATIBLE` | A build with a different signing key is installed. `adb uninstall com.gmprakhar.housescanvr`, then reinstall. |
| App opens to a black void | No scan in the Scans folder. Check `adb shell ls /sdcard/Android/data/com.gmprakhar.housescanvr/files/Scans`. |
| Scan appears sideways / floor on a wall | Wrong source convention — set `m_Convention` to `ThreeDGS_YDown`. |
| Doubled or misaligned eyes | Render mode reverted to single-pass. Re-run `QuestBuild.ConfigureQuest`. |
| Low frame rate | Lower `m_MaxSplatsMobile`. |
| Build exits 2 | Android Build Support not installed — see step 1. |
