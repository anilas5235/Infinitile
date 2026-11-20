using System.Collections.Generic;
using Engine.Scripts.VoxelConfig.Data.Generation;
using Engine.Scripts.WorldGen.Core;
using Engine.Scripts.WorldGen.Settings;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;

namespace Engine.Tests.EditMode{
    /// <summary>
    ///     The biome layout of the shipped settings: both blend stages are on, and a report of the mini-biome share and
    ///     the border pairs. The report prints its values and only checks sanity; the targets are the user's to accept.
    /// </summary>
    public class ShippedBiomeLayoutTests{
        private const string ShippedSettingsPath = "Assets/EngineSettings/WorldGenSettings.asset";

        /// <summary>4096² columns at stride 4.</summary>
        private const int Size = 1024;

        private const int Stride = 4;

        /// <summary>Patches below 64 × 64 blocks count as mini-biomes.</summary>
        private const int SmallPatchCells = 64 * 64 / (Stride * Stride);

        private static WorldGenSettings LoadShipped(){
            WorldGenSettings settings = AssetDatabase.LoadAssetAtPath<WorldGenSettings>(ShippedSettingsPath);
            Assert.IsNotNull(settings, ShippedSettingsPath);
            return settings;
        }

        [Test]
        public void ShippedSettings_BlendBothStages(){
            WorldGenSettings settings = LoadShipped();
            Assert.AreEqual(BlendMethod.DistanceBlend, settings.MaterialMethod);
            Assert.AreEqual(BlendMethod.DistanceBlend, settings.HeightMethod);
        }

        [Test]
        public void ShippedBiomes_OnlyColdBiomesTakeSnowAndIce(){
            // Snow and ice stay in the biomes that want them (DEC-65); a cold Plains column stays green. Since the
            // tuning of 2026-11-10 the Snow biome is out of the shipped set and only Mountain carries snow.
            string[] cold = { "Mountain" };
            List<Biome> biomes = LoadShipped().Biomes.Biomes;
            foreach (Biome biome in biomes){
                Assert.IsNotNull(biome, "BiomeSet entry");
                bool wanted = System.Array.IndexOf(cold, biome.name) >= 0;
                Assert.AreEqual(wanted, biome.FreezesWater, $"{biome.name}: FreezesWater");
                if (!wanted) Assert.IsNull(biome.SnowTopBlock, $"{biome.name}: SnowTopBlock");
            }

            // Mountain keeps its snow line (GEN-35).
            Biome mountain = biomes.Find(b => b.name == "Mountain");
            Assert.IsNotNull(mountain, "Mountain");
            Assert.IsNotNull(mountain.SnowTopBlock, "Mountain: SnowTopBlock");
        }

        [TestCase(GenArchitecture.TerrainFirst)]
        [TestCase(GenArchitecture.BiomeFirst)]
        public void ShippedSettings_BiomePatchReport(GenArchitecture architecture){
            // Patches come from the classification: blending only dithers the surface along the borders.
            WorldGenSettings settings = Object.Instantiate(LoadShipped());
            settings.Architecture = architecture;
            settings.MaterialMethod = BlendMethod.None;
            settings.HeightMethod = BlendMethod.None;
            string[] names = settings.Biomes.Names();
            WorldGenConfig cfg = settings.ToConfig(TestFixtures.Seed);
            RegionBuffers buffers = new(){
                ColumnHeight = new NativeArray<int>(Size * Size, Allocator.TempJob),
                SurfaceSource = new NativeArray<int>(Size * Size, Allocator.TempJob)
            };
            try{
                WorldGenRequest request = new(){
                    Config = cfg, Origin = new int2(-2048, -2048), Size = new int2(Size, Size), Stride = Stride
                };
                WorldGenApi.SampleRegion(request, ref buffers).Complete();
                int[] height = buffers.ColumnHeight.ToArray();
                int[] biome = buffers.SurfaceSource.ToArray();

                PatchShare(height, biome, cfg.WaterLevel, out int land, out int small);
                Dictionary<(int, int), int> borders = BorderEdges(biome, names.Length);

                string pairs = string.Join(", ", ListPairs(borders, names));
                TestContext.WriteLine($"{architecture}: mini-biomes {100.0 * small / land:F2} % of land (patches < 64×64 blocks)");
                TestContext.WriteLine($"{architecture}: border edges {pairs}");

                Assert.Greater(land, 0, "the sample has land");
                Assert.Greater(borders.Count, 0, "the sample has biome borders");
            }
            finally{
                buffers.ColumnHeight.Dispose();
                buffers.SurfaceSource.Dispose();
                cfg.Dispose();
                Object.DestroyImmediate(settings);
            }
        }

