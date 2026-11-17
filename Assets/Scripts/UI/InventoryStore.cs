using System.Text;
using Engine.Scripts.Diagnostics;
using Engine.Scripts.Inventory;
using Engine.Scripts.VoxelConfig.Registry;
using Unity.Collections;
using UnityEngine;

namespace UI{
    /// <summary>
    ///     Keeps the player's <see cref="BlockInventory" /> (hotbar and backpack) in PlayerPrefs between runs, by block
    ///     name, so a save survives new voxels and changed ids; a block that no longer exists leaves its slot empty. A
    ///     measurement run or a test (<see cref="AutomatedRun" />) neither loads nor saves it.
    /// </summary>
    public static class InventoryStore{
        private const string Key = "inventory.v1";

        /// <summary>Loads the saved inventory; false when none is saved, it cannot be read, or a run is automated.</summary>
        public static bool TryLoad(BlockInventory inventory, VoxelRegistry registry){
            if (AutomatedRun.Active || registry == null || !PlayerPrefs.HasKey(Key)) return false;
            return inventory.TryLoadJson(PlayerPrefs.GetString(Key), name => IdOf(registry, name));
        }

        /// <summary>Saves the inventory, unless a run is automated; false when nothing was saved.</summary>
        public static bool Save(BlockInventory inventory, VoxelRegistry registry){
            if (AutomatedRun.Active || registry == null) return false;
            PlayerPrefs.SetString(Key, inventory.ToJson(id => NameOf(registry, id)));
            PlayerPrefs.Save();
            return true;
        }

        /// <summary>The base voxel id of a full name ("std:Grass"); 0 when there is no such voxel.</summary>
        public static ushort IdOf(VoxelRegistry registry, string fullName){
            if (string.IsNullOrEmpty(fullName) ||
                Encoding.UTF8.GetByteCount(fullName) > FixedString32Bytes.UTF8MaxLengthInBytes) return 0;
            return registry.TryGetId(new FixedString32Bytes(fullName), out ushort id) ? registry.BaseOf(id) : (ushort)0;
        }

        /// <summary>The full name of a voxel id; "" when unknown.</summary>
        public static string NameOf(VoxelRegistry registry, ushort id){
            return registry.TryGetName(id, out FixedString32Bytes name) ? name.ToString() : "";
        }
    }
}
