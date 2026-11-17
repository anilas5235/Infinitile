using System;
using System.Collections.Generic;
using Engine.Scripts.VoxelConfig;
using Engine.Scripts.VoxelConfig.Data.Mesh;
using Engine.Scripts.VoxelConfig.Data.Voxel;
using Engine.Scripts.VoxelConfig.Registry;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.UIElements;

namespace UI{
    /// <summary>
    ///     The hotbar (nine block slots, 1–9 or the wheel select one, the chosen slot is lifted) and the voxel list
    ///     (Inventory key) that puts a clicked voxel into the chosen slot.
    /// </summary>
    public partial class HudController{
        private readonly List<SearchEntry> _searchEntries = new();
        private readonly List<VisualElement> _slots = new();
        private int _focusSearchFrame = -1;
        private VisualElement _search;
        private int _searchBaseCount;
        private TextField _searchField;
        private Label _searchName;
        private bool _searchOpen;
        private ScrollView _searchResults;
        private int _selected;
        private ushort[] _slotIds = Array.Empty<ushort>();
        private Label _voxelName;

        private void BuildHotbar(){
            _voxelName = Root.Q<Label>("voxel-name");
            VisualElement hotbar = Root.Q("hotbar");
            hotbar.Clear();
            _slots.Clear();
            _slotIds = new ushort[math.min(hotbarVoxels.Length, 9)];
            for (int i = 0; i < _slotIds.Length; i++){
                VisualElement slot = CreateSlot($"{i + 1}");
                slot.pickingMode = PickingMode.Ignore;
                hotbar.Add(slot);
                _slots.Add(slot);
            }

            ResolveHotbar();
        }

        /// <summary>A block slot of the hotbar or the voxel list: the voxel's cube icon with a label in the corner.</summary>
        private static VisualElement CreateSlot(string keyText){
            VisualElement slot = new();
            slot.AddToClassList("hs");
            BlockIcon icon = new(){ name = "icon" };
            icon.AddToClassList("hs__icon");
            Label key = new(keyText){ pickingMode = PickingMode.Ignore };
            key.AddToClassList("hs__num");
            slot.Add(icon);
            slot.Add(key);
            return slot;
        }

        /// <summary>A cube with the voxel's top and side textures; plants and other non-cube voxels flat.</summary>
        private static void SetIcon(BlockIcon icon, Voxel voxel){
            if (voxel == null){
                icon.Set(null, null, true);
                return;
            }

            Texture2D side = voxel.GetDisplayTexture(QuadDrawCondition.Forward);
            bool flat = voxel.meshLayer == MeshLayer.Foliage;
            Texture2D top = flat ? null : voxel.GetDisplayTexture(QuadDrawCondition.Up);
            icon.Set(top ? top : side, side, flat);
        }

        private bool HotbarResolved(){
            for (int i = 0; i < _slotIds.Length; i++)
                if (_slotIds[i] == 0 && !string.IsNullOrEmpty(hotbarVoxels[i]))
                    return false;
            return true;
        }

        /// <summary>Looks the slot names up in the voxel registry; slots of unknown names stay empty.</summary>
        private void ResolveHotbar(){
            VoxelRegistry registry = Registry();
            if (registry == null) return;
            bool any = false;
            for (int i = 0; i < _slotIds.Length; i++){
                if (_slotIds[i] != 0 || string.IsNullOrEmpty(hotbarVoxels[i])) continue;
                if (!registry.TryGetId(new FixedString32Bytes(hotbarVoxels[i]), out ushort id)) continue;
                SetSlot(i, id);
                any = true;
            }

            if (!any) return;
            int current = voxelEditor ? Array.IndexOf(_slotIds, voxelEditor.voxelId) : -1;
            SelectSlot(current >= 0 ? current : _selected);
        }

        private void SetSlot(int index, ushort id){
            _slotIds[index] = id;
            VoxelRegistry registry = Registry();
            SetIcon(_slots[index].Q<BlockIcon>("icon"), registry != null && registry.TryGet(id, out Voxel voxel) ? voxel : null);
        }

        private void SelectSlot(int index){
            _selected = index;
            for (int i = 0; i < _slots.Count; i++) _slots[i].EnableInClassList("hs--on", i == index);
            ushort id = _slotIds[index];
            if (voxelEditor && id != 0) voxelEditor.SelectVoxel(id);
            ShowVoxelName(id);
        }

        /// <summary>A pick (middle click) selects the slot holding the voxel, or puts it into the selected slot.</summary>
        private void OnVoxelIdChanged(ushort id){
            if (_slotIds.Length == 0 || _slotIds[_selected] == id) return;
            int existing = Array.IndexOf(_slotIds, id);
            if (existing >= 0){
                SelectSlot(existing);
                return;
            }

            SetSlot(_selected, id);
            ShowVoxelName(id);
        }

        private void ShowVoxelName(ushort id){
            VoxelRegistry registry = Registry();
            _voxelName.text = registry != null && registry.TryGet(id, out Voxel voxel) ? voxel.name : "";
            _voxelName.style.display = string.IsNullOrEmpty(_voxelName.text) ? DisplayStyle.None : DisplayStyle.Flex;
        }

