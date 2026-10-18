using System;
using System.Collections.Generic;
using Engine.Scripts.Settings;
using Engine.Scripts.VoxelConfig.Data.Generation;
using Engine.Scripts.WorldGen.Classify;
using Engine.Scripts.WorldGen.Core;
using Engine.Scripts.WorldGen.Noise;
using Engine.Scripts.WorldGen.Settings;
using NUnit.Framework;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using BlendWeights = Engine.Scripts.WorldGen.Blending.BlendWeights;
using Object = UnityEngine.Object;

namespace Engine.Tests.EditMode{
    /// <summary>GEN-01/03/17/23, TST-09: sampler skeleton, classifier, blend weights and the region API.</summary>
    public class WorldSamplerTests{
        private readonly List<Object> _created = new();
        private readonly List<IDisposable> _native = new();

        [TearDown]
        public void TearDown(){
            foreach (IDisposable d in _native) d.Dispose();
            _native.Clear();
            foreach (Object o in _created) Object.DestroyImmediate(o);
            _created.Clear();
        }

        private T Create<T>(string name) where T : ScriptableObject{
            T so = ScriptableObject.CreateInstance<T>();
            so.name = name;
            _created.Add(so);
            return so;
        }

        private Biome Biome(string name, float t, float f, float c, float e, float pv, float offset, float amp, float[] weights, float j){
            Biome b = Create<Biome>(name);
            b.TargetTemperature = t;
            b.TargetHumidity = f;
            b.TargetContinental = c;
            b.TargetErosion = e;
            b.TargetPV = pv;
            b.HeightOffset = offset;
            b.HeightAmplitude = amp;
            b.OctaveWeights = weights;
            b.Jaggedness = j;
            return b;
        }

        /// <summary>The six §19.2 placeholder biomes.</summary>
        private Biome[] PlaceholderBiomes(){
            return new[]{
                Biome("Plains", .55f, .45f, .45f, .70f, -0.2f, 136, 6, new[]{ 1f, .6f, .3f, .12f, .05f, .02f }, 0),
                Biome("Desert", .90f, .10f, .45f, .60f, 0f, 134, 8, new[]{ 1f, .8f, .2f, .05f, .02f, .01f }, 0),
                Biome("Mountain", .35f, .40f, .80f, .15f, 0.6f, 165, 22, new[]{ .7f, .7f, .6f, .45f, .3f, .15f }, 35),
                Biome("Snow", .15f, .50f, .70f, .35f, 0.3f, 158, 14, new[]{ .8f, .6f, .45f, .3f, .15f, .08f }, 15),
                Biome("Forest", .50f, .70f, .50f, .55f, 0f, 140, 9, new[]{ 1f, .6f, .35f, .15f, .06f, .03f }, 0),
                Biome("Ocean", .50f, .80f, .08f, .50f, 0f, 100, 6, new[]{ 1f, .5f, .2f, .05f, .02f, .01f }, 0)
            };
        }

        private WorldGenConfig Config(int seed, params Biome[] biomes){
            BiomeSet set = Create<BiomeSet>("Set");
            set.Biomes.AddRange(biomes);
            WorldGenSettings s = Create<WorldGenSettings>("WG");
            s.Biomes = set;
            NoiseSettings noise = Create<NoiseSettings>("Noise");
            WorldGenConfig cfg = s.ToConfig(seed, noise);
            _native.Add(cfg);
            return cfg;
        }

        private NativeArray<T> Alloc<T>(int count) where T : unmanaged{
            NativeArray<T> a = new(count, Allocator.Persistent);
            _native.Add(a);
            return a;
        }

        // --- BlendWeights (BLD-02) ------------------------------------------------------------------------------------

        [Test]
        public void BlendWeights_IndexerCoversAllSixteenSlots(){
            BlendWeights w = default;
            for (int i = 0; i < BlendWeights.Capacity; i++) w[i] = i + 1;
            for (int i = 0; i < BlendWeights.Capacity; i++) Assert.AreEqual(i + 1, w[i]);
            Assert.AreEqual(136f, w.Sum());
            Assert.AreEqual(15, w.ArgMax());
            w.Normalize();
            Assert.AreEqual(1f, w.Sum(), 1e-5f);
        }