        [Test]
        public void ShippedSettings_TourTwoPlaces(){
            // The places of the world tour of 2026-11-11 (seed 1337), with the values of that tour in brackets. Terrain-
            // first height does not depend on the biome, so each of them is a matter of which biome a column is given:
            // the Jungle keeps to the lower ground of the steep west (68 % of the window, up to 191 at p95), Badlands and
            // Jungle hardly meet (2.2 % of the border edges), the Badlands domes carry no Mountain cap (10 %) and Mountain
            // holds only the summit of the dome that had a meadow (29 %, from 175), and less of Plains and Savanna lies
            // under water (35 % and 48 %). Since the massif tuning of 2026-11-12 the dome that had a meadow lies in a
            // range: the meadow is gone and Mountain holds 15 % of the window, still only the high ground. Since DEC-120
            // (2026-11-20) Mountain is warmer and drier (T .35, F .35) and that range is Mountain climate: Mountain holds
            // about half of the window (54 %, from 171), still the upper part of the range.
            WorldGenSettings settings = LoadShipped();
            string[] names = settings.Biomes.Names();
            int jungle = System.Array.IndexOf(names, "Jungle"), badlands = System.Array.IndexOf(names, "Badlands");
            int mountain = System.Array.IndexOf(names, "Mountain"), plains = System.Array.IndexOf(names, "Plains");
            int savanna = System.Array.IndexOf(names, "Savanna");
            WorldGenConfig cfg = settings.ToConfig(TestFixtures.Seed);
            RegionBuffers buffers = new(){
                Height = new NativeArray<float>(Size * Size, Allocator.TempJob),
                HardBiome = new NativeArray<int>(Size * Size, Allocator.TempJob)
            };
            try{
                int2 origin = new(-2048, -2048);
                WorldGenRequest request = new(){ Config = cfg, Origin = origin, Size = new int2(Size, Size), Stride = Stride };
                WorldGenApi.SampleRegion(request, ref buffers).Complete();
                float[] height = buffers.Height.ToArray();
                int[] biome = buffers.HardBiome.ToArray();
                float water = cfg.WaterLevel;

                // Heights of one biome's columns in a window (x0, z0, x1, z1 in blocks, both ends included), sorted.
                float[] In(int4 box, int b, out int total){
                    List<float> list = new();
                    total = 0;
                    for (int z = 0; z < Size; z++)
                    for (int x = 0; x < Size; x++){
                        int2 w = origin + new int2(x, z) * Stride;
                        if (w.x < box.x || w.x > box.z || w.y < box.y || w.y > box.w) continue;
                        total++;
                        if (biome[z * Size + x] == b) list.Add(height[z * Size + x]);
                    }

                    list.Sort();
                    return list.ToArray();
                }

                float[] westJungle = In(new int4(-1280, -1152, -960, -576), jungle, out int west);
                Assert.Greater(westJungle.Length, 0, "west window: Jungle is there");
                float p95 = westJungle[(int)(0.95 * westJungle.Length)];
                TestContext.WriteLine($"west Jungle {100.0 * westJungle.Length / west:F1} % of the window, p95 {p95:F0}");
                Assert.Less(westJungle.Length, 0.5 * west, "west window: Jungle share");
                Assert.Less(p95, 182f, "west window: Jungle p95 height");

                Dictionary<(int, int), int> borders = BorderEdges(biome, names.Length);
                int edges = 0;
                foreach (int n in borders.Values) edges += n;
                borders.TryGetValue((math.min(badlands, jungle), math.max(badlands, jungle)), out int meet);
                TestContext.WriteLine($"Badlands–Jungle {100.0 * meet / edges:F2} % of the border edges");
                Assert.Less(meet, 0.01 * edges, "Badlands–Jungle border edges");

                Assert.AreEqual(0, In(new int4(1216, -704, 1344, -576), mountain, out _).Length, "Badlands dome: Mountain columns");
                float[] meadow = In(new int4(896, 640, 1088, 1024), mountain, out int dome);
                TestContext.WriteLine($"Badlands dome with the meadow: Mountain {100.0 * meadow.Length / dome:F1} %" +
                                      (meadow.Length > 0 ? $", from {meadow[0]:F0}" : ""));
                Assert.Less(meadow.Length, 0.65 * dome, "Badlands dome with the meadow: Mountain share");
                if (meadow.Length > 0) Assert.Greater(meadow[0], 165f, "Badlands dome with the meadow: lowest Mountain column");

                int4 tour = new(-1280, -1280, 1280, 1280);
                foreach ((int b, double most) in new[]{ (plains, 0.28), (savanna, 0.32) }){
                    float[] h = In(tour, b, out _);
                    int wet = 0;
                    foreach (float v in h)
                        if (v < water)
                            wet++;
                    TestContext.WriteLine($"{names[b]}: {100.0 * wet / h.Length:F1} % under water");
                    Assert.Less(wet, most * h.Length, $"{names[b]}: share under water");
                }
            }
            finally{
                buffers.Height.Dispose();
                buffers.HardBiome.Dispose();
                cfg.Dispose();
            }
        }

