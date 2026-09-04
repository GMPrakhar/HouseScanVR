using System.Collections.Generic;
using UnityEngine;

namespace HouseScan
{
    /// <summary>
    /// Builds a playable map of a real room from nothing but where the player
    /// physically walked.
    ///
    /// The alternative is asking people to scan their house in another app and
    /// copy a .ply onto the headset over USB, which is not a game. Walking is
    /// something you can do inside the headset, and floor you have stood on is
    /// the strongest possible evidence that floor is walkable - stronger than
    /// anything inferred from a point cloud.
    ///
    /// The grid grows as the player wanders, so no room size has to be declared
    /// up front. Cells are keyed absolutely and only baked into a dense array at
    /// the end.
    /// </summary>
    public class RoomMapper
    {
        public float cellSize { get; private set; }
        public float floorY { get; private set; }

        /// Radius of floor considered walked as the player passes over it.
        /// This is not shoe size: a person walking normally keeps roughly this
        /// much clear floor around them, and never scrapes along the wall. It
        /// has to be at least as wide as the agents that will later have to walk
        /// down the same trail, or the mapped corridors are too narrow to use.
        public float bodyRadius = 0.35f;

        /// Head height is used to estimate the ceiling, and to reject samples
        /// taken while the headset is off or being carried.
        public float minHeadHeight = 0.9f;
        public float maxHeadHeight = 2.3f;

        /// How far beyond the walked trail is still taken to be indoors.
        ///
        /// Kept deliberately small. The idea was that closing the walked set
        /// would recover rooms the player circled rather than crossed, but
        /// MapProbe measures the trade-off directly and it is a straight loss:
        /// from 0.25 m to 1.75 m the share of real walls that keep blocking
        /// sight falls from 99.5% to 89.1% while the share of furniture
        /// correctly seen over does not move off 49.1%. A trail already
        /// encloses whatever it encloses. See <see cref="ClassifyUnwalked"/>.
        public float indoorReach = 0.25f;

        readonly Dictionary<long, int> m_Visits = new();

        public int visitedCellCount => m_Visits.Count;
        public float mappedAreaSqm => m_Visits.Count * cellSize * cellSize;
        public int sampleCount { get; private set; }
        public int rejectedSamples { get; private set; }
        public float pathLength { get; private set; }

        Vector3 m_Last;
        bool m_HasLast;
        int m_MinX = int.MaxValue, m_MinZ = int.MaxValue;
        int m_MaxX = int.MinValue, m_MaxZ = int.MinValue;
        float m_MaxHeadSeen;

        public RoomMapper(float floorY, float cellSize = 0.25f)
        {
            this.floorY = floorY;
            this.cellSize = Mathf.Max(0.05f, cellSize);
        }

        static long Key(int x, int z) => ((long)x << 32) ^ (uint)z;

        public struct VisitedCell { public int x, z, visits; }

        /// <summary>Every cell walked so far, in absolute grid coordinates.
        /// Lets a live map be drawn while it is still being built, before any
        /// bake has fixed its extent.</summary>
        public IEnumerable<VisitedCell> VisitedCells()
        {
            foreach (var kv in m_Visits)
                yield return new VisitedCell
                {
                    x = (int)(kv.Key >> 32),
                    z = (int)(uint)kv.Key,
                    visits = kv.Value,
                };
        }

        /// <summary>
        /// Records one headset pose. Returns false if the sample was rejected,
        /// which happens when the headset is off the head or held at arm's
        /// length - those samples would paint walkable floor through walls.
        /// </summary>
        public bool AddPose(Vector3 head)
        {
            sampleCount++;
            float h = head.y - floorY;
            if (h < minHeadHeight || h > maxHeadHeight)
            {
                rejectedSamples++;
                return false;
            }

            m_MaxHeadSeen = Mathf.Max(m_MaxHeadSeen, head.y);

            // Walking is sampled per frame, but a fast turn or a dropped frame
            // leaves a gap. Interpolate so the trail is continuous rather than
            // a dotted line that leaves unwalkable holes down the middle.
            if (m_HasLast)
            {
                float d = Vector3.Distance(new Vector3(m_Last.x, 0f, m_Last.z),
                                           new Vector3(head.x, 0f, head.z));
                pathLength += d;
                int steps = Mathf.CeilToInt(d / (cellSize * 0.5f));
                for (int i = 1; i <= steps; ++i)
                    Stamp(Vector3.Lerp(m_Last, head, i / (float)steps));
            }
            else
            {
                Stamp(head);
            }

            m_Last = head;
            m_HasLast = true;
            return true;
        }

