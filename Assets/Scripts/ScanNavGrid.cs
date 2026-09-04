using System;
using System.Collections.Generic;
using UnityEngine;

namespace HouseScan
{
    /// <summary>
    /// Turns a <see cref="ScanLevelAnalysis"/> into something agents can actually
    /// navigate: cells eroded by the agent's radius, connected components so
    /// unreachable pockets can be rejected, A* pathfinding, and line of sight.
    ///
    /// The distinction that matters here is between *walkable* and *navigable*.
    /// A cell flush against a wall is walkable, but an agent with a body cannot
    /// stand there. Navigable cells are walkable cells far enough from anything
    /// blocking, which is what stops agents grinding along walls and clipping
    /// through door frames.
    /// </summary>
    public class ScanNavGrid
    {
        public ScanLevelAnalysis analysis { get; private set; }
        public int width { get; private set; }
        public int height { get; private set; }
        public float cellSize => analysis.cellSize;

        /// Chebyshev distance in cells to the nearest blocked cell. 0 = blocked.
        public int[] clearance { get; private set; }
        /// Connected component id per cell, or -1 where not navigable.
        public int[] component { get; private set; }
        public int componentCount { get; private set; }
        /// Cell count of each component, indexed by component id.
        public int[] componentSizes { get; private set; }
        public int largestComponent { get; private set; } = -1;

        /// Minimum clearance, in cells, for a cell to hold an agent body.
        public int minClearanceCells { get; private set; }
        public float agentRadius { get; private set; }

        public bool InBounds(int x, int z) => x >= 0 && z >= 0 && x < width && z < height;

        /// Navigable = walkable, and far enough from anything blocked that a body
        /// of the agent's radius fits.
        public bool IsNavigable(int x, int z) =>
            InBounds(x, z) && clearance[z * width + x] >= minClearanceCells;

        public bool IsNavigable(Vector3 world) =>
            analysis.TryWorldToCell(world, out int x, out int z) && IsNavigable(x, z);

        /// Cells whose splats reach above sitting height block sight. A couch
        /// blocks movement but not vision; a wall blocks both.
        public bool BlocksSight(int x, int z)
        {
            if (!InBounds(x, z))
                return true;
            int i = z * width + x;
            return analysis.sightBlockCounts != null && analysis.sightBlockCounts[i] >= kSightThreshold;
        }

        const int kSightThreshold = 2;

        public static ScanNavGrid Build(ScanLevelAnalysis a, float agentRadius = 0.30f)
        {
            var g = new ScanNavGrid
            {
                analysis = a,
                width = a.gridWidth,
                height = a.gridHeight,
                agentRadius = agentRadius,
                // The agent's body must fit, so its centre has to sit at least
                // its radius away from anything blocked.
                minClearanceCells = Mathf.Max(1, Mathf.CeilToInt(agentRadius / a.cellSize)),
            };
            g.BuildClearance();
            g.BuildComponents();
            return g;
        }

        /// <summary>
        /// Multi-source BFS outward from every blocked cell, giving each cell its
        /// distance to the nearest obstacle in one pass rather than searching a
        /// neighbourhood per cell.
        /// </summary>
        void BuildClearance()
        {
            int n = width * height;
            clearance = new int[n];
            var queue = new Queue<int>();

            for (int z = 0; z < height; ++z)
            for (int x = 0; x < width; ++x)
            {
                int i = z * width + x;
                bool blocked = !analysis.walkable[i];
                // The outside edge of the grid is treated as blocked: a scan
                // simply stops there, and we cannot claim it is open floor.
                if (!blocked && (x == 0 || z == 0 || x == width - 1 || z == height - 1))
                    blocked = true;
                if (blocked)
                {
                    clearance[i] = 0;
                    queue.Enqueue(i);
                }
                else
                {
                    clearance[i] = int.MaxValue;
                }
            }

            while (queue.Count > 0)
            {
                int i = queue.Dequeue();
                int cx = i % width, cz = i / width;
                int d = clearance[i];
                for (int dz = -1; dz <= 1; ++dz)
                for (int dx = -1; dx <= 1; ++dx)
                {
                    if (dx == 0 && dz == 0) continue;
                    int nx = cx + dx, nz = cz + dz;
                    if (!InBounds(nx, nz)) continue;
                    int ni = nz * width + nx;
                    if (clearance[ni] > d + 1)
                    {
                        clearance[ni] = d + 1;
                        queue.Enqueue(ni);
                    }
                }
            }
        }

