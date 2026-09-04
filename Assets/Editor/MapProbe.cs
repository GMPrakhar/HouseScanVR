using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace HouseScan.EditorTools
{
    /// <summary>
    /// Verifies the in-app "map your house by walking" feature without a
    /// headset, by walking a simulated player around a synthetic house and
    /// checking that the map they produce agrees with the house.
    ///
    /// The synthetic splat scan is the ground truth here: it is an independent
    /// description of the same building, derived from geometry rather than from
    /// footsteps. If walking around a house reconstructs the house, the feature
    /// works; if it invents floor through walls, or misses rooms the player
    /// visited, this fails.
    /// </summary>
    public static class MapProbe
    {
        const float kWalkSpeed = 1.1f;         // m/s, an unhurried indoor pace
        const float kSampleHz = 15f;
        const float kEyeHeight = 1.65f;
        /// Must match RoomMappingSession.m_AgentRadius: agents on a walked map
        /// are narrower than the person who walked it.
        const float kMappedAgentRadius = 0.20f;

        public static void Run()
        {
            var report = new StringBuilder();
            bool ok = true;
            int exit = 0;

            try
            {
                string outDir = Path.Combine(Directory.GetCurrentDirectory(), "Build", "Nav");
                Directory.CreateDirectory(outDir);

                string scans = ArgOr("-scanDir",
                    Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "vr-work", "scans"));
                string doors = Path.Combine(scans, "house_doors.ply");

                ok &= WalkingTheHouseReconstructsIt(doors, outDir, report);
                ok &= MappedHouseIsPlayable(doors, report);
                ok &= HeadsetOffSamplesAreIgnored(doors, report);
                ok &= MappingOneRoomStaysInThatRoom(doors, report);
                ok &= SurvivesSaveAndReload(doors, report);

                report.AppendLine();
                report.AppendLine("RESULT: " + (ok ? "PASS" : "FAIL"));
                File.WriteAllText(Path.Combine(outDir, "map_report.txt"), report.ToString());
                exit = ok ? 0 : 1;
            }
            catch (System.Exception e)
            {
                report.AppendLine("EXCEPTION " + e);
                report.AppendLine("RESULT: FAIL");
                exit = 2;
            }

            Debug.Log("[MapProbe]\n" + report);
            EditorApplication.Exit(exit);
        }

        // ---- ground truth --------------------------------------------------

        class Truth
        {
            public HouseScanLoader loader;
            public ScanLevelAnalysis analysis;
            public ScanNavGrid nav;
            public GameObject go;
        }

        static Truth Load(string scanPath, StringBuilder report)
        {
            var go = new GameObject("MapProbeTruth");
            var loader = go.AddComponent<HouseScanLoader>();
            loader.m_ScanPath = scanPath;
            loader.m_LoadOnStart = false;
            loader.m_AnalyzeOnLoad = true;
            if (!loader.Load())
            {
                report.AppendLine($"FAIL load {scanPath}: {loader.lastError}");
                Object.DestroyImmediate(go);
                return null;
            }
            return new Truth
            {
                go = go,
                loader = loader,
                analysis = loader.analysis,
                nav = ScanNavGrid.Build(loader.analysis, 0.30f),
            };
        }

        /// <summary>
        /// Produces the poses a player would generate walking a tour of the
        /// house: a route through widely separated points, followed exactly,
        /// sampled at a plausible headset rate.
        /// </summary>
        static List<Vector3> WalkTour(Truth t, int stops, out float routeLength)
        {
            var route = WalkRoutes.Cover(t.analysis, t.nav, stops);
            routeLength = WalkRoutes.Length(route);
            return WalkRoutes.ToPoses(route, t.analysis.floorY, kEyeHeight, kWalkSpeed, kSampleHz);
        }

        // ---- checks --------------------------------------------------------

        static bool WalkingTheHouseReconstructsIt(string scanPath, string outDir, StringBuilder report)
        {
            report.AppendLine("== walking reconstructs the house ==");
            var t = Load(scanPath, report);
            if (t == null) return false;
            try
            {
                var poses = WalkTour(t, 14, out float routeLength);
                report.AppendLine($"route={routeLength:F1} m poses={poses.Count} " +
                                  $"({poses.Count / kSampleHz:F0}s of walking)");
                if (poses.Count == 0) { report.AppendLine("FAIL empty route"); return false; }

                var mapper = new RoomMapper(t.analysis.floorY, t.analysis.cellSize);
                foreach (var p in poses) mapper.AddPose(p);

                var mapped = mapper.Bake();
                if (mapped == null) { report.AppendLine("FAIL bake returned null"); return false; }

                // Precision: floor the map claims is walkable, that the scan
                // agrees is walkable. A false positive here is floor invented
                // through a wall, which would let hunters walk into the street.
                int claimed = 0, correct = 0;
                foreach (var c in Cells(mapped))
                {
                    if (!mapped.walkable[c.i]) continue;
                    claimed++;
                    if (t.analysis.TryWorldToCell(c.world, out int gx, out int gz) &&
                        t.analysis.walkable[gz * t.analysis.gridWidth + gx])
                        correct++;
                }
                float precision = claimed == 0 ? 0f : correct / (float)claimed;

                // Recall is measured against the floor the player's body
                // actually passed over. A map cannot know about a room nobody
                // entered, so scoring it on the whole house would only measure
                // how thorough the test's route was. What this does catch is a
                // trail with holes punched in it by dropped frames.
                float band = mapper.bodyRadius;
                int covered = 0, found = 0;
                foreach (var c in Cells(t.analysis))
                {
                    if (!t.analysis.walkable[c.i]) continue;
                    if (NearestPoseDistance(poses, c.world) > band) continue;
                    covered++;
                    if (mapped.TryWorldToCell(c.world, out int mx, out int mz) &&
                        mapped.walkable[mz * mapped.gridWidth + mx])
                        found++;
                }
                float recall = covered == 0 ? 0f : found / (float)covered;

                // Reported, not asserted: how much of the house this particular
                // route happened to visit. It says more about the route than
                // about the mapper.
                int houseCells = 0, houseFound = 0;
                foreach (var c in Cells(t.analysis))
                {
                    if (!t.nav.IsNavigable(c.x, c.z)) continue;
                    if (t.nav.component[c.i] != t.nav.largestComponent) continue;
                    houseCells++;
                    if (mapped.TryWorldToCell(c.world, out int mx, out int mz) &&
                        mapped.walkable[mz * mapped.gridWidth + mx])
                        houseFound++;
                }

                report.AppendLine($"mapped={mapped.WalkableAreaSqm:F1} m² " +
                                  $"grid={mapped.gridWidth}x{mapped.gridHeight} " +
                                  $"precision={precision:P1} ({correct}/{claimed}) " +
                                  $"recall={recall:P1} ({found}/{covered}) " +
                                  $"house_visited={(houseCells == 0 ? 0f : houseFound / (float)houseCells):P0}");

                bool ok = Expect(precision >= 0.95f,
                    $"map does not invent floor (precision {precision:P1})", report);
                ok &= Expect(recall >= 0.98f,
                    $"trail has no holes in it (recall {recall:P1})", report);

                // The whole point of walking is that you cannot walk through a
                // wall, so everything mapped must be one connected region.
                var nav = ScanNavGrid.Build(mapped, kMappedAgentRadius);
                report.AppendLine($"mapped nav: components={nav.componentCount} " +
                                  $"largest={(nav.componentCount > 0 ? nav.componentSizes[nav.largestComponent] : 0)} cells");
                ok &= Expect(nav.componentCount >= 1, "mapped space is navigable at all", report);

                int navigableCells = 0;
                foreach (var c in Cells(mapped))
                    if (nav.IsNavigable(c.x, c.z)) navigableCells++;
                int inLargest = nav.componentCount > 0 ? nav.componentSizes[nav.largestComponent] : 0;
                ok &= Expect(navigableCells > 0 && inLargest / (float)navigableCells >= 0.9f,
                    $"one dominant region ({inLargest}/{navigableCells} cells)", report);

                ReportIndoorReachTradeoff(t, poses, report);
                ok &= SightClassificationMatchesTruth(t, mapped, report);
                WriteMap(mapped, poses, Path.Combine(outDir, "map_walked.png"));
                return ok;
            }
            finally { Object.DestroyImmediate(t.go); }
        }

        /// <summary>
        /// The gap-classification heuristic is the one part of mapping that is
        /// a guess rather than a measurement, so it gets checked against the
        /// scan's own sight-blocking data.
        ///
        /// The two errors are not symmetric and are scored separately. Calling
        /// a real wall see-through lets hunters spot the player through the
        /// side of the house, and is close to unacceptable. Calling unmapped
        /// space a wall is the safe direction: it only means the player cannot
        /// be seen across ground nobody has mapped.
        /// </summary>
        static bool SightClassificationMatchesTruth(Truth t, ScanLevelAnalysis mapped,
            StringBuilder report)
        {
            var r = ScoreSight(t, mapped);
            report.AppendLine($"sight: walls_kept={r.wallRecall:P1} " +
                              $"({r.walls - r.wallsMissed}/{r.walls}) " +
                              $"see_over_furniture={r.overFurniture:P1} " +
                              $"({r.furnitureSeenOver}/{r.furniture}) " +
                              $"unmapped_void_treated_as_wall={r.extraWalls}");

            bool ok = Expect(r.walls > 20 && r.wallRecall >= 0.97f,
                $"real walls still block sight ({r.wallsMissed} missed of {r.walls})", report);
            ok &= Expect(r.furniture > 5 && r.overFurniture >= 0.45f,
                $"hunters can see over enclosed furniture " +
                $"({r.furnitureSeenOver}/{r.furniture})", report);
            return ok;
        }

        struct SightScore
        {
            public int walls, wallsMissed, furniture, furnitureSeenOver, extraWalls;
            public float wallRecall => walls == 0 ? 0f : 1f - wallsMissed / (float)walls;
            public float overFurniture => furniture == 0 ? 0f : furnitureSeenOver / (float)furniture;
        }

        static SightScore ScoreSight(Truth t, ScanLevelAnalysis mapped)
        {
            var score = new SightScore();

            foreach (var c in Cells(mapped))
            {
                if (mapped.walkable[c.i]) continue;
                if (!t.analysis.TryWorldToCell(c.world, out int gx, out int gz)) continue;
                int gi = gz * t.analysis.gridWidth + gx;

                bool mapBlocks = mapped.sightBlockCounts[c.i] > 0;
                bool truthBlocks = t.analysis.sightBlockCounts[gi] > 0;
                // Something has to actually be there. Empty space beyond the
                // walls of the house is neither wall nor furniture, and counting
                // it as furniture would swamp the measurement with cells that
                // contain nothing at all.
                bool truthSolid = t.analysis.obstacleCounts[gi] > 0;

                if (truthBlocks)
                {
                    score.walls++;
                    if (!mapBlocks) score.wallsMissed++;
                }
                else if (truthSolid)
                {
                    // Solid but low: a sofa, a table, a bed. The player walked
                    // around it, and should be able to watch over it.
                    score.furniture++;
                    if (!mapBlocks) score.furnitureSeenOver++;
                }
                else if (mapBlocks)
                {
                    // Nothing there, but outside the area the player mapped.
                    score.extraWalls++;
                }
            }
            return score;
        }

        /// <summary>
        /// Reports the trade-off behind RoomMapper.indoorReach, which decides
        /// how far past the walked trail still counts as indoors.
        ///
        /// Closing more aggressively fills in the rooms the player walked round
        /// the edge of, so hunters can see across them - but it also swallows
        /// thin interior walls with a corridor on either side, and hunters end
        /// up seeing through those. There is no setting that gets both right,
        /// because footsteps do not say which is which. This prints the curve
        /// so the choice is made with the numbers in view rather than by feel.
        /// </summary>
        static void ReportIndoorReachTradeoff(Truth t, List<Vector3> poses, StringBuilder report)
        {
            report.AppendLine("  indoor_reach  walls_kept  see_over_furniture");
            foreach (float reach in new[] { 0.001f, 0.25f, 0.50f, 0.75f, 1.00f, 1.25f, 1.75f, 2.50f })
            {
                var m = new RoomMapper(t.analysis.floorY, t.analysis.cellSize) { indoorReach = reach };
                foreach (var p in poses) m.AddPose(p);
                var score = ScoreSight(t, m.Bake());
                report.AppendLine($"  {reach,10:F2} m  {score.wallRecall,9:P1}  {score.overFurniture,17:P1}");
            }
        }

        static bool MappedHouseIsPlayable(string scanPath, StringBuilder report)
        {
            report.AppendLine();
            report.AppendLine("== a mapped house is playable ==");
            var t = Load(scanPath, report);
            if (t == null) return false;

            GameObject sceneGo = null;
            try
            {
                var poses = WalkTour(t, 14, out _);
                sceneGo = new GameObject("MapProbeGame");
                var session = sceneGo.AddComponent<RoomMappingSession>();
                session.m_LoadOnStart = false;
                session.m_CellSize = t.analysis.cellSize;
                session.BeginMapping(t.analysis.floorY);

                bool ok = Expect(session.stage == RoomMappingSession.Stage.Mapping,
                    "session starts mapping", report);
                ok &= Expect(!session.FinishMapping(),
                    "cannot finish with nothing mapped", report);

                foreach (var p in poses) session.Sample(p);

                report.AppendLine($"session: {session.mappedAreaSqm:F1} m² " +
                                  $"walked {session.mapper.pathLength:F0} m " +
                                  $"stage={session.stage} progress={session.progress01:P0}");
                ok &= Expect(session.stage == RoomMappingSession.Stage.ReadyToFinish,
                    "enough mapped to finish", report);
                ok &= Expect(session.FinishMapping(),
                    $"finish succeeds ({session.lastError})", report);
                ok &= Expect(session.stage == RoomMappingSession.Stage.Complete,
                    "stage is Complete", report);
                ok &= Expect(session.spawnPoints.Count >= 4,
                    $"spawn points={session.spawnPoints.Count}", report);

                // The real payoff: the existing game runs on the mapped level
                // through ILevelSource, with no scan loaded at all.
                var playerGo = new GameObject("Player");
                playerGo.transform.SetParent(sceneGo.transform);
                var dir = sceneGo.AddComponent<GameDirector>();
                dir.m_Loader = null;
                dir.m_LevelSource = session;
                dir.m_Rig = null;
                dir.m_HunterCount = 3;
                dir.m_BeginOnScanReady = false;

                sceneGo.transform.position = session.spawnPoints[0];
                ok &= Expect(dir.BeginRound(), "round begins on a mapped house", report);
                if (!dir.isRoundActive) return false;

                ok &= Expect(dir.hunters.Count == 3, $"hunters={dir.hunters.Count}", report);

                var startPositions = new List<Vector3>();
                float nearest = float.MaxValue;
                foreach (var h in dir.hunters)
                {
                    startPositions.Add(h.position);
                    nearest = Mathf.Min(nearest, Vector3.Distance(h.position, sceneGo.transform.position));
                    ok &= Expect(dir.nav.IsNavigable(h.position),
                        $"hunter spawned on mapped floor {V(h.position)}", report);
                }
                report.AppendLine($"player at {V(sceneGo.transform.position)} " +
                                  $"nearest hunter {nearest:F2} m " +
                                  $"(min spawn distance {dir.m_MinSpawnDistance:F1} m)");
                ok &= Expect(nearest >= dir.m_MinSpawnDistance - 0.5f,
                    $"hunters do not spawn on top of the player ({nearest:F2} m)", report);

                float t0 = 0f;
                while (t0 < 120f && dir.isRoundActive)
                {
                    dir.Tick(1f / 72f);
                    t0 += 1f / 72f;
                }

                float travelled = 0f;
                for (int i = 0; i < dir.hunters.Count; ++i)
                    travelled = Mathf.Max(travelled,
                        Vector3.Distance(startPositions[i], dir.hunters[i].position));

                report.AppendLine($"round lasted {t0:F1}s, furthest hunter moved {travelled:F1} m");
                ok &= Expect(dir.isCaught,
                    $"hunters catch the player on a mapped house ({t0:F1}s)", report);
                // Without this, "caught" could just mean a hunter happened to
                // spawn within the catch radius and the mapped navigation was
                // never used at all.
                ok &= Expect(travelled > 1f,
                    $"a hunter navigated the mapped house to get there ({travelled:F1} m)", report);
                return ok;
            }
            finally
            {
                if (sceneGo != null) Object.DestroyImmediate(sceneGo);
                Object.DestroyImmediate(t.go);
            }
        }

        /// <summary>
        /// A headset that is taken off and carried across the room would
        /// otherwise paint a walkable trail straight through the walls.
        /// </summary>
        static bool HeadsetOffSamplesAreIgnored(string scanPath, StringBuilder report)
        {
            report.AppendLine();
            report.AppendLine("== headset off the head ==");
            var t = Load(scanPath, report);
            if (t == null) return false;
            try
            {
                float floorY = t.analysis.floorY;
                var mapper = new RoomMapper(floorY, t.analysis.cellSize);

                var poses = WalkTour(t, 14, out _);
                foreach (var p in poses) mapper.AddPose(p);
                int walkedCells = mapper.visitedCellCount;

                // Set it down on a table, then carry it across the house at
                // hip height, which is well below standing head height.
                var carried = new List<Vector3>();
                Vector3 a = poses[0], b = poses[poses.Count / 2];
                for (int i = 0; i <= 200; ++i)
                {
                    var p = Vector3.Lerp(a, b, i / 200f);
                    carried.Add(new Vector3(p.x, floorY + 0.75f, p.z));
                }
                foreach (var p in carried) mapper.AddPose(p);

                report.AppendLine($"cells before={walkedCells} after={mapper.visitedCellCount} " +
                                  $"rejected={mapper.rejectedSamples}/{mapper.sampleCount}");

                bool ok = Expect(mapper.rejectedSamples >= carried.Count,
                    $"low samples rejected ({mapper.rejectedSamples})", report);
                ok &= Expect(mapper.visitedCellCount == walkedCells,
                    "carried headset added no floor", report);
                return ok;
            }
            finally { Object.DestroyImmediate(t.go); }
        }

        /// <summary>
        /// Negative control. Mapping only the living room must not produce a
        /// level that includes the bedroom: if it does, the map is guessing
        /// rather than recording, and the guess would be a wall the hunters
        /// could walk through.
        /// </summary>
        static bool MappingOneRoomStaysInThatRoom(string scanPath, StringBuilder report)
        {
            report.AppendLine();
            report.AppendLine("== mapping one room only ==");
            var t = Load(scanPath, report);
            if (t == null) return false;
            try
            {
                var full = WalkTour(t, 14, out _);
                var firstHalf = full.GetRange(0, full.Count / 3);

                var mapper = new RoomMapper(t.analysis.floorY, t.analysis.cellSize);
                foreach (var p in firstHalf) mapper.AddPose(p);
                var mapped = mapper.Bake();
                if (mapped == null) { report.AppendLine("FAIL bake null"); return false; }

                // Nowhere the player never went may appear as floor.
                int unvisitedClaimed = 0;
                foreach (var c in Cells(mapped))
                {
                    if (!mapped.walkable[c.i]) continue;
                    if (NearestPoseDistance(firstHalf, c.world) > 0.6f) unvisitedClaimed++;
                }
                float partialArea = mapped.WalkableAreaSqm;

                var fullMapper = new RoomMapper(t.analysis.floorY, t.analysis.cellSize);
                foreach (var p in full) fullMapper.AddPose(p);
                float fullArea = fullMapper.Bake().WalkableAreaSqm;

                report.AppendLine($"partial={partialArea:F1} m² full={fullArea:F1} m² " +
                                  $"claimed_unvisited={unvisitedClaimed}");

                bool ok = Expect(unvisitedClaimed == 0,
                    "no floor claimed where nobody walked", report);
                ok &= Expect(partialArea < fullArea * 0.75f,
                    $"partial map is genuinely smaller ({partialArea:F1} < {fullArea * 0.75f:F1})", report);
                return ok;
            }
            finally { Object.DestroyImmediate(t.go); }
        }

        /// <summary>
        /// You map your house once, not every time you put the headset on, so
        /// the saved map has to come back identical.
        /// </summary>
        static bool SurvivesSaveAndReload(string scanPath, StringBuilder report)
        {
            report.AppendLine();
            report.AppendLine("== save and reload ==");
            var t = Load(scanPath, report);
            if (t == null) return false;
            try
            {
                var poses = WalkTour(t, 14, out _);
                var mapper = new RoomMapper(t.analysis.floorY, t.analysis.cellSize);
                foreach (var p in poses) mapper.AddPose(p);
                var before = mapper.Bake();

                var restored = RoomMapper.FromJson(mapper.ToJson());
                if (restored == null) { report.AppendLine("FAIL FromJson null"); return false; }
                var after = restored.Bake();

                bool same = before.gridWidth == after.gridWidth &&
                            before.gridHeight == after.gridHeight;
                int diff = 0;
                if (same)
                    for (int i = 0; i < before.walkable.Length; ++i)
                        if (before.walkable[i] != after.walkable[i]) diff++;

                report.AppendLine($"before={before.WalkableAreaSqm:F1} m² " +
                                  $"after={after.WalkableAreaSqm:F1} m² " +
                                  $"grid_match={same} differing_cells={diff}");

                bool ok = Expect(same, "grid dimensions survive a round trip", report);
                ok &= Expect(diff == 0, $"walkable cells identical ({diff} differ)", report);
                ok &= Expect(Mathf.Abs(restored.pathLength - mapper.pathLength) < 0.01f,
                    "walked distance preserved", report);
                return ok;
            }
            finally { Object.DestroyImmediate(t.go); }
        }

        // ---- helpers -------------------------------------------------------

        struct Cell { public int x, z, i; public Vector3 world; }

        static IEnumerable<Cell> Cells(ScanLevelAnalysis a)
        {
            for (int z = 0; z < a.gridHeight; ++z)
            for (int x = 0; x < a.gridWidth; ++x)
                yield return new Cell { x = x, z = z, i = z * a.gridWidth + x, world = a.CellToWorld(x, z) };
        }

        static float NearestPoseDistance(List<Vector3> poses, Vector3 p)
        {
            float best = float.MaxValue;
            foreach (var q in poses)
            {
                float dx = q.x - p.x, dz = q.z - p.z;
                float d = dx * dx + dz * dz;
                if (d < best) best = d;
            }
            return Mathf.Sqrt(best);
        }

        /// <summary>Renders the mapped floor with the walked path over it, so a
        /// failure can be looked at rather than only read about.</summary>
        static void WriteMap(ScanLevelAnalysis a, List<Vector3> poses, string path)
        {
            const int scale = 6;
            int w = a.gridWidth * scale, h = a.gridHeight * scale;
            var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
            var px = new Color32[w * h];

            for (int z = 0; z < a.gridHeight; ++z)
            for (int x = 0; x < a.gridWidth; ++x)
            {
                int i = z * a.gridWidth + x;
                Color32 c = a.walkable[i]
                    ? new Color32(60, 170, 90, 255)
                    : a.sightBlockCounts[i] > 0
                        ? new Color32(40, 40, 48, 255)      // wall: blocks sight too
                        : new Color32(120, 105, 70, 255);   // furniture: see over it
                for (int dz = 0; dz < scale; ++dz)
                for (int dx = 0; dx < scale; ++dx)
                    px[(z * scale + dz) * w + x * scale + dx] = c;
            }

            foreach (var p in poses)
            {
                if (!a.TryWorldToCell(p, out int cx, out int cz)) continue;
                int bx = cx * scale + scale / 2, bz = cz * scale + scale / 2;
                for (int dz = -1; dz <= 1; ++dz)
                for (int dx = -1; dx <= 1; ++dx)
                {
                    int x = bx + dx, z = bz + dz;
                    if (x < 0 || z < 0 || x >= w || z >= h) continue;
                    px[z * w + x] = new Color32(255, 255, 255, 255);
                }
            }

            tex.SetPixels32(px);
            tex.Apply();
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            Debug.Log($"[MapProbe] wrote {path}");
        }

        static bool Expect(bool cond, string what, StringBuilder report)
        {
            report.AppendLine((cond ? "  ok   " : "  FAIL ") + what);
            return cond;
        }

        static string V(Vector3 v) => $"({v.x:F2},{v.z:F2})";

        static string ArgOr(string name, string fallback)
        {
            var args = System.Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; ++i)
                if (args[i] == name) return args[i + 1];
            return fallback;
        }
    }
}
