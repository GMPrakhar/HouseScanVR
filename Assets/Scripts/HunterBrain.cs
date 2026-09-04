using System.Collections.Generic;
using UnityEngine;

namespace HouseScan
{
    /// <summary>
    /// The hunter's decision making, deliberately kept as a plain class with no
    /// Unity object dependencies so it can be simulated headlessly and its
    /// behaviour asserted, rather than only being watched in a headset.
    ///
    /// It patrols the house until it sees the player, chases while it can see
    /// them, and searches their last known position after losing them. Every
    /// move is constrained to navigable cells, so it uses doorways for the same
    /// reason a person does.
    /// </summary>
    public class HunterBrain
    {
        public enum State { Patrol, Chase, Search }

        public Vector3 position;
        public Vector3 facing = Vector3.forward;
        public State state { get; private set; } = State.Patrol;

        public float speed = 1.15f;
        public float turnRateDegrees = 220f;
        public float sightRange = 9f;
        public float fovDegrees = 150f;
        public float eyeHeight = 1.5f;
        public float catchRadius = 0.7f;

        /// Set once the hunter has closed to <see cref="catchRadius"/> of the target.
        public bool hasCaughtTarget { get; private set; }
        public Vector3 lastKnownTargetPos { get; private set; }
        public int repathCount { get; private set; }

        readonly List<Vector3> m_Path = new();
        int m_PathIndex;
        Vector3 m_ChaseAnchor;
        Unity.Mathematics.Random m_Rng;

        public IReadOnlyList<Vector3> path => m_Path;

        public HunterBrain(Vector3 start, uint seed)
        {
            position = start;
            m_Rng = new Unity.Mathematics.Random(seed == 0 ? 1u : seed);
        }

        public bool CanSee(Vector3 target, ScanNavGrid nav)
        {
            var flatSelf = new Vector3(position.x, 0f, position.z);
            var flatTarget = new Vector3(target.x, 0f, target.z);
            float dist = Vector3.Distance(flatSelf, flatTarget);
            if (dist > sightRange)
                return false;

            if (dist > 1e-3f)
            {
                var dir = (flatTarget - flatSelf) / dist;
                var flatFacing = new Vector3(facing.x, 0f, facing.z);
                if (flatFacing.sqrMagnitude > 1e-6f &&
                    Vector3.Angle(flatFacing.normalized, dir) > fovDegrees * 0.5f)
                    return false;
            }

            return nav.HasLineOfSight(position, target);
        }

        public void Tick(float dt, Vector3 target, ScanNavGrid nav)
        {
            if (hasCaughtTarget)
                return;

            bool sees = CanSee(target, nav);

            switch (state)
            {
                case State.Patrol:
                    if (sees)
                        EnterChase(target, nav);
                    else if (!HasPath)
                        RepathToRandom(nav);
                    break;

                case State.Chase:
                    if (sees)
                    {
                        lastKnownTargetPos = target;
                        // Only repath once the target has actually moved away from
                        // the point the current path was built for; repathing every
                        // frame would burn CPU for an identical route.
                        if (!HasPath || (target - m_ChaseAnchor).sqrMagnitude > 0.75f * 0.75f)
                            EnterChase(target, nav);
                    }
                    else
                    {
                        state = State.Search;
                        if (!TryRepath(lastKnownTargetPos, nav))
                            RepathToRandom(nav);
                    }
                    break;

                case State.Search:
                    if (sees)
                        EnterChase(target, nav);
                    else if (!HasPath)
                        state = State.Patrol;
                    break;
            }

            Advance(dt);

            if (IsCatch(target, nav, sees))
                hasCaughtTarget = true;
        }

