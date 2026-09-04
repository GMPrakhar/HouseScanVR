using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace HouseScan
{
    /// <summary>
    /// The in-app "map your house" flow: the player walks around wearing the
    /// headset and the space they cover becomes the level.
    ///
    /// This exists so that playing in your own home does not require a second
    /// app, a USB cable and a file copied into a directory whose name you have
    /// to type. Mapping is something you do in the headset, in the game.
    /// </summary>
    public class RoomMappingSession : MonoBehaviour, ILevelSource
    {
        public enum Stage
        {
            /// Nothing mapped yet, waiting for the player to start.
            Idle,
            /// Recording where the player walks.
            Mapping,
            /// Enough space covered; the player can keep going or finish.
            ReadyToFinish,
            /// Baked into a level.
            Complete,
        }

        [Header("Rig")]
        public ScanPlayerRig m_Rig;
        public Transform m_HeadOverride;

        [Header("Mapping")]
        public float m_CellSize = 0.25f;
        /// Enough floor for a round to be worth playing. Roughly a small room.
        public float m_MinimumAreaSqm = 12f;
        public int m_SpawnPointCount = 16;

        /// Agents on a walked map must be narrower than the person who walked
        /// it. The only thing walking proves is that a body of the mapper's
        /// width passed along the trail, so anything narrower than that fits by
        /// construction, and anything wider might not.
        public float m_AgentRadius = 0.20f;
        /// Poses are recorded at most this often; walking does not need 72 Hz.
        public float m_SampleInterval = 1f / 15f;

        [Header("Persistence")]
        public bool m_LoadOnStart = true;
        public string m_FileName = "room-map.json";

        public Stage stage { get; private set; } = Stage.Idle;
        public RoomMapper mapper { get; private set; }
        public ScanLevelAnalysis analysis { get; private set; }
        public IReadOnlyList<Vector3> spawnPoints => m_Spawns;
        public string lastError { get; private set; }
        public float agentRadius => m_AgentRadius;

        /// Raised whenever the mapped area crosses a whole square metre, so the
        /// UI can report progress without polling every frame.
        public event System.Action<RoomMappingSession> onProgress;
        public event System.Action<RoomMappingSession> onComplete;

        readonly List<Vector3> m_Spawns = new();
        float m_SampleTimer;
        int m_ReportedSqm;

        public string SavePath => Path.Combine(Application.persistentDataPath, m_FileName);

        public float mappedAreaSqm => mapper?.mappedAreaSqm ?? 0f;
        public float progress01 =>
            m_MinimumAreaSqm <= 0f ? 1f : Mathf.Clamp01(mappedAreaSqm / m_MinimumAreaSqm);

        bool m_LoadAttempted;

        void Start() => EnsureLoaded();

        /// <summary>
        /// Loads the saved map if there is one, at most once.
        ///
        /// Callable from outside because Start order between components is not
        /// defined: RoomMappingFlow has to know whether a map already exists
        /// before it decides what to tell the player, and if it asked first it
        /// could talk them into re-mapping a house that was already mapped.
        /// </summary>
        public void EnsureLoaded()
        {
            if (m_LoadAttempted) return;
            m_LoadAttempted = true;
            if (m_LoadOnStart)
                TryLoad();
        }

        Transform Head
        {
            get
            {
                if (m_HeadOverride != null) return m_HeadOverride;
                if (m_Rig != null && m_Rig.m_Camera != null) return m_Rig.m_Camera.transform;
                return transform;
            }
        }

        /// <summary>Starts a fresh map, discarding anything mapped before.</summary>
        public void BeginMapping(float floorY)
        {
            m_LoadAttempted = true;     // an explicit start beats anything on disk
            mapper = new RoomMapper(floorY, m_CellSize);
            analysis = null;
            m_Spawns.Clear();
            m_ReportedSqm = 0;
            m_SampleTimer = 0f;
            stage = Stage.Mapping;
            Debug.Log($"[Mapping] Started. Walk around the space; " +
                      $"{m_MinimumAreaSqm:F0} m² needed before you can finish.");
        }

        /// <summary>Floor level is wherever the player is standing right now.</summary>
        public void BeginMapping() => BeginMapping(FloorFromRig());

        float FloorFromRig()
        {
            // The XR origin is floor level under a floor-referenced tracking
            // space, so the rig's own Y is a better estimate of the floor than
            // anything derived from the headset height.
            if (m_Rig != null) return m_Rig.transform.position.y;
            return transform.position.y;
        }

        void Update()
        {
            if (stage != Stage.Mapping && stage != Stage.ReadyToFinish)
                return;

            m_SampleTimer += Time.deltaTime;
            if (m_SampleTimer < m_SampleInterval)
                return;
            m_SampleTimer = 0f;

            Sample(Head.position);
        }

        /// <summary>
        /// Records one headset pose. Public so the flow can be driven at a fixed
        /// rate from a test rather than only by Update.
        /// </summary>
        public void Sample(Vector3 head)
        {
            if (mapper == null) return;
            mapper.AddPose(head);

            if (stage == Stage.Mapping && mappedAreaSqm >= m_MinimumAreaSqm)
            {
                stage = Stage.ReadyToFinish;
                Debug.Log($"[Mapping] {mappedAreaSqm:F1} m² mapped; enough to play. " +
                          $"Keep walking to include more of the house.");
            }

            int sqm = Mathf.FloorToInt(mappedAreaSqm);
            if (sqm != m_ReportedSqm)
            {
                m_ReportedSqm = sqm;
                onProgress?.Invoke(this);
            }
        }

        /// <summary>
        /// Bakes the walked space into a level. Returns false, with a reason in
        /// <see cref="lastError"/>, if there is not enough of it - which is a
        /// normal outcome worth showing the player, not a crash.
        /// </summary>
        public bool FinishMapping()
        {
            lastError = null;

            if (mapper == null || mapper.visitedCellCount == 0)
            {
                lastError = "Nothing was mapped. Put the headset on and walk around the room.";
                return Fail();
            }
            if (mappedAreaSqm < m_MinimumAreaSqm)
            {
                lastError = $"Only {mappedAreaSqm:F1} m² mapped, {m_MinimumAreaSqm:F0} m² needed. " +
                            $"Keep walking, especially through doorways.";
                return Fail();
            }

            analysis = mapper.Bake();
            if (analysis == null)
            {
                lastError = "The mapped area could not be turned into a level.";
                return Fail();
            }

            m_Spawns.Clear();
            m_Spawns.AddRange(ScanLevelAnalyzer.PickSpawnPoints(
                analysis, m_SpawnPointCount, clearance: m_AgentRadius));
            if (m_Spawns.Count == 0)
            {
                lastError = "No usable spawn points; the mapped space is too narrow.";
                return Fail();
            }

            stage = Stage.Complete;
            Debug.Log($"[Mapping] Complete: {mappedAreaSqm:F1} m² over " +
                      $"{mapper.pathLength:F0} m walked, {m_Spawns.Count} spawn points, " +
                      $"grid {analysis.gridWidth}x{analysis.gridHeight}.");
            onComplete?.Invoke(this);
            return true;
        }

        bool Fail()
        {
            Debug.LogWarning("[Mapping] " + lastError);
            return false;
        }

        public bool Save()
        {
            if (mapper == null) return false;
            try
            {
                File.WriteAllText(SavePath, mapper.ToJson());
                Debug.Log($"[Mapping] Saved to {SavePath}");
                return true;
            }
            catch (System.Exception e)
            {
                lastError = e.Message;
                Debug.LogError($"[Mapping] Save failed: {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// Restores a previously mapped house, so the player maps once rather
        /// than every time they put the headset on.
        /// </summary>
        public bool TryLoad()
        {
            if (!File.Exists(SavePath))
                return false;
            try
            {
                mapper = RoomMapper.FromJson(File.ReadAllText(SavePath));
                if (mapper == null) return false;
                m_CellSize = mapper.cellSize;
                bool ok = FinishMapping();
                if (ok)
                    Debug.Log($"[Mapping] Restored a {mappedAreaSqm:F1} m² map from disk.");
                return ok;
            }
            catch (System.Exception e)
            {
                lastError = e.Message;
                Debug.LogWarning($"[Mapping] Could not restore a saved map: {e.Message}");
                return false;
            }
        }
    }
}
