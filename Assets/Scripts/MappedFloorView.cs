using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace HouseScan
{
    /// <summary>
    /// Draws the floor that has been mapped so far, so the player can see their
    /// house being built under their feet as they walk it.
    ///
    /// This is the entire user interface for mapping. There is no progress bar
    /// to read and no menu to aim at: the floor you have covered glows, the
    /// floor you have not does not, and it is obvious what to do next.
    ///
    /// It is one mesh rebuilt occasionally rather than a tile object per cell.
    /// A mapped house runs to a few thousand cells, and a few thousand draw
    /// calls would cost more frame time on a Quest than the splat cloud does.
    /// </summary>
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public class MappedFloorView : MonoBehaviour
    {
        public RoomMappingSession m_Session;
        public Material m_Material;
        public Color m_Colour = new Color(0.20f, 0.85f, 0.55f, 1f);
        public float m_Height = 0.02f;
        /// Rebuilds per second. The map changes by a cell or two per step, which
        /// nobody can see at 72 Hz, and rebuilding costs more than it shows.
        public float m_RefreshHz = 6f;

        Mesh m_Mesh;
        MeshRenderer m_Renderer;
        float m_Timer;
        int m_LastCellCount = -1;

        readonly List<Vector3> m_Verts = new();
        readonly List<int> m_Tris = new();

        void Awake()
        {
            m_Mesh = new Mesh { name = "MappedFloor", indexFormat = IndexFormat.UInt32 };
            GetComponent<MeshFilter>().sharedMesh = m_Mesh;

            m_Renderer = GetComponent<MeshRenderer>();
            m_Renderer.shadowCastingMode = ShadowCastingMode.Off;
            m_Renderer.receiveShadows = false;
            if (m_Material == null)
            {
                var shader = Shader.Find("Universal Render Pipeline/Unlit")
                             ?? Shader.Find("Unlit/Color");
                m_Material = new Material(shader);
            }
            if (m_Material.HasProperty("_BaseColor")) m_Material.SetColor("_BaseColor", m_Colour);
            if (m_Material.HasProperty("_Color")) m_Material.SetColor("_Color", m_Colour);
            m_Renderer.sharedMaterial = m_Material;
        }

        void Update()
        {
            var mapper = m_Session == null ? null : m_Session.mapper;
            if (mapper == null)
            {
                m_Renderer.enabled = false;
                return;
            }

            m_Timer += Time.deltaTime;
            if (m_Timer < 1f / Mathf.Max(0.1f, m_RefreshHz)) return;
            m_Timer = 0f;

            if (mapper.visitedCellCount == m_LastCellCount) return;
            m_LastCellCount = mapper.visitedCellCount;
            Rebuild(mapper);
        }

        void Rebuild(RoomMapper mapper)
        {
            m_Verts.Clear();
            m_Tris.Clear();

            float s = mapper.cellSize;
            float inset = s * 0.04f;         // a hairline gap, so cells read as cells
            float y = mapper.floorY + m_Height;

            foreach (var cell in mapper.VisitedCells())
            {
                float x0 = cell.x * s + inset, x1 = (cell.x + 1) * s - inset;
                float z0 = cell.z * s + inset, z1 = (cell.z + 1) * s - inset;

                int v = m_Verts.Count;
                m_Verts.Add(new Vector3(x0, y, z0));
                m_Verts.Add(new Vector3(x0, y, z1));
                m_Verts.Add(new Vector3(x1, y, z1));
                m_Verts.Add(new Vector3(x1, y, z0));
                m_Tris.Add(v); m_Tris.Add(v + 1); m_Tris.Add(v + 2);
                m_Tris.Add(v); m_Tris.Add(v + 2); m_Tris.Add(v + 3);
            }

            m_Mesh.Clear();
            m_Mesh.SetVertices(m_Verts);
            m_Mesh.SetTriangles(m_Tris, 0);
            m_Mesh.RecalculateBounds();
            m_Renderer.enabled = m_Verts.Count > 0;
        }

        /// <summary>Hides the mapped floor once play starts, so the level is the
        /// player's actual house rather than a grid drawn over it.</summary>
        public void Hide()
        {
            if (m_Renderer != null) m_Renderer.enabled = false;
        }
    }
}