        /// <summary>
        /// Flood fills navigable cells. Rooms only end up in the same component if
        /// a doorway actually connects them, so this is what proves a scan is one
        /// playable space rather than a set of sealed boxes.
        /// </summary>
        void BuildComponents()
        {
            int n = width * height;
            component = new int[n];
            for (int i = 0; i < n; ++i)
                component[i] = -1;

            var sizes = new List<int>();
            var stack = new Stack<int>();

            for (int start = 0; start < n; ++start)
            {
                if (component[start] != -1) continue;
                int sx = start % width, sz = start / width;
                if (!IsNavigable(sx, sz)) continue;

                int id = sizes.Count;
                int size = 0;
                stack.Push(start);
                component[start] = id;

                while (stack.Count > 0)
                {
                    int i = stack.Pop();
                    size++;
                    int cx = i % width, cz = i / width;
                    for (int dz = -1; dz <= 1; ++dz)
                    for (int dx = -1; dx <= 1; ++dx)
                    {
                        if (dx == 0 && dz == 0) continue;
                        int nx = cx + dx, nz = cz + dz;
                        if (!IsNavigable(nx, nz)) continue;
                        // Diagonals may not squeeze between two blocked cells.
                        if (dx != 0 && dz != 0 &&
                            (!IsNavigable(cx + dx, cz) || !IsNavigable(cx, cz + dz)))
                            continue;
                        int ni = nz * width + nx;
                        if (component[ni] != -1) continue;
                        component[ni] = id;
                        stack.Push(ni);
                    }
                }
                sizes.Add(size);
            }

            componentCount = sizes.Count;
            componentSizes = sizes.ToArray();
            largestComponent = -1;
            int best = -1;
            for (int i = 0; i < componentSizes.Length; ++i)
                if (componentSizes[i] > best) { best = componentSizes[i]; largestComponent = i; }
        }

        public int ComponentAt(Vector3 world) =>
            analysis.TryWorldToCell(world, out int x, out int z) && InBounds(x, z)
                ? component[z * width + x]
                : -1;

