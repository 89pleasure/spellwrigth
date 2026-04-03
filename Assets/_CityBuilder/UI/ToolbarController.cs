using CityBuilder.Tools;
using UnityEngine;
using UnityEngine.UIElements;

namespace CityBuilder.UI
{
    /// <summary>
    /// Drives the bottom toolbar UI.
    /// Requires a UIDocument component on the same GameObject with Toolbar.uxml assigned.
    /// Only one tool can be active at a time; activating one deactivates the other.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public class ToolbarController : MonoBehaviour
    {
        [SerializeField] private RoadPlacementTool roadTool;
        [SerializeField] private BulldozerTool     bulldozerTool;
        [SerializeField] private RoadNodeMoveTool  nodeMoveTool;
        [SerializeField] private StyleSheet        toolbarStyles;

        private Button _btnRoad;
        private Button _btnBulldozer;

        private void Start()
        {
            if (roadTool == null)
            {
                Debug.LogError("[ToolbarController] roadTool is not assigned in the Inspector.", this);
                return;
            }

            if (bulldozerTool == null)
            {
                Debug.LogError("[ToolbarController] bulldozerTool is not assigned in the Inspector.", this);
                return;
            }

            UIDocument document = GetComponent<UIDocument>();
            VisualElement root = document.rootVisualElement;

            if (toolbarStyles != null)
            {
                root.styleSheets.Add(toolbarStyles);
            }

            _btnRoad = root.Q<Button>("btn-road");
            _btnBulldozer = root.Q<Button>("btn-bulldozer");

            if (_btnRoad == null || _btnBulldozer == null)
            {
                Debug.LogError("[ToolbarController] One or more buttons not found in UXML.", this);
                return;
            }

            _btnRoad.clicked += OnRoadButtonClicked;
            _btnBulldozer.clicked += OnBulldozerButtonClicked;

            roadTool.OnActiveChanged      += OnRoadToolActiveChanged;
            bulldozerTool.OnActiveChanged += OnBulldozerToolActiveChanged;

            if (nodeMoveTool != null)
                nodeMoveTool.OnActiveChanged += OnNodeMoveToolActiveChanged;

            UpdateButtonState(_btnRoad, roadTool.IsActive);
            UpdateButtonState(_btnBulldozer, bulldozerTool.IsActive);
        }

        private void OnRoadButtonClicked() =>
            roadTool.SetActive(!roadTool.IsActive);

        private void OnBulldozerButtonClicked() =>
            bulldozerTool.SetActive(!bulldozerTool.IsActive);

        private void OnRoadToolActiveChanged(bool isActive)
        {
            UpdateButtonState(_btnRoad, isActive);
            if (isActive)
            {
                bulldozerTool.SetActive(false);
                nodeMoveTool?.SetActive(false);
            }
        }

        private void OnBulldozerToolActiveChanged(bool isActive)
        {
            UpdateButtonState(_btnBulldozer, isActive);
            if (isActive)
            {
                roadTool.SetActive(false);
                nodeMoveTool?.SetActive(false);
            }
        }

        private void OnNodeMoveToolActiveChanged(bool isActive)
        {
            if (isActive)
            {
                roadTool.SetActive(false);
                bulldozerTool.SetActive(false);
            }
        }

        private static void UpdateButtonState(Button button, bool isActive)
        {
            if (isActive)
            {
                button.AddToClassList("tool-button--active");
            }
            else
            {
                button.RemoveFromClassList("tool-button--active");
            }
        }

        private void OnDestroy()
        {
            if (roadTool != null)
                roadTool.OnActiveChanged -= OnRoadToolActiveChanged;

            if (bulldozerTool != null)
                bulldozerTool.OnActiveChanged -= OnBulldozerToolActiveChanged;

            if (nodeMoveTool != null)
                nodeMoveTool.OnActiveChanged -= OnNodeMoveToolActiveChanged;

            if (_btnRoad != null)
            {
                _btnRoad.clicked -= OnRoadButtonClicked;
            }

            if (_btnBulldozer != null)
            {
                _btnBulldozer.clicked -= OnBulldozerButtonClicked;
            }
        }
    }
}
