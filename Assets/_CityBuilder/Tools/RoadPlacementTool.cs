using System;
using CityBuilder.Core;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.InputSystem;

namespace CityBuilder.Tools
{
    public enum RoadBuildMode { Straight, Curved }

    /// <summary>
    /// Handles player input for placing road segments.
    ///
    /// R            – toggle tool on/off
    /// Tab          – cycle between Straight and Curved mode
    /// Left click   – Straight: click1=start, click2=build+chain
    ///                Curved:   click1=start, click2=guide point, click3=build+chain
    /// Right click  – cancel current placement
    ///
    /// Both preview and actual road use the same Bézier handle formula
    /// (ComputeCurveHandles) so what you see is exactly what gets built.
    /// </summary>
    public class RoadPlacementTool : MonoBehaviour
    {
        [SerializeField] private float roadWidth     = 7f;
        [SerializeField] private float roadElevation = 0.05f;
        [SerializeField] private Color previewColor  = new(1f, 0.8f, 0.2f, 1f);

        private const int PreviewSamples = 32;

        private static readonly int _baseColorId = Shader.PropertyToID("_BaseColor");

        public event Action<bool> OnActiveChanged;
        public bool IsActive { get; private set; }

        private RoadBuildMode _mode = RoadBuildMode.Straight;

        // Shared placement state
        private bool    _hasStart;
        private Vector3 _startPoint;

        // Curved mode only
        private bool    _hasControl;
        private Vector3 _controlPoint;

        // Exit tangent of the last built curved segment (for G1 chaining)
        private bool   _hasExitTangent;
        private float3 _exitTangent;

        // Single LineRenderer shared by all preview modes
        private LineRenderer _previewLine;
        private Camera       _camera;

        private void Start()
        {
            _camera = Camera.main;

            Shader flatShader = Shader.Find("CityBuilder/FlatShading");
            Material previewMaterial = new Material(flatShader != null
                ? flatShader
                : Shader.Find("Hidden/InternalErrorShader"));
            previewMaterial.SetColor(_baseColorId, previewColor);

            _previewLine                    = new GameObject("RoadPreview").AddComponent<LineRenderer>();
            _previewLine.material           = previewMaterial;
            _previewLine.widthMultiplier    = roadWidth;
            _previewLine.positionCount      = 0;
            _previewLine.useWorldSpace      = true;
            _previewLine.shadowCastingMode  = UnityEngine.Rendering.ShadowCastingMode.Off;
            _previewLine.receiveShadows     = false;
        }

        private void Update()
        {
            Keyboard kb = Keyboard.current;
            Mouse    ms = Mouse.current;
            if (kb == null || ms == null)
            {
                return;
            }

            if (kb.rKey.wasPressedThisFrame)
            {
                SetActive(!IsActive);
            }

            if (!IsActive)
            {
                return;
            }

            if (kb.tabKey.wasPressedThisFrame)
            {
                _mode = _mode == RoadBuildMode.Straight ? RoadBuildMode.Curved : RoadBuildMode.Straight;
                CancelPlacement();
            }

            Vector3? worldPos = RaycastTerrain(ms.position.value);
            UpdatePreview(worldPos);

            if (ms.leftButton.wasPressedThisFrame && worldPos.HasValue)
            {
                HandleClick(worldPos.Value);
            }

            if (ms.rightButton.wasPressedThisFrame)
            {
                CancelPlacement();
            }
        }

        // ─────────────────────────────────────────────────────────
        //  Click handling
        // ─────────────────────────────────────────────────────────

        private void HandleClick(Vector3 worldPos)
        {
            if (_mode == RoadBuildMode.Straight)
            {
                HandleStraightClick(worldPos);
            }
            else
            {
                HandleCurvedClick(worldPos);
            }
        }

        private void HandleStraightClick(Vector3 worldPos)
        {
            if (!_hasStart)
            {
                _startPoint = worldPos;
                _hasStart   = true;
                return;
            }

            GameServices.Instance!.Roads.BuildRoad(
                new float3(_startPoint.x, _startPoint.y, _startPoint.z),
                new float3(worldPos.x,    worldPos.y,    worldPos.z),
                Time.time);

            _startPoint = worldPos;
        }

