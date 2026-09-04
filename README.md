# HouseScanVR

A VR game base built on **your own house**. You scan your home with a phone, the
scan loads at runtime as a Gaussian splat cloud, and the game turns it into a
playable level — floor height, walkable area and spawn points are all derived
from the scan itself.

This repository is the verified technical foundation for that idea, not the game.
What exists today is the hard part: a runtime scan-ingestion and level-analysis
pipeline that is proven to work end to end, with an automated GPU test that
asserts it.

## Why this shape

Everyone in 2026 shipped *capture* — Meta Hyperscape, Polycam, Scaniverse — but
nobody shipped a *game that treats your scanned space as the level*. The
`aras-p/UnityGaussianSplatting` package renders splats beautifully but only
imports them **in the editor**, which is useless if the player supplies the scan.
The work here is the runtime path that closes that gap.

**To put this on a Quest 3, see [QUEST.md](QUEST.md).**

## What is verified

Everything below is asserted by `RenderProbe` on every run, against a synthetic
house whose ground-truth colours and dimensions are known exactly.

| Property | Small scan | Whole house |
| --- | --- | --- |
| Splats | 115,762 | 722,786 |
| Runtime load | 0.68 s | 2.7–3.3 s |
| GPU payload | 26 MB | 163 MB |
| Frame time (1024², GTX 1650) | 0.7–1.4 ms | 0.5–3.3 ms |
| Walkable area found | 53.3 m² | 53.3 m² |

The two scans differ in density by 6× but produce **identical** level analysis,
which is the property that matters: level generation depends on the room, not on
how good the capture was.

The probe asserts, and will fail the build on:

- **Colour correctness** — four viewpoints aimed at known surfaces must resolve to
  the *nearest* entry in the ground-truth palette, within 0.12 chroma distance.
  Currently the worst is 0.015. A wrong-but-plausible colour cannot pass.
- **Coverage** — at least 50% of each frame lit, which catches the black-screen
  failure mode.
- **Stereo** — the two eyes must agree on colour (Δ 0.0014) yet differ across a
  meaningful fraction of pixels (59%), proving eye position genuinely drives the
  render rather than being ignored.
  This runs a second time for real, through an actual XR provider — see below.
- **Containment** — the player rig is walked 20 m in four directions from every
  spawn point: 48/48 runs must be stopped by geometry, with zero steps off
  captured floor and zero escapes from the scan bounds.

Latest reports: `/home/prak/vr-work/probe/report.txt` and `probe_full/report.txt`,
with rendered PNGs alongside them.

## Verified stereo, without a headset

Stereo is **not** only simulated by moving a camera. A Windows player is built
with Unity's Mock HMD XR provider and run under Wine, so the real XR display
subsystem is exercised: the provider allocates eye textures, drives the camera,
and each eye is read back with `ScreenCapture.StereoScreenCaptureMode`.

Measured (`/home/prak/vr-work/stereo/stereo_report.txt`):

```
device=NVIDIA GeForce GTX 1650      api=Vulkan (via winevulkan)
xr.active_loader=MockHMDLoader      xr.enabled=True
xr.device=Mock HMD Display          xr.stereo_mode=MultiPass
xr.eye_width=1512                   xr.eye_height=1680
splats=115762                       load_ms=398
cam.stereo_enabled=True
left.coverage=0.690                 right.coverage=0.679
stereo.eye_chroma_delta=0.0069      stereo.parallax_pixel_fraction=0.2531
RESULT=PASS
```

The eye images in that folder show the expected circular lens viewport, and their
difference map shows structured, depth-dependent disparity rather than noise.

Build and run it with:

```bash
cd /home/prak/Projects/HouseScanVR
BEE_BUILD_THREADS=2 /home/prak/unity/editor/6000.0.81f1/Editor/Unity \
  -batchmode -nographics -quit -disable-assembly-updater -projectPath . \
  -executeMethod HouseScan.EditorTools.XrSetup.BuildStereoPlayer \
  -logFile /home/prak/vr-work/winbuild.log

cd /home/prak/vr-work/stereo-build
WINEDEBUG=-all xvfb-run -a --server-args="-screen 0 1280x1024x24" \
  wine ./HouseScanVR.exe \
    -scan 'Z:\home\prak\vr-work\scans\house_small.ply' \
    -report 'Z:\home\prak\vr-work\stereo\stereo_report.txt' \
    -logFile 'Z:\home\prak\vr-work\stereo\player.log'
```

This still says nothing about **performance** on a headset — only that the
rendering is correct in stereo.

## Layout

