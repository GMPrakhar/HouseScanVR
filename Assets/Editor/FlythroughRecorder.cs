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
            string shot = System.Environment.GetEnvironmentVariable("FLY_SHOT") ?? "tour";
            Debug.Log($"[Fly] shot={shot}");

            if (shot == "hunt")
            {
                // The hunt shot needs the ceiling off to see anything at all.
                if (!LoadScan(loader, string.IsNullOrEmpty(cutawayPath) ? scanPath : cutawayPath))
                    return;
                RecordHunt(cam, loader, state, fps);
                Finish(state, loader, fps, outDir, shot);
                return;
            }

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

            Finish(state, loader, fps, outDir, shot);
        }

        static void Finish(CaptureState state, HouseScanLoader loader, int fps,
                           string outDir, string shot)
        {
            var summary =
                $"shot={shot}\n" +
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
            if (state.measureAgents)
                summary += $"max_agent_pixels={state.maxAgentPixels.ToString("F4", CultureInfo.InvariantCulture)}\n";
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

            // Same argument for the agents: the splat pass composites over the
            // scene, and a video of hunters that shows no hunters is worse than
            // no video, because it looks like it worked.
            if (state.measureAgents && state.maxAgentPixels < 0.0004f)
            {
                Debug.LogError($"[Fly] agents are not visible in any sampled frame " +
                               $"(max {state.maxAgentPixels:P3} of pixels); the splat " +
                               $"composite is probably drawing over them");
                EditorApplication.Exit(1);
                return;
            }

            EditorApplication.Exit(0);
        }

        /// <summary>
        /// Records an actual round: GameDirector spawns hunters, HunterBrain
        /// chases a fleeing player, and both leave a trail of markers so the
        /// routes through doorways are legible from overhead.
        /// </summary>
        // The scan has cream walls, a brown floor, a red sofa and a green plant,
        // so red capsules are both hard to pick out and impossible to measure
        // separately from the furniture. Magenta appears nowhere in a house.
        static readonly Color kHunterColour = new Color(1.00f, 0.10f, 0.85f);
        static readonly Color kPlayerColour = new Color(0.25f, 0.85f, 1.00f);

        static void RecordHunt(Camera cam, HouseScanLoader loader, CaptureState st, int fps)
        {
            st.measureAgents = true;

            var director = loader.gameObject.GetComponent<GameDirector>();
            if (director == null)
                director = loader.gameObject.AddComponent<GameDirector>();
            director.m_Loader = loader;
            director.m_Rig = null;
            director.m_HunterCount = 3;
            director.m_BeginOnScanReady = false;
            director.m_SpawnOutOfSight = true;

            var playerGo = new GameObject("PlayerMarker");
            var nav0 = ScanNavGrid.Build(loader.analysis, director.m_AgentRadius);
            if (!nav0.TrySnap(new Vector3(-1.5f, 0f, 0f), out var start))
            {
                Debug.LogError("[Fly] no navigable start for the player");
                EditorApplication.Exit(1);
                return;
            }
            director.transform.position = start;

            if (!director.BeginRound())
            {
                Debug.LogError("[Fly] BeginRound failed; nothing to record");
                EditorApplication.Exit(1);
                return;
            }
            var nav = director.nav;
            Debug.Log($"[Fly] hunt: {director.hunters.Count} hunters, " +
                      $"{nav.componentCount} region(s)");

            // Recolour the director's stand-in capsules so they read against the
            // scan and can be counted in the captured pixels.
            foreach (var v in director.hunterViews)
            {
                var mr = v == null ? null : v.GetComponent<MeshRenderer>();
                if (mr != null) mr.sharedMaterial = UnlitMaterial(kHunterColour);
            }

            MakeMarker(playerGo, kPlayerColour, 0.5f, 0.9f);
            playerGo.transform.position = start + Vector3.up * 0.9f;

            var trailRoot = new GameObject("Trails").transform;
            var a = loader.analysis;
            var b = loader.scanBounds;
            var centre = new Vector3(b.center.x, a.floorY, b.center.z);

            // High and angled rather than straight down: a pure top-down view of
            // a splat cloud reads as coloured noise, while a tilt keeps the walls
            // recognisable as walls.
            float radius = Mathf.Max(b.size.x, b.size.z) * 0.40f;
            WarmUp(cam, centre + Vector3.back * radius + Vector3.up * 8f, centre);

            Vector3 playerPos = start;
            int frames = fps * 14;
            int frame = 0;
            float simTime = 0f;
            float caughtAt = -1f;
            int rounds = 1;

            Capture(cam, st, frames,
                t =>
                {
                    // Drift slowly around the house so the shot is not static,
                    // but stay near overhead so the routes stay readable.
                    float ang = (250f + 55f * t) * Mathf.Deg2Rad;
                    var p = centre + new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang)) * radius;
                    p.y = a.floorY + Mathf.Lerp(8.2f, 7.0f, t);
                    return (p, centre);
                },
                _ =>
                {
                    float dt = 1f / fps;
                    simTime += dt;

                    // A round ends in a few seconds, so hold briefly on the
                    // capture and then start another: three short rounds show
                    // respawning and different routes, where one round would
                    // leave ten seconds of frozen scene.
                    if (director.isCaught)
                    {
                        if (caughtAt < 0f) caughtAt = simTime;
                        else if (simTime - caughtAt > 0.8f)
                        {
                            foreach (Transform old in trailRoot)
                                Object.DestroyImmediate(old.gameObject);
                            playerPos = start;
                            director.transform.position = start;
                            director.BeginRound();
                            foreach (var v in director.hunterViews)
                            {
                                var r = v == null ? null : v.GetComponent<MeshRenderer>();
                                if (r != null) r.sharedMaterial = UnlitMaterial(kHunterColour);
                            }
                            caughtAt = -1f;
                            rounds++;
                        }
                    }
                    else
                    {
                        playerPos = Flee(nav, director, playerPos, dt);
                    }
                    playerGo.transform.position = playerPos + Vector3.up * 0.9f;
                    director.transform.position = playerPos;
                    director.Tick(dt);

                    // A marker every few frames, so the paths accumulate into
                    // visible trails instead of vanishing with the agents.
                    if (frame % 4 == 0)
                    {
                        DropTrail(trailRoot, playerPos, kPlayerColour);
                        foreach (var h in director.hunters)
                            DropTrail(trailRoot, h.position, kHunterColour);
                    }
                    frame++;
                });

            Debug.Log($"[Fly] hunt done: {rounds} round(s) in {simTime:F1}s simulated, " +
                      $"agent pixels max {st.maxAgentPixels:P3}");
        }

        static Renderer MakeMarker(GameObject go, Color colour, float width, float height)
        {
            var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            body.transform.SetParent(go.transform, false);
            body.transform.localScale = new Vector3(width, height, width);
            var col = body.GetComponent<Collider>();
            if (col != null) Object.DestroyImmediate(col);
            var mr = body.GetComponent<MeshRenderer>();
            mr.sharedMaterial = UnlitMaterial(colour);
            return mr;
        }

        static void DropTrail(Transform root, Vector3 at, Color colour)
        {
            var dot = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            dot.transform.SetParent(root, worldPositionStays: true);
            dot.transform.position = new Vector3(at.x, at.y + 0.06f, at.z);
            dot.transform.localScale = Vector3.one * 0.16f;
            var col = dot.GetComponent<Collider>();
            if (col != null) Object.DestroyImmediate(col);
            dot.GetComponent<MeshRenderer>().sharedMaterial = UnlitMaterial(colour * 0.85f);
        }

        static readonly Dictionary<Color, Material> s_Materials = new();

        static Material UnlitMaterial(Color colour)
        {
            if (s_Materials.TryGetValue(colour, out var cached))
                return cached;
            var shader = Shader.Find("Universal Render Pipeline/Unlit")
                         ?? Shader.Find("Unlit/Color");
            var m = new Material(shader);
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", colour);
            if (m.HasProperty("_Color")) m.SetColor("_Color", colour);
            s_Materials[colour] = m;
            return m;
        }

        /// <summary>Moves the player away from the nearest hunter, sliding along
        /// walls rather than pressing into them.</summary>
        static Vector3 Flee(ScanNavGrid nav, GameDirector director, Vector3 p, float dt)
        {
            float step = 1.7f * dt;
            Vector3 away = Vector3.zero;
            float nearest = float.MaxValue;
            foreach (var h in director.hunters)
            {
                float d = Vector3.Distance(h.position, p);
                if (d < nearest) { nearest = d; away = p - h.position; }
            }
            if (away.sqrMagnitude < 1e-6f) return p;
            away.y = 0f;
            away.Normalize();

            for (int i = 0; i < 10; ++i)
            {
                float deg = (i + 1) / 2 * 40f * (i % 2 == 0 ? 1f : -1f);
                var next = p + (Quaternion.Euler(0f, deg, 0f) * away) * step;
                if (nav.CorridorClear(p, next)) return next;
            }
            return p;
        }

        class CaptureState
        {
            public string outDir;
            public int index;
            public int rendered;
            public double totalMs;
            public float maxCoverage, sumCoverage;
            public int coverageSamples;

            /// Fraction of pixels matching the hunter colour, sampled per shot.
            /// The splat pass composites over the scene, so "are the agents
            /// actually visible?" has to be measured, not assumed.
            public float maxAgentPixels;
            public bool measureAgents;
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
                            System.Func<float, (Vector3 pos, Vector3 look)> path,
                            System.Action<float> stepSimulation = null)
        {
            for (int f = 0; f < frames; ++f)
            {
                float t = frames <= 1 ? 0f : f / (float)(frames - 1);

                // Advance the simulation before positioning the camera, so the
                // frame shows the state the camera is framed for.
                stepSimulation?.Invoke(1f / Mathf.Max(1, frames));

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
                    int lit = 0, agent = 0;
                    var px = tex.GetPixels();
                    foreach (var c in px)
                    {
                        if (c.r + c.g + c.b > 0.12f) lit++;
                        // Magenta: high red and blue, low green. The scan's
                        // palette (cream, brown, red, green) contains nothing
                        // like it, so this counts agents and not furniture.
                        if (st.measureAgents && c.r > 0.45f && c.b > 0.40f &&
                            c.r - c.g > 0.25f && c.b - c.g > 0.22f) agent++;
                    }
                    float cov = lit / (float)px.Length;
                    st.maxCoverage = Mathf.Max(st.maxCoverage, cov);
                    st.sumCoverage += cov;
                    st.coverageSamples++;
                    if (st.measureAgents)
                        st.maxAgentPixels = Mathf.Max(st.maxAgentPixels,
                                                      agent / (float)px.Length);
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
