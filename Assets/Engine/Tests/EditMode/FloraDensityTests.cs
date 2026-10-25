using System.Linq;
using Engine.Scripts.VoxelConfig.Data.Generation;
using Engine.Scripts.WorldGen.Blending;
using Engine.Scripts.WorldGen.Core;
using Engine.Scripts.WorldGen.Flora;
using Engine.Scripts.WorldGen.Settings;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;

namespace Engine.Tests.EditMode{
    /// <summary>
    ///     Flora density noises: a rule with full influence keeps its long-run density over 4096² columns but clusters
    ///     its plants, the switch on the settings gives exactly the even placement, column probabilities above 1 are
    ///     scaled down, and the multi-block pre-filter stays a necessary condition.
    /// </summary>
    public class FloraDensityTests{
        private FloraFixture _fixture;
        private WorldGenConfig _cfg;

        [SetUp]
        public void SetUp(){
            _fixture = new FloraFixture();
        }

        [TearDown]
        public void TearDown(){
            _cfg.Dispose();
            _cfg = default;
            _fixture.Dispose();
        }

        private static FloraRule Dense(FloraRule rule, FloraDensity noise, float influence = 1f){
            rule.DensityNoise = noise;
            rule.DensityInfluence = influence;
            return rule;
        }

        /// <summary>One flat biome high above the water, so every column is ground.</summary>
        private Biome Flat(params FloraRule[] rules){
            return _fixture.MakeBiome("Flat", .5f, 150f, 2f, _fixture.GrassSo, rules);
        }

        private WorldGenSettings Settings(params Biome[] biomes){
            return _fixture.Settings(GenArchitecture.BiomeFirst, BlendMethod.None, biomes);
        }

        private ushort[] Species(int2 origin, int2 size){
            RegionBuffers b = new(){ FloraSpecies = new NativeArray<ushort>(size.x * size.y, Allocator.TempJob) };
            try{
                WorldGenApi.SampleRegion(new WorldGenRequest{ Config = _cfg, Origin = origin, Size = size, Stride = 1 }, ref b).Complete();
                return b.FloraSpecies.ToArray();
            }
            finally{
                b.FloraSpecies.Dispose();
            }
        }

        [Test]
        public void FullInfluence_KeepsTheMeanDensityOver4096Squared(){
            // One rule per density noise, k = 1: p' stays below 1 everywhere, so each rule's mean is p within ±5 %.
            Biome flat = Flat(Dense(FloraFixture.Rule(_fixture.TuftSo, .1f), FloraDensity.Trees),
                Dense(FloraFixture.Rule(_fixture.FlowerSo, .1f), FloraDensity.Undergrowth),
                Dense(FloraFixture.Rule(_fixture.GlassSo, .1f), FloraDensity.Flowers),
                Dense(FloraFixture.Rule(_fixture.GraniteSo, .1f), FloraDensity.GroundDetails));
            _cfg = _fixture.ToConfig(Settings(flat));
            Assert.AreEqual(1, _cfg.Biomes[0].floraDensity);
            const int n = 4096;
            ushort[] species = Species(new int2(-n / 2, -n / 2), new int2(n, n));
            int[] counts = new int[5];
            foreach (ushort s in species) counts[s]++;
            for (int id = 1; id <= 4; id++){
                float mean = counts[id] / (float)species.Length;
                TestContext.WriteLine($"{(FloraDensity)(id - 1)}: {mean:F5}");
                Assert.AreEqual(.1f, mean, .005f, ((FloraDensity)(id - 1)).ToString());
            }
        }

        [Test]
        public void FullInfluence_ClustersThePlants(){
            // Counts per 16 × 16 cell vary far more than the binomial spread of the even placement.
            float Spread(float influence){
                Biome flat = Flat(Dense(FloraFixture.Rule(_fixture.TuftSo, .2f), FloraDensity.Undergrowth, influence));
                _cfg.Dispose();
                _cfg = _fixture.ToConfig(Settings(flat));
                const int n = 1024, cell = 16;
                ushort[] species = Species(int2.zero, new int2(n, n));
                float[] cells = new float[n / cell * (n / cell)];
                for (int i = 0; i < species.Length; i++)
                    if (species[i] != 0)
                        cells[i / n / cell * (n / cell) + i % n / cell]++;
                float mean = cells.Average();
                return math.sqrt(cells.Select(c => (c - mean) * (c - mean)).Average()) / mean;
            }

            float even = Spread(0f), dense = Spread(1f);
            TestContext.WriteLine($"coefficient of variation: even {even:F3}, dense {dense:F3}");
            Assert.Greater(dense, 3f * even);
        }

