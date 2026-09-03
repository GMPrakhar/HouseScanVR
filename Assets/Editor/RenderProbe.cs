using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using GaussianSplatting.Runtime;
using Debug = UnityEngine.Debug;

namespace HouseScan.EditorTools
{
    /// <summary>
    /// Renders the loaded house scan from fixed viewpoints, writes PNGs, and
    /// asserts that the expected surfaces are actually visible. Without a headset
    /// attached this is the strongest available evidence that the splat pipeline
    /// genuinely renders rather than merely compiling.
    /// </summary>
    public static class RenderProbe
    {
        struct Viewpoint
        {
            public string name;
            public Vector3 pos;
            public Vector3 euler;
            public string expect;      // palette key that must dominate
        }

        // Ground-truth colours from tools/make_house_splat.py, authored in sRGB.
        static readonly Dictionary<string, Color> kPaletteSrgb = new()
        {
            { "floor",   new Color(0.42f, 0.28f, 0.16f) },
            { "ceiling", new Color(0.92f, 0.92f, 0.90f) },
            { "wall",    new Color(0.78f, 0.76f, 0.70f) },
            { "wall_e",  new Color(0.24f, 0.44f, 0.62f) },
            { "couch",   new Color(0.65f, 0.18f, 0.18f) },
            { "plant",   new Color(0.16f, 0.48f, 0.20f) },
        };

        // Which space the read-back pixels are actually in depends on the project
        // colour space, and getting this wrong shifts every channel by a 2.2 gamma:
        //  - Linear colour space: the render target is sRGB-encoded and Unity
        //    converts on write, so read-back pixels come out in sRGB.
        //  - Gamma colour space: no conversion happens, so the raw shader output
        //    (linear) is what is read back.
        // Deriving this instead of hard-coding it keeps the probe correct across
        // the colour-space switch that QuestBuild performs.
        static readonly Dictionary<string, Color> kPalette = BuildPalette();

        static Dictionary<string, Color> BuildPalette()
        {
            if (QualitySettings.activeColorSpace == ColorSpace.Linear)
                return new Dictionary<string, Color>(kPaletteSrgb);

            var d = new Dictionary<string, Color>();
            foreach (var kv in kPaletteSrgb)
                d[kv.Key] = new Color(SrgbToLinear(kv.Value.r),
                                      SrgbToLinear(kv.Value.g),
                                      SrgbToLinear(kv.Value.b));
            return d;
        }

        static float SrgbToLinear(float c)
        {
            return c <= 0.04045f ? c / 12.92f : Mathf.Pow((c + 0.055f) / 1.055f, 2.4f);
        }

        static readonly Viewpoint[] kViews =
        {
            new() { name = "east_blue_wall", pos = new Vector3(2.0f, 1.3f, 2.2f),  euler = new Vector3(0, 90, 0),  expect = "wall_e" },
            new() { name = "couch",          pos = new Vector3(-2.0f, 1.5f, 2.4f), euler = new Vector3(28, 180, 0), expect = "couch" },
            new() { name = "floor_down",     pos = new Vector3(0.5f, 1.6f, 1.8f),  euler = new Vector3(89, 0, 0),  expect = "floor" },
            new() { name = "ceiling_up",     pos = new Vector3(-1.0f, 1.2f, 0.5f), euler = new Vector3(-80, 0, 0), expect = "ceiling" },
            new() { name = "overview",       pos = new Vector3(-3.5f, 2.3f, -2.5f), euler = new Vector3(18, 45, 0), expect = null },
        };

        const int kWidth = 1024;
        const int kHeight = 1024;