        [Test]
        public void BlendWeights_OneHotAndArgMaxTieBreakToLowerIndex(){
            BlendWeights oneHot = BlendWeights.OneHot(5);
            Assert.AreEqual(1f, oneHot[5]);
            Assert.AreEqual(1f, oneHot.Sum());
            Assert.AreEqual(5, oneHot.ArgMax());

            BlendWeights tie = default;
            tie[2] = 0.5f;
            tie[7] = 0.5f;
            Assert.AreEqual(2, tie.ArgMax(), "ties break to the lower index (GEN-23)");
            Assert.AreEqual(0f, BlendWeights.OneHot(-1).Sum(), "no biome → zero vector");
        }

        // --- Classifier (§6.4, GEN-23) --------------------------------------------------------------------------------

        [Test]
        public void Classifier_PicksTheBiomeAtTheFieldsAndBreaksTiesLow(){
            WorldGenConfig cfg = Config(1, PlaceholderBiomes());
            for (int b = 0; b < cfg.BiomeCount; b++){
                Biome.BiomeDef def = cfg.Biomes[b];
                WorldFields f = new(){ T = def.targetTemperature, F = def.targetHumidity, C = def.targetContinental, E = def.targetErosion, PV = def.targetPV };
                Assert.AreEqual(b, BiomeClassifier.Classify(f, cfg), $"fields exactly at biome {b}");
                Assert.AreEqual(0f, BiomeClassifier.DistanceSquared(f, def, cfg));
            }

            Biome a = Biome("A", .5f, .5f, .5f, .5f, 0f, 100, 1, new[]{ 1f }, 0);
            Biome twin = Biome("B", .5f, .5f, .5f, .5f, 0f, 100, 1, new[]{ 1f }, 0);
            WorldGenConfig tieCfg = Config(1, a, twin);
            Assert.AreEqual(0, BiomeClassifier.Classify(new WorldFields{ T = .2f, F = .9f, C = .1f, E = .4f, PV = .7f }, tieCfg));

            Assert.AreEqual(-1, BiomeClassifier.Classify(default, Config(1)), "empty set");
        }

        [Test]
        public void Classifier_WeightsAndPvNormalisationFollowTheSpec(){
            Biome b = Biome("B", .5f, .5f, .5f, .5f, 0f, 100, 1, new[]{ 1f }, 0);
            WorldGenConfig cfg = Config(1, b);
            WorldFields f = new(){ T = .6f, F = .7f, C = .9f, E = .4f, PV = 1f };
            // λ: T 1, F 1, C 2, E 1, PV 0.5; PV enters as (PV+1)/2 → 1 vs 0.5
            float expected = 1f * .1f * .1f + 1f * .2f * .2f + 2f * .4f * .4f + 1f * .1f * .1f + .5f * .5f * .5f;
            Assert.AreEqual(expected, BiomeClassifier.DistanceSquared(f, cfg.Biomes[0], cfg), 1e-6f);
            Assert.AreEqual(expected + 1f * .3f * .3f, BiomeClassifier.DistanceSquared(f, .8f, cfg.Biomes[0], cfg), 1e-6f, "6D adds λh (h − 0.5)²");
        }

        // --- Sampler (GEN-01, TST-09) ---------------------------------------------------------------------------------

        [Test]
        public void SampleColumn_WeightsAreOneHotAndNormalised(){
            WorldGenConfig cfg = Config(1337, PlaceholderBiomes());
            Unity.Mathematics.Random rng = new(7);
            for (int n = 0; n < 1000; n++){
                int2 p = rng.NextInt2(-20000, 20000);
                ColumnSample s = WorldSampler.SampleColumn(p.x, p.y, cfg);
                Assert.That(s.HardBiome, Is.InRange(0, cfg.BiomeCount - 1));
                Assert.AreEqual(1f, s.MaterialWeights.Sum(cfg.BiomeCount), 1e-5f, "TST-09 Σ w = 1");
                Assert.AreEqual(1f, s.HeightWeights.Sum(cfg.BiomeCount), 1e-5f);
                for (int b = 0; b < cfg.BiomeCount; b++) Assert.GreaterOrEqual(s.MaterialWeights[b], 0f);
                Assert.AreEqual(1f, s.MaterialWeights[s.HardBiome]);
                Assert.AreEqual(s.HardBiome, s.DominantMaterialBiome);
                Assert.AreEqual(s.HardBiome, s.SurfaceSource);
                Assert.That(s.Height, Is.InRange(cfg.HeightMin, cfg.HeightMax));
                Assert.AreEqual((int)math.floor(s.Height), s.ColumnHeight);
                Assert.AreEqual(0, s.FloraSpecies);
                Assert.AreEqual(-1, s.FloraSource);
            }
        }