        [Test]
        public void SwitchOff_PlacesLikeZeroInfluence(
            [Values(GenArchitecture.TerrainFirst, GenArchitecture.BiomeFirst)] GenArchitecture architecture){
            Biome[] biomes = _fixture.DefaultBiomes();
            WorldGenSettings even = _fixture.Settings(architecture, BlendMethod.DistanceBlend, biomes);
            _cfg = _fixture.ToConfig(even);
            ushort[] expected = Species(new int2(-300, 120), new int2(256, 256));
            _cfg.Dispose();

            foreach (FloraRule rule in biomes.SelectMany(b => b.FloraRules)) Dense(rule, FloraDensity.Trees);
            WorldGenSettings off = _fixture.Settings(architecture, BlendMethod.DistanceBlend, biomes);
            off.FloraDensityNoise = false;
            _cfg = _fixture.ToConfig(off);
            for (int b = 0; b < _cfg.BiomeCount; b++) Assert.AreEqual(0, _cfg.Biomes[b].floraDensity);
            CollectionAssert.AreEqual(expected, Species(new int2(-300, 120), new int2(256, 256)));
            _cfg.Dispose();

            off.FloraDensityNoise = true;
            _cfg = _fixture.ToConfig(off);
            CollectionAssert.AreNotEqual(expected, Species(new int2(-300, 120), new int2(256, 256)), "the switch on changes the placement");
        }

        [Test]
        public void ColumnProbabilitiesAboveOne_AreScaledToOne(){
            // p' up to 1.6 + .8: where both noises are high every column carries a plant, in the ratio of the p'.
            Biome flat = Flat(Dense(FloraFixture.Rule(_fixture.TuftSo, .8f), FloraDensity.Undergrowth),
                Dense(FloraFixture.Rule(_fixture.FlowerSo, .4f), FloraDensity.Undergrowth));
            _cfg = _fixture.ToConfig(Settings(flat));
            int full = 0;
            for (int x = 0; x < 512; x++)
            for (int z = 0; z < 512; z++){
                float m = FloraPlacement.DensityFactor(x, z, (int)FloraDensity.Undergrowth, _cfg);
                if (m < 1.5f) continue;
                full++;
                WorldSampler.SampleColumn(x, z, _cfg, out ColumnSample s);
                Assert.AreNotEqual(0, s.FloraSpecies, $"({x},{z}) m={m}");
                float u = Hash.Hash01(x, z, _cfg.Seed, HashSalts.FloraSpecies);
                Assert.AreEqual(u < 2f / 3f ? 1 : 2, s.FloraSpecies, $"({x},{z}) u={u}");
            }

            Assert.Greater(full, 1000, "columns with a high noise factor");
        }

        [Test]
        public void PreFilter_HoldsEveryMultiBlockCandidate(){
            // Dense rules on every noise, probabilities that exceed 1 where the noises are high.
            Biome[] biomes = _fixture.DefaultBiomes();
            biomes[0].FloraRules[0].Probability = .7f;
            FloraDensity[] noises ={ FloraDensity.Undergrowth, FloraDensity.Flowers, FloraDensity.Trees };
            foreach (Biome b in biomes)
                for (int r = 0; r < b.FloraRules.Count; r++)
                    Dense(b.FloraRules[r], noises[r % 3]);
            _cfg = _fixture.ToConfig(_fixture.Settings(GenArchitecture.BiomeFirst, BlendMethod.DistanceBlend, biomes));
            BlendCache noCache = default;
            int candidates = 0;
            for (int x = -1200; x < -1200 + 256; x++)
            for (int z = -400; z < -400 + 256; z++){
                WorldSampler.SampleCore(x, z, _cfg, noCache, out ColumnSample s);
                int rule = FloraPlacement.RollRule(x, z, _cfg, s, out int _);
                if (!FloraPlacement.IsMultiBlock(rule, _cfg)) continue;
                candidates++;
                Assert.IsTrue(FloraPlacement.MayBeMultiBlock(x, z, _cfg), $"({x},{z})");
            }

            Assert.Greater(candidates, 500);
        }

        [Test]
        public void DensityFactor_SpansZeroToTwoWithMeanOne(){
            Biome flat = Flat(Dense(FloraFixture.Rule(_fixture.TuftSo, .1f), FloraDensity.Flowers));
            _cfg = _fixture.ToConfig(Settings(flat));
            float sum = 0f, min = float.MaxValue, max = float.MinValue;
            const int n = 512;
            for (int x = 0; x < n; x++)
            for (int z = 0; z < n; z++){
                float m = FloraPlacement.DensityFactor(x, z, (int)FloraDensity.Flowers, _cfg);
                sum += m;
                min = math.min(min, m);
                max = math.max(max, m);
            }

            Assert.AreEqual(0f, min, "clearings");
            Assert.AreEqual(2f, max, "groves");
            Assert.AreEqual(1f, sum / (n * n), .03f, "mean");
        }
    }
}