```
Assets/Scripts/HouseScanLoader.cs      Player-facing "drop your scan here" entry point
Assets/Scripts/ScanLevelAnalyzer.cs    Scan -> floor, occupancy grid, spawn points
Assets/Scripts/ScanPlayerRig.cs        VR/desktop rig constrained to captured floor
Assets/Scripts/ScanNavGrid.cs          Clearance, connected regions, A*, line of sight
Assets/Scripts/HunterBrain.cs          Patrol/chase/search AI, no Unity dependencies
Assets/Scripts/GameDirector.cs         Round loop and hunter spawning
Assets/Editor/ProjectSetup.cs          Headless URP + scene construction
Assets/Editor/RenderProbe.cs           The GPU test harness described above
Assets/Editor/NavProbe.cs              Navigation and AI assertions (no GPU needed)
Assets/Editor/GameProbe.cs             Drives a real round headlessly at 72 Hz
Assets/Editor/XrSetup.cs               Mock HMD registration + Windows stereo player
Assets/Editor/QuestBuild.cs            Quest/OpenXR configuration and APK build
Assets/Scripts/StereoProbe.cs          Runtime stereo assertions inside a built player
tools/install-android-module.py        Install Unity's Android module without Hub
tools/build-quest.sh                   Build the Quest APK
tools/install-quest.sh                 Sideload the APK and a scan onto a Quest 3

Packages/org.nesnausk.gaussian-splatting/   Vendored MIT package (upstream 2c6fed3)
  Runtime/GaussianPlyRuntimeReader.cs       Added: runtime .ply parsing
  Runtime/GaussianSplatRuntimeBuilder.cs    Added: in-memory splats -> GPU asset
  Runtime/GaussianSplatAsset.cs             Modified: assets without editor blobs

tools/nav-probe.sh                     Run the navigation assertions
tools/game-probe.sh                    Run a full round headlessly
tools/record-flythrough.sh             Render the flythrough video
tools/make_house_splat.py              Ground-truth scan generator
```

### Changes to the vendored package

Upstream builds `GaussianSplatAsset` from `TextAsset` blobs written by the editor
importer. `GaussianSplatAsset` was extended with a parallel set of runtime
`NativeArray` payloads and accessors (`GetPosData<T>()` and friends), and
`GaussianSplatRenderer` now goes through those accessors, so an asset can be
built entirely in memory. `GaussianSplatURPFeature` was made public so the render
feature can be added headlessly.

Keep these changes isolated to make rebasing on upstream tractable.

## Running it

Scans are read from `Application.persistentDataPath/Scans`. Drop a 3DGS `.ply`
there and set `HouseScanLoader.m_ScanPath`.

Build the scene:

```bash
cd /home/prak/Projects/HouseScanVR
BEE_BUILD_THREADS=2 /home/prak/unity/editor/6000.0.81f1/Editor/Unity \
  -batchmode -nographics -quit -disable-assembly-updater -projectPath . \
  -executeMethod HouseScan.EditorTools.ProjectSetup.SetupAll \
  -logFile /home/prak/vr-work/setup.log
```

Run the verification probe:

```bash
cd /home/prak/Projects/HouseScanVR
BEE_BUILD_THREADS=2 \
PROBE_SCAN=/home/prak/vr-work/scans/house_small.ply \
PROBE_OUT=/home/prak/vr-work/probe \
xvfb-run -a --server-args="-screen 0 1280x1024x24" \
  /home/prak/unity/editor/6000.0.81f1/Editor/Unity \
  -batchmode -force-vulkan -disable-assembly-updater -projectPath . \
  -executeMethod HouseScan.EditorTools.RenderProbe.Run \
  -logFile /home/prak/vr-work/probe.log
```

It exits 0 on pass, 1 on failure, so it can gate CI directly.

Regenerate the test scans:

```bash
python3 tools/make_house_splat.py \
  --out scans/house_full.ply --density 2500
```

`PALETTE` in that script and `kPaletteSrgb` in `RenderProbe.cs` are ground truth
for each other and must be kept in sync.

## Environment gotchas

These cost real time to diagnose; do not rediscover them.

- **`-force-vulkan` is mandatory.** Under Xvfb alone Unity picks llvmpipe, whose
  compute path cannot run the radix sort (`Invalid kernelIndex (0) ... less than
  19`) and every frame comes out black. Vulkan selects the GTX 1650 and works.