        [Test]
        public void SampleColumn_HeightFollowsTheCommonFormula(){
            Biome b = Biome("Mountain", .5f, .5f, .5f, .5f, 0f, 165f, 22f, new[]{ .7f, .7f, .6f, .45f, .3f, .15f }, 35f);
            WorldGenConfig cfg = Config(99, b);
            int2[] points ={ new(0, 0), new(1234, -5678), new(-77, 4096), new(100000, 100000) };
            foreach (int2 p in points){
                float expected = 165f;
                for (int i = 0; i < 6; i++)
                    expected += 22f * b.OctaveWeights[i] * DetailOctaves.Sample(i, p, 512f / (1 << i), 99u);
                expected += 35f * PeakNoise.Sample(p, 192f, 99u);
                bool expectClamped = expected < 40f || expected > 250f;
                expected = math.clamp(expected, 40f, 250f);

                ColumnSample s = WorldSampler.SampleColumn(p.x, p.y, cfg);
                Assert.AreEqual(expected, s.Height, 1e-4f, $"H at {p}");
                Assert.AreEqual(expectClamped, s.Clamped);
            }
        }

        [Test]
        public void SampleColumn_IsDeterministicAndSeedDependent(){
            WorldGenConfig a = Config(1337, PlaceholderBiomes());
            WorldGenConfig b = Config(1338, PlaceholderBiomes());
            int differentHeights = 0;
            for (int n = 0; n < 200; n++){
                int x = n * 97 - 5000, z = n * 131 + 300;
                ColumnSample s1 = WorldSampler.SampleColumn(x, z, a);
                ColumnSample s2 = WorldSampler.SampleColumn(x, z, a);
                Assert.AreEqual(s1.Height, s2.Height);
                Assert.AreEqual(s1.HardBiome, s2.HardBiome);
                Assert.AreEqual(s1.Fields.PV, s2.Fields.PV);
                if (WorldSampler.SampleColumn(x, z, b).Height != s1.Height) differentHeights++;
            }

            Assert.Greater(differentHeights, 190, "a different seed gives different terrain");
        }

        // --- Region API (GEN-17) ---------------------------------------------------------------------------------------

        [Test]
        public void SampleRegion_MatchesSampleColumnForEveryBuffer(){
            WorldGenConfig cfg = Config(1337, PlaceholderBiomes());
            WorldGenRequest req = new(){ Config = cfg, Origin = new int2(-37, 1200), Size = new int2(24, 17) };
            int count = req.ColumnCount;
            RegionBuffers buffers = new(){
                Fields = Alloc<WorldFields>(count),
                Height = Alloc<float>(count),
                ColumnHeight = Alloc<int>(count),
                HardBiome = Alloc<int>(count),
                DominantMaterialBiome = Alloc<int>(count),
                MaterialWeights = Alloc<BlendWeights>(count),
                HeightWeights = Alloc<BlendWeights>(count),
                SurfaceSource = Alloc<int>(count),
                FloraSpecies = Alloc<ushort>(count),
                FloraSource = Alloc<int>(count),
                Clamped = Alloc<byte>(count)
            };

            WorldGenApi.SampleRegion(req, ref buffers).Complete();

            for (int j = 0; j < req.Size.y; j++)
            for (int i = 0; i < req.Size.x; i++){
                int index = j * req.Size.x + i;
                ColumnSample s = WorldSampler.SampleColumn(req.Origin.x + i, req.Origin.y + j, cfg);
                Assert.AreEqual(s.Fields.T, buffers.Fields[index].T);
                Assert.AreEqual(s.Fields.PV, buffers.Fields[index].PV);
                Assert.AreEqual(s.Height, buffers.Height[index], $"height at {index}");
                Assert.AreEqual(s.ColumnHeight, buffers.ColumnHeight[index]);
                Assert.AreEqual(s.HardBiome, buffers.HardBiome[index]);
                Assert.AreEqual(s.DominantMaterialBiome, buffers.DominantMaterialBiome[index]);
                Assert.AreEqual(s.MaterialWeights.Sum(), buffers.MaterialWeights[index].Sum());
                Assert.AreEqual(s.MaterialWeights.ArgMax(), buffers.MaterialWeights[index].ArgMax());
                Assert.AreEqual(s.HeightWeights.ArgMax(), buffers.HeightWeights[index].ArgMax());
                Assert.AreEqual(s.SurfaceSource, buffers.SurfaceSource[index]);
                Assert.AreEqual(s.FloraSpecies, buffers.FloraSpecies[index]);
                Assert.AreEqual(s.FloraSource, buffers.FloraSource[index]);
                Assert.AreEqual(s.Clamped ? 1 : 0, buffers.Clamped[index]);
            }
        }

