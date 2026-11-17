using System.Collections.Generic;
using Engine.Scripts.Inventory;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace UI{
    /// <summary>
    ///     The inventory with a gamepad (the Inventory* actions of the "Hud" map): the stick or d-pad moves a cursor
    ///     over the page's slots and down onto the hotbar; Use (A) is a click, QuickMove (Y) a shift-click, Grab (X)
    ///     picks a block up and drops it on another slot (drag and drop), Back (B) throws a held block out (clears its
    ///     slot) or closes the inventory; the shoulders switch the tab, the triggers the category.
    /// </summary>
    public partial class HudController{
        private const float NavigateFirstRepeat = 0.35f, NavigateRepeat = 0.09f;

        private InputAction _invNavigate, _invUse, _invGrab, _invQuickMove, _invBack, _invPreviousTab, _invNextTab,
            _invPreviousCategory, _invNextCategory;

        private VisualElement _padElement;
        private SlotTag _padHeld;
        private ushort _padHeldId;
        private int _padIndex;
        private Vector2Int _navDirection;
        private float _navNext;
        private bool _padOn, _padOnHotbar;

        private void BindInventoryInput(InputActionMap map){
            _invNavigate = map?.FindAction("InventoryNavigate");
            _invUse = map?.FindAction("InventoryUse");
            _invGrab = map?.FindAction("InventoryGrab");
            _invQuickMove = map?.FindAction("InventoryQuickMove");
            _invBack = map?.FindAction("InventoryBack");
            _invPreviousTab = map?.FindAction("InventoryPreviousTab");
            _invNextTab = map?.FindAction("InventoryNextTab");
            _invPreviousCategory = map?.FindAction("InventoryPreviousCategory");
            _invNextCategory = map?.FindAction("InventoryNextCategory");
        }

        private void HandlePad(){
            if (Pressed(_invBack)){
                if (_padHeld != null){
                    ThrowOut(_padHeld);
                    EndHold();
                }
                else{
                    SetInventory(false);
                }

                return;
            }

            if (Pressed(_invNextTab) || Pressed(_invPreviousTab)) SetTab(_tab == TabAll ? TabBackpack : TabAll);
            int category = Pressed(_invNextCategory) ? 1 : Pressed(_invPreviousCategory) ? -1 : 0;
            if (category != 0 && _tab == TabAll){
                int index = System.Array.IndexOf(Chips, _category);
                SetCategory(Chips[(index + category + Chips.Length) % Chips.Length]);
            }

            Vector2 stick = _invNavigate != null ? _invNavigate.ReadValue<Vector2>() : Vector2.zero;
            Vector2Int direction = Vector2Int.zero;
            if (Mathf.Abs(stick.x) >= Mathf.Abs(stick.y)){
                if (Mathf.Abs(stick.x) > 0.5f) direction.x = stick.x > 0f ? 1 : -1;
            }
            else if (Mathf.Abs(stick.y) > 0.5f){
                direction.y = stick.y > 0f ? -1 : 1; // rows count downwards
            }

            if (direction == Vector2Int.zero){
                _navDirection = Vector2Int.zero;
            }
            else if (direction != _navDirection){
                _navDirection = direction;
                _navNext = Time.unscaledTime + NavigateFirstRepeat;
                MovePad(direction);
            }
            else if (Time.unscaledTime >= _navNext){
                _navNext = Time.unscaledTime + NavigateRepeat;
                MovePad(direction);
            }

            if (!_padOn) return;
            SlotTag tag = _padElement?.userData as SlotTag;
            if (tag == null) return;
            if (Pressed(_invUse)) ClickSlot(tag, false);
            if (Pressed(_invQuickMove)) ClickSlot(tag, true);
            if (Pressed(_invGrab)){
                if (_padHeld == null){
                    ushort id = IdAt(tag);
                    if (id == 0) return;
                    _padHeld = tag;
                    _padHeldId = id;
                    _padElement.AddToClassList("is--source");
                    ShowGhost(id);
                    PlaceGhostAtCursor();
                }
                else{
                    SlotTag source = _padHeld;
                    ushort id = _padHeldId;
                    EndHold();
                    if (!SameSlot(source, tag)) DropOnto(source, id, tag);
                }
            }
        }

        /// <summary>The page's visible slots in order: the backpack's, or the blocks the search shows.</summary>
        private List<VisualElement> PadGrid(){
            if (_tab == TabBackpack) return _backpackSlots;
            List<VisualElement> visible = new(_allEntries.Count);
            foreach (AllEntry entry in _allEntries)
                if (entry.Visible)
                    visible.Add(entry.Slot);
            return visible;
        }

        /// <summary>
        ///     How many slots a row of <paramref name="grid" /> holds, as laid out: the backpack has
        ///     <see cref="BlockInventory.BackpackColumns" />, the All tab wraps at its width (11 at 1920 x 1080).
        /// </summary>
        private int PadColumns(List<VisualElement> grid){
            if (_tab == TabBackpack || grid.Count == 0) return BlockInventory.BackpackColumns;
            float top = grid[0].layout.y;
            if (float.IsNaN(top)) return BlockInventory.BackpackColumns;
            int columns = 1;
            while (columns < grid.Count && Mathf.Abs(grid[columns].layout.y - top) < 1f) columns++;
            return columns;
        }

        private void MovePad(Vector2Int direction){
            List<VisualElement> grid = PadGrid();
            int columns = PadColumns(grid);
            if (!_padOn){
                // the first push shows the cursor where it was, or on the first slot
                _padOn = true;
                _focusSearchFrame = -1;
                _searchField.Blur();
                if (!_padOnHotbar && grid.Count == 0) _padOnHotbar = true;
                SetPadCursor(grid);
                return;
            }

            if (_padOnHotbar){
                if (direction.x != 0) _padIndex = Mathf.Clamp(_padIndex + direction.x, 0, BlockInventory.HotbarSize - 1);
                else if (direction.y < 0 && grid.Count > 0){
                    int lastRow = (grid.Count - 1) / columns;
                    int column = Mathf.RoundToInt(_padIndex * (columns - 1f) / (BlockInventory.HotbarSize - 1f));
                    _padOnHotbar = false;
                    _padIndex = Mathf.Min(lastRow * columns + column, grid.Count - 1);
                }
            }
            else if (grid.Count > 0){
                int x = _padIndex % columns, row = _padIndex / columns, lastRow = (grid.Count - 1) / columns;
                if (direction.x < 0 && x > 0) _padIndex--;
                else if (direction.x > 0 && x < columns - 1 && _padIndex + 1 < grid.Count) _padIndex++;
                else if (direction.y < 0 && row > 0) _padIndex -= columns;
                else if (direction.y > 0){
                    if (_padIndex + columns < grid.Count) _padIndex += columns;
                    else if (row < lastRow) _padIndex = grid.Count - 1;
                    else{
                        _padOnHotbar = true;
                        _padIndex = Mathf.RoundToInt(x * (BlockInventory.HotbarSize - 1f) / Mathf.Max(columns - 1f, 1f));
                    }
                }
            }

            SetPadCursor(grid);
        }

        private void SetPadCursor(List<VisualElement> grid){
            _padElement?.RemoveFromClassList("is--cursor");
            _padIndex = _padOnHotbar ? Mathf.Clamp(_padIndex, 0, BlockInventory.HotbarSize - 1)
                : Mathf.Clamp(_padIndex, 0, Mathf.Max(grid.Count - 1, 0));
            _padElement = _padOnHotbar ? _slots[_padIndex] : grid.Count > 0 ? grid[_padIndex] : null;
            if (_padElement == null) return;
            _padElement.AddToClassList("is--cursor");
            if (!_padOnHotbar && _tab == TabAll) _allGrid.ScrollTo(_padElement);
            if (_padElement.userData is SlotTag tag){
                ushort id = IdAt(tag);
                if (id != 0) ShowAside(id);
            }

            if (_padHeld != null) PlaceGhostAtCursor();
        }

        /// <summary>The page or the filter changed: the cursor stays in range (and a held block stays held).</summary>
        private void PadTabChanged(){
            if (!_padOn) return;
            if (!_padOnHotbar) _padIndex = 0;
            List<VisualElement> grid = PadGrid();
            if (!_padOnHotbar && grid.Count == 0) _padOnHotbar = true;
            SetPadCursor(grid);
        }

        /// <summary>The held block's icon sits on the cursor slot's top right corner.</summary>
        private void PlaceGhostAtCursor(){
            if (_padElement == null) return;
            Rect bound = _padElement.worldBound;
            MoveGhost(new Vector2(bound.xMax - 8f, bound.yMin + 8f));
        }

        private void EndHold(){
            if (_padHeld == null) return;
            _padHeld = null;
            _padHeldId = 0;
            foreach (VisualElement slot in _backpackSlots) slot.RemoveFromClassList("is--source");
            foreach (VisualElement slot in _slots) slot.RemoveFromClassList("is--source");
            foreach (AllEntry entry in _allEntries) entry.Slot.RemoveFromClassList("is--source");
            HideGhost();
        }

        /// <summary>The cursor is hidden (the mouse took over, or the inventory closed); a held block is put back.</summary>
        private void EndPad(){
            EndHold();
            _padElement?.RemoveFromClassList("is--cursor");
            _padElement = null;
            _padOn = false;
            _navDirection = Vector2Int.zero;
        }

        private void OnPanelPointerMove(PointerMoveEvent evt){
            if (_padOn && evt.pointerType == UnityEngine.UIElements.PointerType.mouse) EndPad();
        }

        private static bool SameSlot(SlotTag a, SlotTag b){
            return a.Area == b.Area && a.Index == b.Index;
        }
    }
}
