using System.Collections.Generic;
using Engine.Editor.WorldGen;
using Engine.Scripts.VoxelConfig.Data.Generation;
using Engine.Scripts.WorldGen.Settings;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;

namespace Engine.Tests.EditMode{
    /// <summary>GEN-24: boundary lengths between biome pairs and the implausible-neighbour flags.</summary>
    public class BiomeBoundaryStatsTests{
        private readonly List<Object> _created = new();
        private BiomeSet _set;

        [SetUp]
        public void SetUp(){
            _set = Create<BiomeSet>("Set");
            foreach (string n in new[]{ "Plains", "Desert", "Snow" }) _set.Biomes.Add(Create<Biome>(n));
        }

        [TearDown]
        public void TearDown(){
            foreach (Object o in _created) Object.DestroyImmediate(o);
            _created.Clear();
        }

        private T Create<T>(string name) where T : ScriptableObject{
            T so = ScriptableObject.CreateInstance<T>();
            so.name = name;
            _created.Add(so);
            return so;
        }

        private static List<BiomeBoundaryStats.PairLength> Run(int[] map, int size, int stride, BiomeSet set){
            using NativeArray<int> biomes = new(map, Allocator.TempJob);
            using NativeArray<int> edges = new(BiomeBoundaryStats.Side * BiomeBoundaryStats.Side, Allocator.TempJob);
            BiomeBoundaryStats.Schedule(biomes, size, edges, default).Complete();
            return BiomeBoundaryStats.Summarize(edges, stride, set);
        }

        [Test]
        public void VerticalSplit_CountsOneEdgePerRow_TimesStride(){
            // 4×4: left half Plains (0), right half Desert (1) → one boundary edge per row.
            int[] map = new int[16];
            for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++)
                map[y * 4 + x] = x < 2 ? 0 : 1;
            List<BiomeBoundaryStats.PairLength> pairs = Run(map, 4, 8, _set);
            Assert.AreEqual(1, pairs.Count);
            Assert.AreEqual(0, pairs[0].A);
            Assert.AreEqual(1, pairs[0].B);
            Assert.AreEqual("Plains", pairs[0].NameA);
            Assert.AreEqual("Desert", pairs[0].NameB);
            Assert.AreEqual(4 * 8, pairs[0].Length);
            Assert.AreEqual(1f, pairs[0].Share, 1e-6f);
            Assert.IsFalse(pairs[0].Implausible);
        }

        [Test]
        public void PairsAreUnordered_SortedLongestFirst_AndIgnoreOutOfRange(){
            // Row 0: 0 1 0 2 → (0,1)×2, (0,2)×1; row 1 all -1 (no biome) → vertical edges ignored.
            int[] map ={ 0, 1, 0, 2, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1 };
            List<BiomeBoundaryStats.PairLength> pairs = Run(map, 4, 1, _set);
            Assert.AreEqual(2, pairs.Count);
            Assert.AreEqual((0, 1, 2L), (pairs[0].A, pairs[0].B, pairs[0].Length));
            Assert.AreEqual((0, 2, 1L), (pairs[1].A, pairs[1].B, pairs[1].Length));
            Assert.AreEqual(2f / 3f, pairs[0].Share, 1e-6f);
        }

        [Test]
        public void ImplausiblePair_IsFlagged_InEitherOrder(){
            _set.ImplausibleNeighbours.Add(new BiomePair{ First = _set.Biomes[2], Second = _set.Biomes[1] });
            Assert.IsTrue(_set.IsImplausible(1, 2));
            Assert.IsTrue(_set.IsImplausible(2, 1));
            Assert.IsFalse(_set.IsImplausible(0, 1));
            Assert.IsFalse(_set.IsImplausible(1, 1));

            int[] map ={ 1, 2, 0, 0 }; // 2×2: Desert|Snow over Plains|Plains
            List<BiomeBoundaryStats.PairLength> pairs = Run(map, 2, 1, _set);
            Assert.IsTrue(pairs.Exists(p => p.A == 1 && p.B == 2 && p.Implausible));
            Assert.IsTrue(pairs.Exists(p => p.A == 0 && p.B == 1 && !p.Implausible));
        }

        [Test]
        public void PairWithInactiveOrEmptyBiome_NeverMatches(){
            _set.ImplausibleNeighbours.Add(new BiomePair{ First = _set.Biomes[1], Second = Create<Biome>("Jungle") });
            _set.ImplausibleNeighbours.Add(new BiomePair{ First = _set.Biomes[1], Second = null });
            _set.ImplausibleNeighbours.Add(null);
            for (int a = 0; a < 3; a++)
            for (int b = 0; b < 3; b++)
                Assert.IsFalse(_set.IsImplausible(a, b));
        }

        [Test]
        public void DefaultBiomeSet_FlagsDesertSnow(){
            BiomeSet asset = UnityEditor.AssetDatabase.LoadAssetAtPath<BiomeSet>("Assets/EngineSettings/BiomeSet_Default.asset");
            Assert.IsNotNull(asset);
            int desert = System.Array.IndexOf(asset.Names(), "Desert");
            int snow = System.Array.IndexOf(asset.Names(), "Snow");
            Assert.GreaterOrEqual(desert, 0);
            Assert.GreaterOrEqual(snow, 0);
            Assert.IsTrue(asset.IsImplausible(desert, snow));
        }
    }
}