        [Test]
        public void ShippedSettings_BadlandsCarryNoMountainCaps(){
            // DEC-120 (owner report 2026-11-20: snow caps in Badlands, Badlands too common). Badlands and Mountain share
            // the massif climate; with a two-sided height target the summits of a Badlands massif went to Mountain and
            // took its snow (5 such patches in this window, 49 at 8192²). Since DEC-120 Badlands' height target is a
            // floor at Mountain's target, so no summit of a Badlands massif turns Mountain by height alone, and the
            // warmer, drier targets keep Badlands to the hot dry massifs (8.0 % of the world before, 11.6 % of land).
            WorldGenSettings settings = LoadShipped();
            string[] names = settings.Biomes.Names();
            int badlands = System.Array.IndexOf(names, "Badlands"), mountain = System.Array.IndexOf(names, "Mountain");
            WorldGenConfig cfg = settings.ToConfig(TestFixtures.Seed);
            RegionBuffers buffers = new(){
                Height = new NativeArray<float>(Size * Size, Allocator.TempJob),
                HardBiome = new NativeArray<int>(Size * Size, Allocator.TempJob)
            };
            try{
                WorldGenRequest request = new(){
                    Config = cfg, Origin = new int2(-2048, -2048), Size = new int2(Size, Size), Stride = Stride
                };
                WorldGenApi.SampleRegion(request, ref buffers).Complete();
                float[] height = buffers.Height.ToArray();
                int[] biome = buffers.HardBiome.ToArray();

                int land = 0, badlandsLand = 0, high = 0, badlandsHigh = 0;
                for (int i = 0; i < biome.Length; i++){
                    if (height[i] < cfg.WaterLevel) continue;
                    land++;
                    if (biome[i] == badlands) badlandsLand++;
                    if (height[i] < 180f) continue;
                    high++;
                    if (biome[i] == badlands) badlandsHigh++;
                }

                // Mountain patches (4-connected) whose outer ring is mostly Badlands: a cap on a Badlands massif.
                bool[] seen = new bool[biome.Length];
                Stack<int> stack = new();
                HashSet<int> ring = new();
                int caps = 0, capCells = 0;
                for (int start = 0; start < biome.Length; start++){
                    if (seen[start] || biome[start] != mountain) continue;
                    ring.Clear();
                    int size = 0;
                    seen[start] = true;
                    stack.Push(start);
                    while (stack.Count > 0){
                        int i = stack.Pop();
                        size++;
                        int x = i % Size, z = i / Size;
                        if (x > 0) Visit(i - 1);
                        if (x < Size - 1) Visit(i + 1);
                        if (z > 0) Visit(i - Size);
                        if (z < Size - 1) Visit(i + Size);
                    }

                    int onBadlands = 0;
                    foreach (int j in ring)
                        if (biome[j] == badlands)
                            onBadlands++;
                    if (ring.Count == 0 || onBadlands * 2 <= ring.Count) continue;
                    caps++;
                    capCells += size;
                }

                TestContext.WriteLine($"Badlands {100.0 * badlandsLand / land:F1} % of land, {100.0 * badlandsHigh / high:F1} % " +
                                      $"of the ground from 180; Mountain caps on Badlands: {caps} patches, {capCells} cells");
                Assert.AreEqual(0, caps, "Mountain patches mostly surrounded by Badlands");
                Assert.Less(badlandsLand, 0.07 * land, "Badlands share of land");
                Assert.Less(badlandsHigh, 0.3 * high, "Badlands share of the ground from 180");

                void Visit(int j){
                    if (seen[j]) return;
                    if (biome[j] != mountain){
                        ring.Add(j);
                        return;
                    }

                    seen[j] = true;
                    stack.Push(j);
                }
            }
            finally{
                buffers.Height.Dispose();
                buffers.HardBiome.Dispose();
                cfg.Dispose();
            }
        }

