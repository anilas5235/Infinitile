using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Engine.Scripts.Inventory;
using Engine.Scripts.VoxelConfig;
using Engine.Scripts.VoxelConfig.Data.Mesh;
using Engine.Scripts.VoxelConfig.Data.Voxel;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Area = Engine.Scripts.Inventory.BlockInventory.Area;

namespace Engine.Tests.EditMode{
    /// <summary>
    ///     The player's block inventory (hotbar and backpack): slot edits, drag and drop, shift-click, "Sort by kind",
    ///     "Fill hotbar", the save by block name; every shipped voxel has an inventory category, and the torch is the
    ///     Light block.
    /// </summary>
    public class BlockInventoryTests{
        private static readonly Dictionary<string, ushort> Ids = new(){ { "std:Grass", 2 }, { "std:Stone", 5 }, { "std:Glass", 9 } };

        private static ushort IdOf(string name){
            return Ids.TryGetValue(name ?? "", out ushort id) ? id : (ushort)0;
        }

        private static string NameOf(ushort id){
            return Ids.FirstOrDefault(p => p.Value == id).Key ?? "";
        }

        [Test]
        public void NewInventory_IsEmptyCreative(){
            BlockInventory inventory = new();
            Assert.AreEqual(InventoryMode.Creative, inventory.Mode);
            Assert.AreEqual(9, BlockInventory.SizeOf(Area.Hotbar));
            Assert.AreEqual(72, BlockInventory.SizeOf(Area.Backpack));
            Assert.AreEqual(0, inventory.CountFilled(Area.Hotbar));
            Assert.AreEqual(0, inventory.CountFilled(Area.Backpack));
        }

        [Test]
        public void Set_StoresUnlimitedStack_AndRaisesChanged(){
            BlockInventory inventory = new();
            int changes = 0;
            inventory.Changed += () => changes++;
            inventory.Set(Area.Backpack, 3, 5);
            BlockStack stack = inventory.Get(Area.Backpack, 3);
            Assert.AreEqual(5, stack.Id);
            Assert.AreEqual(BlockStack.Unlimited, stack.Count);
            Assert.AreEqual(1, changes);
            inventory.Set(Area.Backpack, 3, 5);
            Assert.AreEqual(1, changes, "setting the same block again is no change");
            inventory.Clear(Area.Backpack, 3);
            Assert.IsTrue(inventory.Get(Area.Backpack, 3).IsEmpty);
            Assert.AreEqual(2, changes);
        }

        [Test]
        public void Move_SwapsSlotsAcrossAreas(){
            BlockInventory inventory = new();
            inventory.Set(Area.Hotbar, 0, 2);
            inventory.Set(Area.Backpack, 10, 5);
            inventory.Move(Area.Backpack, 10, Area.Hotbar, 0);
            Assert.AreEqual(5, inventory.Get(Area.Hotbar, 0).Id);
            Assert.AreEqual(2, inventory.Get(Area.Backpack, 10).Id);

            int changes = 0;
            inventory.Changed += () => changes++;
            inventory.Move(Area.Hotbar, 0, Area.Hotbar, 0);
            Assert.AreEqual(0, changes, "a drop onto its own slot does nothing");
        }

        [Test]
        public void QuickMove_GoesToFirstEmptySlotOfTheOtherArea(){
            BlockInventory inventory = new();
            inventory.Set(Area.Hotbar, 0, 9);
            inventory.Set(Area.Hotbar, 4, 2);
            inventory.Set(Area.Backpack, 0, 5);
            Assert.AreEqual(1, inventory.QuickMove(Area.Hotbar, 4));
            Assert.AreEqual(2, inventory.Get(Area.Backpack, 1).Id);
            Assert.IsTrue(inventory.Get(Area.Hotbar, 4).IsEmpty);

            Assert.AreEqual(1, inventory.QuickMove(Area.Backpack, 0));
            Assert.AreEqual(5, inventory.Get(Area.Hotbar, 1).Id);
            Assert.AreEqual(-1, inventory.QuickMove(Area.Backpack, 30), "an empty slot does not move");
        }