        public static void Run()
        {
            string outDir = Environment.GetEnvironmentVariable("PROBE_OUT")
                            ?? "/home/prak/vr-work/probe";
            string scanPath = Environment.GetEnvironmentVariable("PROBE_SCAN")
                              ?? "/home/prak/vr-work/scans/house_small.ply";
            Directory.CreateDirectory(outDir);

            var report = new StringBuilder();
            bool ok = true;

            try
            {
                Debug.Log($"[Probe] Graphics device: {SystemInfo.graphicsDeviceName} " +
                          $"({SystemInfo.graphicsDeviceType}), compute={SystemInfo.supportsComputeShaders}");
                report.AppendLine($"device={SystemInfo.graphicsDeviceName}");
                report.AppendLine($"api={SystemInfo.graphicsDeviceType}");
                report.AppendLine($"color_space={QualitySettings.activeColorSpace}");
                report.AppendLine($"compute={SystemInfo.supportsComputeShaders}");

                if (!SystemInfo.supportsComputeShaders)
                {
                    report.AppendLine("FAIL: compute shaders unsupported on this device");
                    Finish(report, outDir, false);
                    return;
                }

                EditorSceneManager.OpenScene(ProjectSetup.kScenePath, OpenSceneMode.Single);

                var loader = UnityEngine.Object.FindFirstObjectByType<HouseScanLoader>();
                if (loader == null)
                {
                    report.AppendLine("FAIL: HouseScanLoader not found in scene");
                    Finish(report, outDir, false);
                    return;
                }

                var splatRenderer = loader.GetComponent<GaussianSplatRenderer>();
                ProjectSetup.AssignShaders(splatRenderer);

                loader.m_ScanPath = scanPath;
                loader.m_LoadOnStart = false;

                var sw = Stopwatch.StartNew();
                bool loaded = loader.Load();
                sw.Stop();

                report.AppendLine($"scan={scanPath}");
                report.AppendLine($"load_ok={loaded}");
                if (!loaded)
                {
                    report.AppendLine($"FAIL: load error: {loader.lastError}");
                    Finish(report, outDir, false);
                    return;
                }

                report.AppendLine($"splats={loader.loadedSplatCount}");
                report.AppendLine($"load_ms={loader.loadMilliseconds:F0}");
                report.AppendLine($"bounds_min={loader.scanBounds.min}");
                report.AppendLine($"bounds_max={loader.scanBounds.max}");
                report.AppendLine($"has_valid_asset={splatRenderer.HasValidAsset}");

                var asset = splatRenderer.asset;
                if (asset != null)
                {
                    long bytes = asset.posDataSize + asset.otherDataSize +
                                 asset.colorDataSize + asset.shDataSize + asset.chunkDataSize;
                    report.AppendLine($"gpu_payload_mb={(bytes / (1024.0 * 1024.0)).ToString("F1", CultureInfo.InvariantCulture)}");
                    report.AppendLine($"bytes_per_splat={(asset.splatCount > 0 ? bytes / asset.splatCount : 0)}");
                }
                report.AppendLine($"mono_heap_mb={(GC.GetTotalMemory(false) / (1024.0 * 1024.0)).ToString("F1", CultureInfo.InvariantCulture)}");

                if (!splatRenderer.HasValidAsset)
                {
                    report.AppendLine("FAIL: renderer rejected the runtime asset");
                    Finish(report, outDir, false);
                    return;
                }

                var cam = UnityEngine.Object.FindFirstObjectByType<Camera>();
                foreach (var view in kViews)
                {
                    bool viewOk = RenderView(cam, view, outDir, report);
                    ok &= viewOk;
                }

                // Level analysis: prove the scan can drive gameplay, not just visuals.
                AnalyzeLevel(scanPath, report, ref ok);

                // Stereo proxy: no headset is available here, so verify the renderer
                // responds correctly to per-eye camera positions, which is what
                // multi-pass stereo does one eye at a time.
                StereoCheck(cam, outDir, report, ref ok);

                // The scan has no colliders, so prove movement is constrained by
                // the analysed floor instead.
                RigCheck(loader, report, ref ok);
            }
            catch (Exception e)
            {
                report.AppendLine($"FAIL: exception {e}");
                ok = false;
            }

            Finish(report, outDir, ok);
        }

