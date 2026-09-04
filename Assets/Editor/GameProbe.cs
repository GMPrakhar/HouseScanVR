using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace HouseScan.EditorTools
{
    /// <summary>
    /// Runs a real round of the game headlessly: loads a scan through
    /// HouseScanLoader, lets GameDirector build navigation and spawn hunters,
    /// then steps the round at a fixed rate.
    ///
    /// This exercises the actual MonoBehaviour wiring - the onScanReady
    /// handshake, spawn selection and the per-frame loop - rather than the
    /// pathfinding in isolation, which is what NavProbe already covers.
    /// </summary>
    public static class GameProbe
    {
        const float kStep = 1f / 72f;          // Quest 3 display rate
        const float kMaxSeconds = 120f;

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

                ok &= RoundIsWinnable(Path.Combine(scans, "house_doors.ply"), report);
                ok &= FleeingPlayerSurvivesLonger(Path.Combine(scans, "house_doors.ply"), report);
                ok &= SealedHouseProtectsThePlayer(Path.Combine(scans, "house_sealed.ply"), report);

                report.AppendLine();
                report.AppendLine("RESULT: " + (ok ? "PASS" : "FAIL"));
                File.WriteAllText(Path.Combine(outDir, "game_report.txt"), report.ToString());
                exit = ok ? 0 : 1;
            }
            catch (System.Exception e)
            {
                report.AppendLine("EXCEPTION " + e);
                report.AppendLine("RESULT: FAIL");
                exit = 2;
            }

            Debug.Log("[GameProbe]\n" + report);
            EditorApplication.Exit(exit);
        }

        /// <summary>
        /// Builds the minimal live object graph: loader -> director, with a
        /// transform standing in for the player rig.
        /// </summary>
        static (GameDirector dir, HouseScanLoader loader, Transform player) Spawn(
            string scanPath, StringBuilder report)
        {
            var go = new GameObject("GameProbeScene");

            var loader = go.AddComponent<HouseScanLoader>();
            loader.m_ScanPath = scanPath;
            loader.m_LoadOnStart = false;      // we drive Load() ourselves
            loader.m_AnalyzeOnLoad = true;

            var playerGo = new GameObject("Player");
            playerGo.transform.SetParent(go.transform);

            var dir = go.AddComponent<GameDirector>();
            dir.m_Loader = loader;
            dir.m_Rig = null;                  // PlayerPosition() falls back to transform
            dir.m_HunterCount = 3;
            dir.m_BeginOnScanReady = false;    // start explicitly, after placing the player

            return (dir, loader, go.transform);
        }

        static bool RoundIsWinnable(string scanPath, StringBuilder report)
        {
            report.AppendLine("== round: stationary player ==");
            var (dir, loader, player) = Spawn(scanPath, report);
            try
            {
                if (!loader.Load())
                {
                    report.AppendLine($"FAIL load: {loader.lastError}");
                    return false;
                }
                report.AppendLine($"splats={loader.loadedSplatCount} " +
                                  $"walkable_sqm={loader.analysis.WalkableAreaSqm:F1} " +
                                  $"spawn_points={loader.spawnPoints.Count}");

                // Deliberately awkward: this spot is inside the coffee table's
                // radius, so it is not itself navigable. The player in VR stands
                // wherever they physically stand, and the round still has to work.
                player.position = new Vector3(-1.5f, 0f, 0f);

                if (!dir.BeginRound())
                {
                    report.AppendLine("FAIL BeginRound returned false");
                    return false;
                }

                bool snapped = dir.nav.TrySnap(player.position, out var reach);
                report.AppendLine($"player at {V(player.position)} " +
                                  $"navigable={dir.nav.IsNavigable(player.position)} " +
                                  $"nearest_navigable={(snapped ? V(reach) : "none")} " +
                                  $"gap={(snapped ? Vector3.Distance(player.position, reach) : -1f):F2} m");

                bool ok = true;
                ok &= Expect(dir.hunters.Count == 3, $"hunters={dir.hunters.Count} (want 3)", report);
                ok &= Expect(dir.roundNumber == 1, $"round={dir.roundNumber}", report);
                ok &= Expect(dir.isRoundActive, "round is active", report);

                // Every hunter must start somewhere the player could also stand,
                // and not on top of the player.
                for (int i = 0; i < dir.hunters.Count; ++i)
                {
                    var h = dir.hunters[i];
                    float d = Vector3.Distance(h.position, player.position);
                    ok &= Expect(Navigable(dir, h.position),
                                 $"hunter{i} spawn navigable at {V(h.position)}", report);
                    ok &= Expect(d >= dir.m_MinSpawnDistance - 0.5f,
                                 $"hunter{i} spawn distance {d:F2} m", report);
                }

                float t = Step(dir, player, null, report);
                ok &= Expect(dir.isCaught, $"player caught after {t:F1}s", report);
                ok &= Expect(!dir.isRoundActive, "round ended on capture", report);

                // A second round must be startable without leaking hunters.
                ok &= Expect(dir.BeginRound(), "round 2 begins", report);
                ok &= Expect(dir.hunters.Count == 3,
                             $"round 2 hunters={dir.hunters.Count}", report);
                ok &= Expect(dir.roundNumber == 2, $"round={dir.roundNumber}", report);
                ok &= Expect(!dir.isCaught, "round 2 resets caught flag", report);
                return ok;
            }
            finally { Object.DestroyImmediate(dir.gameObject); }
        }

        /// <summary>
        /// A player who runs away should last measurably longer than one who
        /// stands still. Without this, "caught" could just mean the hunters
        /// spawned close and walked in a straight line.
        /// </summary>
        static bool FleeingPlayerSurvivesLonger(string scanPath, StringBuilder report)
        {
            report.AppendLine();
            report.AppendLine("== round: fleeing player ==");
            var (dir, loader, player) = Spawn(scanPath, report);
            try
            {
                if (!loader.Load()) { report.AppendLine("FAIL load"); return false; }
                player.position = new Vector3(-1.5f, 0f, 0f);
                if (!dir.BeginRound()) { report.AppendLine("FAIL BeginRound"); return false; }

                // Unlike the round test above, this one needs the player to start
                // on navigable ground, or they cannot take a single step and the
                // comparison would be meaningless.
                if (!dir.nav.TrySnap(player.position, out var start))
                {
                    report.AppendLine("FAIL no navigable start");
                    return false;
                }
                player.position = start;
                report.AppendLine($"start {V(start)} navigable={dir.nav.IsNavigable(start)}");

                dir.BeginRound();
                float baseline = Step(dir, player, null, report);
                bool caughtStanding = dir.isCaught;

                player.position = start;
                dir.BeginRound();
                var from = player.position;
                float fleeing = Step(dir, player, Flee, report);
                float travelled = m_Travelled;

                report.AppendLine($"standing={baseline:F1}s fleeing={fleeing:F1}s " +
                                  $"travelled={travelled:F1} m " +
                                  $"displaced={Vector3.Distance(from, player.position):F1} m");

                bool ok = Expect(caughtStanding, "standing player was caught", report);
                ok &= Expect(travelled > 1f,
                             $"fleeing player actually moved ({travelled:F1} m)", report);
                ok &= Expect(fleeing > baseline,
                             $"fleeing survives longer ({fleeing:F1}s > {baseline:F1}s)", report);
                return ok;
            }
            finally { Object.DestroyImmediate(dir.gameObject); }
        }

        /// <summary>
        /// Negative control on the sealed scan, whose rooms are disconnected.
        ///
        /// Two things must hold. Spawn selection must never place a hunter in a
        /// region it cannot leave - such a hunter would just stand in an empty
        /// room forever. And a hunter that *is* walled off must never reach the
        /// player, which is what proves nothing walks through geometry.
        /// </summary>
        static bool SealedHouseProtectsThePlayer(string scanPath, StringBuilder report)
        {
            report.AppendLine();
            report.AppendLine("== negative control: sealed house ==");
            var (dir, loader, player) = Spawn(scanPath, report);
            try
            {
                if (!loader.Load()) { report.AppendLine("FAIL load"); return false; }
                player.position = new Vector3(4.2f, 0f, -1.0f);   // bedroom, isolated
                if (!dir.BeginRound()) { report.AppendLine("FAIL BeginRound"); return false; }

                report.AppendLine($"components={dir.nav.componentCount} " +
                                  $"hunters={dir.hunters.Count}");
                bool ok = Expect(dir.nav.componentCount >= 2,
                                 $"sealed scan is fragmented ({dir.nav.componentCount} regions)",
                                 report);

                int playerComp = dir.nav.ComponentAt(player.position);
                int stranded = 0;
                foreach (var h in dir.hunters)
                    if (dir.nav.ComponentAt(h.position) != playerComp) stranded++;
                ok &= Expect(stranded == 0,
                             $"every hunter can reach the player ({stranded} stranded)", report);

                // Now place one deliberately on the far side of a wall.
                int otherComp = -1;
                for (int c = 0; c < dir.nav.componentCount; ++c)
                    if (c != playerComp && dir.nav.componentSizes[c] > 20) { otherComp = c; break; }
                if (otherComp < 0)
                {
                    report.AppendLine("  FAIL no second region large enough to test");
                    return false;
                }

                var walledOff = FirstCellOf(dir, otherComp);
                var intruder = new HunterBrain(walledOff, 3u);
                report.AppendLine($"walled-off hunter at {V(walledOff)} " +
                                  $"(region {otherComp}), player in region {playerComp}");

                float t = 0f, closest = float.MaxValue;
                while (t < kMaxSeconds)
                {
                    intruder.Tick(kStep, player.position, dir.nav);
                    closest = Mathf.Min(closest,
                                        Vector3.Distance(intruder.position, player.position));
                    if (intruder.hasCaughtTarget) break;
                    t += kStep;
                }
                ok &= Expect(!intruder.hasCaughtTarget,
                             $"walled-off hunter never reaches the player in {t:F0}s " +
                             $"(closest {closest:F2} m)", report);
                ok &= Expect(dir.nav.ComponentAt(intruder.position) == otherComp,
                             "walled-off hunter stayed in its own region", report);
                return ok;
            }
            finally { Object.DestroyImmediate(dir.gameObject); }
        }

        static Vector3 FirstCellOf(GameDirector dir, int component)
        {
            var nav = dir.nav;
            for (int z = 0; z < nav.height; ++z)
            for (int x = 0; x < nav.width; ++x)
                if (nav.component[z * nav.width + x] == component)
                    return nav.analysis.CellToWorld(x, z);
            return Vector3.zero;
        }

        /// <summary>Steps the round until capture or the time limit.</summary>
        static float m_Travelled;

        static float Step(GameDirector dir, Transform player,
                          System.Func<GameDirector, Vector3, Vector3> move, StringBuilder report)
        {
            float t = 0f;
            m_Travelled = 0f;
            while (t < kMaxSeconds && dir.isRoundActive && !dir.isCaught)
            {
                if (move != null)
                {
                    var next = move(dir, player.position);
                    if (dir.nav.CorridorClear(player.position, next))
                    {
                        m_Travelled += Vector3.Distance(player.position, next);
                        player.position = next;
                    }
                }
                dir.Tick(kStep);
                t += kStep;
            }
            return t;
        }

        /// <summary>
        /// Moves the player directly away from the nearest hunter, sliding
        /// along the eight grid directions when the retreat is blocked.
        /// </summary>
        static Vector3 Flee(GameDirector dir, Vector3 p)
        {
            const float speed = 1.6f;
            float step = speed * kStep;

            Vector3 away = Vector3.zero;
            float nearest = float.MaxValue;
            foreach (var h in dir.hunters)
            {
                float d = Vector3.Distance(h.position, p);
                if (d < nearest) { nearest = d; away = p - h.position; }
            }
            if (away.sqrMagnitude < 1e-6f) return p;
            away.y = 0f;
            away.Normalize();

            // Try the retreat direction, then progressively wider deflections,
            // so the player follows walls instead of pressing into them.
            for (int i = 0; i < 8; ++i)
            {
                float deg = (i + 1) / 2 * 45f * (i % 2 == 0 ? 1f : -1f);
                var dir2 = Quaternion.Euler(0f, deg, 0f) * away;
                var next = p + dir2 * step;
                if (dir.nav.CorridorClear(p, next)) return next;
            }
            return p;
        }

        static bool Navigable(GameDirector dir, Vector3 p) =>
            dir.nav.analysis.TryWorldToCell(p, out int x, out int z) &&
            dir.nav.IsNavigable(x, z);

        static bool Expect(bool cond, string what, StringBuilder report)
        {
            report.AppendLine((cond ? "  ok   " : "  FAIL ") + what);
            return cond;
        }

        static string V(Vector3 v) => $"({v.x:F2}, {v.z:F2})";

        static string ArgOr(string name, string fallback)
        {
            var args = System.Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; ++i)
                if (args[i] == name) return args[i + 1];
            return fallback;
        }
    }
}