        void Stamp(Vector3 head)
        {
            // Cells are claimed by true distance from the player rather than by
            // an integer cell radius. Rounding the radius to whole cells first
            // turns the footprint into a plus sign at small radii, and a trail
            // stamped with plus signs is notched along both edges - which is
            // enough to break it into disconnected pieces once an agent radius
            // is eroded off it.
            int r = Mathf.CeilToInt(bodyRadius / cellSize);
            int cx = Mathf.FloorToInt(head.x / cellSize);
            int cz = Mathf.FloorToInt(head.z / cellSize);
            float r2 = bodyRadius * bodyRadius;

            for (int dz = -r; dz <= r; ++dz)
            for (int dx = -r; dx <= r; ++dx)
            {
                int x = cx + dx, z = cz + dz;
                float px = (x + 0.5f) * cellSize - head.x;
                float pz = (z + 0.5f) * cellSize - head.z;
                if (px * px + pz * pz > r2) continue;

                long k = Key(x, z);
                m_Visits.TryGetValue(k, out int n);
                m_Visits[k] = n + 1;

                if (x < m_MinX) m_MinX = x;
                if (z < m_MinZ) m_MinZ = z;
                if (x > m_MaxX) m_MaxX = x;
                if (z > m_MaxZ) m_MaxZ = z;
            }
        }

        /// <summary>
        /// Turns the walked trail into the same <see cref="ScanLevelAnalysis"/>
        /// the splat pipeline produces, so navigation, the hunters and the whole
        /// game work on a mapped room without knowing where it came from.
        /// </summary>
        /// <param name="margin">Cells of blocked space kept around the mapped
        /// area, so the walkable region is enclosed rather than running off the
        /// edge of the grid.</param>
        public ScanLevelAnalysis Bake(int margin = 2)
        {
            if (m_Visits.Count == 0)
                return null;

            // The border of the grid has to sit outside anything the indoor
            // test can reach, or the walked area would merge with the outside
            // world and nothing would be left to block sight.
            margin = Mathf.Max(margin, Mathf.RoundToInt(indoorReach / cellSize) + 2);

            int minX = m_MinX - margin, minZ = m_MinZ - margin;
            int w = (m_MaxX - m_MinX + 1) + margin * 2;
            int h = (m_MaxZ - m_MinZ + 1) + margin * 2;

            var a = new ScanLevelAnalysis
            {
                cellSize = cellSize,
                floorY = floorY,
                ceilingY = Mathf.Max(floorY + 2.2f, m_MaxHeadSeen + 0.35f),
                gridWidth = w,
                gridHeight = h,
                walkable = new bool[w * h],
                obstacleCounts = new int[w * h],
                floorCounts = new int[w * h],
                sightBlockCounts = new int[w * h],
            };
            a.bounds = new Bounds();
            a.bounds.SetMinMax(
                new Vector3(minX * cellSize, floorY, minZ * cellSize),
                new Vector3((minX + w) * cellSize, a.ceilingY, (minZ + h) * cellSize));

            foreach (var kv in m_Visits)
            {
                int x = (int)(kv.Key >> 32) - minX;
                int z = (int)(uint)kv.Key - minZ;
                if (x < 0 || z < 0 || x >= w || z >= h) continue;
                int i = z * w + x;
                a.walkable[i] = true;
                a.floorCounts[i] = kv.Value;
            }

            ClassifyUnwalked(a, indoorReach);
            return a;
        }

        /// <summary>
        /// Decides what the gaps mean.
        ///
        /// Everything the player did not walk on is impassable, but not all of
        /// it blocks the view. The middle of a room you walked around, or the
        /// sofa you walked past, is something to see over. The space beyond the
        /// front door is the edge of the world, and has to block sight too, or
        /// the hunters can see through the walls of the house.
        ///
        /// The walked set is closed slightly first, to bridge single-cell
        /// notches in the trail. Closing it harder was tried and measured; see
        /// <see cref="indoorReach"/> for why it is not worth it.
        ///
        /// It is a heuristic and it gets two things wrong in opposite
        /// directions. A partition wall with a corridor walked down either side
        /// looks exactly like a sofa walked around, so hunters can see through
        /// a few of those. And furniture the player only passed on one side is
        /// never enclosed, so it is treated as the edge of the world: about
        /// half the furniture in a typical route, which costs the player
        /// sightlines rather than granting hunters any.
        /// </summary>
        /// <param name="reachMetres">How far the player's route is taken to
        /// imply enclosed indoor space. Roughly the width of a room they could
        /// have walked around rather than through.</param>
        static void ClassifyUnwalked(ScanLevelAnalysis a, float reachMetres = 1.75f)
        {
            int w = a.gridWidth, h = a.gridHeight;
            int n = w * h;
            int r = Mathf.Max(1, Mathf.RoundToInt(reachMetres / a.cellSize));

            var interior = Close(a.walkable, w, h, r);
            var outside = new bool[n];
            var queue = new Queue<int>();

            void Push(int x, int z)
            {
                int i = z * w + x;
                if (interior[i] || outside[i]) return;
                outside[i] = true;
                queue.Enqueue(i);
            }

            for (int x = 0; x < w; ++x) { Push(x, 0); Push(x, h - 1); }
            for (int z = 0; z < h; ++z) { Push(0, z); Push(w - 1, z); }

            while (queue.Count > 0)
            {
                int i = queue.Dequeue();
                int x = i % w, z = i / w;
                if (x > 0) Push(x - 1, z);
                if (x < w - 1) Push(x + 1, z);
                if (z > 0) Push(x, z - 1);
                if (z < h - 1) Push(x, z + 1);
            }

            for (int i = 0; i < n; ++i)
            {
                if (a.walkable[i]) continue;
                a.obstacleCounts[i] = 1;                 // blocks movement either way
                a.sightBlockCounts[i] = outside[i] ? 1 : 0;
            }
        }

