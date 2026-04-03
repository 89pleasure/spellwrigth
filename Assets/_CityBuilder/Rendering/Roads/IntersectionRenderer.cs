using System.Collections.Generic;
using CityBuilder.Core;
using CityBuilder.Core.EventBus;
using CityBuilder.Infrastructure.Roads;
using CityBuilder.Rendering;
using UnityEngine;
using Vector3 = UnityEngine.Vector3;

#nullable enable
namespace CityBuilder.Rendering.Roads
{
    /// <summary>
    /// Maintains the intersection mesh GameObjects at road nodes.
    ///
    /// Every time a road is built or demolished, the nodes at both ends of that
    /// segment are queued as dirty. In LateUpdate – after all event handlers have
    /// run for the frame – dirty nodes are rebuilt:
    ///
    ///   1. IntersectionMeshBuilder computes clip t values and intersection mesh data.
    ///   2. TrimmedStartT / TrimmedEndT are updated on the affected segments.
    ///   3. The intersection GameObject is replaced.
    ///   4. All affected segment meshes are rebuilt via RoadRenderer.RebuildSegment()
    ///      so they reflect their new clipped length.
    ///
    /// The LateUpdate flush avoids ordering issues between this component and
    /// RoadRenderer – both subscribe to the same events, but clipping must be
    /// applied before the segment mesh is finalized.
    ///
    /// Assign RoadProfile and RoadRenderer in the Inspector.
    /// Both components should sit on the same GameObject.
    /// </summary>
    public class IntersectionRenderer : MonoBehaviour,
        IEventHandler<RoadBuiltEvent>,
        IEventHandler<RoadDemolishedEvent>
    {
        [SerializeField] private RoadProfile?  roadProfile;
        [SerializeField] private RoadRenderer? roadRenderer;

        [Header("Materials")]
        [Tooltip("Materials for intersection meshes. Should match the road materials for visual consistency.")]
        [SerializeField] private Material[] intersectionMaterials = System.Array.Empty<Material>();

        [Header("Elevation")]
        [Tooltip("Lifts intersection meshes to match the road surface elevation.")]
        [SerializeField] private float roadElevation = 0.05f;

        // NodeIds whose intersection mesh must be rebuilt next LateUpdate
        private readonly HashSet<int>          _dirtyNodes         = new();
        // SegmentIds that need segment mesh rebuilds (collected while processing dirty nodes)
        private readonly HashSet<int>          _dirtySegments      = new();
        // NodeId → intersection GameObject
        private readonly Dictionary<int, GameObject> _intersections = new();

        private void Start()
        {
            if (roadProfile == null)
            {
                Debug.LogWarning("IntersectionRenderer: no RoadProfile assigned – intersections will not be visible.", this);
                return;
            }

            EventBus bus = GameServices.Instance!.Bus;
            bus.Subscribe<RoadBuiltEvent>(this);
            bus.Subscribe<RoadDemolishedEvent>(this);

            // Mark all existing nodes dirty so their intersections are built on first frame
            foreach (int nodeId in GameServices.Instance.Roads.Graph.Nodes.Keys)
                _dirtyNodes.Add(nodeId);
        }

        // ─────────────────────────────────────────────────────────
        //  Event handlers – queue dirty nodes, rebuild in LateUpdate
        // ─────────────────────────────────────────────────────────

        public void Handle(RoadBuiltEvent evt)
        {
            _dirtyNodes.Add(evt.NodeAId);
            _dirtyNodes.Add(evt.NodeBId);
        }

        public void Handle(RoadDemolishedEvent evt)
        {
            _dirtyNodes.Add(evt.NodeAId);
            _dirtyNodes.Add(evt.NodeBId);
        }

        // ─────────────────────────────────────────────────────────
        //  LateUpdate: rebuild all dirty nodes and their segments
        // ─────────────────────────────────────────────────────────

        private void LateUpdate()
        {
            if (_dirtyNodes.Count == 0 || !roadProfile)
                return;

            RoadGraph graph = GameServices.Instance!.Roads.Graph;

            // ── Pass 1: compute clip t values and rebuild intersection meshes ──
            foreach (int nodeId in _dirtyNodes)
            {
                if (!graph.Nodes.TryGetValue(nodeId, out RoadNode node))
                {
                    // Node was removed (last segment demolished) → destroy intersection
                    DestroyIntersection(nodeId);
                    continue;
                }

                RoadMeshData? meshData = IntersectionMeshBuilder.Build(
                    node, graph.Segments, graph.Nodes, roadProfile, out List<IntersectionMeshBuilder.SegmentClip> clips);

                // Apply clip t values to segments and queue them for mesh rebuild
                foreach (IntersectionMeshBuilder.SegmentClip clip in clips)
                {
                    if (!graph.Segments.TryGetValue(clip.SegmentId, out RoadSegment seg))
                        continue;

                    // Only update and queue if the clip values actually changed
                    bool changed =
                        !Mathf.Approximately(seg.TrimmedStartT, clip.TrimmedStartT) ||
                        !Mathf.Approximately(seg.TrimmedEndT,   clip.TrimmedEndT);

                    seg.TrimmedStartT = clip.TrimmedStartT;
                    seg.TrimmedEndT   = clip.TrimmedEndT;

                    if (changed)
                        _dirtySegments.Add(clip.SegmentId);
                }

                // Replace the intersection GameObject
                DestroyIntersection(nodeId);

                if (meshData != null)
                    SpawnIntersection(nodeId, node.Position, meshData);
            }

            _dirtyNodes.Clear();

            // ── Pass 2: rebuild segment meshes whose clip t values changed ────
            if (roadRenderer)
            {
                foreach (int segId in _dirtySegments)
                    roadRenderer.RebuildSegment(segId);
            }

            _dirtySegments.Clear();
        }

        // ─────────────────────────────────────────────────────────
        //  Intersection GameObject management
        // ─────────────────────────────────────────────────────────

        private void SpawnIntersection(int nodeId, Vector3 position, RoadMeshData data)
        {
            GameObject go = new ($"Intersection_{nodeId}");
            go.transform.position = new Vector3(position.x, position.y + roadElevation, position.z);

            int roadLayer = LayerMask.NameToLayer("Road");
            if (roadLayer != -1)
                go.layer = roadLayer;

            Mesh mesh        = BuildMesh(data);
            go.AddComponent<MeshFilter>().sharedMesh    = mesh;
            go.AddComponent<MeshCollider>().sharedMesh  = mesh;

            MeshRenderer mr           = go.AddComponent<MeshRenderer>();
            mr.sharedMaterials        = BuildMaterialArray(data.Triangles.Length);

            _intersections[nodeId] = go;
        }

        private void DestroyIntersection(int nodeId)
        {
            if (_intersections.TryGetValue(nodeId, out GameObject existing))
            {
                Destroy(existing);
                _intersections.Remove(nodeId);
            }
        }

        private static Mesh BuildMesh(RoadMeshData data)
        {
            Mesh mesh        = new();
            mesh.name        = "IntersectionMesh";
            mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.SetVertices(data.Vertices);
            mesh.SetNormals(data.Normals);
            mesh.SetUVs(0, data.UVs);

            mesh.subMeshCount = data.Triangles.Length;
            for (int i = 0; i < data.Triangles.Length; i++)
                mesh.SetTriangles(data.Triangles[i], i);

            mesh.RecalculateBounds();
            mesh.UploadMeshData(markNoLongerReadable: true);
            return mesh;
        }

        /// <summary>
        /// Returns a material array for the intersection, using the assigned
        /// intersection materials. Falls back to the last available material
        /// when the submesh count exceeds the array length.
        /// </summary>
        private Material[] BuildMaterialArray(int submeshCount)
        {
            Material[] mats = new Material[submeshCount];
            for (int i = 0; i < submeshCount; i++)
            {
                mats[i] = i < intersectionMaterials.Length
                    ? intersectionMaterials[i]
                    : intersectionMaterials.Length > 0
                        ? intersectionMaterials[^1]
                        : RenderingUtils.DefaultErrorMaterial;
            }

            return mats;
        }

        private void OnDestroy()
        {
            if (GameServices.Instance != null)
            {
                EventBus bus = GameServices.Instance.Bus;
                bus.Unsubscribe<RoadBuiltEvent>(this);
                bus.Unsubscribe<RoadDemolishedEvent>(this);
            }

            foreach (GameObject go in _intersections.Values)
            {
                if (go != null)
                    Destroy(go);
            }

            _intersections.Clear();
        }
    }
}
