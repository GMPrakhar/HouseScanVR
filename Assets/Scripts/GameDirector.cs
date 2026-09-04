using System.Collections.Generic;
using UnityEngine;

namespace HouseScan
{
    /// <summary>
    /// The game layer: once a scan has been analysed, this turns it into a round
    /// of hide-and-seek. Hunters spawn in parts of the player's own house that
    /// the player cannot currently see, then work their way through the real
    /// doorways to find them.
    ///
    /// All decisions live in <see cref="HunterBrain"/>, which has no Unity
    /// dependencies and is verified headlessly; this class only owns the scene
    /// objects and the round state.
    /// </summary>
    public class GameDirector : MonoBehaviour
    {
        public HouseScanLoader m_Loader;
        public ScanPlayerRig m_Rig;

        [Tooltip("How many hunters to place at the start of a round.")]
        public int m_HunterCount = 3;

        [Tooltip("Radius of a hunter's body, used to erode the navigable area.")]
        public float m_AgentRadius = 0.30f;

        [Tooltip("Hunters will not spawn closer than this to the player.")]
        public float m_MinSpawnDistance = 4f;

        [Tooltip("Refuse spawn points the player can currently see, so hunters " +
                 "never appear out of thin air in front of them.")]
        public bool m_SpawnOutOfSight = true;

        public uint m_Seed = 7;

        public bool m_BeginOnScanReady = true;

        public ScanNavGrid nav { get; private set; }
        public bool isRoundActive { get; private set; }
        public bool isCaught { get; private set; }
        public float survivalSeconds { get; private set; }
        public int roundNumber { get; private set; }

        readonly List<HunterBrain> m_Hunters = new();
        readonly List<Transform> m_Views = new();
        Transform m_ViewRoot;

        public IReadOnlyList<HunterBrain> hunters => m_Hunters;

        void OnEnable()
        {
            if (m_Loader != null)
                m_Loader.onScanReady += OnScanReady;
        }

        void OnDisable()
        {
            if (m_Loader != null)
                m_Loader.onScanReady -= OnScanReady;
        }

        void OnScanReady(HouseScanLoader loader)
        {
            if (m_BeginOnScanReady)
                BeginRound();
        }

        /// <summary>
        /// Builds navigation from the current scan and places hunters. Safe to
        /// call again to restart.
        /// </summary>
        public bool BeginRound()
        {
            if (m_Loader == null || m_Loader.analysis == null)
            {
                Debug.LogWarning("[Game] No analysed scan; cannot start a round.");
                return false;
            }

            nav = ScanNavGrid.Build(m_Loader.analysis, m_AgentRadius);
            if (nav.largestComponent < 0)
            {
                Debug.LogWarning("[Game] Scan has no navigable space; is the capture " +
                                 "only a single surface, or is the agent radius too large?");
                return false;
            }

            ClearHunters();

            var player = PlayerPosition();
            var spawns = ChooseSpawns(player, m_HunterCount);
            for (int i = 0; i < spawns.Count; ++i)
            {
                m_Hunters.Add(new HunterBrain(spawns[i], m_Seed + (uint)i * 977u));
                m_Views.Add(CreateView(i));
            }

            roundNumber++;
            survivalSeconds = 0f;
            isCaught = false;
            isRoundActive = m_Hunters.Count > 0;

            Debug.Log($"[Game] Round {roundNumber}: {m_Hunters.Count} hunters, " +
                      $"{nav.componentCount} navigable region(s), " +
                      $"largest {nav.componentSizes[nav.largestComponent]} cells.");
            return isRoundActive;
        }

        void Update()
        {
            Tick(Time.deltaTime);
        }

        /// <summary>
        /// Advances the round by <paramref name="dt"/> seconds. Update() just
        /// forwards Time.deltaTime; keeping the step explicit lets the round be
        /// driven at a fixed rate from a headless test.
        /// </summary>
        public void Tick(float dt)
        {
            if (!isRoundActive || isCaught)
                return;

            var player = PlayerPosition();
            survivalSeconds += dt;

            for (int i = 0; i < m_Hunters.Count; ++i)
            {
                var h = m_Hunters[i];
                h.Tick(dt, player, nav);

                var view = m_Views[i];
                if (view != null)
                {
                    view.position = h.position + Vector3.up * 0.9f;
                    if (h.facing.sqrMagnitude > 1e-6f)
                        view.rotation = Quaternion.LookRotation(h.facing, Vector3.up);
                }

                if (h.hasCaughtTarget)
                {
                    isCaught = true;
                    isRoundActive = false;
                    Debug.Log($"[Game] Caught after {survivalSeconds:F1}s.");
                    break;
                }
            }
        }

