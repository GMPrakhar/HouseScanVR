using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using GaussianSplatting.Runtime;
using Unity.Collections;
using UnityEditor;
using UnityEngine;

namespace HouseScan.EditorTools
{
    /// <summary>
    /// Headless verification of the navigation and AI layer.
    ///
    /// The central test is an A/B against two scans that differ only in whether
    /// the rooms have doorways. Both are generated from the same seed and
    /// density, so if pathfinding succeeds on one and fails on the other, it is
    /// genuinely reading the geometry rather than connecting any two points
    /// handed to it. A pathfinder that ignored walls would pass the positive
    /// test and fail the negative one.
    ///
    /// No GPU is required: this loads the .ply and reasons about the derived
    /// grid, so it runs under plain -batchmode -nographics.
    /// </summary>
    public static class NavProbe
    {
        // Room layout from tools/make_house_splat.py. These points are chosen to
        // be clear of the furniture in each room: the living room couch occupies
        // x -3..-1, z -0.45..0.45 and the bedroom couch x 3.4..5.0, z -2.4..-0.4,
        // so the obvious "room centre" is actually inside a sofa.
        static readonly Vector3 kLivingRoom = new(-2.0f, 0f, 1.8f);
        static readonly Vector3 kBedroom = new(6.0f, 0f, 0.0f);
        static readonly Vector3 kHall = new(0.5f, 0f, 4.6f);
        // Middle of each doorway, used only for diagnostics.
        static readonly Vector3 kDoorToBedroom = new(3.0f, 0f, -1.0f);
        static readonly Vector3 kDoorToHall = new(0.5f, 0f, 3.0f);
        // The only opening between the living room and the bedroom.
        static readonly Vector2 kDoorZRange = new(-1.6f, -0.4f);
        const float kDoorX = 3.0f;

        public static void Run()
        {
            string doorsPath = Env("NAV_SCAN_DOORS");
            string sealedPath = Env("NAV_SCAN_SEALED");
            string outDir = Env("NAV_OUT") ?? "/tmp/navprobe";
            Directory.CreateDirectory(outDir);

            var report = new StringBuilder();
            bool ok = true;

            try
            {
                report.AppendLine("== connected house (doorways present) ==");
                var doors = LoadGrid(doorsPath, report);
                ok &= CheckConnectivity(doors, report);
                var doorRoute = new List<Vector3>();
                ok &= CheckPathThroughDoorway(doors, report, doorRoute);
                ok &= CheckLineOfSight(doors, report);
                var hunterTrail = new List<Vector3>();
                ok &= CheckHunterReachesTarget(doors, report, outDir, hunterTrail);
                WriteNavMap(doors, outDir, "nav_map_doors.png", report, doorRoute, hunterTrail);

                report.AppendLine();
                report.AppendLine("== sealed house (negative control, same seed and density) ==");
                var sealedGrid = LoadGrid(sealedPath, report);
                ok &= CheckSealedHouseIsUnreachable(sealedGrid, report);
                WriteNavMap(sealedGrid, outDir, "nav_map_sealed.png", report);
            }
            catch (Exception e)
            {
                report.AppendLine($"FAIL: exception {e}");
                ok = false;
            }

            report.AppendLine();
            report.AppendLine(ok ? "RESULT: PASS" : "RESULT: FAIL");
            var text = report.ToString();
            File.WriteAllText(Path.Combine(outDir, "nav_report.txt"), text);
            Debug.Log("[Nav]\n" + text);
            EditorApplication.Exit(ok ? 0 : 1);
        }

        static string Env(string k) => Environment.GetEnvironmentVariable(k);

        static ScanNavGrid LoadGrid(string path, StringBuilder report)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                throw new FileNotFoundException($"scan not found: {path}");

            NativeArray<RuntimeSplatData> splats = default;
            try
            {
                splats = GaussianPlyRuntimeReader.Load(
                    path, GaussianPlyRuntimeReader.SourceConvention.AsAuthored);
                var analysis = ScanLevelAnalyzer.Analyze(splats);
                var nav = ScanNavGrid.Build(analysis);
                report.AppendLine($"scan={Path.GetFileName(path)}");
                report.AppendLine($"splats={splats.Length}");
                report.AppendLine($"grid={nav.width}x{nav.height} cell={analysis.cellSize}");
                report.AppendLine($"walkable_sqm={F(analysis.WalkableAreaSqm)}");
                report.AppendLine($"min_clearance_cells={nav.minClearanceCells}");
                report.AppendLine($"components={nav.componentCount} sizes=[" +
                                  string.Join(",", nav.componentSizes) + "]");
                report.AppendLine($"navigable_cells={CountNavigable(nav)}");
                foreach (var (label, p) in new (string, Vector3)[]
                         {
                             ("living", kLivingRoom), ("bedroom", kBedroom), ("hall", kHall),
                             ("door_to_bedroom", kDoorToBedroom), ("door_to_hall", kDoorToHall),
                         })
                {
                    nav.analysis.TryWorldToCell(p, out int cx, out int cz);
                    report.AppendLine($"  probe {label,-16} cell=({cx},{cz}) " +
                                      $"walkable={IsWalkable(nav, cx, cz)} " +
                                      $"clearance={ClearanceAt(nav, cx, cz)} " +
                                      $"navigable={nav.IsNavigable(cx, cz)} " +
                                      $"component={nav.ComponentAt(p)}");
                }
                return nav;
            }
            finally
            {
                if (splats.IsCreated)
                    splats.Dispose();
            }
        }