- **`BEE_BUILD_THREADS=2` is mandatory.** This shell's cgroup
  (`aakhar-terminal-gotty.service`) has `pids.max = 512` and threads count
  against it. Unity plus bee plus the ILPP `dotnet` servers exhaust it, and the
  symptom is either `posix_spawn failed ... Resource temporarily unavailable` or
  a silent hang with bee at 0% CPU. Clean up orphaned `dotnet` / `netcorerun` /
  `bee_backend` processes between runs.
- **A wedged `UnityLinker` will not recover on its own.** If the pid budget is
  exhausted at the moment the linker starts, it spawns but deadlocks at 0% CPU
  and bee waits on it forever — a player build sat at `[BUSY 1518s] UnityLinker`
  this way. Freeing pids afterwards does **not** unstick it; kill the build and
  restart it once there is headroom. Check with:
  `cat /sys/fs/cgroup/<scope>/pids.current`. Idle `UnityShaderComp` workers are
  usually the biggest reclaimable block (~12 threads each).
  Note that processes in *other* cgroups (another login session) do not count
  against this budget, however old they look.
- **Read-back colour space follows the project colour space.** In Linear colour
  space the render target is sRGB-encoded on write, so read-back pixels are sRGB;
  in Gamma colour space they come back linear. `RenderProbe` derives this from
  `QualitySettings.activeColorSpace` rather than hard-coding it. Getting it wrong
  shifts every channel by roughly a 2.2 gamma and fails every view. The project
  is Linear, which `QuestBuild.ConfigureQuest` sets **project-wide** (colour space
  is not a per-platform setting).
- The living room's blue feature wall at `x = 3` is coincident with the bedroom's
  beige wall for `z ∈ (-3, 1)`. Test viewpoints aiming at it must sit at `z > 1`.

## Known limitations

- **The Quest APK builds, but has never run on a headset.** It is built on Linux
  and verified structurally with `aapt2`/`apksigner` (arm64-v8a, Vulkan,
  `libopenxr_loader.so`, `com.oculus.intent.category.VR`, minSdk 32, valid
  signature, `supportedDevices` includes `eureka` = Quest 3), and the splat sort
  kernel `InitDeviceRadixSort` is present in the shipped assets. What is untested
  is **on-device behaviour and frame rate** — there is no headset here. All
  desktop numbers above are mono, desktop-GPU numbers and say **nothing** about
  Quest performance. See **[QUEST.md](QUEST.md)**.
- **Unity's Android module *is* installable on Linux**, despite the release
  metadata listing it as a macOS `.pkg`. A `.pkg` is an xar archive and Unity Hub
  merely extracts its payload; every sub-component is a real linux-x64 build.
  `tools/install-android-module.py` does this without Hub.
- **722k splats is a frame-time problem on Quest, not a memory one.** 163 MB against
  8 GB of shared memory is comfortable; sorting that many Gaussians twice per frame
  at 72 fps is not. `HouseScanLoader` caps mobile loads at 400k splats
  (`m_MaxSplatsMobile`) for that reason. At 236 bytes/splat the encoding
  is deliberately lossless (Float32 position/scale/colour/SH) because upstream
  only allows chunkless assets when all four formats are lossless. Compressed
  encoding (Norm11 position, Norm8x4 colour, Norm6 clustered SH, plus
  `CalcChunkDataJob`) is the next significant piece of work and should cut this
  by roughly an order of magnitude.
- **The `ThreeDGS_YDown` pre-rotation is untested against a real capture.** The
  reader offers a 180° pre-rotation about X for Y-down exports, but this has only
  been exercised against synthetic Y-up data. Validate it against an actual
  Polycam or Scaniverse export before trusting it.
- **`.spz` is not supported at runtime.** Scaniverse exports it; upstream has an
  editor-side `SPZFileReader.cs` that could be ported.
- Stereo correctness is verified through a real XR provider (Mock HMD, multi-pass)
  in a Windows player under Wine, but **performance** on a headset is not and
  cannot be measured here. Multi-pass is required — upstream only consults
  `XRSettings.eyeTextureWidth`, so single-pass instanced would break.


## Navigation and AI

A walkable grid is not a game. `ScanNavGrid` turns the analysed scan into
something an agent can use:

- **Clearance** — Chebyshev distance to the nearest blocked cell, by multi-source
  BFS. A cell is *navigable* only if `clearance >= ceil(agentRadius / cellSize)`,
  so a body actually fits. Walkable and navigable are not the same thing, and
  conflating them is how agents end up inside walls.
- **Connected regions** — flood fill, so "can A reach B" is answered before A* is
  ever run, and so hunters are never spawned somewhere they cannot leave.
- **A\*** — 8-connected, octile heuristic, corner cutting disallowed, with a
  clearance penalty so routes drift to the middle of a corridor instead of
  scraping the walls. Paths are then string-pulled.
