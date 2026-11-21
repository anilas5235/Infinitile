using System.Collections.Generic;
using Engine.Scripts.Inventory;
using Engine.Scripts.VoxelConfig;
using Engine.Scripts.VoxelConfig.Data.Mesh;
using Engine.Scripts.VoxelConfig.Data.Voxel;
using Engine.Scripts.VoxelConfig.Registry;
using UnityEngine;
using UnityEngine.UIElements;

namespace UI{
    /// <summary>
    ///     The hotbar: nine block slots of the player's <see cref="BlockInventory" />; 1–9, the wheel or the shoulder
    ///     buttons select one, the chosen slot is lifted and its block is the one placed.
    /// </summary>
    public partial class HudController{
        private readonly List<VisualElement> _slots = new();
        private BlockInventory _blocks = new();
        private bool _blocksDirty, _blocksLoaded;
        private VisualElement _hotbar;
        private int _selected;
        private Label _voxelName;

        private void BuildHotbar(){
            _voxelName = Root.Q<Label>("voxel-name");
            _hotbar = Root.Q("hotbar");
            _hotbar.Clear();
            _slots.Clear();
            for (int i = 0; i < BlockInventory.HotbarSize; i++){
                VisualElement slot = CreateSlot($"{i + 1}");
                slot.pickingMode = PickingMode.Ignore; // pickable only while the inventory is open
                RegisterSlot(slot, new SlotTag(SlotArea.Hotbar, i));
                _hotbar.Add(slot);
                _slots.Add(slot);
            }

            _blocks = new BlockInventory();
            _blocks.Changed += OnBlocksChanged;
            _blocksLoaded = false;
            LoadBlocks();
        }

        /// <summary>A block slot of the hotbar: the voxel's cube icon with its key in the corner.</summary>
        private static VisualElement CreateSlot(string keyText){
            VisualElement slot = new();
            slot.AddToClassList("hs");
            BlockIcon icon = new(){ name = "icon" };
            icon.AddToClassList("hs__icon");
            slot.Add(icon);
            if (keyText.Length == 0) return slot;
            Label key = new(keyText){ pickingMode = PickingMode.Ignore };
            key.AddToClassList("hs__num");
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
            // a biome-tinted grass side: dirt untinted, the grass band through the side overlay (DEC-122)
            Texture2D overlay = flat || voxel.biomeTint == VoxelBiomeTint.None ? null : voxel.biomeTintMask;
            icon.Set(top ? top : side, side, flat, voxel.DisplayTint, voxel.DisplaySideTint, overlay);
        }

        private static void SetIcon(BlockIcon icon, ushort id){
            VoxelRegistry registry = Registry();
            SetIcon(icon, id != 0 && registry != null && registry.TryGet(id, out Voxel voxel) ? voxel : null);
        }

        /// <summary>
        ///     Loads the saved hotbar and backpack once the registry has its voxels (it fills in the DataImporter's
        ///     Awake); without a save, the hotbar gets <see cref="hotbarVoxels" /> and the backpack stays empty.
        /// </summary>
        private void LoadBlocks(){
            VoxelRegistry registry = Registry();
            if (_blocksLoaded || registry == null || registry.BaseCount <= 1) return;
            _blocksLoaded = true;
            if (!InventoryStore.TryLoad(_blocks, registry)){
                for (int i = 0; i < BlockInventory.HotbarSize && i < hotbarVoxels.Length; i++)
                    _blocks.Set(BlockInventory.Area.Hotbar, i, InventoryStore.IdOf(registry, hotbarVoxels[i]));
            }

            _blocksDirty = false;
            int current = voxelEditor && voxelEditor.voxelId != 0
                ? _blocks.IndexOf(BlockInventory.Area.Hotbar, voxelEditor.voxelId)
                : -1;
            RefreshHotbar();
            SelectSlot(current >= 0 ? current : _selected);
        }

        private void SaveBlocks(){
            if (!_blocksDirty || !_blocksLoaded) return;
            // stays dirty when the save was skipped (the registry may already be gone on quit)
            if (InventoryStore.Save(_blocks, Registry())) _blocksDirty = false;
        }

        private void OnBlocksChanged(){
            _blocksDirty = true;
            RefreshHotbar();
            // the selected slot may hold another block now
            ushort id = _blocks.Get(BlockInventory.Area.Hotbar, _selected).Id;
            SelectInEditor(id);
            ShowVoxelName(id);
            if (_invOpen) RefreshInventory();
        }

        private void RefreshHotbar(){
            for (int i = 0; i < _slots.Count; i++)
                SetIcon(_slots[i].Q<BlockIcon>("icon"), _blocks.Get(BlockInventory.Area.Hotbar, i).Id);
        }

        private void SelectSlot(int index){
            _selected = index;
            for (int i = 0; i < _slots.Count; i++) _slots[i].EnableInClassList("hs--on", i == index);
            ushort id = _blocks.Get(BlockInventory.Area.Hotbar, index).Id;
            SelectInEditor(id);
            ShowVoxelName(id);
            if (_invOpen) RefreshInventory();
        }

        /// <summary>The voxel editor places the selected slot's block; an empty slot places nothing.</summary>
        private void SelectInEditor(ushort id){
            if (!voxelEditor) return;
            if (id != 0) voxelEditor.SelectVoxel(id);
            else voxelEditor.ClearSelection();
        }

        /// <summary>A pick (middle click) selects the slot holding the voxel, or puts it into the selected slot.</summary>
        private void OnVoxelIdChanged(ushort id){
            if (!_blocksLoaded || _blocks.Get(BlockInventory.Area.Hotbar, _selected).Id == id) return;
            int existing = _blocks.IndexOf(BlockInventory.Area.Hotbar, id);
            if (existing >= 0) SelectSlot(existing);
            else _blocks.Set(BlockInventory.Area.Hotbar, _selected, id);
        }

        private void ShowVoxelName(ushort id){
            VoxelRegistry registry = Registry();
            _voxelName.text = id != 0 && registry != null && registry.TryGet(id, out Voxel voxel) ? voxel.name : "";
            _voxelName.style.display = string.IsNullOrEmpty(_voxelName.text) ? DisplayStyle.None : DisplayStyle.Flex;
        }

        private static VoxelRegistry Registry(){
            return DataImporter.Instance ? DataImporter.Instance.VoxelRegistry : null;
        }
    }
}
