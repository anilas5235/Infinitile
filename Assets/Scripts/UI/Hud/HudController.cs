using System.Collections.Generic;
using Engine.Scripts.Render;
using Player;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;
using Cursor = UnityEngine.Cursor;

namespace UI{
    /// <summary>
    ///     The in-game HUD (UI Toolkit, Hud.uxml): crosshair, key hints, a hotbar of voxels to place, the debug panels
    ///     (player, world, the block looked at; frame, streaming, GPU memory, culling) and the Esc menu. All keys come
    ///     from the "Hud" map of the project's input actions (see <see cref="HudInput" />): TogglePanels (F3) shows and
    ///     hides the panels, Inventory (E) opens a list of every voxel that puts the clicked voxel into the hotbar, Menu
    ///     (Esc) closes that list or opens and closes the menu. The cursor is free only while the list or the menu is
    ///     open.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public partial class HudController : MonoBehaviour{
        [SerializeField] private Texture2D crosshair;

        [Tooltip("Voxels of hotbar slots 1–9 by full name (package:name). Middle click replaces the selected slot.")]
        [SerializeField] private string[] hotbarVoxels = {
            "std:Grass", "std:Dirt", "std:Stone", "std:Sand", "std:LogOak", "std:Planks", "std:Leaves", "std:Glass",
            "std:Water"
        };

        [SerializeField] private bool showDebugPanels = true;

        [Tooltip("Font of the menus, the hotbar numbers and the voxel name; the default font when empty.")]
        [SerializeField] private Font displayFont;

        [Tooltip("Seconds between text refreshes of the debug panels.")]
        [SerializeField] private float refreshInterval = 0.1f;

        [SerializeField] private VoxelEditor voxelEditor;

        private readonly List<HintChip> _hints = new();

        private InputAction _confirm, _hotbarNext, _hotbarPrevious, _hotbarScroll, _hotbarSlot,
            _inventory, _menuAction, _togglePanels;

        private bool _cursorFree;
        private VisualElement _crosshair, _debugLeft, _debugRight, _hintRow, _hotbarArea;
        private LookController _look;
        private PlayerControllerManager _movement;
        private PlayerInput _playerInput;

        /// <summary>The HUD's root element (the "hud" element of Hud.uxml).</summary>
        public VisualElement Root{ get; private set; }