        /// <summary>
        /// Normally a catch is simply closing to <see cref="catchRadius"/>.
        ///
        /// A scanned house needs one extra case. The player physically stands
        /// where they like, including spots the grid calls unreachable - wedged
        /// in a corner the agent-radius erosion closed off, or leaning over a
        /// table. A hunter can then stand right next to them, in plain sight,
        /// forever, and the player is invincible. So if the target is not on
        /// navigable ground, the hunter has reached the nearest ground that is,
        /// and it can see them, that counts.
        /// </summary>
        bool IsCatch(Vector3 target, ScanNavGrid nav, bool sees)
        {
            if (Vector3.Distance(Flat(position), Flat(target)) <= catchRadius)
                return true;

            if (!sees || nav.IsNavigable(target))
                return false;

            return nav.TrySnap(target, out var reachable) &&
                   Vector3.Distance(Flat(position), Flat(reachable)) <= catchRadius;
        }

        bool HasPath => m_PathIndex < m_Path.Count;

        static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0f, v.z);

        void EnterChase(Vector3 target, ScanNavGrid nav)
        {
            state = State.Chase;
            lastKnownTargetPos = target;
            m_ChaseAnchor = target;
            if (!TryRepath(target, nav))
                RepathToRandom(nav);
        }

        bool TryRepath(Vector3 goal, ScanNavGrid nav)
        {
            repathCount++;

            // The player is frequently not standing on a navigable cell: right up
            // against a wall, or within the agent radius of furniture. Pathing to
            // their exact position would simply fail and the hunter would give up
            // and wander, so aim at the closest cell that can actually be stood on.
            if (!nav.IsNavigable(goal) && nav.TrySnap(goal, out var reachable))
                goal = reachable;

            var fresh = new List<Vector3>();
            if (!nav.TryFindPath(position, goal, fresh) || fresh.Count == 0)
            {
                m_Path.Clear();
                m_PathIndex = 0;
                return false;
            }
            m_Path.Clear();
            m_Path.AddRange(fresh);

            // The path starts at the centre of the cell the hunter occupies, not
            // at its exact position. Skipping straight to the second waypoint is
            // only safe if that shortcut is itself clear; otherwise the hunter
            // must first step to its own cell centre, or it can clip a corner
            // that the path never went near.
            m_PathIndex = 0;
            if (m_Path.Count > 1 && nav.CorridorClear(position, m_Path[1]))
                m_PathIndex = 1;
            return true;
        }

        void RepathToRandom(ScanNavGrid nav)
        {
            int comp = nav.ComponentAt(position);
            for (int attempt = 0; attempt < 24; ++attempt)
            {
                int x = m_Rng.NextInt(0, nav.width);
                int z = m_Rng.NextInt(0, nav.height);
                if (!nav.IsNavigable(x, z))
                    continue;
                if (comp >= 0 && nav.component[z * nav.width + x] != comp)
                    continue;
                var goal = nav.analysis.CellToWorld(x, z);
                if (Vector3.Distance(Flat(goal), Flat(position)) < 1.5f)
                    continue;
                if (TryRepath(goal, nav))
                    return;
            }
        }

        void Advance(float dt)
        {
            if (!HasPath)
                return;

            float budget = speed * dt;
            while (budget > 0f && HasPath)
            {
                var flatWp = Flat(m_Path[m_PathIndex]);
                var flatPos = Flat(position);
                float d = Vector3.Distance(flatPos, flatWp);

                // Follow the polyline exactly. Any tolerance for "close enough to
                // the waypoint" lets the hunter cut the corner onto a segment that
                // was never checked for obstacles, which is how it ends up inside
                // walls.
                if (d <= kEpsilon)
                {
                    m_PathIndex++;
                    continue;
                }

                var dir = (flatWp - flatPos) / d;
                float step = Mathf.Min(budget, d);
                position += dir * step;
                budget -= step;
                if (step >= d - kEpsilon)
                    m_PathIndex++;

                facing = Vector3.RotateTowards(
                    facing.sqrMagnitude > 1e-6f ? facing : Vector3.forward,
                    dir, turnRateDegrees * Mathf.Deg2Rad * dt, 0f);
            }
        }

        const float kEpsilon = 1e-4f;
    }
}
