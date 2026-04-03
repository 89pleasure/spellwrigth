using CityBuilder.Core;
using CityBuilder.Tools;
using UnityEngine;
using UnityEngine.InputSystem;

#nullable enable
namespace CityBuilder.Rendering.Roads
{
    public class RoadDemolishHandler : MonoBehaviour, IDemolishHandler
    {
        [SerializeField] private BulldozerTool? bulldozerTool;
        [SerializeField] private RoadRenderer? roadRenderer;

        private Camera? _camera;

        private void Start()
        {
            if (bulldozerTool == null)
                bulldozerTool = FindAnyObjectByType<BulldozerTool>();

            if (bulldozerTool == null)
            {
                Debug.LogError("[RoadDemolishHandler] BulldozerTool not found!", this);
                return;
            }

            if (roadRenderer == null)
                roadRenderer = FindAnyObjectByType<RoadRenderer>();

            if (roadRenderer == null)
            {
                Debug.LogError("[RoadDemolishHandler] RoadRenderer not found!", this);
                return;
            }

            _camera = Camera.main;
            bulldozerTool.RegisterHandler(this);
            Debug.Log("[RoadDemolishHandler] Registered successfully.", this);
        }

        private void Update()
        {
            if (!bulldozerTool || roadRenderer?.Registry == null) { return; }
            if (!bulldozerTool.IsActive) { roadRenderer.Registry.ClearHighlight(); return; }

            Mouse ms = Mouse.current;
            if (ms == null || !_camera) { roadRenderer.Registry.ClearHighlight(); return; }

            Ray ray = _camera.ScreenPointToRay(ms.position.value);
            int roadMask = LayerMask.GetMask("Road");

            // ── DEBUG ─────────────────────────────────────────────────────────
            // Press D to fire a one-shot diagnostic that checks layer setup,
            // unmasked hits, and MeshCollider state for all road objects.
            if (Keyboard.current != null && Keyboard.current.dKey.wasPressedThisFrame)
                LogRaycastDiagnostics(ray, roadMask);
            // ─────────────────────────────────────────────────────────────────

            if (Physics.Raycast(ray, out RaycastHit hit, float.MaxValue, roadMask))
            {
                if (roadRenderer.Registry.TryGetId(hit.collider.gameObject, out int segId))
                {
                    roadRenderer.Registry.SetHighlight(segId);
                }
                else
                {
                    Debug.Log($"[RoadDemolishHandler] Hover hit '{hit.collider.gameObject.name}' – not in Registry.");
                    roadRenderer.Registry.ClearHighlight();
                }
            }
            else
            {
                roadRenderer.Registry.ClearHighlight();
            }
        }

        /// <summary>
        /// One-shot diagnostic: call once while hovering over a road to identify
        /// why Physics.Raycast is not hitting. Press D in Play mode.
        /// </summary>
        private void LogRaycastDiagnostics(Ray ray, int roadMask)
        {
            Debug.Log($"[RoadDebug] roadMask value = {roadMask} " +
                      $"(0 means the 'Road' layer does not exist in Project Settings → Tags & Layers)");

            // Try unmasked raycast to see what's actually under the cursor
            if (Physics.Raycast(ray, out RaycastHit anyHit, float.MaxValue))
            {
                GameObject go = anyHit.collider.gameObject;
                Debug.Log($"[RoadDebug] Unmasked hit: '{go.name}'  layer={go.layer} ({LayerMask.LayerToName(go.layer)})  " +
                          $"collider={anyHit.collider.GetType().Name}  point={anyHit.point}");
            }
            else
            {
                Debug.Log("[RoadDebug] Unmasked raycast hit NOTHING – check camera direction and collider presence.");
            }
        }

        private void OnDestroy()
        {
            bulldozerTool?.UnregisterHandler(this);
            roadRenderer?.Registry?.ClearHighlight();
        }

        public bool TryDemolish(RaycastHit hit, float gameTime)
        {
            if (roadRenderer?.Registry == null)
            {
                Debug.LogError("[RoadDemolishHandler] TryDemolish: Registry is null!");
                return false;
            }

            if (GameServices.Instance == null)
            {
                Debug.LogError("[RoadDemolishHandler] TryDemolish: GameServices.Instance is null!");
                return false;
            }

            if (!roadRenderer.Registry.TryGetId(hit.collider.gameObject, out int segmentId))
            {
                Debug.Log($"[RoadDemolishHandler] TryDemolish: '{hit.collider.gameObject.name}' not in Registry.");
                return false;
            }

            Debug.Log($"[RoadDemolishHandler] Demolishing segment {segmentId}.");
            return GameServices.Instance.Roads.DemolishRoad(segmentId, gameTime);
        }
    }
}