        [Test]
        public void QuickMove_IntoFullArea_ChangesNothing(){
            BlockInventory inventory = new();
            for (int i = 0; i < BlockInventory.HotbarSize; i++) inventory.Set(Area.Hotbar, i, 2);
            inventory.Set(Area.Backpack, 7, 5);
            Assert.AreEqual(-1, inventory.FirstEmpty(Area.Hotbar));
            Assert.AreEqual(-1, inventory.QuickMove(Area.Backpack, 7));
            Assert.AreEqual(5, inventory.Get(Area.Backpack, 7).Id);
        }

        [Test]
        public void IndexOfAndCountOf_FindDuplicates(){
            BlockInventory inventory = new();
            inventory.Set(Area.Backpack, 4, 5);
            inventory.Set(Area.Backpack, 9, 5);
            Assert.AreEqual(4, inventory.IndexOf(Area.Backpack, 5));
            Assert.AreEqual(2, inventory.CountOf(Area.Backpack, 5));
            Assert.AreEqual(-1, inventory.IndexOf(Area.Backpack, 0), "id 0 is the empty slot, never a block");
            Assert.AreEqual(0, inventory.FirstEmpty(Area.Backpack));
        }

        [Test]
        public void SortBackpack_OrdersByKindThenId_EmptySlotsLast(){
            BlockInventory inventory = new();
            // kind: 9 → 0, 5 → 1, 2 → 1
            int KindOf(ushort id) => id == 9 ? 0 : 1;
            inventory.Set(Area.Backpack, 2, 5);
            inventory.Set(Area.Backpack, 20, 2);
            inventory.Set(Area.Backpack, 40, 9);
            inventory.Set(Area.Backpack, 71, 5);
            inventory.SortBackpack(KindOf);
            ushort[] expected ={ 9, 2, 5, 5 };
            for (int i = 0; i < expected.Length; i++) Assert.AreEqual(expected[i], inventory.Get(Area.Backpack, i).Id, $"slot {i}");
            for (int i = expected.Length; i < BlockInventory.BackpackSize; i++) Assert.IsTrue(inventory.Get(Area.Backpack, i).IsEmpty);
        }

        [Test]
        public void FillHotbar_CopiesFirstNineBackpackBlocks_KeepsTheRest(){
            BlockInventory inventory = new();
            inventory.Set(Area.Hotbar, 8, 9);
            inventory.Set(Area.Backpack, 5, 2);
            inventory.Set(Area.Backpack, 30, 5);
            inventory.FillHotbar();
            Assert.AreEqual(2, inventory.Get(Area.Hotbar, 0).Id);
            Assert.AreEqual(5, inventory.Get(Area.Hotbar, 1).Id);
            Assert.IsTrue(inventory.Get(Area.Hotbar, 2).IsEmpty);
            Assert.AreEqual(9, inventory.Get(Area.Hotbar, 8).Id, "slots past the copied blocks stay");
            Assert.AreEqual(2, inventory.CountFilled(Area.Backpack), "the backpack keeps its blocks");
        }

        [Test]
        public void SaveAndLoad_ByName_DropsUnknownBlocks(){
            BlockInventory inventory = new();
            inventory.Set(Area.Hotbar, 0, 2);
            inventory.Set(Area.Hotbar, 3, 9);
            inventory.Set(Area.Backpack, 11, 5);
            inventory.Set(Area.Backpack, 12, new BlockStack(5, 17));
            string json = inventory.ToJson(NameOf);
            StringAssert.Contains("std:Grass", json);

            BlockInventory loaded = new();
            Assert.IsTrue(loaded.TryLoadJson(json, IdOf));
            Assert.AreEqual(2, loaded.Get(Area.Hotbar, 0).Id);
            Assert.AreEqual(9, loaded.Get(Area.Hotbar, 3).Id);
            Assert.AreEqual(5, loaded.Get(Area.Backpack, 11).Id);
            Assert.AreEqual(BlockStack.Unlimited, loaded.Get(Area.Backpack, 11).Count);
            Assert.AreEqual(17, loaded.Get(Area.Backpack, 12).Count, "counts are kept for the later survival mode");

            // a block that no longer exists leaves its slot empty
            BlockInventory.SaveData data = inventory.ToSaveData(NameOf);
            data.hotbar[3] = "std:Removed";
            BlockInventory changed = new();
            Assert.AreEqual(1, changed.Load(data, IdOf));
            Assert.IsTrue(changed.Get(Area.Hotbar, 3).IsEmpty);
            Assert.AreEqual(2, changed.Get(Area.Hotbar, 0).Id);
        }