        /// <summary>Morphological closing: dilate by <paramref name="r"/> cells
        /// then erode by the same, which fills gaps narrower than 2r without
        /// growing the outer boundary.</summary>
        static bool[] Close(bool[] set, int w, int h, int r)
        {
            var dilated = Spread(set, w, h, r, true);
            // Eroding is dilating the complement, so the same distance pass
            // serves both and the boundary comes back where it started.
            var inverted = new bool[set.Length];
            for (int i = 0; i < set.Length; ++i) inverted[i] = !dilated[i];
            var grown = Spread(inverted, w, h, r, false);
            var closed = new bool[set.Length];
            for (int i = 0; i < set.Length; ++i) closed[i] = !grown[i];
            return closed;
        }

        /// <summary>Multi-source BFS marking every cell within <paramref name="r"/>
        /// steps of a set cell. <paramref name="outsideIsSet"/> treats the space
        /// beyond the grid as part of the source, which is what keeps the erode
        /// half of a closing from eating the outer boundary inwards.</summary>
        static bool[] Spread(bool[] set, int w, int h, int r, bool outsideIsSet)
        {
            int n = w * h;
            var dist = new int[n];
            var queue = new Queue<int>();
            for (int i = 0; i < n; ++i)
            {
                dist[i] = int.MaxValue;
                if (set[i]) { dist[i] = 0; queue.Enqueue(i); }
            }

            if (!outsideIsSet)
            {
                // The border behaves as if the set continued past the edge.
                for (int x = 0; x < w; ++x) { Seed(x, 0); Seed(x, h - 1); }
                for (int z = 0; z < h; ++z) { Seed(0, z); Seed(w - 1, z); }
                void Seed(int x, int z)
                {
                    int i = z * w + x;
                    if (dist[i] != 0) { dist[i] = 0; queue.Enqueue(i); }
                }
            }

            while (queue.Count > 0)
            {
                int i = queue.Dequeue();
                if (dist[i] >= r) continue;
                int x = i % w, z = i / w;
                for (int dz = -1; dz <= 1; ++dz)
                for (int dx = -1; dx <= 1; ++dx)
                {
                    if (dx == 0 && dz == 0) continue;
                    int nx = x + dx, nz = z + dz;
                    if (nx < 0 || nz < 0 || nx >= w || nz >= h) continue;
                    int ni = nz * w + nx;
                    if (dist[ni] <= dist[i] + 1) continue;
                    dist[ni] = dist[i] + 1;
                    queue.Enqueue(ni);
                }
            }

            var result = new bool[n];
            for (int i = 0; i < n; ++i) result[i] = dist[i] <= r;
            return result;
        }

        // ---- persistence -------------------------------------------------

        [System.Serializable]
        class Saved
        {
            public float cellSize, floorY, bodyRadius, indoorReach, maxHeadSeen, pathLength;
            public int[] x, z, visits;
        }

        public string ToJson()
        {
            var s = new Saved
            {
                cellSize = cellSize,
                floorY = floorY,
                bodyRadius = bodyRadius,
                indoorReach = indoorReach,
                maxHeadSeen = m_MaxHeadSeen,
                pathLength = pathLength,
                x = new int[m_Visits.Count],
                z = new int[m_Visits.Count],
                visits = new int[m_Visits.Count],
            };
            int n = 0;
            foreach (var kv in m_Visits)
            {
                s.x[n] = (int)(kv.Key >> 32);
                s.z[n] = (int)(uint)kv.Key;
                s.visits[n] = kv.Value;
                n++;
            }
            return JsonUtility.ToJson(s);
        }

        public static RoomMapper FromJson(string json)
        {
            var s = JsonUtility.FromJson<Saved>(json);
            if (s == null || s.x == null) return null;

            var m = new RoomMapper(s.floorY, s.cellSize)
            {
                bodyRadius = s.bodyRadius,
                indoorReach = s.indoorReach > 0f ? s.indoorReach : 1.75f,
            };
            m.m_MaxHeadSeen = s.maxHeadSeen;
            m.pathLength = s.pathLength;
            for (int i = 0; i < s.x.Length; ++i)
            {
                m.m_Visits[Key(s.x[i], s.z[i])] = s.visits[i];
                m.m_MinX = Mathf.Min(m.m_MinX, s.x[i]);
                m.m_MinZ = Mathf.Min(m.m_MinZ, s.z[i]);
                m.m_MaxX = Mathf.Max(m.m_MaxX, s.x[i]);
                m.m_MaxZ = Mathf.Max(m.m_MaxZ, s.z[i]);
            }
            return m;
        }
    }
}
