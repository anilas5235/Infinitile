using System.Collections.Generic;
using System.IO;
using System.Linq;
using Engine.Scripts.WorldGen.Biomes;
using Engine.Scripts.WorldGen.Graph;
using Engine.Scripts.WorldGen.Settings;
using NUnit.Framework;
using Unity.Collections;
using UnityEditor;
using UnityEngine;

namespace Engine.Tests.EditMode{
    /// <summary>
    ///     The biome layout of WorldGen v2 §6 (phase V3): the compiled lookup against brute force (W2-BIO-03), nearest
    ///     box and ties, overlaps per source and across sources (W2-BIO-02, DEC-157), the validator's implausible
    ///     neighbours at corners, in the gap fill and among dry-column substitutes (W2-BIO-05), the biome limit, the
    ///     shipped starting layout and the test mod (§14).
    /// </summary>
    public class BiomeLayoutTests{
        internal const string BiomeSetPath = "Assets/EngineSettings/BiomeSet_Default.asset";
        internal const string TestModPath = "Assets/Engine/Tests/Fixtures/TestMod/";
        private const int P = BiomeBox.Parameters;

        internal static BiomeLayout Shipped(){
            return BiomeLayout.FromBiomeSet(AssetDatabase.LoadAssetAtPath<BiomeSet>(BiomeSetPath));
        }

        internal static BiomeLayout WithTestMod(){
            BiomeLayout layout = Shipped();
            layout.AddModJson("TestMod", "Biomes.json", File.ReadAllText(TestModPath + "Biomes.json"));
            return layout;
        }

        private static BiomeLayout Make(params (string Name, bool Water)[] biomes){
            BiomeLayout layout = new();
            layout.Sources.Add(BiomeLayout.BaseSourceName);
            for (int i = 0; i < biomes.Length; i++)
                layout.Biomes.Add(new LayoutBiome{ Id = i, Name = biomes[i].Name, IsWater = biomes[i].Water, Source = 0, File = biomes[i].Name + ".asset" });
            return layout;
        }

        private static BiomeBox Box(float t0 = -1, float t1 = 1, float f0 = -1, float f1 = 1, float c0 = -1, float c1 = 1, float e0 = -1, float e1 = 1,
            float w0 = -1, float w1 = 1){
            return new BiomeBox{
                TemperatureRange = new Vector2(t0, t1), HumidityRange = new Vector2(f0, f1), ContinentalnessRange = new Vector2(c0, c1),
                ErosionRange = new Vector2(e0, e1), WeirdnessRange = new Vector2(w0, w1)
            };
        }

        private static BiomeLookup Compile(BiomeLayout layout){
            Assert.IsTrue(BiomeLookupCompiler.TryCompile(layout.Boxes, Allocator.Persistent, out BiomeLookup lookup, out _, out string error), error);
            return lookup;
        }

        private static string Messages(LayoutValidation v){
            return v.ToString();
        }

        // --- Lookup ---------------------------------------------------------------------------------------------------

        /// <summary>A random layout of boxes on a 0.1 grid (so edges coincide and gaps remain) from two sources.</summary>
        private static BiomeLayout RandomLayout(int seed){
            System.Random r = new(seed);
            BiomeLayout layout = Make(("A", false), ("B", false), ("C", true), ("D", false), ("E", false), ("F", false));
            layout.Sources.Add("mod");
            int boxes = 6 + r.Next(10);
            for (int i = 0; i < boxes; i++){
                BiomeBox box = new();
                for (int d = 0; d < P; d++){
                    if (r.NextDouble() < 0.3) continue; // full range
                    int a = r.Next(-10, 10), b = r.Next(a + 1, 11);
                    box.SetRange(d, new Vector2(a * 0.1f, b * 0.1f));
                }

                layout.AddBox(r.Next(6), r.NextDouble() < 0.3 ? 1 : 0, "random", box);
            }

            return layout;
        }

        private static float[] RandomPoint(System.Random r, BiomeLayout layout){
            float[] p = new float[P];
            for (int d = 0; d < P; d++){
                if (r.NextDouble() < 0.25 && layout.Boxes.Count > 0){
                    // exactly on an edge, or next to one
                    LayoutBox b = layout.Boxes[r.Next(layout.Boxes.Count)];
                    float e = r.NextDouble() < 0.5 ? b.Min[d] : b.Max[d];
                    if (float.IsInfinity(e)) e = 0.3f;
                    p[d] = r.Next(3) switch{ 0 => e, 1 => BitDecrement(e), _ => BitIncrement(e) };
                }
                else p[d] = (float)(r.NextDouble() * 2.6 - 1.3);
            }

            return p;
        }

