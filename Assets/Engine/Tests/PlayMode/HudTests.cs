using System.Collections;
using Engine.Scripts.World;
using Engine.Scripts.WorldGen.Core;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Engine.Tests.PlayMode{
    /// <summary>
    ///     GEN-39: the UI Toolkit HUD of the game scene runs without errors and its world panel shows the biome of
    ///     the column under the camera. The HUD lives in the game assembly, so the test reads it by element names.
    /// </summary>
    public class HudTests{
        [UnitySetUp]
        public IEnumerator SetUp(){
            yield return PlayModeFixtures.LoadGameScene();
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

            LogAssert.NoUnexpectedReceived();
        }

        private static VisualElement FindHud(){
            foreach (UIDocument document in Object.FindObjectsByType<UIDocument>(FindObjectsSortMode.None)){
                VisualElement hud = document.rootVisualElement?.Q("hud");
                if (hud != null) return hud;
            }

            return null;
        }
    }
}
