using System.Collections;
using System.Collections.Generic;
using Engine.Scripts.Inventory;
using Engine.Scripts.Render;
using Engine.Scripts.VoxelConfig;
using Engine.Scripts.VoxelConfig.Data.Voxel;
using Engine.Scripts.VoxelConfig.Registry;
using Engine.Scripts.World;
using Engine.Scripts.WorldGen.Core;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Engine.Tests.PlayMode{
    /// <summary>
    ///     The UI Toolkit HUD of the game scene runs without errors, its F3 panels start hidden and F3 shows them, its
    ///     world panel shows the biome of the column under the camera, its GPU memory panel the engine's buffers, and
    ///     the Esc menu opens, goes to its settings page and back, and closes; the inventory opens with its backpack
    ///     and block list, filters and closes. The HUD lives in the game assembly, so the test reads it by element
    ///     names.
    /// </summary>
    public class HudTests{
        /// <summary>
        ///     Warnings and errors logged while the test runs. Plain log lines do not count: the world may still
        ///     build behind the HUD and reports the growth of its render buffers.
        /// </summary>
        private readonly List<string> _problems = new();

        [UnitySetUp]
        public IEnumerator SetUp(){
            yield return PlayModeFixtures.LoadGameScene();
            _problems.Clear();
            Application.logMessageReceived += CollectProblem;
        }

        [TearDown]
        public void TearDown(){
            Application.logMessageReceived -= CollectProblem;
        }

        private void CollectProblem(string condition, string stackTrace, LogType type){
            if (type != LogType.Log) _problems.Add($"[{type}] {condition}");
        }

        [UnityTest]
        public IEnumerator Hud_ShowsTheBiomeUnderThePlayer_WithoutErrors(){
            for (int i = 0; i < 60; i++) yield return null;

            VisualElement hud = FindHud();
            Assert.IsNotNull(hud, "no UIDocument with a \"hud\" element in the scene");
            Assert.AreEqual(9, hud.Q("hotbar").childCount, "hotbar slots");
            StringAssert.Contains("FPS", hud.Q<Label>("fps").text);
            VisualElement hints = hud.Q("hints");
            Assert.AreEqual(3, hints.childCount, "key hints: panels, inventory, menu");
            Assert.AreEqual("F3", hints[0].Q<Label>().text, "the panels key");

            // the panels start hidden and do not read the culling counts back; F3 shows them
            Assert.AreEqual(DisplayStyle.None, hud.Q("debug-left").resolvedStyle.display, "left panels hidden at start");
            Assert.AreEqual(DisplayStyle.None, hud.Q("debug-right").resolvedStyle.display, "right panels hidden at start");
            Assert.AreEqual("show panels", hints[0].Q<Label>(className: "hint__text").text, "the panels hint");
            Assert.IsFalse(VoxelWorldRenderer.Instance.CollectCullingStats, "no culling readback while hidden");
            yield return ShowPanels(hud);
            Assert.AreEqual("hide panels", hints[0].Q<Label>(className: "hint__text").text, "the panels hint");

            // the panels refresh every 0.1 s; wait for one refresh with the camera at rest
            yield return new WaitForSecondsRealtime(0.25f);
            Vector3 pos = Camera.main.transform.position;
            Assert.IsTrue(VoxelWorld.Instance.TrySampleColumn(Mathf.FloorToInt(pos.x), Mathf.FloorToInt(pos.z),
                out ColumnSample sample));
            string biome = VoxelWorld.Instance.BiomeName(sample.SurfaceSource);
            Assert.IsNotNull(biome);
            StringAssert.StartsWith(biome, hud.Q<Label>("biome").text);
            bool looking = hud.Q("look-body").resolvedStyle.display == DisplayStyle.Flex;
            bool empty = hud.Q("look-empty").resolvedStyle.display == DisplayStyle.Flex;
            Assert.AreNotEqual(looking, empty, "the looked-at panel shows a block or \"no block in reach\"");

            Assert.IsEmpty(_problems, "warnings or errors while the HUD ran");
        }

        [UnityTest]
        public IEnumerator GpuPanel_ShowsTheEngineBuffers_AndTheMenuOpensAndCloses(){
            for (int i = 0; i < 30; i++) yield return null;
            VisualElement hud = FindHud(out UIDocument document);
            Assert.IsNotNull(hud, "no UIDocument with a \"hud\" element in the scene");

            // shown, so the menu below has panels to hide
            yield return ShowPanels(hud);
            yield return new WaitForSecondsRealtime(0.25f); // one refresh of the panels
            StringAssert.EndsWith(" ms", hud.Q<Label>("frame-ms").text);
            StringAssert.StartsWith("avg ", hud.Q<Label>("frame-average").text, "the frame time is marked as an average");
            StringAssert.EndsWith(" MB", hud.Q<Label>("gpu-total").text);
            StringAssert.EndsWith(" MB", hud.Q<Label>("gpu-store").text);
            StringAssert.Contains(" % used · peak ", hud.Q<Label>("gpu-usage").text);
            StringAssert.Contains(" · ", hud.Q<Label>("gpu-render").text);
            StringAssert.Contains(" × ", hud.Q<Label>("gpu-slots").text, "the renderer has build slots");
            // the Editor has Unity's counter, so the row is shown
            Assert.AreEqual(DisplayStyle.Flex, hud.Q("vram-row").resolvedStyle.display, "VRAM row");
            StringAssert.EndsWith(" MB", hud.Q<Label>("vram").text);

            VisualElement menu = hud.Q("menu");
            Assert.IsNotNull(menu, "menu element");
            Assert.AreEqual(DisplayStyle.None, menu.resolvedStyle.display, "the menu starts closed");

            // The HUD lives in the game assembly: open the menu by message, as the Esc key does.
            document.gameObject.SendMessage("SetMenu", true);
            yield return null;
            try{
                Assert.AreEqual(DisplayStyle.Flex, menu.resolvedStyle.display, "the menu opens");
                Assert.AreEqual(CursorLockMode.None, UnityEngine.Cursor.lockState, "the cursor is free while the menu is open");
                Assert.AreEqual(DisplayStyle.None, hud.Q("hotbar-area").resolvedStyle.display, "hotbar hidden");
                Assert.AreEqual(DisplayStyle.None, hud.Q("debug-left").resolvedStyle.display, "panels hidden");
                Assert.AreEqual(DisplayStyle.Flex, hud.Q("menu-main").resolvedStyle.display, "the main page opens");
                Assert.IsNotNull(hud.Q<Button>("menu-continue"));
                Assert.IsNotNull(hud.Q<Button>("menu-quit"));
                menu.Query<Label>().ForEach(label => StringAssert.DoesNotContain("Esc", label.text, "no key text"));

                Button settings = hud.Q<Button>("menu-settings");
                using (NavigationSubmitEvent submit = NavigationSubmitEvent.GetPooled()){
                    submit.target = settings;
                    settings.SendEvent(submit);
                }

                yield return null;
                Assert.AreEqual(DisplayStyle.Flex, hud.Q("menu-settings-page").resolvedStyle.display, "settings page");
                Assert.AreEqual(DisplayStyle.None, hud.Q("menu-main").resolvedStyle.display, "main page hidden");
                CollectionAssert.AreEqual(new[]{ "4", "6", "8", "10", "12", "16" }, Texts(hud.Q("menu-draw-distance")));
                VisualElement now = hud.Q("menu-draw-distance").Q(className: "badge")?.parent;
                Assert.IsNotNull(now, "the current draw distance has a badge");
                Assert.AreEqual($"{VoxelWorld.Instance.DrawDistance}", ((Button)now).text);
                Assert.IsTrue(now.ClassListContains("t--on"), "the current draw distance is picked");
                Assert.IsFalse(hud.Q<Button>("menu-apply").enabledSelf, "Apply waits for another distance");
                StringAssert.StartsWith("GPU interval store ", hud.Q<Label>("menu-store-estimate").text);
                CollectionAssert.AreEqual(new[]{ "60", "90", "120", "300", "Uncapped" },
                    Texts(hud.Q("menu-frame-rate")));
                Assert.IsNotNull(hud.Q<Button>("menu-vsync"));

                // the menu key goes back to the main page first
                document.gameObject.SendMessage("MenuBack");
                yield return null;
                Assert.AreEqual(DisplayStyle.Flex, hud.Q("menu-main").resolvedStyle.display, "back on the main page");
                Assert.AreEqual(DisplayStyle.Flex, menu.resolvedStyle.display, "the menu is still open");
            }
            finally{
                document.gameObject.SendMessage("SetMenu", false);
            }

            yield return null;
            Assert.AreEqual(DisplayStyle.None, menu.resolvedStyle.display, "the menu closes");
            Assert.IsEmpty(_problems, "warnings or errors while the HUD ran");
        }

        [UnityTest]
        public IEnumerator Inventory_OpensWithBackpackAndAllBlocks_FiltersAndCloses(){
            for (int i = 0; i < 30; i++) yield return null;
            VisualElement hud = FindHud(out UIDocument document);
            Assert.IsNotNull(hud, "no UIDocument with a \"hud\" element in the scene");
            Assert.AreEqual("inventory", hud.Q("hints")[1].Q<Label>(className: "hint__text").text, "inventory hint");

            VisualElement inventory = hud.Q("inventory");
            Assert.IsNotNull(inventory, "inventory element");
            Assert.AreEqual(DisplayStyle.None, inventory.resolvedStyle.display, "the inventory starts closed");

            // The HUD lives in the game assembly: open the inventory by message, as the inventory key does.
            document.gameObject.SendMessage("SetInventory", true);
            yield return null;
            try{
                Assert.AreEqual(DisplayStyle.Flex, inventory.resolvedStyle.display, "the inventory opens");
                Assert.AreEqual(CursorLockMode.None, UnityEngine.Cursor.lockState, "the cursor is free");
                Assert.AreEqual(DisplayStyle.None, hud.Q("crosshair").resolvedStyle.display, "crosshair hidden");
                Assert.AreEqual(DisplayStyle.Flex, hud.Q("hotbar-area").resolvedStyle.display, "the hotbar stays");
                Assert.IsTrue(hud.Q("hotbar").ClassListContains("hotbar--open"), "hotbar slots are drop targets");

                Assert.AreEqual(72, hud.Q("inv-backpack-grid").childCount, "backpack slots");
                VisualElement tabs = hud.Q("inv-tabs");
                Assert.AreEqual(4, tabs.childCount, "tabs: backpack, all blocks, saved hotbars, found nearby");
                Assert.AreEqual(2, tabs.Query(className: "badge").ToList().Count, "two tabs come later");

                ScrollView all = hud.Q<ScrollView>("inv-all-grid");
                int total = all.contentContainer.childCount;
                Assert.Greater(total, 50, "one entry per base voxel");
                Assert.AreEqual($"{total} blocks", hud.Q<Label>("inv-search-count").text);
                inventory.Query<Label>().ForEach(label => {
                    StringAssert.DoesNotContain("Esc", label.text, "no key text");
                    StringAssert.DoesNotContain("Drag", label.text, "no how-to text");
                });
                // every button text sits in the middle of its button (Unity's button class is removed, hud-btn centres)
                inventory.Query<Button>().ForEach(button => {
                    Assert.IsTrue(button.ClassListContains("hud-btn"), button.name + " is a HUD button");
                    Assert.AreEqual(TextAnchor.MiddleCenter, button.resolvedStyle.unityTextAlign, button.name + " text centred");
                });

                // the Liquids chip narrows the list; Enter (PutFirstMatch) puts its first block on the hotbar
                document.gameObject.SendMessage("SetTab", 1);
                document.gameObject.SendMessage("SetCategory", (int)VoxelCategory.Liquids);
                yield return null;
                int shown = 0;
                foreach (VisualElement slot in all.contentContainer.Children())
                    if (slot.resolvedStyle.display == DisplayStyle.Flex)
                        shown++;
                Assert.Greater(shown, 0, "liquids shown");
                Assert.Less(shown, total, "the chip filters");
                Assert.AreEqual($"{shown} blocks", hud.Q<Label>("inv-search-count").text);

                // the Light chip shows the voxels of category Light only (the torch); lava glows but stays a liquid
                document.gameObject.SendMessage("SetCategory", (int)VoxelCategory.Light);
                yield return null;
                int lightVoxels = 0;
                VoxelRegistry registry = DataImporter.Instance.VoxelRegistry;
                for (ushort id = 1; id < registry.BaseCount; id++)
                    if (registry.TryGet(id, out Voxel voxel) && voxel.category == VoxelCategory.Light)
                        lightVoxels++;
                Assert.AreEqual(1, lightVoxels, "the torch is the one Light block");
                Assert.AreEqual("1 block", hud.Q<Label>("inv-search-count").text, "Light chip");

                document.gameObject.SendMessage("SetCategory", (int)VoxelCategory.Liquids);
                yield return null;
                document.gameObject.SendMessage("PutFirstMatch");
                yield return null;
                string placed = hud.Q<Label>("voxel-name").text;
                Assert.IsNotEmpty(placed, "the selected slot holds the block");
                Assert.AreEqual(hud.Q<Label>("inv-aside-name").text, placed, "the side panel shows it");

                // the side panel: Package, Id, Layer, Collision and Light rows, no button, no backpack or hotbar rows
                Assert.IsNull(hud.Q("inv-put"), "no Put on hotbar button");
                Assert.IsNull(hud.Q("inv-aside-backpack"), "no In backpack row");
                Assert.IsNull(hud.Q("inv-aside-hotbar"), "no On hotbar row");
                string fullId = hud.Q<Label>("inv-aside-id").text;
                Assert.IsTrue(registry.TryGetId(fullId, out ushort placedId), "the Id row is the registry's string id");
                Assert.IsTrue(registry.TryGet(placedId, out Voxel placedVoxel));
                Assert.AreEqual(placed, placedVoxel.name, "the Id row names the shown block");
                Assert.AreEqual(placedVoxel.package.name, hud.Q<Label>("inv-aside-package").text, "Package row");
                Assert.AreEqual(placedVoxel.meshLayer.ToString(), hud.Q<Label>("inv-aside-layer").text, "Layer row");
                Assert.AreEqual(placedVoxel.collision ? "solid" : "none", hud.Q<Label>("inv-aside-collision").text,
                    "Collision row");
                Assert.IsNotEmpty(hud.Q<Label>("inv-aside-light").text, "Light row");
            }
            finally{
                document.gameObject.SendMessage("SetInventory", false);
            }

            yield return null;
            Assert.AreEqual(DisplayStyle.None, inventory.resolvedStyle.display, "the inventory closes");
            Assert.AreEqual(DisplayStyle.Flex, hud.Q("crosshair").resolvedStyle.display, "crosshair back");
            Assert.IsEmpty(_problems, "warnings or errors while the HUD ran");
        }

        /// <summary>
        ///     An empty selected hotbar slot places nothing (review M1): the voxel editor had kept the last block. A
        ///     stone target is built in front of the camera; with the slot empty, holding place leaves the cell before
        ///     it empty, and with stone in the slot the same press places stone there. The cells are restored.
        /// </summary>
        [UnityTest]
        public IEnumerator EmptyHotbarSlot_PlacesNothing(){
            VisualElement hudRoot = FindHud(out UIDocument document);
            Assert.IsNotNull(hudRoot, "no UIDocument with a \"hud\" element in the scene");
            Component hud = document.GetComponent("HudController");
            Assert.IsNotNull(hud, "HudController on the HUD document");
            MonoBehaviour editor = null;
            foreach (MonoBehaviour behaviour in Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None))
                if (behaviour.GetType().Name == "VoxelEditor")
                    editor = behaviour;
            Assert.IsNotNull(editor, "VoxelEditor in the scene");

            // the player (and its editor) turns on once the ground under the spawn is built, then falls onto it
            float deadline = Time.realtimeSinceStartup + 60f;
            while (!editor.isActiveAndEnabled && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.IsTrue(editor.isActiveAndEnabled, "the player did not spawn");
            Camera camera = Camera.main;
            Vector3 last = camera.transform.position;
            int still = 0;
            deadline = Time.realtimeSinceStartup + 10f; // the spawn is under water, where the player may drift
            while (still < 30 && Time.realtimeSinceStartup < deadline){
                yield return null;
                still = (camera.transform.position - last).sqrMagnitude < 1e-6f ? still + 1 : 0;
                last = camera.transform.position;
            }

            BlockInventory blocks = (BlockInventory)Private(hud, "_blocks");
            int selected = (int)Private(hud, "_selected");
            System.Reflection.FieldInfo place = editor.GetType().GetField("place");
            System.Reflection.FieldInfo voxelId = editor.GetType().GetField("voxelId");
            System.Reflection.MethodInfo lookAt = editor.GetType().GetMethod("GetLookAtVoxelPos", Instance);
            VoxelRegistry registry = DataImporter.Instance.VoxelRegistry;
            Assert.IsTrue(registry.TryGetId("std:Stone", out ushort stone));
            VoxelWorld world = VoxelWorld.Instance;

            // a stone 3.5 m ahead, and no solid block on the ray before it (water and air stay, so the player
            // does not move); every changed cell is put back. The editor casts from the camera each fixed step.
            Vector3 eye = camera.transform.position, forward = camera.transform.forward;
            Vector3Int target = Vector3Int.FloorToInt(eye + forward * 3.5f);
            Dictionary<Vector3Int, ushort> saved = new(){ { target, VoxelAt(world, target) } };
            for (float t = 0.8f; t <= 3.5f; t += 0.05f){
                Vector3Int cell = Vector3Int.FloorToInt(eye + forward * t);
                ushort id = VoxelAt(world, cell);
                if (!saved.ContainsKey(cell) && registry.TryGet(id, out Voxel voxel) && voxel.collision) saved[cell] = id;
            }

            ushort slotBefore = blocks.Get(BlockInventory.Area.Hotbar, selected).Id;
            try{
                foreach (Vector3Int cell in saved.Keys) world.SetVoxel(cell == target ? stone : (ushort)0, cell);
                object[] args = { null, null, false };
                deadline = Time.realtimeSinceStartup + 30f; // the collider bake of the edited partition
                while (Time.realtimeSinceStartup < deadline){
                    yield return new WaitForFixedUpdate();
                    if ((bool)lookAt.Invoke(editor, args) && (Vector3Int)args[0] == target) break;
                }

                Assert.AreEqual(target, (Vector3Int)args[0], $"the editor looks at the stone (forward {forward})");
                args[2] = true;
                Assert.IsTrue((bool)lookAt.Invoke(editor, args));
                Vector3Int cellBefore = (Vector3Int)args[0];
                ushort original = VoxelAt(world, cellBefore);
                if (!saved.ContainsKey(cellBefore)) saved[cellBefore] = original;

                blocks.Set(BlockInventory.Area.Hotbar, selected, 0);
                Assert.AreEqual(0, (ushort)voxelId.GetValue(editor), "an empty slot selects nothing");
                place.SetValue(editor, true);
                for (int i = 0; i < 5; i++) yield return new WaitForFixedUpdate();
                place.SetValue(editor, false);
                Assert.AreEqual(original, VoxelAt(world, cellBefore), "an empty slot placed a block");

                blocks.Set(BlockInventory.Area.Hotbar, selected, stone);
                Assert.AreEqual(stone, (ushort)voxelId.GetValue(editor), "a filled slot selects its block");
                place.SetValue(editor, true);
                yield return new WaitForFixedUpdate();
                place.SetValue(editor, false);
                Assert.AreEqual(stone, registry.BaseOf(VoxelAt(world, cellBefore)),
                    "the same press with stone in the slot places stone");
            }
            finally{
                place.SetValue(editor, false);
                blocks.Set(BlockInventory.Area.Hotbar, selected, slotBefore);
                foreach (KeyValuePair<Vector3Int, ushort> cell in saved) world.SetVoxel(cell.Value, cell.Key);
            }

            Assert.IsEmpty(_problems, "warnings or errors while placing");
        }

        private static ushort VoxelAt(VoxelWorld world, Vector3Int cell){
            return world.GetVoxel(new int3(cell.x, cell.y, cell.z));
        }

        private const System.Reflection.BindingFlags Instance =
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;

        private static object Private(Component component, string field){
            System.Reflection.FieldInfo info = component.GetType().GetField(field, Instance);
            Assert.IsNotNull(info, $"{component.GetType().Name}.{field}");
            return info.GetValue(component);
        }

        private static List<string> Texts(VisualElement tiles){
            List<string> texts = new();
            foreach (VisualElement tile in tiles.Children()) texts.Add(((Button)tile).text);
            return texts;
        }

        /// <summary>Shows the F3 panels (hidden at start) by message, as the panels key does, and checks them.</summary>
        private static IEnumerator ShowPanels(VisualElement hud){
            FindHud(out UIDocument document);
            document.gameObject.SendMessage("SetDebugPanels", true);
            yield return null;
            Assert.AreEqual(DisplayStyle.Flex, hud.Q("debug-left").resolvedStyle.display, "left panels shown");
            Assert.AreEqual(DisplayStyle.Flex, hud.Q("debug-right").resolvedStyle.display, "right panels shown");
        }

        private static VisualElement FindHud(){
            return FindHud(out _);
        }

        private static VisualElement FindHud(out UIDocument hudDocument){
            foreach (UIDocument document in Object.FindObjectsByType<UIDocument>(FindObjectsSortMode.None)){
                VisualElement hud = document.rootVisualElement?.Q("hud");
                if (hud == null) continue;
                hudDocument = document;
                return hud;
            }

            hudDocument = null;
            return null;
        }
    }
}
