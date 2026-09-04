using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using GaussianSplatting.Runtime;
using Unity.Collections;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace HouseScan
{
    /// <summary>
    /// Loads a player-supplied house scan (.ply) from disk at runtime and hands it
    /// to a GaussianSplatRenderer. This is the ingestion path for captures made
    /// with Polycam / Scaniverse / Hyperscape-style tools; nothing here depends on
    /// the asset having been imported in the editor.
    /// </summary>
    [RequireComponent(typeof(GaussianSplatRenderer))]
    public class HouseScanLoader : MonoBehaviour, ILevelSource
    {
        [Tooltip("Absolute path, or a file name resolved against the scans folder.")]
        public string m_ScanPath = "house.ply";

        [Tooltip("Coordinate convention of the source capture.")]
        public GaussianPlyRuntimeReader.SourceConvention m_Convention =
            GaussianPlyRuntimeReader.SourceConvention.AsAuthored;

        [Tooltip("Hard cap on loaded splats; 0 means load everything. Large whole-house " +
                 "captures are subsampled to fit the platform memory budget.")]
        public int m_MaxSplats;

        [Tooltip("Splat cap applied on mobile VR (Quest) when m_MaxSplats is 0. " +
                 "Standalone headsets sort far fewer Gaussians per frame than a " +
                 "desktop GPU, and there are two eyes to sort for.")]
        public int m_MaxSplatsMobile = 400000;

        /// <summary>
        /// Cap actually used for the current platform. An explicit
        /// <see cref="m_MaxSplats"/> always wins; otherwise mobile VR gets a
        /// conservative default and desktop is uncapped.
        /// </summary>
        public int EffectiveMaxSplats
        {
            get
            {
                if (m_MaxSplats > 0)
                    return m_MaxSplats;
                return Application.isMobilePlatform ? m_MaxSplatsMobile : 0;
            }
        }


        [Tooltip("Load during Start automatically.")]
        public bool m_LoadOnStart = true;

        [Tooltip("Derive floor height, walkable area and spawn points as the scan loads.")]
        public bool m_AnalyzeOnLoad = true;

        [Tooltip("Occupancy grid resolution in metres.")]
        public float m_AnalysisCellSize = 0.25f;

        public int m_SpawnPointCount = 12;

        /// Level data derived from the scan. Null until a scan has been analysed.
        public ScanLevelAnalysis analysis { get; private set; }

        /// Spawn positions on the floor, spread across the walkable area.
        public List<Vector3> spawnPoints { get; private set; } = new();

        IReadOnlyList<Vector3> ILevelSource.spawnPoints => spawnPoints;

        // A scan measures the whole room, so a person-sized agent is fine.
        float ILevelSource.agentRadius => 0f;

        /// Raised once the scan is loaded and analysed, so gameplay can start.
        public event System.Action<HouseScanLoader> onScanReady;

        public bool isLoaded { get; private set; }
        public int loadedSplatCount { get; private set; }
        public double loadMilliseconds { get; private set; }
        public Bounds scanBounds { get; private set; }
        public string lastError { get; private set; }

        GaussianSplatAsset m_RuntimeAsset;

        /// <summary>Folder that user scans are dropped into on any platform.</summary>
        public static string ScansFolder =>
            System.IO.Path.Combine(Application.persistentDataPath, "Scans");

        public string ResolvePath()
        {
            if (!string.IsNullOrEmpty(m_ScanPath) && System.IO.Path.IsPathRooted(m_ScanPath))
                return m_ScanPath;

            string folder = ScansFolder;
            if (!string.IsNullOrEmpty(m_ScanPath))
            {
                string named = System.IO.Path.Combine(folder, m_ScanPath);
                if (System.IO.File.Exists(named))
                    return named;
            }

            // Sideloading a headset build means copying a scan in by hand, so the
            // file name rarely matches the default. Fall back to whatever scan is
            // actually there instead of failing on a name mismatch.
            if (System.IO.Directory.Exists(folder))
            {
                var found = System.IO.Directory.GetFiles(folder, "*.ply");
                if (found.Length > 0)
                {
                    System.Array.Sort(found);
                    return found[0];
                }
            }

            return string.IsNullOrEmpty(m_ScanPath)
                ? null
                : System.IO.Path.Combine(folder, m_ScanPath);
        }

        void Awake()
        {
            // Create the folder up front so it is visible to adb/MTP before the
            // user has managed to load anything.
            try { System.IO.Directory.CreateDirectory(ScansFolder); }
            catch (System.Exception e) { Debug.LogWarning($"[HouseScanLoader] {e.Message}"); }
        }

        void Start()
        {
            if (m_LoadOnStart)
                Load();
        }

        public bool Load()
        {
            lastError = null;
            string path = ResolvePath();
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
            {
                lastError = $"Scan not found: {path}";
                Debug.LogError($"[HouseScanLoader] {lastError}");
                return false;
            }

            var sw = Stopwatch.StartNew();
            NativeArray<RuntimeSplatData> splats = default;
            try
            {
                splats = GaussianPlyRuntimeReader.Load(path, m_Convention, EffectiveMaxSplats);
                var asset = GaussianSplatRuntimeBuilder.Build(splats, System.IO.Path.GetFileName(path));
                if (asset == null)
                {
                    lastError = "Failed to build splat asset.";
                    return false;
                }

                Apply(asset);
                loadedSplatCount = asset.splatCount;
                var b = new Bounds();
                b.SetMinMax(asset.boundsMin, asset.boundsMax);
                scanBounds = b;

                if (m_AnalyzeOnLoad)
                {
                    analysis = ScanLevelAnalyzer.Analyze(splats, m_AnalysisCellSize);
                    spawnPoints = ScanLevelAnalyzer.PickSpawnPoints(analysis, m_SpawnPointCount);
                    Debug.Log($"[HouseScanLoader] Level: floor {analysis.floorY:F2} m, " +
                              $"ceiling {analysis.ceilingY:F2} m, " +
                              $"{analysis.WalkableAreaSqm:F1} m² walkable, " +
                              $"{spawnPoints.Count} spawn points.");
                }
            }
            catch (System.Exception e)
            {
                lastError = e.Message;
                Debug.LogError($"[HouseScanLoader] Failed to load '{path}': {e}");
                return false;
            }
            finally
            {
                if (splats.IsCreated)
                    splats.Dispose();
            }

            sw.Stop();
            loadMilliseconds = sw.Elapsed.TotalMilliseconds;
            isLoaded = true;
            Debug.Log($"[HouseScanLoader] Loaded {loadedSplatCount} splats from '{path}' " +
                      $"in {loadMilliseconds:F0} ms, bounds {scanBounds.min} .. {scanBounds.max}");
            onScanReady?.Invoke(this);
            return true;
        }

        void Apply(GaussianSplatAsset asset)
        {
            var renderer = GetComponent<GaussianSplatRenderer>();
            DisposeRuntimeAsset();
            m_RuntimeAsset = asset;

            // The splat renderer sorts on the GPU, so it cannot initialise without
            // compute support. That is the normal case in a -nographics batch run,
            // where we only want the derived level data; loading should still
            // succeed rather than drown the log in kernel errors.
            bool headless = SystemInfo.graphicsDeviceType ==
                            UnityEngine.Rendering.GraphicsDeviceType.Null;
            if (renderer == null || headless || !SystemInfo.supportsComputeShaders)
            {
                if (renderer != null)
                    renderer.enabled = false;
                Debug.Log("[HouseScanLoader] No compute shader support; scan loaded for " +
                          "analysis only, not rendered.");
                return;
            }

            renderer.m_Asset = asset;
            // Force the renderer to rebuild its GPU resources for the new asset.
            renderer.enabled = false;
            renderer.enabled = true;
        }

        void DisposeRuntimeAsset()
        {
            if (m_RuntimeAsset == null)
                return;
            m_RuntimeAsset.DisposeRuntimeData();
            // Object.Destroy is rejected outside play mode and does nothing, so
            // loading a second scan from editor tooling would leak the first.
            if (Application.isPlaying)
                Destroy(m_RuntimeAsset);
            else
                DestroyImmediate(m_RuntimeAsset);
            m_RuntimeAsset = null;
        }

        void OnDestroy()
        {
            DisposeRuntimeAsset();
        }
    }
}
