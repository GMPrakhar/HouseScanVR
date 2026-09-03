using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

namespace HouseScan
{
    /// <summary>
    /// Puts a player inside a loaded house scan and keeps them on the parts of it
    /// that were actually captured as floor.
    ///
    /// A Gaussian splat scan has no collision geometry, so movement is constrained
    /// against the occupancy grid produced by <see cref="ScanLevelAnalyzer"/>
    /// rather than against physics colliders. The rig works with an XR headset
    /// when one is connected and falls back to keyboard/mouse otherwise, so the
    /// scene remains testable on a desktop without hardware.
    /// </summary>
    public class ScanPlayerRig : MonoBehaviour
    {
        [Tooltip("Scan to spawn inside. Found automatically when left empty.")]
        public HouseScanLoader m_Loader;

        [Tooltip("Camera driven by this rig. Uses the main camera when left empty.")]
        public Camera m_Camera;

        [Tooltip("Eye height above the floor used when no headset reports one.")]
        public float m_EyeHeight = 1.65f;

        public float m_MoveSpeed = 2.0f;
        public float m_LookSpeed = 120f;

        [Tooltip("How far from a wall the player is kept, in metres.")]
        public float m_WallClearance = 0.25f;

        [Tooltip("Which of the analyser's spawn points to start on.")]
        public int m_SpawnIndex;

        public bool isPlaced { get; private set; }
        public Vector3 floorPosition { get; private set; }

        ScanLevelAnalysis m_Analysis;
        float m_Yaw;
        float m_Pitch;

        void Awake()
        {
            if (m_Loader == null)
                m_Loader = FindFirstObjectByType<HouseScanLoader>();
            if (m_Camera == null)
                m_Camera = Camera.main;
        }

        void OnEnable()
        {
            if (m_Loader == null)
                return;
            m_Loader.onScanReady += OnScanReady;
            if (m_Loader.isLoaded)
                OnScanReady(m_Loader);
        }

        void OnDisable()
        {
            if (m_Loader != null)
                m_Loader.onScanReady -= OnScanReady;
        }

        void OnScanReady(HouseScanLoader loader)
        {
            Bind(loader.analysis);
            if (m_Analysis != null)
                Place(loader.spawnPoints);
        }

        /// Attaches level data to the rig directly. Used by the scan-ready event and
        /// by tests that drive the rig without entering play mode.
        public void Bind(ScanLevelAnalysis analysis)
        {
            m_Analysis = analysis;
            if (m_Analysis == null)
            {
                Debug.LogWarning("[ScanPlayerRig] Scan has no level analysis; " +
                                 "movement will be unconstrained.");
            }
        }

        /// Drops the player onto a spawn point, or onto the centre of the largest
        /// walkable area if the scan produced no spawn points.
        public void Place(List<Vector3> spawnPoints)
        {
            Vector3 p;
            if (spawnPoints != null && spawnPoints.Count > 0)
            {
                int i = Mathf.Clamp(m_SpawnIndex, 0, spawnPoints.Count - 1);
                p = spawnPoints[i];
            }
            else
            {
                p = m_Analysis != null ? m_Analysis.bounds.center : Vector3.zero;
                p.y = m_Analysis != null ? m_Analysis.floorY : 0f;
            }

            floorPosition = p;
            transform.position = p;
            isPlaced = true;

            // Face the middle of the space, which is where the interesting geometry
            // usually is, rather than an arbitrary compass direction.
            if (m_Analysis != null)
            {
                Vector3 toCentre = m_Analysis.bounds.center - p;
                toCentre.y = 0f;
                if (toCentre.sqrMagnitude > 1e-4f)
                    m_Yaw = Quaternion.LookRotation(toCentre).eulerAngles.y;
            }

            ApplyCamera();
            Debug.Log($"[ScanPlayerRig] Placed at {p} facing {m_Yaw:F0}°.");
        }

        void Update()
        {
            if (!isPlaced)
                return;

            if (!IsHeadsetPresent())
                DesktopLook();

            Vector3 wish = ReadMoveInput();
            if (wish.sqrMagnitude > 1e-6f)
            {
                Vector3 dir = Quaternion.Euler(0f, m_Yaw, 0f) * wish;
                Move(dir.normalized * (m_MoveSpeed * Time.deltaTime));
            }

            ApplyCamera();
        }