        static string F(float v) => v.ToString("F2", CultureInfo.InvariantCulture);

        static int CountNavigable(ScanNavGrid nav)
        {
            int n = 0;
            for (int z = 0; z < nav.height; ++z)
            for (int x = 0; x < nav.width; ++x)
                if (nav.IsNavigable(x, z)) n++;
            return n;
        }

        static bool IsWalkable(ScanNavGrid nav, int x, int z) =>
            nav.InBounds(x, z) && nav.analysis.walkable[z * nav.width + x];

        static int ClearanceAt(ScanNavGrid nav, int x, int z) =>
            nav.InBounds(x, z) ? nav.clearance[z * nav.width + x] : -1;

        // ---- checks -----------------------------------------------------------

        static bool CheckConnectivity(ScanNavGrid nav, StringBuilder report)
        {
            bool ok = true;
            int cl = nav.ComponentAt(kLivingRoom);
            int cb = nav.ComponentAt(kBedroom);
            int ch = nav.ComponentAt(kHall);
            report.AppendLine($"component(living)={cl} component(bedroom)={cb} component(hall)={ch}");

            if (cl < 0 || cb < 0 || ch < 0)
            {
                report.AppendLine("FAIL: a room centre is not navigable at all");
                ok = false;
            }
            else if (cl != cb || cl != ch)
            {
                report.AppendLine("FAIL: rooms are in different components despite doorways");
                ok = false;
            }
            else
            {
                report.AppendLine("PASS: all three rooms are one connected space");
            }
            return ok;
        }

        static bool CheckPathThroughDoorway(ScanNavGrid nav, StringBuilder report,
                                            List<Vector3> routeOut)
        {
            bool ok = true;
            var path = routeOut ?? new List<Vector3>();
            path.Clear();
            if (!nav.TryFindPath(kLivingRoom, kBedroom, path))
            {
                report.AppendLine("FAIL: no path from living room to bedroom");
                return false;
            }

            float length = 0f;
            for (int i = 1; i < path.Count; ++i)
                length += Vector3.Distance(path[i - 1], path[i]);
            report.AppendLine($"path_waypoints={path.Count} path_length_m={F(length)}");

            // Every point along the path, not just the waypoints, must be
            // navigable - string pulling could otherwise cut a corner through
            // a wall and still look fine at the waypoints.
            int sampled = 0, offGrid = 0;
            for (int i = 1; i < path.Count; ++i)
            {
                int steps = Mathf.Max(1, Mathf.CeilToInt(
                    Vector3.Distance(path[i - 1], path[i]) / (nav.cellSize * 0.5f)));
                for (int s = 0; s <= steps; ++s)
                {
                    var p = Vector3.Lerp(path[i - 1], path[i], s / (float)steps);
                    sampled++;
                    if (!nav.IsNavigable(p))
                        offGrid++;
                }
            }
            report.AppendLine($"path_samples={sampled} non_navigable={offGrid}");
            if (offGrid > 0)
            {
                report.AppendLine("FAIL: path leaves navigable space");
                ok = false;
            }
            else
            {
                report.AppendLine("PASS: every sampled point on the path is navigable");
            }

            // The only gap in the wall at x=3 is the doorway, so a legitimate
            // path must cross that plane inside the door's z range.
            bool crossedAtDoor = false, crossedElsewhere = false;
            for (int i = 1; i < path.Count; ++i)
            {
                var a = path[i - 1];
                var b = path[i];
                if ((a.x - kDoorX) * (b.x - kDoorX) > 0f)
                    continue; // segment does not cross the wall plane
                float t = Mathf.Approximately(b.x, a.x) ? 0f : (kDoorX - a.x) / (b.x - a.x);
                float z = Mathf.Lerp(a.z, b.z, t);
                if (z >= kDoorZRange.x && z <= kDoorZRange.y)
                    crossedAtDoor = true;
                else
                    crossedElsewhere = true;
            }
            report.AppendLine($"crossed_at_door={crossedAtDoor} crossed_elsewhere={crossedElsewhere}");
            if (!crossedAtDoor || crossedElsewhere)
            {
                report.AppendLine("FAIL: path did not go through the doorway");
                ok = false;
            }
            else
            {
                report.AppendLine("PASS: path crosses the wall only at the doorway");
            }
            return ok;
        }

