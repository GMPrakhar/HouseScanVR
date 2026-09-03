using System.Collections.Generic;
using GaussianSplatting.Runtime;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace HouseScan
{
    /// <summary>
    /// Derives playable structure from a raw house capture: floor height, a
    /// walkable/blocked occupancy grid, and candidate spawn points. A splat cloud
    /// on its own is only scenery; this is what lets gameplay reason about it.
    /// </summary>
    public class ScanLevelAnalysis
    {
        public float cellSize;
        public float floorY;
        public float ceilingY;
        public Bounds bounds;
        public int gridWidth, gridHeight;

        /// Splat count per cell, above floor level and below head height.
        public int[] obstacleCounts;
        /// Splat count per cell within the floor slab.
        public int[] floorCounts;
        public bool[] walkable;

        public Vector3 CellToWorld(int x, int z) =>
            new Vector3(bounds.min.x + (x + 0.5f) * cellSize, floorY,
                        bounds.min.z + (z + 0.5f) * cellSize);

        public bool TryWorldToCell(Vector3 p, out int x, out int z)
        {
            x = Mathf.FloorToInt((p.x - bounds.min.x) / cellSize);
            z = Mathf.FloorToInt((p.z - bounds.min.z) / cellSize);
            return x >= 0 && z >= 0 && x < gridWidth && z < gridHeight;
        }

        public int WalkableCellCount
        {
            get
            {
                int n = 0;
                foreach (var w in walkable)
                    if (w) n++;
                return n;
            }
        }

        public float WalkableAreaSqm => WalkableCellCount * cellSize * cellSize;
    }

    public static class ScanLevelAnalyzer
    {
        /// <param name="cellSize">Grid resolution in metres.</param>
        /// <param name="floorSlab">Thickness of the band treated as floor.</param>
        /// <param name="clearance">Height band that must be empty to walk.</param>
        public static ScanLevelAnalysis Analyze(NativeArray<RuntimeSplatData> splats,
            float cellSize = 0.25f, float floorSlab = 0.12f, float clearance = 1.7f,
            int minFloorSplatsPerCell = 3)
        {
            var result = new ScanLevelAnalysis { cellSize = cellSize };

            float3 lo = float.PositiveInfinity, hi = float.NegativeInfinity;
            for (int i = 0; i < splats.Length; ++i)
            {
                float3 p = splats[i].pos;
                lo = math.min(lo, p);
                hi = math.max(hi, p);
            }

            var bounds = new Bounds();
            bounds.SetMinMax(lo, hi);
            result.bounds = bounds;

            result.floorY = EstimateFloorY(splats, lo.y, hi.y);
            result.ceilingY = EstimateCeilingY(splats, lo.y, hi.y);

            int w = Mathf.Max(1, Mathf.CeilToInt((hi.x - lo.x) / cellSize));
            int h = Mathf.Max(1, Mathf.CeilToInt((hi.z - lo.z) / cellSize));
            result.gridWidth = w;
            result.gridHeight = h;
            result.obstacleCounts = new int[w * h];
            result.floorCounts = new int[w * h];
            result.walkable = new bool[w * h];

            float obstacleLo = result.floorY + floorSlab;
            float obstacleHi = result.floorY + clearance;

            for (int i = 0; i < splats.Length; ++i)
            {
                float3 p = splats[i].pos;
                int cx = Mathf.FloorToInt((p.x - lo.x) / cellSize);
                int cz = Mathf.FloorToInt((p.z - lo.z) / cellSize);
                if (cx < 0 || cz < 0 || cx >= w || cz >= h)
                    continue;
                int idx = cz * w + cx;

                if (p.y >= result.floorY - floorSlab && p.y <= result.floorY + floorSlab)
                    result.floorCounts[idx]++;
                else if (p.y > obstacleLo && p.y < obstacleHi)
                    result.obstacleCounts[idx]++;
            }

            // A cell is walkable when it has real floor evidence and nothing
            // standing in the body-height band above it.
            int obstacleThreshold = Mathf.Max(2, minFloorSplatsPerCell);
            for (int i = 0; i < result.walkable.Length; ++i)
            {
                result.walkable[i] = result.floorCounts[i] >= minFloorSplatsPerCell &&
                                     result.obstacleCounts[i] < obstacleThreshold;
            }

            return result;
        }

        static float EstimateFloorY(NativeArray<RuntimeSplatData> splats, float minY, float maxY)
        {
            return HistogramPeak(splats, minY, maxY, takeLowerHalf: true);
        }

        static float EstimateCeilingY(NativeArray<RuntimeSplatData> splats, float minY, float maxY)
        {
            return HistogramPeak(splats, minY, maxY, takeLowerHalf: false);
        }

        // Floors and ceilings are the two densest horizontal slabs in an indoor
        // capture, so a height histogram finds them far more reliably than min/max,
        // which are dominated by scanning noise.
        static float HistogramPeak(NativeArray<RuntimeSplatData> splats, float minY, float maxY,
            bool takeLowerHalf)
        {
            const int kBins = 256;
            float range = Mathf.Max(maxY - minY, 1e-4f);
            var bins = new int[kBins];
            for (int i = 0; i < splats.Length; ++i)
            {
                int b = Mathf.Clamp((int)((splats[i].pos.y - minY) / range * (kBins - 1)), 0, kBins - 1);
                bins[b]++;
            }

            int start = takeLowerHalf ? 0 : kBins / 2;
            int end = takeLowerHalf ? kBins / 2 : kBins;
            int best = start, bestCount = -1;
            for (int b = start; b < end; ++b)
            {
                if (bins[b] > bestCount)
                {
                    bestCount = bins[b];
                    best = b;
                }
            }
            return minY + (best + 0.5f) / kBins * range;
        }

        /// <summary>
        /// Picks spawn points on walkable cells, spread out by at least
        /// <paramref name="minSeparation"/> metres. Deterministic for a given seed
        /// so a level can be reproduced from a scan.
        ///
        /// Cells within <paramref name="clearance"/> metres of a non-walkable cell
        /// are rejected, so a player never spawns half inside a wall or a piece of
        /// furniture.
        /// </summary>
        public static List<Vector3> PickSpawnPoints(ScanLevelAnalysis a, int count,
            float minSeparation = 1.0f, uint seed = 12345, float clearance = 0.35f)
        {
            int margin = Mathf.CeilToInt(clearance / a.cellSize);

            var candidates = new List<Vector3>();
            for (int z = 0; z < a.gridHeight; ++z)
            for (int x = 0; x < a.gridWidth; ++x)
                if (HasClearance(a, x, z, margin))
                    candidates.Add(a.CellToWorld(x, z));

            // A tightly furnished or noisy scan can leave nothing with full
            // clearance; fall back to bare walkability rather than to no spawns.
            if (candidates.Count == 0)
            {
                for (int z = 0; z < a.gridHeight; ++z)
                for (int x = 0; x < a.gridWidth; ++x)
                    if (a.walkable[z * a.gridWidth + x])
                        candidates.Add(a.CellToWorld(x, z));
            }

            var rng = new Unity.Mathematics.Random(seed == 0 ? 1u : seed);
            for (int i = candidates.Count - 1; i > 0; --i)
            {
                int j = rng.NextInt(0, i + 1);
                (candidates[i], candidates[j]) = (candidates[j], candidates[i]);
            }

            var picked = new List<Vector3>();
            float minSq = minSeparation * minSeparation;
            foreach (var c in candidates)
            {
                if (picked.Count >= count)
                    break;
                bool ok = true;
                foreach (var p in picked)
                {
                    if ((p - c).sqrMagnitude < minSq) { ok = false; break; }
                }
                if (ok)
                    picked.Add(c);
            }
            return picked;
        }

        /// True when the cell and every cell within <paramref name="margin"/> cells
        /// of it are walkable.
        static bool HasClearance(ScanLevelAnalysis a, int x, int z, int margin)
        {
            for (int dz = -margin; dz <= margin; ++dz)
            for (int dx = -margin; dx <= margin; ++dx)
            {
                int nx = x + dx, nz = z + dz;
                if (nx < 0 || nz < 0 || nx >= a.gridWidth || nz >= a.gridHeight)
                    return false;
                if (!a.walkable[nz * a.gridWidth + nx])
                    return false;
            }
            return true;
        }
    }
}
