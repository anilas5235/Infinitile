using System;
using System.Collections.Generic;
using Engine.Scripts.Inventory;
using Engine.Scripts.VoxelConfig.Data.Voxel;
using Engine.Scripts.VoxelConfig.Registry;
using Unity.Collections;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace UI{
    /// <summary>
    ///     The inventory (Inventory key, E): folder tabs over a page — the backpack (72 slots, sort by kind, fill the
    ///     hotbar) and all blocks (search, category chips) — and a side panel with the block under the pointer. A
    ///     click puts a block on the selected hotbar slot and the inventory stays open; blocks drag between the
    ///     slots and the hotbar below, a drag out of the panel clears the slot, shift-click moves between backpack
    ///     and hotbar, a hovered block goes to hotbar slot 1–9 with that key, Confirm (Enter) takes the first search
    ///     match. Menu (Esc) or the close button closes it; the Inventory key does not. The game does not pause.
    /// </summary>
    public partial class HudController{
        private const int TabBackpack = 0, TabAll = 1;
        private const int AllCategories = -1;
        private const float DragThreshold = 6f;

        private static readonly string[] TabNames = { "Backpack", "All blocks", "Saved hotbars", "Found nearby" };
        private static readonly string[] TabIcons = { "std:Planks", "std:Grass", "std:BrickRed", "std:Leaves" };

        /// <summary>The category chips in order; <see cref="AllCategories" /> first.</summary>
        private static readonly int[] Chips = {
            AllCategories, (int)VoxelCategory.Terrain, (int)VoxelCategory.Stone, (int)VoxelCategory.Wood,
            (int)VoxelCategory.Plants, (int)VoxelCategory.Liquids, (int)VoxelCategory.Light,
            (int)VoxelCategory.Building
        };

        private readonly List<AllEntry> _allEntries = new();
        private readonly List<VisualElement> _backpackSlots = new();
        private readonly List<Button> _chipButtons = new();
        private readonly List<Tab> _tabs = new();

        private int _allBaseCount, _category = AllCategories, _focusSearchFrame = -1, _tab = TabBackpack;
        private ScrollView _allGrid;
        private ushort _asideId;
        private BlockIcon _asideIcon, _ghost;
        private Label _asideName, _asideMeta, _asideBackpack, _asideHotbar, _asideLight, _searchCount;
        private VisualElement _asideChips, _backpackPage, _allPage, _inv, _invPanel;
        private Button _close, _put, _sort, _fill;
        private SlotTag _hovered;
        private bool _invOpen;
        private Press _press;
        private TextField _searchField;

        /// <summary>Where a slot belongs: the hotbar, the backpack or the list of all blocks.</summary>
        private enum SlotArea : byte{
            Hotbar,
            Backpack,
            All
        }

        // ── Build ────────────────────────────────────────────────────────────

        private void BindInventory(){
            _inv = Root.Q("inventory");
            _invPanel = Root.Q("inv-panel");
            _backpackPage = Root.Q("inv-backpack");
            _allPage = Root.Q("inv-all");
            _allGrid = Root.Q<ScrollView>("inv-all-grid");
            _searchField = Root.Q<TextField>("inv-search-field");
            _searchCount = Root.Q<Label>("inv-search-count");
            _asideName = Root.Q<Label>("inv-aside-name");
            _asideMeta = Root.Q<Label>("inv-aside-meta");
            _asideChips = Root.Q("inv-aside-chips");
            _asideBackpack = Root.Q<Label>("inv-aside-backpack");
            _asideHotbar = Root.Q<Label>("inv-aside-hotbar");
            _asideLight = Root.Q<Label>("inv-aside-light");
            _close = Root.Q<Button>("inv-close");
            _put = Root.Q<Button>("inv-put");
            _sort = Root.Q<Button>("inv-sort");
            _fill = Root.Q<Button>("inv-fill");
            foreach (Button button in new[]{ _close, _put, _sort, _fill }) PlainButton(button);

            AddGlyph("inv-close-icon", GlyphKind.Close, Paper);
            AddGlyph("inv-search-icon", GlyphKind.Search, Accent);
            _asideIcon = new BlockIcon{ style ={ flexGrow = 1 } };
            VisualElement asideWell = Root.Q("inv-aside-icon");
            asideWell.Clear();
            asideWell.Add(_asideIcon);
            _ghost = new BlockIcon{ style ={ flexGrow = 1 } };
            VisualElement ghost = Root.Q("inv-ghost");
            ghost.Clear();
            ghost.Add(_ghost);

            _close.clicked += CloseInventory;
            _put.clicked += PutAsideOnHotbar;
            _sort.clicked += SortBackpack;
            _fill.clicked += FillHotbar;
            _searchField.textEdition.placeholder = "Search blocks";
            _searchField.RegisterValueChangedCallback(OnSearchChanged);
            _searchField.RegisterCallback<FocusOutEvent>(OnSearchFocusOut);
            _searchField.RegisterCallback<KeyDownEvent>(OnSearchKeyDown, TrickleDown.TrickleDown);
            _invPanel.RegisterCallback<PointerMoveEvent>(OnPanelPointerMove);

            BuildTabs();
            BuildChips();
            BuildBackpack();
            _allEntries.Clear();
            _allGrid.Clear();
            _allBaseCount = 0;
            _inv.style.display = DisplayStyle.None;
        }

        private void UnbindInventory(){
            if (_inv == null) return;
            if (_invOpen) SetInventory(false);
            _close.clicked -= CloseInventory;
            _put.clicked -= PutAsideOnHotbar;
            _sort.clicked -= SortBackpack;
            _fill.clicked -= FillHotbar;
            _searchField.UnregisterValueChangedCallback(OnSearchChanged);
            _searchField.UnregisterCallback<FocusOutEvent>(OnSearchFocusOut);
            _searchField.UnregisterCallback<KeyDownEvent>(OnSearchKeyDown, TrickleDown.TrickleDown);
            _invPanel.UnregisterCallback<PointerMoveEvent>(OnPanelPointerMove);
        }

        private void BuildTabs(){
            VisualElement row = Root.Q("inv-tabs");
            row.Clear();
            _tabs.Clear();
            for (int i = 0; i < TabNames.Length; i++){
                int index = i;
                bool later = i > TabAll;
                Button button = PlainButton(new Button());
                button.AddToClassList("tab");
                button.EnableInClassList("tab--later", later);
                BlockIcon icon = new();
                icon.AddToClassList("tab__icon");
                Label text = new(TabNames[i]){ pickingMode = PickingMode.Ignore };
                text.AddToClassList("tab__text");
                UseDisplayFont(text);
                button.Add(icon);
                button.Add(text);
                Label chip = null;
                if (later){
                    Label badge = new("later"){ pickingMode = PickingMode.Ignore };
                    badge.AddToClassList("badge");
                    UseDisplayFont(badge);
                    button.Add(badge);
                }
                else{
                    chip = new Label{ pickingMode = PickingMode.Ignore };
                    chip.AddToClassList("tab__chip");
                    button.Add(chip);
                    button.clicked += () => SetTab(index);
                }

                row.Add(button);
                _tabs.Add(new Tab{ Button = button, Icon = icon, Chip = chip });
            }
        }

        private void BuildChips(){
            VisualElement row = Root.Q("inv-chips");
            row.Clear();
            _chipButtons.Clear();
            foreach (int category in Chips){
                int value = category;
                Button chip = new(() => SetCategory(value)){
                    text = category == AllCategories ? "All" : ((VoxelCategory)category).ToString(), focusable = false
                };
                PlainButton(chip);
                chip.AddToClassList("cat");
                UseDisplayFont(chip);
                row.Add(chip);
                _chipButtons.Add(chip);
            }
        }

        private void BuildBackpack(){
            VisualElement grid = Root.Q("inv-backpack-grid");
            grid.Clear();
            _backpackSlots.Clear();
            for (int i = 0; i < BlockInventory.BackpackSize; i++){
                VisualElement slot = CreateSlot("");
                slot.AddToClassList("is");
                RegisterSlot(slot, new SlotTag(SlotArea.Backpack, i));
                grid.Add(slot);
                _backpackSlots.Add(slot);
            }
        }

        /// <summary>
        ///     One slot per base voxel in id order; turned variants are placed from their base. Rebuilt when the
        ///     registry's voxel count changes (it fills in the DataImporter's Awake).
        /// </summary>
        private void BuildAllBlocks(){
            VoxelRegistry registry = Registry();
            int baseCount = registry?.BaseCount ?? 0;
            if (baseCount == _allBaseCount) return;
            _allBaseCount = baseCount;
            _allEntries.Clear();
            _allGrid.Clear();
            for (ushort id = 1; id < baseCount; id++){
                if (!registry.TryGet(id, out Voxel voxel)) continue;
                string fullName = registry.TryGetName(id, out FixedString32Bytes name) ? name.ToString() : voxel.name;
                VisualElement slot = CreateSlot("");
                slot.AddToClassList("is");
                SetIcon(slot.Q<BlockIcon>("icon"), voxel);
                RegisterSlot(slot, new SlotTag(SlotArea.All, id));
                _allGrid.Add(slot);
                _allEntries.Add(new AllEntry{
                    Id = id, IdText = $"{id}", Name = fullName.ToLowerInvariant(), Category = voxel.category, Slot = slot
                });
            }

            for (int i = 0; i < _tabs.Count; i++)
                if (i < TabIcons.Length)
                    SetIcon(_tabs[i].Icon, InventoryStore.IdOf(registry, TabIcons[i]));
        }

        private void UseDisplayFont(VisualElement element){
            if (displayFont) element.style.unityFontDefinition = new StyleFontDefinition(FontDefinition.FromFont(displayFont));
        }

        // ── Open, close, tabs ────────────────────────────────────────────────

        /// <summary>Opens or closes the inventory; while it is open the cursor is free and the player's input is off.</summary>
        private void SetInventory(bool open){
            if (_inv == null || open == _invOpen) return;
            _invOpen = open;
            _inv.style.display = open ? DisplayStyle.Flex : DisplayStyle.None;
            _hotbar.EnableInClassList("hotbar--open", open);
            foreach (VisualElement slot in _slots) slot.pickingMode = open ? PickingMode.Position : PickingMode.Ignore;
            SetPlayerInput(!open);

            if (!open){
                EndPress();
                EndPad();
                _hovered = null;
                _focusSearchFrame = -1;
                _searchField.Blur();
                SaveBlocks();
                UpdateLayers();
                return;
            }

            LoadBlocks();
            BuildAllBlocks();
            _searchField.SetValueWithoutNotify("");
            _category = AllCategories;
            FilterAll();
            ShowAside(_blocks.Get(BlockInventory.Area.Hotbar, _selected).Id);
            SetTab(_tab);
            UpdateLayers();
        }

        private void CloseInventory(){
            SetInventory(false);
        }

        private void SetTab(int tab){
            _tab = Mathf.Clamp(tab, TabBackpack, TabAll);
            for (int i = 0; i < _tabs.Count; i++) _tabs[i].Button.EnableInClassList("tab--on", i == _tab);
            Show(_backpackPage, _tab == TabBackpack);
            Show(_allPage, _tab == TabAll);
            PadTabChanged();
            if (_tab == TabAll && !_padOn){
                // a frame later, so the key that opened the inventory is not typed into the field
                _focusSearchFrame = Time.frameCount + 1;
            }
            else{
                _focusSearchFrame = -1;
                _searchField.Blur();
            }

            RefreshInventory();
        }

        private void SetCategory(int category){
            _category = category;
            FilterAll();
            _allGrid.scrollOffset = Vector2.zero;
            PadTabChanged();
        }

        /// <summary>The slots, tab chips, side panel and the marks of the selected hotbar block.</summary>
        private void RefreshInventory(){
            if (!_invOpen) return;
            ushort current = _blocks.Get(BlockInventory.Area.Hotbar, _selected).Id;
            for (int i = 0; i < _backpackSlots.Count; i++){
                ushort id = _blocks.Get(BlockInventory.Area.Backpack, i).Id;
                SetIcon(_backpackSlots[i].Q<BlockIcon>("icon"), id);
                _backpackSlots[i].EnableInClassList("is--current", id != 0 && id == current);
            }

            foreach (AllEntry entry in _allEntries) entry.Slot.EnableInClassList("is--current", entry.Id == current);
            if (_tabs.Count > TabAll){
                _tabs[TabBackpack].Chip.text =
                    $"{_blocks.CountFilled(BlockInventory.Area.Backpack)} / {BlockInventory.BackpackSize}";
                _tabs[TabAll].Chip.text = $"{_allEntries.Count}";
            }

            ShowAside(_asideId);
        }

        // ── All blocks: search and chips ─────────────────────────────────────

        private void OnSearchChanged(ChangeEvent<string> _){
            FilterAll();
            _allGrid.scrollOffset = Vector2.zero;
            PadTabChanged();
        }

        /// <summary>
        ///     A click on the scrollbar or a slot takes the focus from the field; it comes back a frame later, so
        ///     typing goes on filtering.
        /// </summary>
        private void OnSearchFocusOut(FocusOutEvent _){
            if (_invOpen && _tab == TabAll && !_padOn && _focusSearchFrame < 0) _focusSearchFrame = Time.frameCount + 1;
        }

        /// <summary>While a block is hovered, 1–9 put it on the hotbar (HandleInventoryInput) instead of typing.</summary>
        private void OnSearchKeyDown(KeyDownEvent evt){
            if (_hovered == null || IdAt(_hovered) == 0) return;
            bool digit = evt.keyCode is >= KeyCode.Alpha1 and <= KeyCode.Alpha9 || evt.character is >= '1' and <= '9';
            if (!digit) return;
            evt.StopImmediatePropagation();
        }

        /// <summary>Shows the blocks whose full name contains the query or whose id starts with it, in the chosen category.</summary>
        private void FilterAll(){
            string query = _searchField.value.Trim().ToLowerInvariant();
            int shown = 0;
            foreach (AllEntry entry in _allEntries){
                bool category = _category == AllCategories || (int)entry.Category == _category;
                entry.Visible = category && (query.Length == 0 || entry.Name.Contains(query) ||
                                             entry.IdText.StartsWith(query, StringComparison.Ordinal));
                entry.Slot.style.display = entry.Visible ? DisplayStyle.Flex : DisplayStyle.None;
                if (entry.Visible) shown++;
            }

            _searchCount.text = shown == 1 ? "1 block" : $"{shown} blocks";
            for (int i = 0; i < _chipButtons.Count; i++) _chipButtons[i].EnableInClassList("cat--on", Chips[i] == _category);
        }

        /// <summary>Confirm (Enter): the first block the search shows goes on the selected hotbar slot.</summary>
        private void PutFirstMatch(){
            foreach (AllEntry entry in _allEntries)
                if (entry.Visible){
                    PutOnHotbar(entry.Id);
                    ShowAside(entry.Id);
                    return;
                }
        }

        // ── Side panel ───────────────────────────────────────────────────────

        /// <summary>The block under the pointer (or the gamepad cursor); it stays until another block is shown.</summary>
        private void ShowAside(ushort id){
            _asideId = id;
            VoxelRegistry registry = Registry();
            Voxel voxel = null;
            bool known = id != 0 && registry != null && registry.TryGet(id, out voxel);
            SetIcon(_asideIcon, known ? voxel : null);
            _put.SetEnabled(known);
            _asideChips.Clear();
            if (!known){
                _asideName.text = _asideMeta.text = "";
                _asideBackpack.text = _asideHotbar.text = _asideLight.text = "–";
                return;
            }

            _asideName.text = voxel.name;
            string turns = voxel.facing switch{
                VoxelFacing.Axis => "turns on 3 axes",
                VoxelFacing.Horizontal => "turns 4 ways",
                VoxelFacing.Full => "turns 6 ways",
                _ => ""
            };
            if (voxel.topBottom) turns = turns.Length == 0 ? "top or bottom" : $"{turns}, top or bottom";
            _asideMeta.text = turns.Length == 0 ? $"id {id}" : $"id {id} · {turns}";
            if (voxel.category != VoxelCategory.None) AddAsideChip(voxel.category.ToString());
            AddAsideChip(voxel.meshLayer.ToString());
            if (voxel.collision) AddAsideChip("Collision");
            if (voxel.lightEmission != Vector3Int.zero) AddAsideChip("Glows");

            int inBackpack = _blocks.CountOf(BlockInventory.Area.Backpack, id);
            _asideBackpack.text = inBackpack == 0 ? "–" : inBackpack == 1 ? "1 slot" : $"{inBackpack} slots";
            int onHotbar = _blocks.IndexOf(BlockInventory.Area.Hotbar, id);
            _asideHotbar.text = onHotbar < 0 ? "–" : $"slot {onHotbar + 1}";
            _asideLight.text = voxel.lightEmission != Vector3Int.zero ? "gives light"
                : voxel.lightAttenuation >= VoxelLightLimits.MaxLevel ? "blocks all"
                : voxel.lightAttenuation <= 0 ? "lets all through"
                : $"dims by {voxel.lightAttenuation}";
        }

        private void AddAsideChip(string text){
            Label chip = new(text){ pickingMode = PickingMode.Ignore };
            chip.AddToClassList("tag");
            _asideChips.Add(chip);
        }

        // ── Actions ──────────────────────────────────────────────────────────

        private void PutOnHotbar(ushort id){
            if (id != 0) _blocks.Set(BlockInventory.Area.Hotbar, _selected, id);
        }

        private void PutAsideOnHotbar(){
            PutOnHotbar(_asideId);
        }

        private void SortBackpack(){
            VoxelRegistry registry = Registry();
            _blocks.SortBackpack(id => {
                if (registry == null || !registry.TryGet(id, out Voxel voxel) || voxel.category == VoxelCategory.None)
                    return int.MaxValue;
                return (int)voxel.category;
            });
        }

        private void FillHotbar(){
            _blocks.FillHotbar();
        }

        /// <summary>A click (no drag) on a slot: the hotbar selects; elsewhere the block goes on the selected hotbar slot.</summary>
        private void ClickSlot(SlotTag tag, bool shift){
            ushort id = IdAt(tag);
            if (shift){
                QuickMove(tag);
                return;
            }

            if (tag.Area == SlotArea.Hotbar) SelectSlot(tag.Index);
            else PutOnHotbar(id);
            if (id != 0) ShowAside(id);
        }

        /// <summary>Shift-click: backpack ↔ hotbar; a block of the full list goes to the first free backpack slot.</summary>
        private void QuickMove(SlotTag tag){
            ushort id = IdAt(tag);
            if (id == 0) return;
            if (tag.Area == SlotArea.All){
                int free = _blocks.FirstEmpty(BlockInventory.Area.Backpack);
                if (free >= 0) _blocks.Set(BlockInventory.Area.Backpack, free, id);
            }
            else{
                _blocks.QuickMove(AreaOf(tag), tag.Index);
            }
        }

        /// <summary>Drag and drop: slots swap; a block of the full list is copied onto the target.</summary>
        private void DropOnto(SlotTag source, ushort id, SlotTag target){
            if (target == null || target.Area == SlotArea.All || id == 0) return;
            if (source.Area == SlotArea.All) _blocks.Set(AreaOf(target), target.Index, id);
            else _blocks.Move(AreaOf(source), source.Index, AreaOf(target), target.Index);
        }

        /// <summary>A block dragged out of the panel: its slot is cleared (a block of the full list just drops).</summary>
        private void ThrowOut(SlotTag source){
            if (source.Area != SlotArea.All) _blocks.Clear(AreaOf(source), source.Index);
        }

        /// <summary>Hover + 1–9: the hovered block goes on that hotbar slot (a hotbar block swaps).</summary>
        private void HotbarKeyInInventory(int index){
            if (index < 0 || index >= BlockInventory.HotbarSize) return;
            ushort id = _hovered != null ? IdAt(_hovered) : (ushort)0;
            if (id == 0){
                if (_searchField.focusController?.focusedElement != _searchField) SelectSlot(index);
                return;
            }

            if (_hovered.Area == SlotArea.Hotbar) _blocks.Move(BlockInventory.Area.Hotbar, _hovered.Index, BlockInventory.Area.Hotbar, index);
            else _blocks.Set(BlockInventory.Area.Hotbar, index, id);
        }

        private void HandleInventoryInput(){
            if (Pressed(_confirm) && _tab == TabAll) PutFirstMatch();
            if (Pressed(_hotbarSlot)) HotbarKeyInInventory(_hotbarSlot.GetBindingIndexForControl(_hotbarSlot.activeControl));
            HandlePad();
            if (_invOpen && _focusSearchFrame >= 0 && Time.frameCount >= _focusSearchFrame){
                _focusSearchFrame = -1;
                if (_tab == TabAll && !_padOn) _searchField.Focus();
            }
        }

        private ushort IdAt(SlotTag tag){
            return tag.Area == SlotArea.All ? (ushort)tag.Index : _blocks.Get(AreaOf(tag), tag.Index).Id;
        }

        private static BlockInventory.Area AreaOf(SlotTag tag){
            return tag.Area == SlotArea.Hotbar ? BlockInventory.Area.Hotbar : BlockInventory.Area.Backpack;
        }

        // ── Pointer: hover, click, drag and drop ─────────────────────────────

        private void RegisterSlot(VisualElement slot, SlotTag tag){
            slot.userData = tag;
            slot.RegisterCallback<PointerEnterEvent>(_ => OnSlotEnter(tag));
            slot.RegisterCallback<PointerLeaveEvent>(_ => OnSlotLeave(tag));
            slot.RegisterCallback<PointerDownEvent>(evt => OnSlotDown(slot, tag, evt));
            slot.RegisterCallback<PointerMoveEvent>(OnSlotMove);
            slot.RegisterCallback<PointerUpEvent>(OnSlotUp);
            slot.RegisterCallback<PointerCaptureOutEvent>(_ => EndPress());
        }

        private void OnSlotEnter(SlotTag tag){
            if (!_invOpen) return;
            _hovered = tag;
            ushort id = IdAt(tag);
            if (id != 0) ShowAside(id);
        }

        private void OnSlotLeave(SlotTag tag){
            if (_hovered == tag) _hovered = null;
        }

        private void OnSlotDown(VisualElement slot, SlotTag tag, PointerDownEvent evt){
            if (!_invOpen || evt.button != 0 || _press != null) return;
            _press = new Press{
                Tag = tag, Element = slot, Start = evt.position, PointerId = evt.pointerId, Id = IdAt(tag)
            };
            slot.CapturePointer(evt.pointerId);
            evt.StopPropagation();
        }

        private void OnSlotMove(PointerMoveEvent evt){
            if (_press == null || evt.pointerId != _press.PointerId) return;
            Vector2 position = evt.position;
            if (!_press.Dragging){
                if (_press.Id == 0 || (position - _press.Start).sqrMagnitude < DragThreshold * DragThreshold) return;
                _press.Dragging = true;
                _press.Element.AddToClassList("is--source");
                ShowGhost(_press.Id);
            }

            MoveGhost(position);
        }

        private void OnSlotUp(PointerUpEvent evt){
            if (_press == null || evt.pointerId != _press.PointerId) return;
            Press press = _press;
            _press = null; // before the release, so the capture-out event does not end it twice
            if (press.Element.HasPointerCapture(press.PointerId)) press.Element.ReleasePointer(press.PointerId);
            press.Element.RemoveFromClassList("is--source");
            HideGhost();
            SlotTag target = SlotAt(evt.position);
            if (press.Dragging){
                if (target != null) DropOnto(press.Tag, press.Id, target);
                else if (!_invPanel.worldBound.Contains(evt.position) && !_hotbar.worldBound.Contains(evt.position))
                    ThrowOut(press.Tag);
            }
            else if (target == press.Tag){
                ClickSlot(press.Tag, evt.shiftKey);
            }

            evt.StopPropagation();
        }

        private void EndPress(){
            if (_press == null) return;
            Press press = _press;
            _press = null;
            if (press.Element.HasPointerCapture(press.PointerId)) press.Element.ReleasePointer(press.PointerId);
            press.Element.RemoveFromClassList("is--source");
            HideGhost();
        }

        /// <summary>The slot under a panel position, or null.</summary>
        private SlotTag SlotAt(Vector2 position){
            VisualElement element = Root.panel?.Pick(position);
            while (element != null && element.userData is not SlotTag) element = element.parent;
            return element?.userData as SlotTag;
        }

        private void ShowGhost(ushort id){
            SetIcon(_ghost, id);
            _ghost.parent.style.display = DisplayStyle.Flex;
        }

        private void MoveGhost(Vector2 panelPosition){
            Vector2 local = _inv.WorldToLocal(panelPosition);
            _ghost.parent.style.left = local.x - 32f;
            _ghost.parent.style.top = local.y - 32f;
        }

        private void HideGhost(){
            if (_ghost != null) _ghost.parent.style.display = DisplayStyle.None;
        }

        private sealed class SlotTag{
            public readonly SlotArea Area;

            /// <summary>The slot index; for the full list the voxel id.</summary>
            public readonly int Index;

            public SlotTag(SlotArea area, int index){
                Area = area;
                Index = index;
            }
        }

        private sealed class Press{
            public bool Dragging;
            public VisualElement Element;
            public ushort Id;
            public int PointerId;
            public Vector2 Start;
            public SlotTag Tag;
        }

        private sealed class Tab{
            public Button Button;
            public Label Chip;
            public BlockIcon Icon;
        }

        private sealed class AllEntry{
            public VoxelCategory Category;
            public ushort Id;
            public string IdText, Name;
            public VisualElement Slot;
            public bool Visible;
        }
    }
}