        /// <summary>The next float above <paramref name="v" /> (finite v).</summary>
        private static float BitIncrement(float v){
            if (v == 0f) return float.Epsilon;
            int bits = Unity.Mathematics.math.asint(v);
            return Unity.Mathematics.math.asfloat(v > 0f ? bits + 1 : bits - 1);
        }

        private static float BitDecrement(float v){
            return -BitIncrement(-v);
        }

        [Test]
        public void Lookup_EqualsBruteForce_OnSampledPoints([NUnit.Framework.Range(0, 15)] int seed){
            BiomeLayout layout = RandomLayout(seed);
            BiomeLookup lookup = Compile(layout);
            try{
                System.Random r = new(seed * 7919 + 1);
                for (int i = 0; i < 20000; i++){
                    float[] p = RandomPoint(r, layout);
                    Assert.AreEqual(lookup.BruteForce(p), lookup.Lookup(p), $"point {string.Join(", ", p)}");
                }
            }
            finally{
                lookup.Dispose();
            }
        }

        [Test]
        public void Lookup_EqualsBruteForce_InEveryGapCell([NUnit.Framework.Range(0, 7)] int seed){
            BiomeLayout layout = RandomLayout(100 + seed);
            BiomeLookup lookup = Compile(layout);
            try{
                List<float>[] lines = new List<float>[P];
                for (int d = 0; d < P; d++){
                    SortedSet<float> set = new(){ -1.5f, 1.5f };
                    foreach (LayoutBox b in layout.Boxes){
                        if (!float.IsInfinity(b.Min[d])) set.Add(b.Min[d]);
                        if (!float.IsInfinity(b.Max[d])) set.Add(b.Max[d]);
                    }

                    lines[d] = set.ToList();
                }

                System.Random r = new(seed);
                int[] k = new int[P];
                int gaps = 0;
                while (true){
                    float[] centre = new float[P];
                    for (int d = 0; d < P; d++) centre[d] = 0.5f * (lines[d][k[d]] + lines[d][k[d] + 1]);
                    if (layout.Boxes.All(b => !b.Contains(centre))){
                        gaps++;
                        for (int s = 0; s < 6; s++){
                            float[] p = new float[P];
                            for (int d = 0; d < P; d++){
                                float lo = lines[d][k[d]], hi = lines[d][k[d] + 1];
                                p[d] = s == 0 ? centre[d] : lo + (float)r.NextDouble() * (hi - lo);
                            }

                            Assert.AreEqual(lookup.BruteForce(p), lookup.Lookup(p), $"gap cell point {string.Join(", ", p)}");
                        }
                    }

                    int dd = 0;
                    while (dd < P && ++k[dd] >= lines[dd].Count - 1){
                        k[dd] = 0;
                        dd++;
                    }

                    if (dd == P) break;
                }

                Assert.Greater(gaps, 0, "the random layout has gaps");
            }
            finally{
                lookup.Dispose();
            }
        }

        [Test]
        public void Lookup_ShippedLayout_EqualsBruteForce(){
            BiomeLayout layout = Shipped();
            BiomeLookup lookup = Compile(layout);
            try{
                System.Random r = new(5);
                for (int i = 0; i < 50000; i++){
                    float[] p = RandomPoint(r, layout);
                    Assert.AreEqual(lookup.BruteForce(p), lookup.Lookup(p), $"point {string.Join(", ", p)}");
                }
            }
            finally{
                lookup.Dispose();
            }
        }

