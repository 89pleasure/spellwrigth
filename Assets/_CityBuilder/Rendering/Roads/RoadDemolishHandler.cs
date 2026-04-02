using CityBuilder.Core;
using CityBuilder.Tools;
using UnityEngine;
using UnityEngine.InputSystem;

#nullable enable
namespace CityBuilder.Rendering.Roads
{
    /// <summary>
    /// Demolishes road segments via direct raycast hit on the road mesh.
    /// Highlights the hovered segment while the bulldozer tool is active.
    ///
    /// Both BulldozerTool and RoadRenderer are resolved automatically at runtime
    /// via FindObjectOfType – no manual Inspector wiring required.
    /// Optionally assign them in the Inspector to override the auto-resolve.
    /// </summary>
    public class RoadDemolishHandler : MonoBehaviour, IDemolishHandler
    {
        [SerializeField] private BulldozerTool? bulldozerTool;
        [SerializeField] private RoadRenderer? roadRenderer;

        private Camera? _camera;

        private void Start()
        {
            // Auto-resolve if not wired in the Inspector
            if (bulldozerTool == null)
                bulldozerTool = FindObjectOfType<BulldozerTool>();

            if (roadRenderer == null)
                roadRenderer = FindObjectOfType<RoadRenderer>();

            if (bulldozerTool == null)
            {
                Debug.LogError("[RoadDemolishHandler] No BulldozerTool found in scene. " +
                               "Add one or assign it in the Inspector.", this);
                return;
            }

            if (roadRenderer == null)
            {
                Debug.LogError("[RoadDemolishHandler] No RoadRenderer found in scene. " +
                               "Add one or assign it in the Inspector.", this);
                return;
            }

            _camera = Camera.main;
            bulldozerTool.RegisterHandler(this);

            Debug.Log("[RoadDemolishHandler] Registered with BulldozerTool.", this);
        }

        private void Update()
        {
            if (bulldozerTool == null || roadRenderer?.Registry == null)
                return;

            if (!bulldozerTool.IsActive)
            {
                roadRenderer.Registry.ClearHighlight();
                return;
            }

            Mouse ms = Mouse.current;
            if (ms == null || _camera == null)
            {
                roadRenderer.Registry.ClearHighlight();
                return;
            }

            Ray ray = _camera.ScreenPointToRay(ms.position.value);
            if (Physics.Raycast(ray, out RaycastHit hit) &&
                roadRenderer.Registry.TryGetId(hit.collider.gameObject, out int segId))
            {
                roadRenderer.Registry.SetHighlight(segId);
            }
            else
            {
                roadRenderer.Registry.ClearHighlight();
            }
        }

        private void OnDestroy()
        {
            bulldozerTool?.UnregisterHandler(this);
            roadRenderer?.Registry?.ClearHighlight();
        }

        public bool TryDemolish(RaycastHit hit, float gameTime)
        {
            if (roadRenderer?.Registry == null || GameServices.Instance == null)
                return false;

            if (!roadRenderer.Registry.TryGetId(hit.collider.gameObject, out int segmentId))
                return false;

            return GameServices.Instance.Roads.DemolishRoad(segmentId, gameTime);
        }
    }
}