        Vector3 PlayerPosition()
        {
            if (m_Rig != null && m_Rig.m_Camera != null)
                return m_Rig.m_Camera.transform.position;
            if (m_Rig != null)
                return m_Rig.transform.position;
            return transform.position;
        }

        /// <summary>
        /// Prefers spawn points that are far from the player and out of sight,
        /// but degrades rather than failing: a small or open-plan scan may not
        /// have anywhere that satisfies both.
        /// </summary>
        public List<Vector3> ChooseSpawns(Vector3 player, int count)
        {
            var chosen = new List<Vector3>();
            if (nav == null)
                return chosen;

            // The player's real position is wherever they physically stood, which
            // is regularly not a navigable cell: pressed against a wall, inside
            // the radius of a table, or a little off from where tracking thinks
            // the floor is. Snap first, and fall back to the largest region,
            // otherwise the component test matches nothing and no hunters spawn.
            int playerComponent = nav.ComponentAt(player);
            if (playerComponent < 0 && nav.TrySnap(player, out var onGrid))
                playerComponent = nav.ComponentAt(onGrid);
            if (playerComponent < 0)
                playerComponent = nav.largestComponent;

            var candidates = new List<Vector3>();
            foreach (var s in m_Loader.spawnPoints)
            {
                // Only spawn hunters that can actually reach the player. One
                // sealed off in another room is just a decoration.
                if (nav.TrySnap(s, out var snapped) &&
                    nav.ComponentAt(snapped) == playerComponent)
                    candidates.Add(snapped);
            }

            if (candidates.Count == 0)
                Debug.LogWarning("[Game] No spawn point shares the player's navigable " +
                                 "region; the scan may be fragmented.");

            // Farthest first, so hunters start spread across the house.
            candidates.Sort((a, b) =>
                Vector3.SqrMagnitude(b - player).CompareTo(Vector3.SqrMagnitude(a - player)));

            void Take(System.Func<Vector3, bool> accept)
            {
                foreach (var c in candidates)
                {
                    if (chosen.Count >= count) return;
                    if (chosen.Contains(c)) continue;
                    if (accept(c)) chosen.Add(c);
                }
            }

            Take(c => Vector3.Distance(c, player) >= m_MinSpawnDistance &&
                      (!m_SpawnOutOfSight || !nav.HasLineOfSight(c, player)));
            Take(c => Vector3.Distance(c, player) >= m_MinSpawnDistance);
            Take(_ => true);
            return chosen;
        }

        void ClearHunters()
        {
            m_Hunters.Clear();
            foreach (var v in m_Views)
            {
                if (v != null)
                    Destroy(v.gameObject);
            }
            m_Views.Clear();
        }

        Transform CreateView(int index)
        {
            if (m_ViewRoot == null)
                m_ViewRoot = new GameObject("Hunters").transform;

            var go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            go.name = $"Hunter {index}";
            go.transform.SetParent(m_ViewRoot, worldPositionStays: true);
            go.transform.localScale = new Vector3(0.45f, 0.9f, 0.45f);
            // Colliders would fight the grid-based movement, which is the single
            // source of truth for where a hunter may be.
            var col = go.GetComponent<Collider>();
            if (col != null)
                Destroy(col);

            var mr = go.GetComponent<MeshRenderer>();
            if (mr != null)
                mr.sharedMaterial = HunterMaterial();
            return go.transform;
        }

        static Material s_HunterMaterial;

        /// Primitives default to the built-in pipeline's material, which renders
        /// magenta under URP, so build an explicit unlit one.
        static Material HunterMaterial()
        {
            if (s_HunterMaterial != null)
                return s_HunterMaterial;

            var shader = Shader.Find("Universal Render Pipeline/Unlit")
                         ?? Shader.Find("Unlit/Color")
                         ?? Shader.Find("Sprites/Default");
            s_HunterMaterial = new Material(shader) { name = "HunterUnlit" };
            var colour = new Color(0.95f, 0.25f, 0.2f, 1f);
            if (s_HunterMaterial.HasProperty("_BaseColor"))
                s_HunterMaterial.SetColor("_BaseColor", colour);
            if (s_HunterMaterial.HasProperty("_Color"))
                s_HunterMaterial.SetColor("_Color", colour);
            return s_HunterMaterial;
        }
    }
}
