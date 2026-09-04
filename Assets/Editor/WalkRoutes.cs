using System.Collections.Generic;
using UnityEngine;

namespace HouseScan.EditorTools
{
    /// <summary>
    /// Builds routes that a person could plausibly have walked through a scanned
    /// house, for tests and for recordings.
    ///
    /// This is deliberately not the camera tour in FlythroughRecorder. A tour
    /// wants a short, pretty path that reads well on screen; someone mapping
    /// their house wants coverage, and will walk into corners and back out of
    /// them. Using the tour route to test mapping made the map look far worse
    /// than the feature deserved, because the walk was never trying to cover
    /// anything.
    /// </summary>
    public static class WalkRoutes
    {
        /// <summary>
        /// A route that visits widely separated parts of the largest reachable
        /// region, pathfinding between the stops so it goes through doorways
        /// rather than through walls.
        /// </summary>
        public static List<Vector3> Cover(ScanLevelAnalysis a, ScanNavGrid nav, int stops)
        {
            var targets = Spread(a, nav, stops);
            var route = new List<Vector3>();
            var leg = new List<Vector3>();

            for (int i = 0; i + 1 < targets.Count; ++i)
            {
                leg.Clear();
                if (!nav.TryFindPath(targets[i], targets[i + 1], leg)) continue;
                for (int j = route.Count == 0 ? 0 : 1; j < leg.Count; ++j)
                    route.Add(leg[j]);
            }
            return route;
        }

        /// <summary>Farthest-point sampling over navigable floor, so the stops
        /// span the whole house instead of clustering in one room.</summary>
        public static List<Vector3> Spread(ScanLevelAnalysis a, ScanNavGrid nav, int count)
        {
            var pool = new List<Vector3>();
            for (int z = 0; z < a.gridHeight; ++z)
            for (int x = 0; x < a.gridWidth; ++x)
                if (nav.IsNavigable(x, z) &&
                    nav.component[z * a.gridWidth + x] == nav.largestComponent)
                    pool.Add(a.CellToWorld(x, z));

            var picked = new List<Vector3>();
            if (pool.Count == 0) return picked;
            picked.Add(pool[0]);
            while (picked.Count < count)
            {
                float best = -1f;
                int bestI = -1;
                for (int i = 0; i < pool.Count; ++i)
                {
                    float d = float.MaxValue;
                    foreach (var p in picked)
                        d = Mathf.Min(d, Vector3.SqrMagnitude(pool[i] - p));
                    if (d > best) { best = d; bestI = i; }
                }
                if (bestI < 0) break;
                picked.Add(pool[bestI]);
            }
            return picked;
        }

        public static float Length(List<Vector3> route)
        {
            float d = 0f;
            for (int i = 1; i < route.Count; ++i)
                d += Vector3.Distance(route[i - 1], route[i]);
            return d;
        }

        /// <summary>Turns a polyline into headset poses at a fixed sample rate
        /// and walking speed, which is what a real mapping session sees.</summary>
        public static List<Vector3> ToPoses(List<Vector3> route, float floorY,
            float eyeHeight = 1.65f, float speed = 1.1f, float sampleHz = 15f)
        {
            var poses = new List<Vector3>();
            if (route.Count == 0) return poses;
            float step = speed / sampleHz;

            float carry = 0f;
            for (int i = 1; i < route.Count; ++i)
            {
                Vector3 a = route[i - 1], b = route[i];
                float len = Vector3.Distance(a, b);
                if (len <= 1e-6f) continue;
                while (carry + step <= len)
                {
                    carry += step;
                    var p = Vector3.Lerp(a, b, carry / len);
                    poses.Add(new Vector3(p.x, floorY + eyeHeight, p.z));
                }
                carry -= len;
                if (carry < 0f) carry = 0f;
            }
            return poses;
        }

        /// <summary>Position along a route at normalised distance t.</summary>
        public static Vector3 Sample(List<Vector3> route, float t)
        {
            if (route.Count == 0) return Vector3.zero;
            if (route.Count == 1) return route[0];
            float target = Mathf.Clamp01(t) * Length(route);
            float travelled = 0f;
            for (int i = 1; i < route.Count; ++i)
            {
                float len = Vector3.Distance(route[i - 1], route[i]);
                if (travelled + len >= target)
                    return Vector3.Lerp(route[i - 1], route[i],
                                        len <= 1e-6f ? 0f : (target - travelled) / len);
                travelled += len;
            }
            return route[route.Count - 1];
        }
    }
}