        [Test]
        public void TryLoadJson_RejectsEmptyOrBrokenInput(){
            BlockInventory inventory = new();
            inventory.Set(Area.Hotbar, 0, 2);
            Assert.IsFalse(inventory.TryLoadJson("", IdOf));
            Assert.IsFalse(inventory.TryLoadJson("{ not json", IdOf));
            Assert.IsFalse(inventory.TryLoadJson("{}", IdOf));
            Assert.AreEqual(2, inventory.Get(Area.Hotbar, 0).Id, "a failed load changes nothing");
        }

        [Test]
        public void TryLoadJson_RejectsASaveWhoseBlocksAllVanished(){
            BlockInventory saved = new();
            saved.Set(Area.Hotbar, 0, 2);
            saved.Set(Area.Backpack, 4, 5);
            string json = saved.ToJson(id => id == 2 ? "old:Grass" : "old:Stone");

            BlockInventory inventory = new();
            inventory.Set(Area.Hotbar, 1, 9);
            LogAssert.Expect(LogType.Warning, new Regex("2 of 2 block names no longer exist"));
            Assert.IsFalse(inventory.TryLoadJson(json, IdOf), "the caller falls back to its default hotbar");
            Assert.AreEqual(9, inventory.Get(Area.Hotbar, 1).Id, "a rejected save changes nothing");

            BlockInventory empty = new();
            Assert.IsTrue(inventory.TryLoadJson(empty.ToJson(NameOf), IdOf), "an emptied inventory is a real save");
            Assert.IsTrue(inventory.Get(Area.Hotbar, 1).IsEmpty);
        }

        [Test]
        public void ShippedVoxels_AllHaveACategory(){
            VoxelDataPackage[] packages = Resources.LoadAll<VoxelDataPackage>("VoxelDataPackages");
            Assert.IsNotEmpty(packages);
            List<string> missing = new();
            foreach (VoxelDataPackage package in packages)
            foreach (Voxel voxel in package.voxel.Where(v => v))
                if (voxel.category == VoxelCategory.None)
                    missing.Add(voxel.name);
            Assert.IsEmpty(missing, "voxels without an inventory category: " + string.Join(", ", missing));
        }

        [Test]
        public void ShippedTorch_IsALightBlockThatGlows(){
            Voxel torch = Resources.LoadAll<VoxelDataPackage>("VoxelDataPackages")
                .SelectMany(package => package.voxel).FirstOrDefault(v => v && v.name == "Torch");
            Assert.IsNotNull(torch, "a Torch voxel in the shipped packages");
            Assert.AreEqual(VoxelCategory.Light, torch.category);
            Assert.AreEqual(new Vector3Int(14, 10, 5), torch.lightEmission, "warm light, a bit below lava (15, 9, 3)");
            Assert.AreEqual(0, torch.lightAttenuation, "see-through like the plants");
            Assert.IsFalse(torch.collision, "no collider, like the plants");
            Assert.AreEqual(MeshLayer.Foliage, torch.meshLayer, "drawn with the cutout plants");
            Assert.IsNotNull(torch.all, "the torch tile");

            // one thin stick in the block centre: four sides and a top, always drawn, x/z 7..9/16, y 0..10/16
            Assert.AreEqual("Torch", torch.shape.name);
            Assert.AreEqual(5, torch.shape.quads.Length, "four sides and the glowing top");
            foreach (VoxelQuad quad in torch.shape.quads){
                Assert.AreEqual(QuadDrawCondition.Always, quad.drawCondition, quad.quadDef.name);
                foreach (Vector3 p in new[]{ quad.quadDef.position00, quad.quadDef.position01, quad.quadDef.position02, quad.quadDef.position03 }){
                    Assert.That(p.x, Is.InRange(7f / 16f, 9f / 16f), quad.quadDef.name);
                    Assert.That(p.z, Is.InRange(7f / 16f, 9f / 16f), quad.quadDef.name);
                    Assert.That(p.y, Is.InRange(0f, 10f / 16f), quad.quadDef.name);
                }
            }
        }
    }
}
