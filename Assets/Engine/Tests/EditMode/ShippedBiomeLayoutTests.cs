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
            // Snow and ice stay in the biomes that want them (DEC-65); a cold Plains column stays green.
            string[] cold = { "Mountain", "Snow" };
            List<Biome> biomes = LoadShipped().Biomes.Biomes;
            foreach (Biome biome in biomes){
                Assert.IsNotNull(biome, "BiomeSet entry");
                bool wanted = System.Array.IndexOf(cold, biome.name) >= 0;
                Assert.AreEqual(wanted, biome.FreezesWater, $"{biome.name}: FreezesWater");
                if (!wanted) Assert.IsNull(biome.SnowTopBlock, $"{biome.name}: SnowTopBlock");
            }

            // Mountain keeps its snow line (GEN-35); the Snow biome is white through its own top block.
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