- **Line of sight** — separate from movement. Cells are only sight-blocking if
  their splats reach above 1.2 m, so an 0.85 m couch blocks a body but not a
  view, while a 2.6 m wall blocks both.

`HunterBrain` patrols until it sees the player, chases while it can, and searches
the last known position after losing them. It is a plain C# class with no Unity
object dependencies and a seeded RNG, so a full chase can be simulated headlessly
and asserted rather than eyeballed in a headset.

### The test that matters

`tools/nav-probe.sh` runs the same checks against two scans that differ **only**
in whether the rooms have doorways — the sealed file is byte-identical to the
original scan before the doors were cut. With doorways: one connected region, and
a route that crosses the dividing wall only where the door is. Sealed: three
regions, no route, and a hunter that never arrives.

That A/B is the point. A pathfinder that quietly ignored geometry would sail
through the first test.

`tools/game-probe.sh` then drives the real thing: `HouseScanLoader.Load()`, the
`onScanReady` handshake, spawn selection, and `GameDirector.Tick()` stepped at
72 Hz until capture. Neither probe needs a GPU.

### Six bugs these caught

Worth recording, because every one of them looked correct while reading it.

Three let agents pass through walls:

1. A "close enough to the waypoint" tolerance that skipped the agent onto a
   segment nothing had checked.
2. An unchecked first segment. `path[0]` is the centre of the agent's cell, not
   where the agent is standing, so the step to `path[1]` needs its own test.
3. The traversal check itself point-sampled the line, which misses a cell the
   segment only clips at a corner. Replaced with exact Amanatides & Woo DDA.

Off-grid steps went 16 → 2 → 0.

The other three were one assumption: *the player is standing on a navigable
cell*. In VR they are not — they stand against a wall, or inside the half-metre
the agent radius carves around a table.

4. Spawn selection compared regions using the player's cell, which is `-1` when
   the player is off-grid, so no spawn matched and the round silently refused to
   start.
5. Hunters pathed to the player's exact position, which fails for an off-grid
   goal, so they gave up and wandered.
6. With both fixed the player was still invincible: the nearest cell a hunter
   could reach was 1.07 m away and the catch radius is 0.70 m. A hunter that has
   reached the closest ground it can stand on, and can see the player, now counts
   as a catch.

### Limits

At 0.25 m cells a 1 m doorway is only about three free cells wide, so furniture
within half a metre of a door erodes it shut and splits the house in two. This
happened during development — a couch parked beside the bedroom door — and it
will happen on real scans.


## Recording videos

Every feature gets its own short clip, recorded from the running build. The
point is that the page shows what the code does now, not what it did when
someone last remembered to re-record.

```
tools/record-all-videos.sh            # every shot
FLY_SHOT=hunt tools/record-flythrough.sh   # just one
```

| Shot | Shows |
| --- | --- |
| `tour` | Ceiling-cutaway orbit, then an eye-height walkthrough following walkable cells |
| `hunt` | Four rounds from overhead: hunters spawn, path through the doorway and close in |

Both run the real `GaussianSplatRenderer` under Xvfb with Vulkan, so the footage
comes from the same renderer the Quest build ships. It is still desktop and
monoscopic, and says nothing about headset frame rate.

**Each shot asserts something about its own footage.** Six hundred black frames
encode into a perfectly valid video, so `tour` fails the run if splat coverage
never exceeds 2%, and `hunt` fails if the agents never appear — the splat pass
composites over the scene, and a hunter video showing no hunters is worse than
no video, because it looks like it worked. The agents are magenta specifically
because the scan contains cream, brown, red and green, and a red capsule cannot
be told from the sofa by a pixel count.

Adding a shot means adding a case to `FlythroughRecorder` and a line to
`record-all-videos.sh`.

### Regenerating the test scans

```
python3 tools/make_house_splat.py --out scans/house_doors.ply
python3 tools/make_house_splat.py --out scans/house_cutaway.ply --no-ceiling
python3 tools/make_house_splat.py --out scans/house_sealed.ply   --no-doors   # negative control
```

## Next steps

1. Compressed/chunked encoding, to fit a house in a Quest memory budget.
2. Validate against a real phone capture, which will settle the coordinate
   convention question and expose real-world scan noise.
3. Install Android Build Support and measure actual on-device frame times.
4. Hunters exist but are untuned: speeds, counts and sight range have never been
   judged by a human, only asserted by a probe.
5. Nothing about the game loop has been seen in a headset. Everything above is
   desktop, monoscopic, and headless.
