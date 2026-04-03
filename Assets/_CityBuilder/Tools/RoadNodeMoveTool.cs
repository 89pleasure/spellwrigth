using System;
using CityBuilder.Core;
using CityBuilder.Infrastructure.Roads;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.InputSystem;

#nullable enable
namespace CityBuilder.Tools
{
    /// <summary>
    /// Drag-to-move tool for road nodes.
    ///
    /// G              – toggle tool on/off
    /// Left click     – grab the nearest node within snap radius and start drag
    /// Drag + release – commits the node to the new terrain position; meshes rebuild automatically
    /// Right click    – cancel the active drag (node stays at original position)
    ///
    /// While dragging a cyan sphere follows the cursor to show where the node will land.
    /// </summary>
    public class RoadNodeMoveTool : MonoBehaviour
    {
        private const float NodeSnapRadius = 5f;
        private static readonly int _baseColorId = Shader.PropertyToID("_BaseColor");

        [SerializeField] private Color previewColor  = new(0.2f, 0.8f, 1f, 1f);
        [SerializeField] private float previewRadius = 1f;

        public event Action<bool>? OnActiveChanged;
        public bool IsActive { get; private set; }

        private int   _draggedNodeId = -1;
        private Camera?     _camera;
        private GameObject? _previewSphere;

        private void Start()
        {
            _camera = Camera.main;

            _previewSphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            _previewSphere.name = "NodeMovePreview";
            _previewSphere.transform.localScale = Vector3.one * (previewRadius * 2f);

            // Remove the sphere collider so it does not interfere with terrain raycasts.
            Destroy(_previewSphere.GetComponent<Collider>());

            Material mat = new(Shader.Find("CityBuilder/FlatShading") ?? Shader.Find("Hidden/InternalErrorShader"));
            mat.SetColor(_baseColorId, previewColor);
            _previewSphere.GetComponent<MeshRenderer>().sharedMaterial = mat;
            _previewSphere.SetActive(false);
        }

        private void Update()
        {
            Keyboard kb = Keyboard.current;
            Mouse    ms = Mouse.current;
            if (kb == null || ms == null)
                return;

            if (kb.gKey.wasPressedThisFrame)
                SetActive(!IsActive);

            if (!IsActive)
                return;

            Vector3? worldPos = RaycastTerrain(ms.position.value);

            if (_draggedNodeId >= 0)
                UpdateDrag(ms, worldPos);
            else
                UpdateIdle(ms, worldPos);
        }

        // ─────────────────────────────────────────────────────────
        //  Drag state
        // ─────────────────────────────────────────────────────────

        private void UpdateIdle(Mouse ms, Vector3? worldPos)
        {
            _previewSphere!.SetActive(false);

            if (worldPos.HasValue && ms.leftButton.wasPressedThisFrame)
                TryStartDrag(new float3(worldPos.Value.x, worldPos.Value.y, worldPos.Value.z));
        }

        private void UpdateDrag(Mouse ms, Vector3? worldPos)
        {
            if (worldPos.HasValue)
            {
                _previewSphere!.transform.position = worldPos.Value + Vector3.up * 0.5f;
                _previewSphere.SetActive(true);
            }
            else
            {
                _previewSphere!.SetActive(false);
            }

            if (ms.leftButton.wasReleasedThisFrame && worldPos.HasValue)
                CommitMove(new float3(worldPos.Value.x, worldPos.Value.y, worldPos.Value.z));

            if (ms.rightButton.wasPressedThisFrame)
                CancelDrag();
        }

        private void TryStartDrag(float3 worldPos)
        {
            RoadNode? node = GameServices.Instance!.Roads.Graph.FindNearestNode(worldPos, NodeSnapRadius);
            if (node == null)
                return;

            _draggedNodeId = node.Id;
        }

        private void CommitMove(float3 newPos)
        {
            GameServices.Instance!.Roads.MoveNode(_draggedNodeId, newPos, Time.time);
            _draggedNodeId = -1;
            _previewSphere!.SetActive(false);
        }

        private void CancelDrag()
        {
            _draggedNodeId = -1;
            _previewSphere!.SetActive(false);
        }

        // ─────────────────────────────────────────────────────────
        //  Public API (tool management / toolbar integration)
        // ─────────────────────────────────────────────────────────

        public void SetActive(bool active)
        {
            IsActive = active;
            if (!IsActive)
                CancelDrag();
            OnActiveChanged?.Invoke(IsActive);
        }

        // ─────────────────────────────────────────────────────────
        //  Helpers
        // ─────────────────────────────────────────────────────────

        private Vector3? RaycastTerrain(Vector2 screenPos)
        {
            if (!_camera)
                return null;

            int roadLayer = LayerMask.NameToLayer("Road");
            int layerMask = roadLayer != -1 ? ~(1 << roadLayer) : Physics.DefaultRaycastLayers;

            Ray ray = _camera.ScreenPointToRay(screenPos);
            return Physics.Raycast(ray, out RaycastHit hit, Mathf.Infinity, layerMask) ? hit.point : null;
        }

        private void OnDestroy()
        {
            if (_previewSphere != null)
                Destroy(_previewSphere);
        }
    }
}