        /// <summary>Land cells and the land cells in 4-connected single-biome patches below <see cref="SmallPatchCells" />.</summary>
        private static void PatchShare(int[] height, int[] biome, int waterLevel, out int land, out int small){
            bool[] seen = new bool[height.Length];
            Stack<int> stack = new();
            land = 0;
            small = 0;
            for (int start = 0; start < height.Length; start++){
                if (seen[start] || !IsLand(start)) continue;
                int b = biome[start], size = 0;
                seen[start] = true;
                stack.Push(start);
                while (stack.Count > 0){
                    int i = stack.Pop();
                    size++;
                    int x = i % Size, z = i / Size;
                    if (x > 0) Visit(i - 1);
                    if (x < Size - 1) Visit(i + 1);
                    if (z > 0) Visit(i - Size);
                    if (z < Size - 1) Visit(i + Size);
                }

                land += size;
                if (size < SmallPatchCells) small += size;

                void Visit(int j){
                    if (seen[j] || biome[j] != b || !IsLand(j)) return;
                    seen[j] = true;
                    stack.Push(j);
                }
            }

            bool IsLand(int i){
                return biome[i] >= 0 && height[i] + 1 >= waterLevel;
            }
        }

        /// <summary>4-neighbour edges between different biomes, keyed by the ordered index pair.</summary>
        private static Dictionary<(int, int), int> BorderEdges(int[] biome, int count){
            Dictionary<(int, int), int> edges = new();
            for (int z = 0; z < Size; z++)
            for (int x = 0; x < Size; x++){
                int i = z * Size + x;
                if (x + 1 < Size) Add(biome[i], biome[i + 1]);
                if (z + 1 < Size) Add(biome[i], biome[i + Size]);
            }

            return edges;

            void Add(int a, int b){
                if (a == b || a < 0 || b < 0 || a >= count || b >= count) return;
                (int, int) key = (math.min(a, b), math.max(a, b));
                edges.TryGetValue(key, out int n);
                edges[key] = n + 1;
            }
        }

        private static IEnumerable<string> ListPairs(Dictionary<(int, int), int> edges, string[] names){
            List<KeyValuePair<(int, int), int>> list = new(edges);
            list.Sort((a, b) => b.Value.CompareTo(a.Value));
            foreach (KeyValuePair<(int, int), int> kv in list)
                yield return $"{names[kv.Key.Item1]}–{names[kv.Key.Item2]} {kv.Value}";
        }
    }
}