        [Test]
        public void Lookup_GapTakesTheNearestBox_TiesGoToTheLowerBiomeThenTheLowerBox(){
            BiomeLayout layout = Make(("Low", false), ("High", false));
            layout.AddBox(1, 0, "t", Box(0.5f, 1f));   // box 0, biome 1
            layout.AddBox(0, 0, "t", Box(-1f, -0.5f)); // box 1, biome 0
            BiomeLookup lookup = Compile(layout);
            try{
                Assert.AreEqual(0, lookup.Lookup(new[]{ 0.1f, 0, 0, 0, 0 }), "nearer to the upper box");
                Assert.AreEqual(1, lookup.Lookup(new[]{ -0.1f, 0, 0, 0, 0 }), "nearer to the lower box");
                Assert.AreEqual(1, lookup.Lookup(new[]{ 0f, 0, 0, 0, 0 }), "a tie goes to the lower biome id");
            }
            finally{
                lookup.Dispose();
            }

            BiomeLayout same = Make(("Only", false));
            same.AddBox(0, 0, "t", Box(0.5f, 1f));
            same.AddBox(0, 0, "t", Box(-1f, -0.5f));
            lookup = Compile(same);
            try{
                Assert.AreEqual(0, lookup.Lookup(new[]{ 0f, 0, 0, 0, 0 }), "a tie within one biome goes to the lower box index");
            }
            finally{
                lookup.Dispose();
            }
        }

        [Test]
        public void Lookup_LaterSourceWinsInTheOverlap(){
            BiomeLayout layout = Make(("Base", false));
            layout.AddBox(0, 0, "base", Box(-1f, 0.5f));
            layout.Sources.Add("mod");
            layout.Biomes.Add(new LayoutBiome{ Id = 1, Name = "Mod", Source = 1, File = "mod.json" });
            layout.AddBox(1, 1, "mod.json", Box(0f, 1f));
            BiomeLookup lookup = Compile(layout);
            try{
                Assert.AreEqual(1, lookup.Lookup(new[]{ 0.25f, 0, 0, 0, 0 }), "the mod's box wins where both hold");
                Assert.AreEqual(0, lookup.Lookup(new[]{ -0.25f, 0, 0, 0, 0 }));
            }
            finally{
                lookup.Dispose();
            }
        }

        [Test]
        public void LayoutData_LandLookupGivesTheDryColumnSubstitute(){
            BiomeLayout layout = Make(("Taiga", false), ("Ocean", true), ("Desert", false));
            layout.AddBox(0, 0, "t", Box(-1f, -0.3f));
            layout.AddBox(1, 0, "t", Box(-0.3f, 0.3f));
            layout.AddBox(2, 0, "t", Box(0.3f, 1f));
            BiomeLayoutData data = BiomeLayoutData.Create(layout, null, Allocator.Persistent);
            try{
                Assert.IsTrue(data.Water.Contains(1));
                Assert.IsFalse(data.Water.Contains(0));
                Assert.AreEqual(1, data.Main.Lookup(new[]{ -0.1f, 0, 0, 0, 0 }), "the main lookup gives the ocean box");
                Assert.AreEqual(0, data.Land.BoxBiome[data.Land.Lookup(new[]{ -0.1f, 0, 0, 0, 0 })], "a dry column there takes Taiga");
                Assert.AreEqual(2, data.Land.BoxBiome[data.Land.Lookup(new[]{ 0.1f, 0, 0, 0, 0 })], "and Desert on the other side");
            }
            finally{
                data.Dispose();
            }
        }

        // --- Validator ------------------------------------------------------------------------------------------------