        // Renders the same viewpoint from two eye positions an IPD apart. Both eyes
        // must render the scene, agree on colour, and yet differ in detail - if the
        // two images were identical the renderer would be ignoring eye position and
        // stereo would collapse to a flat image in the headset.
        static void StereoCheck(Camera cam, string outDir, StringBuilder report, ref bool ok)
        {
            const float kIpd = 0.063f;
            var basePos = new Vector3(-1.0f, 1.6f, 1.0f);
            var euler = new Vector3(5f, 115f, 0f);
            var rot = Quaternion.Euler(euler);
            Vector3 right = rot * Vector3.right;

            var eyes = new[]
            {
                ("stereo_left",  basePos - right * (kIpd * 0.5f)),
                ("stereo_right", basePos + right * (kIpd * 0.5f)),
            };

            var means = new Color[2];
            var images = new Texture2D[2];

            for (int i = 0; i < 2; ++i)
            {
                var view = new Viewpoint { name = eyes[i].Item1, pos = eyes[i].Item2, euler = euler };
                bool eyeOk = RenderView(cam, view, outDir, report, out images[i], out float cov, out means[i]);
                ok &= eyeOk;
                if (cov < 0.50f)
                    report.AppendLine($"FAIL: stereo eye {view.name} coverage {cov:F3} below 0.50");
            }

            if (images[0] == null || images[1] == null)
            {
                report.AppendLine("FAIL: stereo eyes did not render");
                ok = false;
                return;
            }

            float eyeChroma = ChromaDistance(means[0], means[1]);
            report.AppendLine($"stereo.ipd_m={kIpd.ToString("F3", CultureInfo.InvariantCulture)}");
            report.AppendLine($"stereo.eye_chroma_delta={eyeChroma.ToString("F4", CultureInfo.InvariantCulture)}");

            var a = images[0].GetPixels();
            var b = images[1].GetPixels();
            double diffSum = 0;
            int changed = 0;
            for (int i = 0; i < a.Length; ++i)
            {
                float d = Mathf.Abs(a[i].r - b[i].r) + Mathf.Abs(a[i].g - b[i].g) + Mathf.Abs(a[i].b - b[i].b);
                diffSum += d;
                if (d > 0.02f)
                    changed++;
            }
            float parallaxFraction = changed / (float)a.Length;
            float meanDiff = (float)(diffSum / a.Length);
            report.AppendLine($"stereo.parallax_pixel_fraction={parallaxFraction.ToString("F4", CultureInfo.InvariantCulture)}");
            report.AppendLine($"stereo.mean_abs_diff={meanDiff.ToString("F4", CultureInfo.InvariantCulture)}");

            // The eyes look at the same surfaces, so their colour must match closely.
            if (eyeChroma > 0.02f)
            {
                report.AppendLine($"FAIL: stereo eyes disagree on colour (chroma delta {eyeChroma:F4})");
                ok = false;
            }

            // A 63 mm baseline over room-scale geometry must shift a meaningful
            // number of pixels; near-zero means eye position is being ignored.
            if (parallaxFraction < 0.02f)
            {
                report.AppendLine($"FAIL: no stereo parallax detected " +
                                  $"(only {parallaxFraction:P2} of pixels differ between eyes)");
                ok = false;
            }

            // Conversely, a huge difference would mean the eyes are not looking at
            // the same place at all.
            if (meanDiff > 0.25f)
            {
                report.AppendLine($"FAIL: stereo eyes differ too much (mean abs diff {meanDiff:F4})");
                ok = false;
            }
            if (images[0] != null) UnityEngine.Object.DestroyImmediate(images[0]);
            if (images[1] != null) UnityEngine.Object.DestroyImmediate(images[1]);
        }