        [Test]
        public void SampleRegion_FillsOnlyRequestedBuffersAndLeavesOthersUnallocated(){
            WorldGenConfig cfg = Config(1337, PlaceholderBiomes());
            WorldGenRequest req = new(){ Config = cfg, Origin = new int2(512, -256), Size = new int2(8, 8) };
            RegionBuffers buffers = new(){ Height = Alloc<float>(req.ColumnCount) };

            JobHandle handle = WorldGenApi.SampleRegion(req, ref buffers);
            handle.Complete();

            Assert.IsFalse(buffers.Fields.IsCreated);
            Assert.IsFalse(buffers.HardBiome.IsCreated);
            Assert.IsFalse(buffers.MaterialWeights.IsCreated);
            Assert.AreEqual(WorldSampler.SampleColumn(512 + 3, -256 + 5, cfg).Height, buffers.Height[5 * 8 + 3]);
        }

        [Test]
        public void SampleRegion_IsAPureFunctionOfWorldCoordinates(){
            // GEN-03/R3: the same column gives the same result regardless of the region it is sampled in.
            WorldGenConfig cfg = Config(4242, PlaceholderBiomes());
            WorldGenRequest a = new(){ Config = cfg, Origin = new int2(0, 0), Size = new int2(32, 32) };
            WorldGenRequest b = new(){ Config = cfg, Origin = new int2(-5, 7), Size = new int2(40, 30) };
            RegionBuffers ba = new(){ Height = Alloc<float>(a.ColumnCount), HardBiome = Alloc<int>(a.ColumnCount) };
            RegionBuffers bb = new(){ Height = Alloc<float>(b.ColumnCount), HardBiome = Alloc<int>(b.ColumnCount) };
            JobHandle ha = WorldGenApi.SampleRegion(a, ref ba);
            JobHandle hb = WorldGenApi.SampleRegion(b, ref bb);
            JobHandle.CombineDependencies(ha, hb).Complete();

            for (int z = 7; z < 32; z++)
            for (int x = 0; x < 32; x++){
                int ia = z * 32 + x;
                int ib = (z - 7) * 40 + (x + 5);
                Assert.AreEqual(ba.Height[ia], bb.Height[ib], $"column ({x},{z})");
                Assert.AreEqual(ba.HardBiome[ia], bb.HardBiome[ib]);
            }
        }

        [Test]
        public void SampleRegion_RejectsShortBuffersAndEmptySizes(){
            WorldGenConfig cfg = Config(1, PlaceholderBiomes());
            WorldGenRequest req = new(){ Config = cfg, Origin = int2.zero, Size = new int2(4, 4) };
            RegionBuffers tooShort = new(){ Height = Alloc<float>(15) };
            Assert.Throws<ArgumentException>(() => WorldGenApi.SampleRegion(req, ref tooShort));

            WorldGenRequest empty = new(){ Config = cfg, Origin = int2.zero, Size = new int2(0, 4) };
            RegionBuffers ok = new(){ Height = Alloc<float>(16) };
            Assert.Throws<ArgumentException>(() => WorldGenApi.SampleRegion(empty, ref ok));

            WorldGenRequest noConfig = new(){ Config = default, Origin = int2.zero, Size = new int2(4, 4) };
            Assert.Throws<ArgumentException>(() => WorldGenApi.SampleRegion(noConfig, ref ok));
        }
    }
}