        private void OnEnable(){
            Root = GetComponent<UIDocument>().rootVisualElement.Q("hud");
            if (Root == null) return;
            if (!voxelEditor) voxelEditor = FindFirstObjectByType<VoxelEditor>();
            _look = FindFirstObjectByType<LookController>();
            _movement = FindFirstObjectByType<PlayerControllerManager>();
            BindInput();

            _crosshair = Root.Q("crosshair");
            _crosshair.style.backgroundImage = crosshair;
            _debugLeft = Root.Q("debug-left");
            _debugRight = Root.Q("debug-right");
            _hintRow = Root.Q("hints");
            _hotbarArea = Root.Q("hotbar-area");
            ApplyDisplayFont();

            // Unity's own graphics allocations (textures, meshes, buffers, render targets); in the Editor this
            // includes the Editor's windows. Only the Editor and development players have this counter.
            _gfxMemory = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "Gfx Used Memory");
            BindPanels();
            BuildHints();
            BuildHotbar();
            BindVoxelSearch();
            BindMenu();
            GameSettings.ApplyFrameCapAtStart();
            if (voxelEditor) voxelEditor.OnVoxelIdChanged += OnVoxelIdChanged;
            SetDebugPanels(showDebugPanels);
        }

        private void OnDisable(){
            if (voxelEditor) voxelEditor.OnVoxelIdChanged -= OnVoxelIdChanged;
            if (_searchOpen) SetVoxelSearch(false);
            UnbindMenu();
            _searchField?.UnregisterValueChangedCallback(OnSearchChanged);
            _searchField?.UnregisterCallback<FocusOutEvent>(OnSearchFocusOut);
            if (_cursorFree) SetCursorFree(false);
            _gfxMemory.Dispose();
            if (VoxelWorldRenderer.Instance) VoxelWorldRenderer.Instance.CollectCullingStats = false;
        }

        private void Update(){
            if (Root == null) return;
            _frameTimeSum += Time.unscaledDeltaTime;
            _frames++;

            HandleInput();
            if (_searchOpen && _focusSearchFrame >= 0 && Time.frameCount >= _focusSearchFrame){
                _focusSearchFrame = -1;
                _searchField.Focus();
            }

            if (!HotbarResolved()) ResolveHotbar(); // the registry fills in the DataImporter's Awake

            if (Time.unscaledTime < _nextRefresh) return;
            _nextRefresh = Time.unscaledTime + refreshInterval;
            if (PanelsVisible){
                RefreshPlayerAndWorld();
                RefreshLookedAt();
                RefreshPerformance();
            }

            if (_menuOpen) RefreshStoreEstimate();
            _frameTimeSum = 0f;
            _frames = 0;
        }

        private void BindInput(){
            InputActionMap map = HudInput.Map();
            _togglePanels = map?.FindAction("TogglePanels");
            _inventory = map?.FindAction("Inventory");
            _menuAction = map?.FindAction("Menu");
            _confirm = map?.FindAction("Confirm");
            _hotbarSlot = map?.FindAction("HotbarSlot");
            _hotbarScroll = map?.FindAction("HotbarScroll");
            _hotbarPrevious = map?.FindAction("HotbarPrevious");
            _hotbarNext = map?.FindAction("HotbarNext");
        }

        private static bool Pressed(InputAction action){
            return action != null && action.WasPressedThisFrame();
        }

        private void HandleInput(){
            if (Pressed(_togglePanels)) SetDebugPanels(!showDebugPanels);
            if (_searchOpen){
                // Letters and digits type into the search field, so only the menu key closes the list.
                if (Pressed(_menuAction)) SetVoxelSearch(false);
                else if (Pressed(_confirm)) PickFirstMatch();
            }
            else if (Pressed(_menuAction)){
                MenuBack();
            }
            else if (!_menuOpen && Pressed(_inventory)){
                SetVoxelSearch(true);
            }

            bool cursorFree = _searchOpen || _menuOpen;
            if (cursorFree != _cursorFree) SetCursorFree(cursorFree);

            if (_slotIds.Length == 0 || _cursorFree) return;
            if (Pressed(_hotbarSlot)){
                int index = _hotbarSlot.GetBindingIndexForControl(_hotbarSlot.activeControl);
                if (index >= 0 && index < _slotIds.Length) SelectSlot(index);
            }

            int step = Pressed(_hotbarNext) ? 1 : Pressed(_hotbarPrevious) ? -1 : 0;
            float scroll = _hotbarScroll != null ? _hotbarScroll.ReadValue<float>() : 0f;
            if (scroll != 0f) step = scroll < 0f ? 1 : -1;
            if (step != 0) SelectSlot((_selected + step + _slotIds.Length) % _slotIds.Length);
        }

        /// <summary>The F3 panels are switched on and not hidden behind the menu.</summary>
        private bool PanelsVisible => showDebugPanels && !_menuOpen;

        private void SetDebugPanels(bool show){
            showDebugPanels = show;
            UpdateLayers();
            _nextRefresh = 0f;
        }

        /// <summary>
        ///     Shows what goes with the current state: the menu hides the panels, hints, hotbar and crosshair; the voxel
        ///     list hides the crosshair.
        /// </summary>
        private void UpdateLayers(){
            bool play = !_menuOpen;
            Show(_debugLeft, PanelsVisible);
            Show(_debugRight, PanelsVisible);
            Show(_hintRow, play);
            Show(_hotbarArea, play);
            Show(_crosshair, play && !_searchOpen);
            if (VoxelWorldRenderer.Instance) VoxelWorldRenderer.Instance.CollectCullingStats = PanelsVisible;
            RefreshHints();
        }

        private static void Show(VisualElement element, bool show){
            if (element != null) element.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
        }

        /// <summary>
        ///     Unlocks the cursor and stops looking and editing while the cursor key is held, the voxel list or the
        ///     menu is open.
        /// </summary>
        private void SetCursorFree(bool free){
            _cursorFree = free;
            Cursor.lockState = free ? CursorLockMode.None : CursorLockMode.Locked;
            Cursor.visible = free;
            if (_look) _look.enabled = !free;
            if (!voxelEditor) return;
            voxelEditor.enabled = !free;
            voxelEditor.destruct = voxelEditor.place = false;
        }

        /// <summary>Turns the player's input off while the voxel list or the menu is open, and on again.</summary>
        private void SetPlayerInput(bool active){
            // Looked up on every switch-off: the player may spawn after the HUD, or be replaced.
            if (!active) _playerInput = FindFirstObjectByType<PlayerInput>();
            if (!_playerInput) return;
            if (!active) _playerInput.DeactivateInput();
            // In teardown the PlayerInput may already be disabled; it activates itself when enabled again.
            else if (_playerInput.isActiveAndEnabled) _playerInput.ActivateInput();
        }

        /// <summary>
        ///     The display font on the menu and the hotbar; their children inherit it. Its glyphs cover Latin text,
        ///     digits and common punctuation, but no arrows.
        /// </summary>
        private void ApplyDisplayFont(){
            if (!displayFont) return;
            StyleFontDefinition font = new(FontDefinition.FromFont(displayFont));
            Root.Query(className: "lil").ForEach(element => element.style.unityFontDefinition = font);
            Root.Q("hotbar").style.unityFontDefinition = font;
        }

        // ── Key hints ────────────────────────────────────────────────────────

        private void BuildHints(){
            _hintRow.Clear();
            _hints.Clear();
            AddHint(_togglePanels, "panels");
            AddHint(_inventory, "voxels");
            AddHint(_menuAction, "menu");
        }

        private void AddHint(InputAction action, string text){
            string key = HudInput.KeyOf(action);
            if (key.Length == 0) return;
            VisualElement chip = new(){ pickingMode = PickingMode.Ignore };
            chip.AddToClassList("hint");
            Label keyLabel = new(key){ pickingMode = PickingMode.Ignore };
            keyLabel.AddToClassList("hint__key");
            Label textLabel = new(text){ pickingMode = PickingMode.Ignore };
            textLabel.AddToClassList("hint__text");
            chip.Add(keyLabel);
            chip.Add(textLabel);
            _hintRow.Add(chip);
            _hints.Add(new HintChip{ Action = action, Text = textLabel, Base = text });
        }

        /// <summary>The panels hint says what F3 does next: show or hide them.</summary>
        private void RefreshHints(){
            foreach (HintChip hint in _hints)
                if (hint.Action == _togglePanels)
                    hint.Text.text = showDebugPanels ? "hide panels" : hint.Base;
        }

        private class HintChip{
            public InputAction Action;
            public string Base;
            public Label Text;
        }
    }
}