        /// <summary>
        /// Finds the nearest navigable cell to a world position, so a point taken
        /// from a scan (or a player standing in a doorway) can be used as a path
        /// endpoint without failing outright.
        /// </summary>
        public bool TrySnap(Vector3 world, out Vector3 snapped, float maxRadius = 1.5f)
        {
            snapped = world;
            if (!analysis.TryWorldToCell(world, out int x, out int z))
                return false;
            if (IsNavigable(x, z))
            {
                snapped = analysis.CellToWorld(x, z);
                return true;
            }

            int maxCells = Mathf.CeilToInt(maxRadius / cellSize);
            for (int r = 1; r <= maxCells; ++r)
            {
                for (int dz = -r; dz <= r; ++dz)
                for (int dx = -r; dx <= r; ++dx)
                {
                    // Only the ring at radius r, so nearer cells win.
                    if (Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dz)) != r) continue;
                    int nx = x + dx, nz = z + dz;
                    if (!IsNavigable(nx, nz)) continue;
                    snapped = analysis.CellToWorld(nx, nz);
                    return true;
                }
            }
            return false;
        }

        // ---- A* ---------------------------------------------------------------

        static readonly int[] kDx = { 1, -1, 0, 0, 1, 1, -1, -1 };
        static readonly int[] kDz = { 0, 0, 1, -1, 1, -1, 1, -1 };

        /// <summary>
        /// A* over navigable cells, 8-connected, with corner cutting disallowed.
        /// The returned path is string-pulled so agents move in straight lines
        /// between corners instead of stair-stepping along the grid.
        /// </summary>
        public bool TryFindPath(Vector3 from, Vector3 to, List<Vector3> path)
        {
            path.Clear();
            if (!TrySnap(from, out var start) || !TrySnap(to, out var goal))
                return false;
            if (!analysis.TryWorldToCell(start, out int sx, out int sz) ||
                !analysis.TryWorldToCell(goal, out int gx, out int gz))
                return false;

            int si = sz * width + sx, gi = gz * width + gx;
            // Cheap rejection: separate components can never be connected, and
            // without this A* would explore the whole reachable space to find out.
            if (component[si] != component[gi] || component[si] < 0)
                return false;

            if (si == gi)
            {
                path.Add(start);
                return true;
            }

            int n = width * height;
            var gScore = new float[n];
            var cameFrom = new int[n];
            var closed = new bool[n];
            for (int i = 0; i < n; ++i) { gScore[i] = float.PositiveInfinity; cameFrom[i] = -1; }

            var open = new MinHeap(256);
            gScore[si] = 0f;
            open.Push(si, Heuristic(sx, sz, gx, gz));

            bool found = false;
            while (open.Count > 0)
            {
                int cur = open.Pop();
                if (cur == gi) { found = true; break; }
                if (closed[cur]) continue;
                closed[cur] = true;

                int cx = cur % width, cz = cur / width;
                for (int k = 0; k < 8; ++k)
                {
                    int nx = cx + kDx[k], nz = cz + kDz[k];
                    if (!IsNavigable(nx, nz)) continue;
                    bool diagonal = kDx[k] != 0 && kDz[k] != 0;
                    if (diagonal &&
                        (!IsNavigable(cx + kDx[k], cz) || !IsNavigable(cx, cz + kDz[k])))
                        continue;

                    int ni = nz * width + nx;
                    if (closed[ni]) continue;

                    float step = diagonal ? 1.41421356f : 1f;
                    // Prefer routes with room to spare, so agents drift toward the
                    // middle of a corridor rather than scraping the wall.
                    float penalty = Mathf.Max(0, minClearanceCells + 2 - clearance[ni]) * 0.25f;
                    float tentative = gScore[cur] + step + penalty;
                    if (tentative < gScore[ni])
                    {
                        gScore[ni] = tentative;
                        cameFrom[ni] = cur;
                        open.Push(ni, tentative + Heuristic(nx, nz, gx, gz));
                    }
                }
            }

            if (!found)
                return false;

            var cells = new List<int>();
            for (int i = gi; i != -1; i = cameFrom[i])
                cells.Add(i);
            cells.Reverse();

            SmoothPath(cells, path);
            return path.Count > 0;
        }

        static float Heuristic(int x, int z, int gx, int gz)
        {
            int dx = Mathf.Abs(x - gx), dz = Mathf.Abs(z - gz);
            // Octile distance: exact for 8-connected movement, so A* stays
            // admissible and does not wander.
            return (dx + dz) + (1.41421356f - 2f) * Mathf.Min(dx, dz);
        }

        /// String-pulling: keep a waypoint only when the straight line from the
        /// last kept point to the next one would leave navigable space.
        void SmoothPath(List<int> cells, List<Vector3> outPath)
        {
            outPath.Clear();
            if (cells.Count == 0)
                return;

            int anchor = 0;
            outPath.Add(CellCentre(cells[0]));
            for (int i = 1; i < cells.Count; ++i)
            {
                if (i == cells.Count - 1)
                {
                    outPath.Add(CellCentre(cells[i]));
                    break;
                }
                if (!CorridorClear(CellCentre(cells[anchor]), CellCentre(cells[i + 1])))
                {
                    outPath.Add(CellCentre(cells[i]));
                    anchor = i;
                }
            }
        }

        Vector3 CellCentre(int i) => analysis.CellToWorld(i % width, i / width);

        /// True when every cell the segment passes through is navigable.
        public bool CorridorClear(Vector3 a, Vector3 b)
        {
            return TraverseClear(a, b, (x, z) => IsNavigable(x, z));
        }

        /// True when nothing tall enough to block vision lies between the points.
        public bool HasLineOfSight(Vector3 a, Vector3 b)
        {
            return TraverseClear(a, b, (x, z) => !BlocksSight(x, z));
        }

        /// <summary>
        /// Exact 2D voxel traversal (Amanatides &amp; Woo): visits every cell the
        /// segment actually touches, including ones it only clips at a corner.
        ///
        /// This replaced point sampling along the line. Sampling at half a cell
        /// looks safe but is not: a segment can cross the corner of a cell over a
        /// distance shorter than the sample spacing and be missed entirely, which
        /// let agents shave corners into walls.
        /// </summary>
        bool TraverseClear(Vector3 a, Vector3 b, Func<int, int, bool> ok)
        {
            float minX = analysis.bounds.min.x, minZ = analysis.bounds.min.z;
            float cs = cellSize;

            if (!analysis.TryWorldToCell(a, out int x, out int z))
                return false;
            if (!analysis.TryWorldToCell(b, out int gx, out int gz))
                return false;
            if (!ok(x, z))
                return false;

            float dx = b.x - a.x, dz = b.z - a.z;
            int stepX = dx > 0 ? 1 : (dx < 0 ? -1 : 0);
            int stepZ = dz > 0 ? 1 : (dz < 0 ? -1 : 0);

            // Parametric distance to the next cell boundary, and between boundaries.
            float tMaxX = stepX == 0 ? float.PositiveInfinity
                : (minX + (x + (stepX > 0 ? 1 : 0)) * cs - a.x) / dx;
            float tMaxZ = stepZ == 0 ? float.PositiveInfinity
                : (minZ + (z + (stepZ > 0 ? 1 : 0)) * cs - a.z) / dz;
            float tDeltaX = stepX == 0 ? float.PositiveInfinity : cs / Mathf.Abs(dx);
            float tDeltaZ = stepZ == 0 ? float.PositiveInfinity : cs / Mathf.Abs(dz);

            // Bounded by the grid diagonal; a segment cannot cross more cells.
            int guard = width + height + 2;
            while ((x != gx || z != gz) && guard-- > 0)
            {
                if (tMaxX < tMaxZ)
                {
                    if (tMaxX > 1f) break;
                    x += stepX;
                    tMaxX += tDeltaX;
                }
                else
                {
                    if (tMaxZ > 1f) break;
                    z += stepZ;
                    tMaxZ += tDeltaZ;
                }
                if (!InBounds(x, z) || !ok(x, z))
                    return false;
            }
            return true;
        }

        /// Minimal binary heap. Unity has no priority queue in this runtime, and
        /// a linear scan of the open set dominates A* cost on grids this size.
        class MinHeap
        {
            int[] m_Items;
            float[] m_Keys;
            public int Count { get; private set; }

            public MinHeap(int capacity)
            {
                m_Items = new int[capacity];
                m_Keys = new float[capacity];
            }

            public void Push(int item, float key)
            {
                if (Count == m_Items.Length)
                {
                    Array.Resize(ref m_Items, Count * 2);
                    Array.Resize(ref m_Keys, Count * 2);
                }
                int i = Count++;
                m_Items[i] = item;
                m_Keys[i] = key;
                while (i > 0)
                {
                    int parent = (i - 1) / 2;
                    if (m_Keys[parent] <= m_Keys[i]) break;
                    Swap(parent, i);
                    i = parent;
                }
            }

            public int Pop()
            {
                int top = m_Items[0];
                Count--;
                m_Items[0] = m_Items[Count];
                m_Keys[0] = m_Keys[Count];
                int i = 0;
                while (true)
                {
                    int l = 2 * i + 1, r = l + 1, best = i;
                    if (l < Count && m_Keys[l] < m_Keys[best]) best = l;
                    if (r < Count && m_Keys[r] < m_Keys[best]) best = r;
                    if (best == i) break;
                    Swap(best, i);
                    i = best;
                }
                return top;
            }

            void Swap(int a, int b)
            {
                (m_Items[a], m_Items[b]) = (m_Items[b], m_Items[a]);
                (m_Keys[a], m_Keys[b]) = (m_Keys[b], m_Keys[a]);
            }
        }
    }
}