        private void HandleCurvedClick(Vector3 worldPos)
        {
            if (!_hasStart)
            {
                _startPoint = worldPos;
                _hasStart   = true;
                return;
            }

            if (!_hasControl)
            {
                _controlPoint = worldPos;
                _hasControl   = true;
                return;
            }

            // Third click: build using the same handle formula as the preview.
            (Vector3 cA, Vector3 cB) = ComputeCurveHandles(_startPoint, worldPos, _controlPoint);
            float3 from     = new (_startPoint.x, _startPoint.y, _startPoint.z);
            float3 to       = new (worldPos.x,    worldPos.y,    worldPos.z);
            float3 controlA = new (cA.x, cA.y, cA.z);
            float3 controlB = new (cB.x, cB.y, cB.z);

            GameServices.Instance!.Roads.BuildRoad(from, to, controlA, controlB, Time.time);

            // B'(1) ∝ (to – controlB) → store as exit tangent for G1 chaining
            _exitTangent    = math.normalizesafe(to - controlB);
            _hasExitTangent = true;

            _startPoint = worldPos;
            _hasControl = false;
        }

        // ─────────────────────────────────────────────────────────
        //  Preview  (same Bézier math as the build path)
        // ─────────────────────────────────────────────────────────

        private void UpdatePreview(Vector3? worldPos)
        {
            if (!_hasStart || !worldPos.HasValue)
            {
                HidePreview();
                return;
            }

            Vector3 cursor = worldPos.Value;

            if (_mode == RoadBuildMode.Straight)
            {
                // Degenerate cubic Bézier with equidistant handles = perfect straight line
                DrawBezierPreview(
                    _startPoint,
                    Vector3.Lerp(_startPoint, cursor, 1f / 3f),
                    Vector3.Lerp(_startPoint, cursor, 2f / 3f),
                    cursor);
                return;
            }

            if (!_hasControl)
            {
                // Phase 1 – guide not yet placed: use cursor as guide approximation.
                // When chaining, ComputeCurveHandles will still apply the exit tangent
                // for cA so the G1-continuous arc is visible from the first click.
                (Vector3 cA1, Vector3 cB1) = ComputeCurveHandles(_startPoint, cursor, cursor);
                DrawBezierPreview(_startPoint, cA1, cB1, cursor);
                return;
            }

            // Phase 2 – guide is set: identical formula to HandleCurvedClick
            (Vector3 cA2, Vector3 cB2) = ComputeCurveHandles(_startPoint, cursor, _controlPoint);
            DrawBezierPreview(_startPoint, cA2, cB2, cursor);
        }

        // ─────────────────────────────────────────────────────────
        //  Shared Bézier logic
        // ─────────────────────────────────────────────────────────

        /// <summary>
        /// Computes the two inner cubic Bézier handles for a curved segment.
        /// cB always comes from the guide via the 2/3-rule.
        /// cA uses the stored exit tangent when chaining (G1 continuity),
        /// otherwise the guide too.
        /// </summary>
        private (Vector3 cA, Vector3 cB) ComputeCurveHandles(Vector3 from, Vector3 to, Vector3 guide)
        {
            Vector3 cB = Vector3.Lerp(to, guide, 2f / 3f);
            Vector3 cA = _hasExitTangent
                ? from + new Vector3(_exitTangent.x, _exitTangent.y, _exitTangent.z)
                       * (Vector3.Distance(from, to) / 3f)
                : Vector3.Lerp(from, guide, 2f / 3f);
            return (cA, cB);
        }

        private void DrawBezierPreview(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3)
        {
            Vector3 elevOffset = Vector3.up * roadElevation;
            _previewLine.positionCount = PreviewSamples + 1;
            for (int i = 0; i <= PreviewSamples; i++)
            {
                float   t  = i / (float)PreviewSamples;
                float   u  = 1f - t;
                Vector3 pt = u*u*u*p0 + 3f*u*u*t*p1 + 3f*u*t*t*p2 + t*t*t*p3;
                _previewLine.SetPosition(i, pt + elevOffset);
            }
        }

        private void HidePreview() => _previewLine.positionCount = 0;

        // ─────────────────────────────────────────────────────────
        //  Helpers
        // ─────────────────────────────────────────────────────────

        private void CancelPlacement()
        {
            _hasStart       = false;
            _hasControl     = false;
            _hasExitTangent = false;
            HidePreview();
        }

        public void SetActive(bool active)
        {
            IsActive = active;
            if (!IsActive)
            {
                CancelPlacement();
            }

            OnActiveChanged?.Invoke(IsActive);
        }

        private Vector3? RaycastTerrain(Vector2 screenPos)
        {
            if (!_camera)
            {
                return null;
            }

            int roadLayer = LayerMask.NameToLayer("Road");
            int layerMask = roadLayer != -1 ? ~(1 << roadLayer) : Physics.DefaultRaycastLayers;

            Ray ray = _camera.ScreenPointToRay(screenPos);
            return Physics.Raycast(ray, out RaycastHit hit, Mathf.Infinity, layerMask) ? hit.point : null;
        }

        private void OnDestroy()
        {
            if (_previewLine != null)
            {
                Destroy(_previewLine.gameObject);
            }
        }
    }
}