        // Walks the rig in a straight line until it stops, from every spawn point,
        // and checks it never leaves captured floor and never escapes the scan.
        static void RigCheck(HouseScanLoader loader, StringBuilder report, ref bool ok)
        {
            var rig = UnityEngine.Object.FindFirstObjectByType<ScanPlayerRig>();
            if (rig == null)
            {
                report.AppendLine("FAIL: ScanPlayerRig not found in scene");
                ok = false;
                return;
            }

            if (loader.analysis == null)
            {
                report.AppendLine("FAIL: loader produced no level analysis");
                ok = false;
                return;
            }

            report.AppendLine($"rig.spawn_points={loader.spawnPoints.Count}");
            if (loader.spawnPoints.Count == 0)
            {
                report.AppendLine("FAIL: no spawn points produced from scan");
                ok = false;
                return;
            }

            rig.Bind(loader.analysis);
            rig.Place(loader.spawnPoints);

            if (!rig.isPlaced)
            {
                report.AppendLine("FAIL: rig refused to place the player");
                ok = false;
                return;
            }

            // Every spawn point must itself be a legal standing position, otherwise
            // the player starts inside a wall.
            int badSpawns = 0;
            foreach (var p in loader.spawnPoints)
                if (!rig.IsWalkable(p))
                    badSpawns++;
            report.AppendLine($"rig.unwalkable_spawns={badSpawns}");
            if (badSpawns > 0)
            {
                report.AppendLine($"FAIL: {badSpawns} spawn points are not walkable");
                ok = false;
            }

            var dirs = new[] { Vector3.forward, Vector3.back, Vector3.left, Vector3.right };
            var scanBounds = loader.scanBounds;
            int escapes = 0, offFloor = 0, blockedRuns = 0;
            float maxTravel = 0f;

            foreach (var spawn in loader.spawnPoints)
            {
                foreach (var dir in dirs)
                {
                    rig.transform.position = spawn;
                    Vector3 start = rig.transform.position;

                    // 400 steps of 5 cm is 20 m, further than any room here, so a
                    // run that never stops means containment is broken.
                    for (int i = 0; i < 400; ++i)
                    {
                        Vector3 before = rig.transform.position;
                        rig.Move(dir * 0.05f);
                        Vector3 after = rig.transform.position;

                        if (!rig.IsWalkable(after))
                            offFloor++;
                        if (!scanBounds.Contains(new Vector3(after.x, scanBounds.center.y, after.z)))
                            escapes++;

                        if ((after - before).sqrMagnitude < 1e-8f)
                        {
                            blockedRuns++;
                            break;
                        }
                    }

                    maxTravel = Mathf.Max(maxTravel,
                                          Vector3.Distance(start, rig.transform.position));
                }
            }

            int totalRuns = loader.spawnPoints.Count * dirs.Length;
            report.AppendLine($"rig.runs={totalRuns}");
            report.AppendLine($"rig.blocked_runs={blockedRuns}");
            report.AppendLine($"rig.off_floor_steps={offFloor}");
            report.AppendLine($"rig.out_of_bounds_steps={escapes}");
            report.AppendLine($"rig.max_travel_m={maxTravel.ToString("F2", CultureInfo.InvariantCulture)}");

            if (offFloor > 0)
            {
                report.AppendLine($"FAIL: rig left captured floor on {offFloor} steps");
                ok = false;
            }
            if (escapes > 0)
            {
                report.AppendLine($"FAIL: rig escaped the scan bounds on {escapes} steps");
                ok = false;
            }
            // Walking into a wall must eventually stop the player in every run.
            if (blockedRuns != totalRuns)
            {
                report.AppendLine($"FAIL: only {blockedRuns}/{totalRuns} runs were stopped by geometry");
                ok = false;
            }
        }

        static bool RenderView(Camera cam, Viewpoint view, string outDir, StringBuilder report)
        {
            bool r = RenderView(cam, view, outDir, report, out var image, out _, out _);
            if (image != null)
                UnityEngine.Object.DestroyImmediate(image);
            return r;
        }

