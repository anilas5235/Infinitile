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
    ///     under the camera, its performance panel the engine's GPU buffers, and the settings menu opens and closes.
    ///     The HUD lives in the game assembly, so the test reads it by element names.
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

            // the panels refresh every 0.1 s; wait for one refresh with the camera at rest
            yield return new WaitForSecondsRealtime(0.25f);
            Vector3 pos = Camera.main.transform.position;
            Assert.IsTrue(VoxelWorld.Instance.TrySampleColumn(Mathf.FloorToInt(pos.x), Mathf.FloorToInt(pos.z),
                out ColumnSample sample));
            string biome = VoxelWorld.Instance.BiomeName(sample.SurfaceSource);
            Assert.IsNotNull(biome);
            StringAssert.StartsWith($"Biome {biome}", hud.Q<Label>("biome").text);

            Assert.IsEmpty(_problems, "warnings or errors while the HUD ran");
        }

        [UnityTest]
        public IEnumerator PerformancePanel_ShowsTheEngineGpuBuffers_AndTheMenuOpensAndCloses(){
            for (int i = 0; i < 30; i++) yield return null;
            VisualElement hud = FindHud(out UIDocument document);
            Assert.IsNotNull(hud, "no UIDocument with a \"hud\" element in the scene");

            yield return new WaitForSecondsRealtime(0.25f); // one refresh of the panels
            StringAssert.Contains("ms (avg", hud.Q<Label>("fps").text, "the frame time is marked as an average");
            StringAssert.StartsWith("GPU buffers ", hud.Q<Label>("gpu-total").text);
            StringAssert.StartsWith("Interval store ", hud.Q<Label>("gpu-store").text);
            StringAssert.StartsWith("Render buffers ", hud.Q<Label>("gpu-render").text);
            StringAssert.StartsWith("Build slots ", hud.Q<Label>("gpu-slots").text);
            StringAssert.DoesNotContain("Build slots -", hud.Q<Label>("gpu-slots").text, "the renderer has build slots");
            // the Editor has Unity's counter, so the line is shown
            StringAssert.StartsWith("VRAM (Unity) ", hud.Q<Label>("vram").text);

            VisualElement menu = hud.Q("menu");
            Assert.IsNotNull(menu, "menu element");
            Assert.AreEqual(DisplayStyle.None, menu.resolvedStyle.display, "the menu starts closed");

            // The HUD lives in the game assembly: open the menu by message, as the Esc key does.
            document.gameObject.SendMessage("SetMenu", true);
            yield return null;
            try{
                Assert.AreEqual(DisplayStyle.Flex, menu.resolvedStyle.display, "the menu opens");
                Assert.AreEqual(CursorLockMode.None, UnityEngine.Cursor.lockState, "the cursor is free while the menu is open");
                DropdownField drawDistance = hud.Q<DropdownField>("menu-draw-distance");
                CollectionAssert.AreEqual(
                    new[]{ "4 chunks", "6 chunks", "8 chunks", "10 chunks", "12 chunks", "16 chunks" },
                    drawDistance.choices);
                Assert.AreEqual($"{VoxelWorld.Instance.DrawDistance} chunks", drawDistance.value);
                Assert.IsFalse(hud.Q<Button>("menu-apply").enabledSelf, "Apply waits for another distance");
                CollectionAssert.AreEqual(new[]{ "60 fps", "90 fps", "120 fps", "300 fps", "Uncapped" },
                    hud.Q<DropdownField>("menu-frame-rate").choices);
                Assert.IsNotNull(hud.Q<Toggle>("menu-vsync"));
                Assert.IsNotNull(hud.Q<Toggle>("menu-debug-panels"));
                Assert.IsNotNull(hud.Q<Button>("menu-resume"));
                Assert.IsNotNull(hud.Q<Button>("menu-quit"));
            }
            finally{
                document.gameObject.SendMessage("SetMenu", false);
            }

            yield return null;
            Assert.AreEqual(DisplayStyle.None, menu.resolvedStyle.display, "the menu closes");
            Assert.IsEmpty(_problems, "warnings or errors while the HUD ran");
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