        static bool CheckLineOfSight(ScanNavGrid nav, StringBuilder report)
        {
            bool ok = true;

            // Across the living room, passing over the couch at (-2, 0.42) which
            // is 0.85 m tall: movement is blocked there, sight should not be.
            var a = new Vector3(-3.4f, 0f, 0.0f);
            var b = new Vector3(-0.6f, 0f, 0.0f);
            bool overCouch = nav.HasLineOfSight(a, b);
            bool couchBlocksMovement = !nav.CorridorClear(a, b);
            report.AppendLine($"los_over_couch={overCouch} couch_blocks_movement={couchBlocksMovement}");
            if (!overCouch)
            {
                report.AppendLine("FAIL: a 0.85 m couch blocked sight");
                ok = false;
            }
            else if (!couchBlocksMovement)
            {
                report.AppendLine("FAIL: the couch did not block movement, so this proves nothing");
                ok = false;
            }
            else
            {
                report.AppendLine("PASS: couch blocks movement but not sight");
            }

            // Living room to bedroom on a line that misses the doorway entirely,
            // so it must pass through the solid part of the wall.
            var c = new Vector3(0.0f, 0f, 2.0f);
            var d = new Vector3(5.0f, 0f, 0.5f);
            bool throughWall = nav.HasLineOfSight(c, d);
            report.AppendLine($"los_through_wall={throughWall}");
            if (throughWall)
            {
                report.AppendLine("FAIL: sight passed through a solid wall");
                ok = false;
            }
            else
            {
                report.AppendLine("PASS: wall blocks sight");
            }

            return ok;
        }

        static bool CheckHunterReachesTarget(ScanNavGrid nav, StringBuilder report, string outDir,
                                             List<Vector3> trailOut = null)
        {
            var player = kBedroom;
            var hunter = new HunterBrain(kLivingRoom, 4242);

            const float dt = 1f / 30f;
            const int maxSteps = 30 * 90; // 90 simulated seconds
            int steps = 0, offGrid = 0;
            float travelled = 0f;
            var prev = hunter.position;
            var trail = trailOut ?? new List<Vector3>();
            trail.Clear();

            while (steps < maxSteps && !hunter.hasCaughtTarget)
            {
                hunter.Tick(dt, player, nav);
                travelled += Vector3.Distance(prev, hunter.position);
                prev = hunter.position;
                if (!nav.IsNavigable(hunter.position))
                    offGrid++;
                if (steps % 3 == 0)
                    trail.Add(hunter.position);
                steps++;
            }

            File.WriteAllLines(Path.Combine(outDir, "hunter_trail.csv"),
                trail.ConvertAll(p => $"{F(p.x)},{F(p.z)}"));

            report.AppendLine($"hunter_steps={steps} sim_seconds={F(steps * dt)} " +
                              $"caught={hunter.hasCaughtTarget} repaths={hunter.repathCount} " +
                              $"travelled_m={F(travelled)} off_grid_steps={offGrid}");

            bool ok = true;
            if (!hunter.hasCaughtTarget)
            {
                report.AppendLine("FAIL: hunter never reached the target in 90 s");
                ok = false;
            }
            else
            {
                report.AppendLine("PASS: hunter navigated between rooms and reached the target");
            }

            if (offGrid > 0)
            {
                report.AppendLine("FAIL: hunter left navigable space, so it clipped geometry");
                ok = false;
            }
            else
            {
                report.AppendLine("PASS: hunter stayed on navigable cells for every step");
            }

            // Straight-line distance is ~7 m; going through the doorway must be
            // longer. If it were not, the hunter walked through the wall.
            float direct = Vector3.Distance(kLivingRoom, kBedroom);
            report.AppendLine($"direct_distance_m={F(direct)}");
            if (travelled <= direct)
            {
                report.AppendLine("FAIL: hunter's route was no longer than a straight line " +
                                  "through the wall");
                ok = false;
            }
            else
            {
                report.AppendLine("PASS: route is longer than the direct line, as a detour " +
                                  "through the doorway must be");
            }
            return ok;
        }