        private static VoxelRegistry Registry(){
            return DataImporter.Instance ? DataImporter.Instance.VoxelRegistry : null;
        }

        // ── Voxel list ───────────────────────────────────────────────────────

        private void BindVoxelSearch(){
            _search = Root.Q("voxel-search");
            _searchField = Root.Q<TextField>("voxel-search-field");
            _searchResults = Root.Q<ScrollView>("voxel-search-results");
            _searchName = Root.Q<Label>("voxel-search-name");
            _searchField.textEdition.placeholder = "Name or id";
            _searchField.RegisterValueChangedCallback(OnSearchChanged);
            _searchField.RegisterCallback<FocusOutEvent>(OnSearchFocusOut);
            _searchEntries.Clear();
            _searchResults.Clear();
            _searchBaseCount = 0;
            _search.style.display = DisplayStyle.None;
        }

        private void OnSearchChanged(ChangeEvent<string> _){
            FilterVoxelSearch();
        }

        /// <summary>
        ///     A click on the scrollbar or the panel takes the focus from the field; it comes back a frame later, so
        ///     typing goes on filtering.
        /// </summary>
        private void OnSearchFocusOut(FocusOutEvent _){
            if (_searchOpen && _focusSearchFrame < 0) _focusSearchFrame = Time.frameCount + 1;
        }

        /// <summary>Opens or closes the voxel list; while it is open the cursor is free and the player's input is off.</summary>
        private void SetVoxelSearch(bool open){
            _searchOpen = open;
            _search.style.display = open ? DisplayStyle.Flex : DisplayStyle.None;
            SetPlayerInput(!open);
            UpdateLayers();

            if (!open){
                _focusSearchFrame = -1;
                _searchField.Blur();
                return;
            }

            BuildVoxelSearch();
            _searchField.SetValueWithoutNotify("");
            FilterVoxelSearch();
            _searchResults.scrollOffset = Vector2.zero;
            // Focus a frame later, so the key that opened the list is not typed into the field.
            _focusSearchFrame = Time.frameCount + 1;
        }

        /// <summary>
        ///     One slot per base voxel in id order; turned variants are placed from their base. Rebuilt when the
        ///     registry's voxel count changes (it fills in the DataImporter's Awake).
        /// </summary>
        private void BuildVoxelSearch(){
            VoxelRegistry registry = Registry();
            int baseCount = registry?.BaseCount ?? 0;
            if (baseCount == _searchBaseCount) return;
            _searchBaseCount = baseCount;
            _searchEntries.Clear();
            _searchResults.Clear();
            for (ushort id = 1; id < baseCount; id++){
                if (!registry.TryGet(id, out Voxel voxel)) continue;
                string fullName = registry.TryGetName(id, out FixedString32Bytes name) ? name.ToString() : voxel.name;
                ushort voxelId = id;
                VisualElement slot = CreateSlot($"{id}");
                SetIcon(slot.Q<BlockIcon>("icon"), voxel);
                slot.RegisterCallback<ClickEvent>(_ => PickFromSearch(voxelId));
                slot.RegisterCallback<PointerEnterEvent>(_ => _searchName.text = $"{fullName}   id {voxelId}");
                slot.RegisterCallback<PointerLeaveEvent>(_ => _searchName.text = "");
                _searchResults.Add(slot);
                _searchEntries.Add(new SearchEntry{
                    Id = voxelId, IdText = $"{id}", Name = fullName.ToLowerInvariant(), Slot = slot
                });
            }
        }

        /// <summary>Shows the voxels whose full name contains the query or whose id starts with it.</summary>
        private void FilterVoxelSearch(){
            string query = _searchField.value.Trim().ToLowerInvariant();
            int shown = 0;
            foreach (SearchEntry entry in _searchEntries){
                entry.Visible = query.Length == 0 || entry.Name.Contains(query) ||
                                entry.IdText.StartsWith(query, StringComparison.Ordinal);
                entry.Slot.style.display = entry.Visible ? DisplayStyle.Flex : DisplayStyle.None;
                entry.Slot.EnableInClassList("hs--on", voxelEditor && entry.Id == voxelEditor.voxelId);
                if (entry.Visible) shown++;
            }

            _searchName.text = _searchEntries.Count == 0 ? "The voxels are not loaded yet."
                : shown == 0 ? "No voxel matches."
                : "";
        }

        private void PickFirstMatch(){
            foreach (SearchEntry entry in _searchEntries)
                if (entry.Visible){
                    PickFromSearch(entry.Id);
                    return;
                }
        }

        /// <summary>Selects the slot holding the voxel, or puts it into the selected slot, and closes the list.</summary>
        private void PickFromSearch(ushort id){
            if (_slotIds.Length == 0){
                if (voxelEditor) voxelEditor.SelectVoxel(id);
            }
            else{
                int existing = Array.IndexOf(_slotIds, id);
                if (existing < 0) SetSlot(_selected, id);
                SelectSlot(existing >= 0 ? existing : _selected);
            }

            SetVoxelSearch(false);
        }

        private class SearchEntry{
            public ushort Id;
            public string IdText, Name;
            public VisualElement Slot;
            public bool Visible;
        }
    }
}
