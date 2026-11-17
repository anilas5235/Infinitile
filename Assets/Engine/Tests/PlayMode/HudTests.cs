using System.Collections;
using System.Collections.Generic;
using Engine.Scripts.World;
using Engine.Scripts.WorldGen.Core;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Engine.Tests.PlayMode{
    /// <summary>
    ///     The UI Toolkit HUD of the game scene runs without errors, its world panel shows the biome of the column
    ///     under the camera, its GPU memory panel the engine's buffers, and the Esc menu opens, goes to its settings
    ///     page and back, and closes. The HUD lives in the game assembly, so the test reads it by element names.
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
            Assert.AreEqual(3, hints.childCount, "key hints: panels, voxels, menu");
            Assert.AreEqual("F3", hints[0].Q<Label>().text, "the panels key");

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
                VisualElement now = hud.Q(className: "badge")?.parent;
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

        private static List<string> Texts(VisualElement tiles){
            List<string> texts = new();
            foreach (VisualElement tile in tiles.Children()) texts.Add(((Button)tile).text);
            return texts;
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