        static bool RenderView(Camera cam, Viewpoint view, string outDir, StringBuilder report,
                               out Texture2D image, out float coverageOut, out Color meanOut)
        {
            cam.transform.position = view.pos;
            cam.transform.rotation = Quaternion.Euler(view.euler);

            var rt = new RenderTexture(kWidth, kHeight, 24, RenderTextureFormat.ARGB32)
            {
                antiAliasing = 1
            };
            rt.Create();
            cam.targetTexture = rt;

            // One warm-up frame so shader/compute resources exist, then a timed frame.
            cam.Render();

            var sw = Stopwatch.StartNew();
            const int kFrames = 20;
            for (int i = 0; i < kFrames; ++i)
                cam.Render();
            GL.Flush();
            sw.Stop();
            double msPerFrame = sw.Elapsed.TotalMilliseconds / kFrames;

            var tex = new Texture2D(kWidth, kHeight, TextureFormat.RGBA32, false);
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            tex.ReadPixels(new Rect(0, 0, kWidth, kHeight), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;

            cam.targetTexture = null;

            File.WriteAllBytes(Path.Combine(outDir, view.name + ".png"), tex.EncodeToPNG());

            var pixels = tex.GetPixels();
            int lit = 0;
            Color sum = Color.black;
            foreach (var p in pixels)
            {
                // Background is near-black; anything brighter came from a splat.
                if (p.r + p.g + p.b > 0.12f)
                {
                    lit++;
                    sum += p;
                }
            }
            float coverage = lit / (float)pixels.Length;
            Color mean = lit > 0 ? sum / lit : Color.black;

            image = tex;
            coverageOut = coverage;
            meanOut = mean;

            // The expectation is evaluated on the centre of the frame, where the
            // aimed-at surface is, rather than the whole frame which necessarily
            // mixes several surfaces together.
            Color centreMean = CentreMean(tex, 0.30f);

            report.AppendLine($"view.{view.name}.coverage={coverage.ToString("F3", CultureInfo.InvariantCulture)}");
            report.AppendLine($"view.{view.name}.mean_rgb=" +
                              $"{mean.r.ToString("F3", CultureInfo.InvariantCulture)}," +
                              $"{mean.g.ToString("F3", CultureInfo.InvariantCulture)}," +
                              $"{mean.b.ToString("F3", CultureInfo.InvariantCulture)}");
            report.AppendLine($"view.{view.name}.centre_rgb=" +
                              $"{centreMean.r.ToString("F3", CultureInfo.InvariantCulture)}," +
                              $"{centreMean.g.ToString("F3", CultureInfo.InvariantCulture)}," +
                              $"{centreMean.b.ToString("F3", CultureInfo.InvariantCulture)}");
            report.AppendLine($"view.{view.name}.ms_per_frame={msPerFrame.ToString("F2", CultureInfo.InvariantCulture)}");

            bool ok = true;

            // The view must not be empty or black: that was the exact failure mode
            // worth guarding against.
            if (coverage < 0.50f)
            {
                report.AppendLine($"FAIL: view {view.name} coverage {coverage:F3} below 0.50");
                ok = false;
            }

            if (view.expect != null)
            {
                Color want = kPalette[view.expect];
                float dist = ChromaDistance(want, centreMean);
                report.AppendLine($"view.{view.name}.hue_dist_to_{view.expect}=" +
                                  dist.ToString("F3", CultureInfo.InvariantCulture));

                // The nearest palette entry to what was rendered must be the one we
                // aimed at, so a wrong-but-close colour cannot pass.
                string nearest = NearestPaletteKey(centreMean);
                report.AppendLine($"view.{view.name}.nearest_palette={nearest}");

                if (nearest != view.expect || dist > 0.12f)
                {
                    report.AppendLine($"FAIL: view {view.name} expected {view.expect} " +
                                      $"({want.r:F2},{want.g:F2},{want.b:F2}) but centre reads " +
                                      $"({centreMean.r:F2},{centreMean.g:F2},{centreMean.b:F2}) " +
                                      $"nearest={nearest} dist={dist:F3}");
                    ok = false;
                }
            }

            rt.Release();
            UnityEngine.Object.DestroyImmediate(rt);
            return ok;
        }

        // Compare chromaticity rather than absolute brightness, since exposure and
        // splat overlap change intensity but not hue.
        static Color Normalize(Color c)
        {
            float s = c.r + c.g + c.b;
            if (s < 1e-5f)
                return new Color(0.333f, 0.333f, 0.333f);
            return new Color(c.r / s, c.g / s, c.b / s);
        }

        static float ChromaDistance(Color a, Color b)
        {
            Color x = Normalize(a), y = Normalize(b);
            return Mathf.Sqrt((x.r - y.r) * (x.r - y.r) +
                              (x.g - y.g) * (x.g - y.g) +
                              (x.b - y.b) * (x.b - y.b));
        }

        static string NearestPaletteKey(Color c)
        {
            string best = null;
            float bestDist = float.MaxValue;
            foreach (var kv in kPalette)
            {
                float d = ChromaDistance(kv.Value, c);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = kv.Key;
                }
            }
            return best;
        }