        /// <summary>
        /// Renders the derived grid top-down so the analysis can be inspected
        /// rather than taken on trust: blocked cells, cells too close to an
        /// obstacle to stand in, and each navigable region in its own colour.
        /// </summary>
        static void WriteNavMap(ScanNavGrid nav, string outDir, string name, StringBuilder report,
                                List<Vector3> route = null, List<Vector3> trail = null)
        {
            const int kScale = 12;
            int w = nav.width * kScale, h = nav.height * kScale;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            var px = new Color32[w * h];

            var componentColours = new[]
            {
                new Color32(60, 190, 130, 255),
                new Color32(70, 150, 230, 255),
                new Color32(180, 130, 220, 255),
                new Color32(225, 170, 70, 255),
            };

            for (int z = 0; z < nav.height; ++z)
            for (int x = 0; x < nav.width; ++x)
            {
                int i = z * nav.width + x;
                Color32 c;
                if (!nav.analysis.walkable[i])
                    c = new Color32(26, 30, 36, 255);              // solid
                else if (!nav.IsNavigable(x, z))
                    c = new Color32(72, 62, 44, 255);              // too tight to stand
                else
                {
                    int comp = nav.component[i];
                    c = comp >= 0 ? componentColours[comp % componentColours.Length]
                                  : new Color32(90, 90, 90, 255);
                }

                for (int dy = 0; dy < kScale; ++dy)
                for (int dx = 0; dx < kScale; ++dx)
                {
                    // Texture row 0 is the bottom of the image, so mapping z
                    // straight to y puts +Z (north) at the top, like a floor plan.
                    px[(z * kScale + dy) * w + x * kScale + dx] = c;
                }
            }

            void Plot(List<Vector3> pts, Color32 colour, int radius)
            {
                if (pts == null) return;
                foreach (var p in pts)
                {
                    if (!nav.analysis.TryWorldToCell(p, out int cx, out int cz)) continue;
                    int px0 = cx * kScale + kScale / 2;
                    int py0 = cz * kScale + kScale / 2;
                    for (int dy = -radius; dy <= radius; ++dy)
                    for (int dx = -radius; dx <= radius; ++dx)
                    {
                        int fx = px0 + dx, fy = py0 + dy;
                        if (fx < 0 || fy < 0 || fx >= w || fy >= h) continue;
                        px[fy * w + fx] = colour;
                    }
                }
            }

            // Densify the route so it draws as a line rather than a few dots.
            if (route != null && route.Count > 1)
            {
                var dense = new List<Vector3>();
                for (int i = 1; i < route.Count; ++i)
                {
                    int steps = Mathf.Max(1, Mathf.CeilToInt(
                        Vector3.Distance(route[i - 1], route[i]) / (nav.cellSize * 0.25f)));
                    for (int k = 0; k <= steps; ++k)
                        dense.Add(Vector3.Lerp(route[i - 1], route[i], k / (float)steps));
                }
                Plot(dense, new Color32(255, 255, 255, 255), 1);
            }
            Plot(trail, new Color32(235, 70, 60, 255), 2);

            tex.SetPixels32(px);
            tex.Apply();
            var path = Path.Combine(outDir, name);
            File.WriteAllBytes(path, tex.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(tex);
            report.AppendLine($"nav_map={name} ({w}x{h})");
        }

        static bool CheckSealedHouseIsUnreachable(ScanNavGrid nav, StringBuilder report)
        {
            bool ok = true;

            int cl = nav.ComponentAt(kLivingRoom);
            int cb = nav.ComponentAt(kBedroom);
            report.AppendLine($"component(living)={cl} component(bedroom)={cb}");
            if (cl < 0 || cb < 0)
            {
                report.AppendLine("FAIL: a room centre is not navigable, so the control is void");
                return false;
            }
            if (cl == cb)
            {
                report.AppendLine("FAIL: sealed rooms were reported as connected");
                ok = false;
            }
            else
            {
                report.AppendLine("PASS: sealed rooms are separate components");
            }

            var path = new List<Vector3>();
            bool found = nav.TryFindPath(kLivingRoom, kBedroom, path);
            report.AppendLine($"path_found={found}");
            if (found)
            {
                report.AppendLine("FAIL: pathfinder walked through a solid wall");
                ok = false;
            }
            else
            {
                report.AppendLine("PASS: no path exists without a doorway");
            }

            var hunter = new HunterBrain(kLivingRoom, 4242);
            for (int i = 0; i < 30 * 60; ++i)
                hunter.Tick(1f / 30f, kBedroom, nav);
            report.AppendLine($"sealed_hunter_caught={hunter.hasCaughtTarget}");
            if (hunter.hasCaughtTarget)
            {
                report.AppendLine("FAIL: hunter reached a target it could not possibly reach");
                ok = false;
            }
            else
            {
                report.AppendLine("PASS: hunter could not reach the sealed room");
            }
            return ok;
        }
    }
}