        static bool IsHeadsetPresent()
        {
            var devices = new List<InputDevice>();
            InputDevices.GetDevicesWithCharacteristics(
                InputDeviceCharacteristics.HeadMounted, devices);
            return devices.Count > 0;
        }

        Vector3 ReadMoveInput()
        {
            var wish = Vector3.zero;

            // Thumbstick when a controller is connected.
            var hands = new List<InputDevice>();
            InputDevices.GetDevicesWithCharacteristics(
                InputDeviceCharacteristics.Controller | InputDeviceCharacteristics.Left, hands);
            foreach (var d in hands)
            {
                if (d.TryGetFeatureValue(CommonUsages.primary2DAxis, out Vector2 axis) &&
                    axis.sqrMagnitude > 0.04f)
                {
                    wish += new Vector3(axis.x, 0f, axis.y);
                }
            }

            if (wish.sqrMagnitude < 1e-6f)
            {
                // Desktop fallback so the scene is usable without hardware.
                if (Input.GetKey(KeyCode.W)) wish.z += 1f;
                if (Input.GetKey(KeyCode.S)) wish.z -= 1f;
                if (Input.GetKey(KeyCode.D)) wish.x += 1f;
                if (Input.GetKey(KeyCode.A)) wish.x -= 1f;
            }
            return wish;
        }

        void DesktopLook()
        {
            if (!Input.GetMouseButton(1))
                return;
            m_Yaw += Input.GetAxis("Mouse X") * m_LookSpeed * Time.deltaTime;
            m_Pitch = Mathf.Clamp(m_Pitch - Input.GetAxis("Mouse Y") * m_LookSpeed * Time.deltaTime,
                                  -85f, 85f);
        }

        /// Moves by <paramref name="delta"/>, sliding along blocked directions so
        /// the player brushes past furniture instead of sticking to it.
        public void Move(Vector3 delta)
        {
            Vector3 p = transform.position;
            Vector3 target = p + delta;

            if (IsWalkable(target))
            {
                transform.position = new Vector3(target.x, FloorAt(target), target.z);
                return;
            }

            // Try each axis on its own to slide along the obstacle.
            var slideX = new Vector3(target.x, p.y, p.z);
            if (IsWalkable(slideX))
            {
                transform.position = new Vector3(slideX.x, FloorAt(slideX), slideX.z);
                return;
            }

            var slideZ = new Vector3(p.x, p.y, target.z);
            if (IsWalkable(slideZ))
                transform.position = new Vector3(slideZ.x, FloorAt(slideZ), slideZ.z);
        }

        float FloorAt(Vector3 p) => m_Analysis != null ? m_Analysis.floorY : 0f;

        /// True when the point, plus a clearance margin around it, is on captured
        /// floor. The margin stops the camera from pushing into a wall, where a
        /// splat scan looks like a smear of noise.
        public bool IsWalkable(Vector3 p)
        {
            if (m_Analysis == null)
                return true;

            if (!CellWalkable(p))
                return false;

            float r = m_WallClearance;
            return CellWalkable(p + new Vector3(r, 0f, 0f))
                && CellWalkable(p + new Vector3(-r, 0f, 0f))
                && CellWalkable(p + new Vector3(0f, 0f, r))
                && CellWalkable(p + new Vector3(0f, 0f, -r));
        }

        bool CellWalkable(Vector3 p)
        {
            if (!m_Analysis.TryWorldToCell(p, out int x, out int z))
                return false;
            return m_Analysis.walkable[z * m_Analysis.gridWidth + x];
        }

        void ApplyCamera()
        {
            if (m_Camera == null)
                return;

            // With a headset the runtime drives the camera pose; the rig only
            // supplies the floor-level origin it is tracked relative to.
            if (IsHeadsetPresent())
            {
                m_Camera.transform.localPosition = Vector3.zero;
                m_Camera.transform.localRotation = Quaternion.identity;
                return;
            }

            m_Camera.transform.position = transform.position + Vector3.up * m_EyeHeight;
            m_Camera.transform.rotation = Quaternion.Euler(m_Pitch, m_Yaw, 0f);
        }
    }
}