        static Color CentreMean(Texture2D tex, float fraction)
        {
            int w = Mathf.Max(1, (int)(tex.width * fraction));
            int h = Mathf.Max(1, (int)(tex.height * fraction));
            int x0 = (tex.width - w) / 2;
            int y0 = (tex.height - h) / 2;
            var px = tex.GetPixels(x0, y0, w, h);
            Color sum = Color.black;
            foreach (var p in px)
                sum += p;
            return sum / px.Length;
        }

        static void AnalyzeLevel(string scanPath, StringBuilder report, ref bool ok)
        {
            var splats = GaussianPlyRuntimeReader.Load(
                scanPath, GaussianPlyRuntimeReader.SourceConvention.AsAuthored, 200000);
            try
            {
                var a = ScanLevelAnalyzer.Analyze(splats);
                report.AppendLine($"analysis.floor_y={a.floorY.ToString("F3", CultureInfo.InvariantCulture)}");
                report.AppendLine($"analysis.ceiling_y={a.ceilingY.ToString("F3", CultureInfo.InvariantCulture)}");
                report.AppendLine($"analysis.grid={a.gridWidth}x{a.gridHeight}");
                report.AppendLine($"analysis.walkable_cells={a.WalkableCellCount}");
                report.AppendLine($"analysis.walkable_sqm={a.WalkableAreaSqm.ToString("F1", CultureInfo.InvariantCulture)}");

                var spawns = ScanLevelAnalyzer.PickSpawnPoints(a, 12, 1.0f);
                report.AppendLine($"analysis.spawn_points={spawns.Count}");

                // The synthetic house has a floor at y=0 and a 2.6 m ceiling.
                if (Mathf.Abs(a.floorY) > 0.15f)
                {
                    report.AppendLine($"FAIL: floor detected at {a.floorY:F3}, expected ~0.0");
                    ok = false;
                }
                if (Mathf.Abs(a.ceilingY - 2.6f) > 0.20f)
                {
                    report.AppendLine($"FAIL: ceiling detected at {a.ceilingY:F3}, expected ~2.6");
                    ok = false;
                }
                if (a.WalkableAreaSqm < 20f)
                {
                    report.AppendLine($"FAIL: walkable area {a.WalkableAreaSqm:F1} m^2 too small");
                    ok = false;
                }
                if (spawns.Count < 8)
                {
                    report.AppendLine($"FAIL: only {spawns.Count} spawn points found");
                    ok = false;
                }
            }
            finally
            {
                if (splats.IsCreated)
                    splats.Dispose();
            }
        }

        static void Finish(StringBuilder report, string outDir, bool ok)
        {
            report.AppendLine(ok ? "RESULT=PASS" : "RESULT=FAIL");
            string text = report.ToString();
            File.WriteAllText(Path.Combine(outDir, "report.txt"), text);
            Debug.Log("[Probe] REPORT\n" + text);
            EditorApplication.Exit(ok ? 0 : 1);
        }
    }
}
