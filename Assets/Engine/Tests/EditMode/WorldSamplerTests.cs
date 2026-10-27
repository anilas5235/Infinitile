using System;
using System.Collections.Generic;
using Engine.Scripts.Settings;
using Engine.Scripts.VoxelConfig.Data.Generation;
using Engine.Scripts.WorldGen.Classify;
using Engine.Scripts.WorldGen.Core;
using Engine.Scripts.WorldGen.Height;
using Engine.Scripts.WorldGen.Noise;
using Engine.Scripts.WorldGen.Settings;
using NUnit.Framework;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using BlendWeights = Engine.Scripts.WorldGen.Blending.BlendWeights;
using Object = UnityEngine.Object;

namespace Engine.Tests.EditMode{
    /// <summary>Sampler skeleton, classifier, blend weights and the region API.</summary>
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

        /// <summary>The six placeholder biomes.</summary>
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
            return Config(seed, GenArchitecture.BiomeFirst, biomes);
        }

        /// <summary>Config for <paramref name="architecture" />; Terrain-first gets the placeholder splines.</summary>
        private WorldGenConfig Config(int seed, GenArchitecture architecture, params Biome[] biomes){
            return Config(seed, architecture, BlendMethod.None, biomes);
        }

        /// <summary>Config with <paramref name="blend" /> for the material and the height stage.</summary>
        private WorldGenConfig Config(int seed, GenArchitecture architecture, BlendMethod blend, params Biome[] biomes){
            BiomeSet set = Create<BiomeSet>("Set");
            set.Biomes.AddRange(biomes);
            WorldGenSettings s = Create<WorldGenSettings>("WG");
            s.Biomes = set;
            s.Architecture = architecture;
            s.MaterialMethod = blend;
            s.HeightMethod = blend;
            if (architecture == GenArchitecture.TerrainFirst){
                s.Splines = Create<TerrainSplineSet>("Splines");
                s.Splines.ResetToPlaceholders();
            }

            WorldGenConfig cfg = s.ToConfig(seed);
            _native.Add(cfg);
            return cfg;
        }

        private delegate void SampleColumnFn(int x, int z, in WorldGenConfig cfg, out ColumnSample sample);

        private delegate void BiomeHeightFn(in float2 p, in BlendWeights weights, in WorldGenConfig cfg,
            out float height, out float unclamped);

        /// <summary>
        ///     A Burst compile error in a direct-call entry point (struct return, bool in the signature) is only
        ///     logged at domain reload and disables Burst for every job in the Editor. Compiling the entry point as
        ///     a function pointer surfaces such an error as a test failure.
        /// </summary>
        [Test]
        public void SampleColumn_CompilesWithBurst(){
            Assert.IsTrue(BurstCompiler.IsEnabled, "Burst compilation is disabled in the Editor (Jobs > Burst)");
            FunctionPointer<SampleColumnFn> fp = BurstCompiler.CompileFunctionPointer<SampleColumnFn>(WorldSampler.SampleColumn);
            Assert.IsTrue(fp.IsCreated);

            WorldGenConfig cfg = Config(5, PlaceholderBiomes());
            fp.Invoke(17, -23, cfg, out ColumnSample viaPointer);
            ColumnSample viaDirectCall = WorldSampler.SampleColumn(17, -23, cfg);
            Assert.AreEqual(viaDirectCall.Height, viaPointer.Height);
            Assert.AreEqual(viaDirectCall.HardBiome, viaPointer.HardBiome);
            Assert.AreEqual(viaDirectCall.Fields.T, viaPointer.Fields.T);

            WorldGenConfig terrainFirst = Config(5, GenArchitecture.TerrainFirst, PlaceholderBiomes());
            fp.Invoke(17, -23, terrainFirst, out ColumnSample tfPointer);
            ColumnSample tfDirect = WorldSampler.SampleColumn(17, -23, terrainFirst);
            Assert.AreEqual(tfDirect.Height, tfPointer.Height);
            Assert.AreEqual(tfDirect.TerrainParams, tfPointer.TerrainParams);
            Assert.AreEqual(tfDirect.HardBiome, tfPointer.HardBiome);
        }

        [Test]
        public void BiomeFirstHeight_CompilesWithBurst(){
            // Same guard for the second direct-call entry point (the calibration helper and tests call it).
            Assert.IsTrue(BurstCompiler.IsEnabled, "Burst compilation is disabled in the Editor (Jobs > Burst)");
            FunctionPointer<BiomeHeightFn> fp = BurstCompiler.CompileFunctionPointer<BiomeHeightFn>(BiomeFirstHeight.Sample);
            Assert.IsTrue(fp.IsCreated);

            WorldGenConfig cfg = Config(5, PlaceholderBiomes());
            BlendWeights w = default;
            w[0] = 0.25f;
            w[2] = 0.75f;
            float2 p = new(321f, -77f);
            fp.Invoke(p, w, cfg, out float hPointer, out float uPointer);
            BiomeFirstHeight.Sample(p, w, cfg, out float hDirect, out float uDirect);
            Assert.AreEqual(hDirect, hPointer);
            Assert.AreEqual(uDirect, uPointer);
        }

        [Test]
        public void UnblendedHeight_IsTheHeightWithoutTheSpatialBlend(){
            // Terrain-first has no height blend, so the unblended height is the height; Biome-first takes the hard
            // biome alone, which is the height of the same world without blending and differs where the blend acts.
            Biome[] biomes = PlaceholderBiomes();
            WorldGenConfig tf = Config(7, GenArchitecture.TerrainFirst, BlendMethod.DistanceBlend, biomes);
            WorldGenConfig bf = Config(7, GenArchitecture.BiomeFirst, BlendMethod.DistanceBlend, biomes);
            WorldGenConfig bfNone = Config(7, GenArchitecture.BiomeFirst, BlendMethod.None, biomes);
            int differ = 0;
            for (int x = -1024; x < 1024; x += 37)
            for (int z = -1024; z < 1024; z += 41){
                WorldSampler.SampleUnblendedHeight(x, z, tf, out float tfUnblended);
                WorldSampler.SampleHeight(x, z, tf, default, out float tfHeight);
                Assert.AreEqual(tfHeight, tfUnblended, $"Terrain-first at ({x}, {z})");
                WorldSampler.SampleUnblendedHeight(x, z, bf, out float bfUnblended);
                WorldSampler.SampleHeight(x, z, bfNone, default, out float bfHard);
                Assert.AreEqual(bfHard, bfUnblended, $"Biome-first at ({x}, {z})");
                WorldSampler.SampleHeight(x, z, bf, default, out float bfBlended);
                if (bfBlended != bfUnblended) differ++;
            }

            Assert.Greater(differ, 0, "the Biome-first blend changes some heights");
        }

        private NativeArray<T> Alloc<T>(int count) where T : unmanaged{
            NativeArray<T> a = new(count, Allocator.Persistent);
            _native.Add(a);
            return a;
        }

        // --- BlendWeights ---------------------------------------------------------------------------------------------

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
            Assert.AreEqual(2, tie.ArgMax(), "ties break to the lower index");
            Assert.AreEqual(0f, BlendWeights.OneHot(-1).Sum(), "no biome → zero vector");
        }

        // --- Classifier -----------------------------------------------------------------------------------------------

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
        public void Classifier_DistanceUsesAxisWeightsAndPvNormalisation(){
            Biome b = Biome("B", .5f, .5f, .5f, .5f, 0f, 100, 1, new[]{ 1f }, 0);
            WorldGenConfig cfg = Config(1, b);
            WorldFields f = new(){ T = .6f, F = .7f, C = .9f, E = .4f, PV = 1f };
            // λ: T 1, F 1, C 2, E 1, PV 0.5; PV enters as (PV+1)/2 → 1 vs 0.5
            float expected = 1f * .1f * .1f + 1f * .2f * .2f + 2f * .4f * .4f + 1f * .1f * .1f + .5f * .5f * .5f;
            Assert.AreEqual(expected, BiomeClassifier.DistanceSquared(f, cfg.Biomes[0], cfg), 1e-6f);
            Assert.AreEqual(expected + 1f * .3f * .3f, BiomeClassifier.DistanceSquared(f, .8f, cfg.Biomes[0], cfg), 1e-6f, "6D adds λh (h − 0.5)²");
        }

        // --- Sampler --------------------------------------------------------------------------------------------------

        [Test]
        public void SampleColumn_WeightsAreOneHotAndNormalised(){
            WorldGenConfig cfg = Config(1337, PlaceholderBiomes());
            Unity.Mathematics.Random rng = new(7);
            for (int n = 0; n < 1000; n++){
                int2 p = rng.NextInt2(-20000, 20000);
                ColumnSample s = WorldSampler.SampleColumn(p.x, p.y, cfg);
                Assert.That(s.HardBiome, Is.InRange(0, cfg.BiomeCount - 1));
                Assert.AreEqual(1f, s.MaterialWeights.Sum(cfg.BiomeCount), 1e-5f, "Σ w = 1");
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

        // --- Terrain-first --------------------------------------------------------------------------------------------

        [Test]
        public void SampleColumn_TerrainFirst_HeightFollowsTheSplineFormula(){
            WorldGenConfig cfg = Config(2024, GenArchitecture.TerrainFirst, PlaceholderBiomes());
            float[] g ={ 1f, .5f, .25f, .125f, .0625f, .03125f };
            int2[] points ={ new(0, 0), new(1234, -5678), new(-77, 4096), new(100000, 100000), new(-9000, 777) };
            foreach (int2 p in points){
                ColumnSample s = WorldSampler.SampleColumn(p.x, p.y, cfg);
                WorldFields f = s.Fields;
                float offset = NestedSpline.Evaluate(cfg.OffsetSpline, f.C, f.E, f.PV, cfg.SplineNodes, cfg.SplinePoints);
                float amp = NestedSpline.Evaluate(cfg.AmpSpline, f.C, f.E, f.PV, cfg.SplineNodes, cfg.SplinePoints);
                float j = NestedSpline.Evaluate(cfg.JaggednessSpline, f.C, f.E, f.PV, cfg.SplineNodes, cfg.SplinePoints);
                // Managed evaluation vs the Burst-compiled entry point: equal up to float contraction (1 ulp).
                Assert.AreEqual(offset, s.TerrainParams.x, 1e-3f, $"Offset at {p}");
                Assert.AreEqual(amp, s.TerrainParams.y, 1e-4f, $"Amp at {p}");
                Assert.AreEqual(j, s.TerrainParams.z, 1e-4f, $"J at {p}");

                float detail = 0f;
                for (int i = 0; i < 6; i++) detail += g[i] * DetailOctaves.Sample(i, p, 512f / (1 << i), 2024u);
                float expected = offset + amp * detail + j * PeakNoise.Sample(p, 192f, 2024u);
                bool expectClamped = expected < 40f || expected > 250f;
                expected = math.clamp(expected, 40f, 250f);
                Assert.AreEqual(expected, s.Height, 1e-3f, $"H at {p}");
                Assert.AreEqual(expectClamped, s.Clamped);
                Assert.AreEqual((int)math.floor(s.Height), s.ColumnHeight);
                Assert.AreEqual(math.saturate((s.Height - 40f) / 210f), s.NormalizedHeight, 1e-6f);
            }
        }

        [Test]
        public void SampleColumn_TerrainFirst_ClassifiesInSixDimensions(){
            WorldGenConfig cfg = Config(2024, GenArchitecture.TerrainFirst, PlaceholderBiomes());
            for (int n = 0; n < 400; n++){
                int x = n * 613 - 100000, z = n * 379 + 2500;
                ColumnSample s = WorldSampler.SampleColumn(x, z, cfg);
                int expected = BiomeClassifier.Classify(s.Fields, s.NormalizedHeight, cfg);
                Assert.AreEqual(expected, s.HardBiome, $"6D class at ({x},{z})");
                Assert.AreEqual(s.HardBiome, s.SurfaceSource);
                Assert.AreEqual(1f, s.MaterialWeights.Sum(), 1e-6f);
                Assert.AreEqual(s.HardBiome, s.MaterialWeights.ArgMax());
            }

            // Two biomes that differ only in their height target: 5D would always tie to index 0; 6D splits on h.
            Biome low = Biome("Low", .5f, .5f, .5f, .5f, 0f, 0, 0, new float[6], 0);
            Biome high = Biome("High", .5f, .5f, .5f, .5f, 0f, 0, 0, new float[6], 0);
            low.TargetHeight = 0.2f;
            high.TargetHeight = 0.9f;
            WorldGenConfig pair = Config(2024, GenArchitecture.TerrainFirst, low, high);
            int lows = 0, highs = 0;
            for (int n = 0; n < 400; n++){
                int x = n * 613 - 100000, z = n * 379 + 2500;
                ColumnSample s = WorldSampler.SampleColumn(x, z, pair);
                int expected = s.NormalizedHeight > 0.55f ? 1 : 0;
                Assert.AreEqual(expected, s.HardBiome, $"h = {s.NormalizedHeight} at ({x},{z})");
                if (expected == 0) lows++;
                else highs++;
            }

            Assert.Greater(lows, 0);
            Assert.Greater(highs, 0, "the placeholder world reaches h > 0.55 somewhere in the sample");
        }

        [Test]
        public void SampleColumn_TerrainFirst_HeightDoesNotDependOnBiomes(){
            // H is a function of the fields and splines only; the biome set changes only the class.
            WorldGenConfig all = Config(31, GenArchitecture.TerrainFirst, PlaceholderBiomes());
            WorldGenConfig one = Config(31, GenArchitecture.TerrainFirst, PlaceholderBiomes()[2]);
            for (int n = 0; n < 100; n++){
                int x = n * 911 - 40000, z = n * 557;
                Assert.AreEqual(WorldSampler.SampleColumn(x, z, all).Height, WorldSampler.SampleColumn(x, z, one).Height);
            }

            Assert.AreEqual(0, WorldSampler.SampleColumn(5, 5, one).HardBiome);
        }

        [Test]
        public void SampleColumn_TerrainFirst_IsContinuousAcrossBiomeBoundaries(){
            // Hard biome boundaries do not step the height (the height does not know the biome).
            WorldGenConfig cfg = Config(2024, GenArchitecture.TerrainFirst, PlaceholderBiomes());
            int boundaries = 0;
            float maxStep = 0f;
            ColumnSample previous = WorldSampler.SampleColumn(-20000, 0, cfg);
            for (int x = -19999; x < 20000; x++){
                ColumnSample s = WorldSampler.SampleColumn(x, 0, cfg);
                if (s.HardBiome != previous.HardBiome){
                    boundaries++;
                    maxStep = math.max(maxStep, math.abs(s.Height - previous.Height));
                }

                previous = s;
            }

            Assert.Greater(boundaries, 5, "the walk crosses biome boundaries");
            // Per-block height steps come from the octaves alone; the largest octave slope is a few blocks at most.
            Assert.Less(maxStep, 8f, $"height jump of {maxStep} at a biome boundary");
        }

        // --- Biome-first ----------------------------------------------------------------------------------------------

        [Test]
        public void SampleColumn_BiomeFirst_ClassifiesInFiveDimensionsBeforeTheHeight(){
            // The class is argmin over (T, F, C, E, (PV+1)/2) only; h never enters, and the one-hot weights
            // of both stages follow the hard class (method None).
            WorldGenConfig cfg = Config(4242, GenArchitecture.BiomeFirst, PlaceholderBiomes());
            Unity.Mathematics.Random rng = new(11);
            int classesSeen = 0;
            for (int n = 0; n < 500; n++){
                int2 p = rng.NextInt2(-30000, 30000);
                ColumnSample s = WorldSampler.SampleColumn(p.x, p.y, cfg);
                Assert.AreEqual(BiomeClassifier.Classify(s.Fields, cfg), s.HardBiome, $"5D class at {p}");
                Assert.AreEqual(s.HardBiome, s.MaterialWeights.ArgMax());
                Assert.AreEqual(1f, s.HeightWeights[s.HardBiome]);
                Assert.AreEqual(float3.zero, s.TerrainParams, "no spline parameters in Biome-first");
                classesSeen |= 1 << s.HardBiome;
            }

            Assert.Greater(math.countbits(classesSeen), 3, "the placeholder world shows several biomes");
        }

        [Test]
        public void SampleColumn_BiomeFirst_HeightIsTheHardBiomesFormula(){
            // With one-hot weights the blended parameters are the hard biome's own: H = Offset_b + Σ a_{b,i} N_i + J_b P
            // with a_{b,i} = HeightAmplitude_b · OctaveWeights_{b,i}.
            Biome[] biomes = PlaceholderBiomes();
            WorldGenConfig cfg = Config(77, GenArchitecture.BiomeFirst, biomes);
            for (int n = 0; n < 200; n++){
                int x = n * 613 - 50000, z = n * 389 + 17;
                ColumnSample s = WorldSampler.SampleColumn(x, z, cfg);
                Biome b = biomes[s.HardBiome];
                float2 p = new(x, z);
                float expected = b.HeightOffset;
                for (int i = 0; i < 6; i++)
                    expected += b.HeightAmplitude * b.OctaveWeights[i] * DetailOctaves.Sample(i, p, 512f / (1 << i), 77u);
                expected += b.Jaggedness * PeakNoise.Sample(p, 192f, 77u);
                Assert.AreEqual(math.clamp(expected, 40f, 250f), s.Height, 1e-3f, $"H at ({x},{z}) in {b.name}");
            }
        }

        [Test]
        public void BiomeFirstHeight_IsLinearInTheWeights(){
            // Computing H once from blended parameters equals Σ w_b H_b, because every biome samples the same N_i and
            // P and only the coefficients differ.
            WorldGenConfig cfg = Config(2025, GenArchitecture.BiomeFirst, PlaceholderBiomes());
            Unity.Mathematics.Random rng = new(3);
            for (int n = 0; n < 200; n++){
                float2 p = rng.NextFloat2(-20000f, 20000f);
                BlendWeights w = default;
                float sum = 0f;
                for (int b = 0; b < cfg.BiomeCount; b++){
                    w[b] = rng.NextFloat();
                    sum += w[b];
                }

                float expected = 0f;
                for (int b = 0; b < cfg.BiomeCount; b++){
                    w[b] /= sum;
                    BiomeFirstHeight.Sample(p, BlendWeights.OneHot(b), cfg, out _, out float hb);
                    expected += w[b] * hb;
                }

                BiomeFirstHeight.Sample(p, w, cfg, out float height, out float unclamped);
                Assert.AreEqual(expected, unclamped, 1e-3f, $"Σ w_b H_b at {p}");
                Assert.AreEqual(math.clamp(unclamped, cfg.HeightMin, cfg.HeightMax), height);
            }
        }

        [Test]
        public void SampleColumn_BiomeFirst_StepsAtBiomeBoundaries(){
            // Without a height blend the Offsets differ across a boundary, so cliffs appear;
            // the counterpart of SampleColumn_TerrainFirst_IsContinuousAcrossBiomeBoundaries.
            WorldGenConfig cfg = Config(2024, GenArchitecture.BiomeFirst, PlaceholderBiomes());
            int boundaries = 0, steps = 0;
            ColumnSample previous = WorldSampler.SampleColumn(-20000, 0, cfg);
            for (int x = -19999; x < 20000; x++){
                ColumnSample s = WorldSampler.SampleColumn(x, 0, cfg);
                if (s.HardBiome != previous.HardBiome){
                    boundaries++;
                    if (math.abs(s.Height - previous.Height) > 8f) steps++;
                }

                previous = s;
            }

            Assert.Greater(boundaries, 5, "the walk crosses biome boundaries");
            Assert.Greater(steps, 0, "at least one boundary shows a cliff (expected without blending)");
        }

        // --- Region API -----------------------------------------------------------------------------------------------

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
                Clamped = Alloc<byte>(count),
                NormalizedHeight = Alloc<float>(count),
                TerrainParams = Alloc<float3>(count)
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
                Assert.AreEqual(s.NormalizedHeight, buffers.NormalizedHeight[index]);
                Assert.AreEqual(s.TerrainParams, buffers.TerrainParams[index]);
            }
        }

        [Test]
        public void SampleRegion_TerrainFirst_MatchesSampleColumn(){
            WorldGenConfig cfg = Config(99, GenArchitecture.TerrainFirst, PlaceholderBiomes());
            WorldGenRequest req = new(){ Config = cfg, Origin = new int2(3000, -4000), Size = new int2(16, 16), Stride = 4 };
            int count = req.ColumnCount;
            RegionBuffers buffers = new(){
                Height = Alloc<float>(count),
                HardBiome = Alloc<int>(count),
                TerrainParams = Alloc<float3>(count),
                NormalizedHeight = Alloc<float>(count)
            };
            WorldGenApi.SampleRegion(req, ref buffers).Complete();
            for (int j = 0; j < 16; j++)
            for (int i = 0; i < 16; i++){
                ColumnSample s = WorldSampler.SampleColumn(3000 + i * 4, -4000 + j * 4, cfg);
                int index = j * 16 + i;
                Assert.AreEqual(s.Height, buffers.Height[index]);
                Assert.AreEqual(s.HardBiome, buffers.HardBiome[index]);
                Assert.AreEqual(s.TerrainParams, buffers.TerrainParams[index]);
                Assert.AreEqual(s.NormalizedHeight, buffers.NormalizedHeight[index]);
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
            // The same column gives the same result regardless of the region it is sampled in.
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