        [Test]
        public void Validator_ShippedLayout_HasNoErrors(){
            LayoutValidation v = BiomeLayoutValidator.Validate(Shipped());
            Assert.IsFalse(v.HasErrors, Messages(v));
            GraphCompileResult r = ShippedNoiseGraph.Compile();
            Assert.IsTrue(r.Succeeded, string.Join("\n", r.Messages));
            using CompiledNoiseGraph graph = r.Graph;
            v = BiomeLayoutValidator.Validate(Shipped(), graph);
            Assert.IsFalse(v.HasErrors, Messages(v));
            Debug.Log($"[BiomeLayout] shipped layout: {v.Cells} cells, ε = {string.Join(", ", v.Epsilon.Select(e => e.ToString("0.#####")))}, " +
                      $"range {string.Join(", ", v.RangeMin.Zip(v.RangeMax, (a, b) => $"{a:0.###}…{b:0.###}"))}\n{Messages(v)}");
        }

        [Test]
        public void Validator_SameSourceOverlap_IsAnError(){
            BiomeLayout layout = Make(("A", false), ("B", false));
            layout.AddBox(0, 0, "a.asset", Box(-1f, 0.2f));
            layout.AddBox(1, 0, "b.asset", Box(0f, 1f));
            LayoutValidation v = BiomeLayoutValidator.Validate(layout);
            Assert.IsTrue(v.Errors.Any(m => m.Kind == "overlap" && m.File.Contains("a.asset") && m.File.Contains("b.asset")), Messages(v));
        }

        [Test]
        public void Validator_LaterSourceOverlap_IsInfoWithItsShare(){
            BiomeLayout layout = Make(("Plains", false));
            layout.AddBox(0, 0, "Plains.asset", Box(-1f, 1f));
            layout.AddModJson("Mod", "biomes.json",
                "{\"formatVersion\":1,\"biomes\":[{\"name\":\"Heath\",\"base\":\"Plains\",\"boxes\":[{\"temperature\":[-1,0]}]}]}");
            LayoutValidation v = BiomeLayoutValidator.Validate(layout);
            Assert.IsFalse(v.HasErrors, Messages(v));
            LayoutMessage info = v.Messages.Single(m => m.Kind == "override");
            Assert.AreEqual(LayoutSeverity.Info, info.Severity);
            StringAssert.Contains("50%", info.Text, "the mod's box takes half of the Plains box inside ±2… range is symmetric");
        }

        [Test]
        public void Validator_ImplausiblePairAtACorner_IsAnError(){
            BiomeLayout layout = Make(("Taiga", false), ("Desert", false), ("Plains", false));
            layout.AddBox(0, 0, "t", Box(-1f, 0f, -1f, 0f));
            layout.AddBox(1, 0, "t", Box(0f, 1f, 0f, 1f));
            layout.AddBox(2, 0, "t", Box(0f, 1f, -1f, 0f));
            layout.AddBox(2, 0, "t", Box(-1f, 0f, 0f, 1f));
            layout.AddImplausible(0, 1);
            LayoutValidation v = BiomeLayoutValidator.Validate(layout);
            Assert.IsTrue(v.Errors.Any(m => m.Kind == "corner"), Messages(v));
        }

        [Test]
        public void Validator_ImplausiblePairInTheGapFill_IsAnError_AndABufferFixesIt(){
            BiomeLayout layout = Make(("Taiga", false), ("Desert", false), ("Plains", false));
            layout.AddBox(0, 0, "t", Box(-1f, -0.2f));
            layout.AddBox(1, 0, "t", Box(0.2f, 1f));
            layout.AddImplausible(0, 1);
            LayoutValidation v = BiomeLayoutValidator.Validate(layout);
            Assert.IsTrue(v.Errors.Any(m => m.Kind == "gap"), Messages(v));

            layout.AddBox(2, 0, "t", Box(-0.2f, 0.2f));
            v = BiomeLayoutValidator.Validate(layout);
            Assert.IsFalse(v.HasErrors, Messages(v));
        }

        [Test]
        public void Validator_ImplausibleDryColumnSubstitutes_AreAnError(){
            BiomeLayout layout = Make(("Taiga", false), ("Ocean", true), ("Desert", false));
            layout.AddBox(0, 0, "t", Box(-1f, -0.3f));
            layout.AddBox(1, 0, "t", Box(-0.3f, 0.3f));
            layout.AddBox(2, 0, "t", Box(0.3f, 1f));
            layout.AddImplausible(0, 2);
            LayoutValidation v = BiomeLayoutValidator.Validate(layout);
            Assert.IsTrue(v.Errors.Any(m => m.Kind == "substitute"), Messages(v));
            Assert.IsFalse(v.Errors.Any(m => m.Kind is "neighbour" or "corner" or "gap"), "the main map keeps them apart: " + Messages(v));
        }

        [Test]
        public void Validator_FarApartPair_IsNoError(){
            BiomeLayout layout = Make(("Taiga", false), ("Plains", false), ("Desert", false));
            layout.AddBox(0, 0, "t", Box(-1f, -0.3f));
            layout.AddBox(1, 0, "t", Box(-0.3f, 0.3f));
            layout.AddBox(2, 0, "t", Box(0.3f, 1f));
            layout.AddImplausible(0, 2);
            LayoutValidation v = BiomeLayoutValidator.Validate(layout);
            Assert.IsFalse(v.HasErrors, Messages(v));
        }

        [Test]
        public void Validator_MoreThan255Biomes_IsAnError(){
            (string, bool)[] names = Enumerable.Range(0, 256).Select(i => ($"B{i}", false)).ToArray();
            BiomeLayout layout = Make(names);
            layout.AddBox(0, 0, "t", Box());
            LayoutValidation v = BiomeLayoutValidator.Validate(layout);
            Assert.IsTrue(v.Errors.Any(m => m.Kind == "count"), Messages(v));

            BiomeLayout most = Make(names.Take(255).ToArray());
            for (int i = 0; i < 255; i++) most.AddBox(i, 0, "t", Box(-1f + i * (2f / 255f), i == 254 ? 1f : -1f + (i + 1) * (2f / 255f)));
            v = BiomeLayoutValidator.Validate(most);
            Assert.IsFalse(v.Errors.Any(m => m.Kind == "count"), Messages(v));
            BiomeLayoutData data = BiomeLayoutData.Create(most, null, Allocator.Persistent);
            try{
                Assert.AreEqual(254, data.Main.BoxBiome[data.Main.Lookup(new[]{ 0.999f, 0, 0, 0, 0 })], "id 254 is the last; 255 means none");
            }
            finally{
                data.Dispose();
            }
        }

        [Test]
        public void Validator_NeverWinningBoxAndEmptyBiome_AreReported(){
            BiomeLayout layout = Make(("A", false), ("B", false), ("C", false));
            layout.AddBox(0, 0, "t", Box(-1f, 0.5f));
            layout.AddBox(1, 0, "t", Box(0.5f, 1f));
            layout.Sources.Add("mod");
            layout.AddBox(0, 1, "mod", Box(0.4f, 1f)); // covers B entirely
            LayoutValidation v = BiomeLayoutValidator.Validate(layout);
            Assert.IsTrue(v.Messages.Any(m => m.Kind == "unused" && m.Severity == LayoutSeverity.Warning && m.Text.StartsWith("B")), Messages(v));
            Assert.IsTrue(v.Messages.Any(m => m.Kind == "unused" && m.Severity == LayoutSeverity.Info && m.Text.StartsWith("C")), Messages(v));
        }

        // --- Mods -----------------------------------------------------------------------------------------------------

        [Test]
        public void TestMod_MergesAndValidates(){
            BiomeLayout layout = WithTestMod();
            Assert.AreEqual(2, layout.Sources.Count);
            int heath = layout.IdOf("Heath");
            Assert.GreaterOrEqual(heath, 0, "the mod's biome is in the palette");
            Assert.AreEqual(layout.Biomes.Count - 1, heath, "a new name takes the next id");
            Assert.IsTrue(layout.IsImplausible(heath, layout.IdOf("Desert")));
            GraphCompileResult r = TestModGraph();
            Assert.IsTrue(r.Succeeded, string.Join("\n", r.Messages));
            using CompiledNoiseGraph graph = r.Graph;
            LayoutValidation v = BiomeLayoutValidator.Validate(layout, graph);
            Assert.IsFalse(v.HasErrors, Messages(v));
            Assert.IsTrue(v.Messages.Any(m => m.Kind == "override" && m.Text.StartsWith("Heath")), "the share Heath takes from Plains: " + Messages(v));
            Assert.IsNotNull(graph.Output("testmod_wobble"), "the mod's output is in the merged graph");
        }

        [Test]
        public void ModJson_WithAnError_AddsNothing(){
            BiomeLayout layout = Shipped();
            int biomes = layout.Biomes.Count, boxes = layout.Boxes.Count;
            layout.AddModJson("Broken", "biomes.json", "{\"formatVersion\":1,\"biomes\":[{\"name\":\"X\",\"base\":\"NoSuchBiome\",\"boxes\":[{}]}]}");
            Assert.AreEqual(biomes, layout.Biomes.Count);
            Assert.AreEqual(boxes, layout.Boxes.Count);
            Assert.IsTrue(layout.MergeMessages.Any(m => m.Severity == LayoutSeverity.Error && m.Source == "Broken" && m.File == "biomes.json"));
        }

        /// <summary>The shipped graph with the test mod's graph file merged in.</summary>
        internal static GraphCompileResult TestModGraph(){
            NoiseGraph graph = NoiseGraphJson.Parse(ShippedNoiseGraph.LoadJson());
            graph.MergeFrom(NoiseGraphJson.Parse(File.ReadAllText(TestModPath + "Graph.json")));
            return GraphCompiler.Compile(graph, "NoiseGraph.json + TestMod/Graph.json");
        }
    }
}
