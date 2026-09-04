using System.Collections.Generic;
using System.Globalization;
using System.IO;
using GaussianSplatting.Runtime;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace HouseScan.EditorTools
{
    /// <summary>
    /// Records a camera flythrough of a loaded scan to a PNG sequence, for
    /// turning into a video with ffmpeg.
    ///
    /// This renders through exactly the same GaussianSplatRenderer and URP setup
    /// the player build uses, so the footage is representative of the renderer -
    /// but it is captured on a desktop GPU, in mono, and is not evidence of
    /// headset frame rate.
    ///
    /// Two passes are recorded:
    ///   orbit - a ceiling-cutaway scan, circled from above, to show the layout
    ///   walk  - the full scan at eye height, following the walkable cells the
    ///           level analyser derived from the splats
    /// </summary>
    public static class FlythroughRecorder
    {
        static int kWidth = 1280;
        static int kHeight = 720;

        public static void Run()
        {
            string scanPath = System.Environment.GetEnvironmentVariable("FLY_SCAN");
            string cutawayPath = System.Environment.GetEnvironmentVariable("FLY_SCAN_CUTAWAY");
            string outDir = System.Environment.GetEnvironmentVariable("FLY_OUT")
                            ?? "/tmp/flythrough";
            kWidth = ParseInt("FLY_WIDTH", 1280);
            kHeight = ParseInt("FLY_HEIGHT", 720);
            int fps = ParseInt("FLY_FPS", 30);

            Directory.CreateDirectory(outDir);

            if (!SystemInfo.supportsComputeShaders)
            {
                Debug.LogError("[Fly] compute shaders unsupported; run with -force-vulkan");
                EditorApplication.Exit(1);
                return;
            }

            EditorSceneManager.OpenScene(ProjectSetup.kScenePath, OpenSceneMode.Single);

            var loader = Object.FindFirstObjectByType<HouseScanLoader>();
            var splatRenderer = loader.GetComponent<GaussianSplatRenderer>();
            ProjectSetup.AssignShaders(splatRenderer);
            loader.m_LoadOnStart = false;

            var cam = Object.FindFirstObjectByType<Camera>();
            cam.fieldOfView = 70f;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 200f;

            var state = new CaptureState { outDir = outDir };

            // Pass 1: cutaway orbit. Falls back to the main scan if no cutaway
            // was supplied, rather than silently skipping the shot.
            if (!LoadScan(loader, string.IsNullOrEmpty(cutawayPath) ? scanPath : cutawayPath))
                return;
            {
                var a = loader.analysis;
                var b = loader.scanBounds;
                var centre = new Vector3(b.center.x, a.floorY, b.center.z);
                float radius = Mathf.Max(b.size.x, b.size.z) * 0.72f;
                WarmUp(cam, centre + Vector3.back * radius + Vector3.up * 6f, centre);

                Capture(cam, state, fps * 9, t =>
                {
                    float ang = (-40f + 340f * t) * Mathf.Deg2Rad;
                    var p = centre + new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang)) * radius;
                    p.y = a.floorY + Mathf.Lerp(9.0f, 5.0f, t);
                    return (p, centre);
                });
                Debug.Log($"[Fly] orbit done, {state.index} frames");
            }

            // Pass 2: eye-height walkthrough of the real scan.
            if (!LoadScan(loader, scanPath)) return;
            {
                var a = loader.analysis;
                var route = BuildRoute(a, loader.spawnPoints);
                Debug.Log($"[Fly] walk route has {route.Count} waypoints, " +
                          $"walkable {a.WalkableAreaSqm:F1} m^2");

                WarmUp(cam, route[0], route[Mathf.Min(1, route.Count - 1)]);

                Capture(cam, state, fps * 13, t =>
                {
                    var p = SampleRoute(route, t);
                    // Look ahead along the route so the camera turns into
                    // doorways rather than sliding sideways through them.
                    var ahead = SampleRoute(route, Mathf.Min(1f, t + 0.03f));
                    if ((ahead - p).sqrMagnitude < 1e-4f)
                        ahead = p + Vector3.forward;
                    return (p, new Vector3(ahead.x, p.y, ahead.z));
                });
                Debug.Log($"[Fly] walk done, {state.index} frames total");
            }

            var summary =
                $"frames={state.index}\n" +
                $"fps={fps}\n" +
                $"resolution={kWidth}x{kHeight}\n" +
                $"splats={loader.loadedSplatCount}\n" +
                $"device={SystemInfo.graphicsDeviceName}\n" +
                $"api={SystemInfo.graphicsDeviceType}\n" +
                $"walkable_sqm={loader.analysis.WalkableAreaSqm.ToString("F1", CultureInfo.InvariantCulture)}\n" +
                $"max_coverage={state.maxCoverage.ToString("F3", CultureInfo.InvariantCulture)}\n" +
                $"mean_coverage={(state.sumCoverage / Mathf.Max(1, state.coverageSamples)).ToString("F3", CultureInfo.InvariantCulture)}\n" +
                $"mean_render_ms={(state.totalMs / Mathf.Max(1, state.rendered)).ToString("F2", CultureInfo.InvariantCulture)}\n";
            File.WriteAllText(Path.Combine(outDir, "flythrough.txt"), summary);
            Debug.Log("[Fly] " + summary.Replace("\n", " "));

            // 600 black frames would encode into a perfectly valid video, so the
            // capture is only trustworthy if splats were actually on screen.
            if (state.maxCoverage < 0.02f)
            {
                Debug.LogError($"[Fly] frames are effectively empty " +
                               $"(max coverage {state.maxCoverage:P2}); splats were not drawn");
                EditorApplication.Exit(1);
                return;
            }

            EditorApplication.Exit(0);
        }

        class CaptureState
        {
            public string outDir;
            public int index;
            public int rendered;
            public double totalMs;
            public float maxCoverage, sumCoverage;
            public int coverageSamples;
        }

        static bool LoadScan(HouseScanLoader loader, string path)
        {
            loader.m_ScanPath = path;
            if (!loader.Load())
            {
                Debug.LogError($"[Fly] load failed for {path}: {loader.lastError}");
                EditorApplication.Exit(1);
                return false;
            }
            Debug.Log($"[Fly] loaded {path}: {loader.loadedSplatCount} splats, " +
                      $"floorY={loader.analysis.floorY:F2}");
            return true;
        }

        static void Capture(Camera cam, CaptureState st, int frames,
                            System.Func<float, (Vector3 pos, Vector3 look)> path)
        {
            for (int f = 0; f < frames; ++f)
            {
                float t = frames <= 1 ? 0f : f / (float)(frames - 1);
                var (pos, look) = path(t);

                cam.transform.position = pos;
                var dir = look - pos;
                cam.transform.rotation = Quaternion.LookRotation(
                    dir.sqrMagnitude > 1e-6f ? dir.normalized : Vector3.forward, Vector3.up);

                // A fresh render target and readback texture per frame. Allocating
                // the Texture2D before the first render crashed the Vulkan driver
                // inside ReadbackImage; this order matches the render probe, which
                // is the only path proven to work headless here.
                var rt = new RenderTexture(kWidth, kHeight, 24, RenderTextureFormat.ARGB32)
                { antiAliasing = 1 };
                rt.Create();
                cam.targetTexture = rt;

                var sw = System.Diagnostics.Stopwatch.StartNew();
                cam.Render();
                GL.Flush();
                sw.Stop();
                st.totalMs += sw.Elapsed.TotalMilliseconds;
                st.rendered++;

                var tex = new Texture2D(kWidth, kHeight, TextureFormat.RGBA32, false);
                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, kWidth, kHeight), 0, 0);
                tex.Apply();
                RenderTexture.active = prev;
                cam.targetTexture = null;

                File.WriteAllBytes(
                    Path.Combine(st.outDir, $"frame_{st.index:D5}.png"), tex.EncodeToPNG());

                if (st.index % 15 == 0)
                {
                    int lit = 0;
                    var px = tex.GetPixels();
                    foreach (var c in px)
                        if (c.r + c.g + c.b > 0.12f) lit++;
                    float cov = lit / (float)px.Length;
                    st.maxCoverage = Mathf.Max(st.maxCoverage, cov);
                    st.sumCoverage += cov;
                    st.coverageSamples++;
                }

                Object.DestroyImmediate(tex);
                rt.Release();
                Object.DestroyImmediate(rt);
                st.index++;
            }
        }

        // The splat renderer's compute buffers do not exist until it has been
        // through a camera render, so the first frames legitimately draw nothing
        // and log "shader requires a compute buffer ... none provided".
        static void WarmUp(Camera cam, Vector3 pos, Vector3 look)
        {
            var rt = new RenderTexture(kWidth, kHeight, 24, RenderTextureFormat.ARGB32);
            rt.Create();
            cam.transform.position = pos;
            var dir = look - pos;
            cam.transform.rotation = Quaternion.LookRotation(
                dir.sqrMagnitude > 1e-6f ? dir.normalized : Vector3.forward, Vector3.up);
            cam.targetTexture = rt;
            for (int i = 0; i < 4; ++i)
                cam.Render();
            GL.Flush();
            cam.targetTexture = null;
            rt.Release();
            Object.DestroyImmediate(rt);
        }

        static int ParseInt(string env, int fallback)
        {
            var s = System.Environment.GetEnvironmentVariable(env);
            return int.TryParse(s, out var v) && v > 0 ? v : fallback;
        }

        /// <summary>
        /// Orders spawn points into a route by nearest-neighbour hops, keeping
        /// only hops whose straight segment stays inside walkable cells. Without
        /// that check the camera cuts through walls between rooms.
        /// </summary>
        static List<Vector3> BuildRoute(ScanLevelAnalysis a, List<Vector3> spawns)
        {
            float eye = a.floorY + 1.55f;
            var pending = new List<Vector3>();
            foreach (var s in spawns)
                pending.Add(new Vector3(s.x, eye, s.z));

            if (pending.Count == 0)
                pending.Add(new Vector3(a.bounds.center.x, eye, a.bounds.center.z));

            var route = new List<Vector3> { pending[0] };
            pending.RemoveAt(0);

            while (pending.Count > 0)
            {
                var cur = route[route.Count - 1];
                int best = -1;
                float bestD = float.MaxValue;
                for (int i = 0; i < pending.Count; ++i)
                {
                    if (!SegmentWalkable(a, cur, pending[i]))
                        continue;
                    float d = (pending[i] - cur).sqrMagnitude;
                    if (d < bestD) { bestD = d; best = i; }
                }
                if (best < 0)
                    break; // nothing else reachable without crossing a wall
                route.Add(pending[best]);
                pending.RemoveAt(best);
            }
            return route;
        }

        static bool SegmentWalkable(ScanLevelAnalysis a, Vector3 from, Vector3 to)
        {
            float dist = Vector3.Distance(new Vector3(from.x, 0, from.z),
                                          new Vector3(to.x, 0, to.z));
            int steps = Mathf.Max(2, Mathf.CeilToInt(dist / (a.cellSize * 0.5f)));
            for (int i = 0; i <= steps; ++i)
            {
                var p = Vector3.Lerp(from, to, i / (float)steps);
                if (!a.TryWorldToCell(p, out int x, out int z))
                    return false;
                if (!a.walkable[z * a.gridWidth + x])
                    return false;
            }
            return true;
        }

        static Vector3 SampleRoute(List<Vector3> route, float t)
        {
            if (route.Count == 1)
                return route[0];
            float f = Mathf.Clamp01(t) * (route.Count - 1);
            int i = Mathf.Min(route.Count - 2, Mathf.FloorToInt(f));
            return Vector3.Lerp(route[i], route[i + 1], f - i);
        }
    }
}
