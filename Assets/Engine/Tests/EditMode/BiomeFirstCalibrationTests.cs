using System;
using System.Collections.Generic;
using Engine.Editor.WorldGen;
using Engine.Scripts.VoxelConfig.Data.Generation;
using Engine.Scripts.WorldGen.Core;
using Engine.Scripts.WorldGen.Noise;
using Engine.Scripts.WorldGen.Settings;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Engine.Tests.EditMode{
    /// <summary>
    ///     The calibration helper suggests Jaggedness_b = mean J, Offset_b = mean H − Jaggedness_b · mean P and
    ///     HeightAmplitude_b from the detail residual of the Terrain-first reference, writes nothing and restores the
    ///     settings.
    /// </summary>
    public class BiomeFirstCalibrationTests{
        private const int Seed = 1234;
        private static readonly int2 Origin = new(-4000, 2500);
        private const int Size = 64;
        private const int Stride = 8;

        private readonly List<Object> _created = new();
        private WorldGenSettings _settings;

        [SetUp]
        public void SetUp(){
            _settings = Create<WorldGenSettings>("WG");
            _settings.Architecture = GenArchitecture.BiomeFirst;
            _settings.Splines = Create<TerrainSplineSet>("Splines");
            _settings.Splines.ResetToPlaceholders();
            BiomeSet set = Create<BiomeSet>("Set");
            set.Biomes.Add(MakeBiome("Plains", .55f, .45f, .45f, .70f, -0.2f, 136, 6, new[]{ 1f, .6f, .3f, .12f, .05f, .02f }, 0));
            set.Biomes.Add(MakeBiome("Desert", .90f, .10f, .45f, .60f, 0f, 134, 8, new[]{ 1f, .8f, .2f, .05f, .02f, .01f }, 0));
            set.Biomes.Add(MakeBiome("Mountain", .35f, .40f, .80f, .15f, 0.6f, 165, 22, new[]{ .7f, .7f, .6f, .45f, .3f, .15f }, 35));
            set.Biomes.Add(MakeBiome("Snow", .15f, .50f, .70f, .35f, 0.3f, 158, 14, new[]{ .8f, .6f, .45f, .3f, .15f, .08f }, 15));
            set.Biomes.Add(MakeBiome("Forest", .50f, .70f, .50f, .55f, 0f, 140, 9, new[]{ 1f, .6f, .35f, .15f, .06f, .03f }, 0));
            set.Biomes.Add(MakeBiome("Ocean", .50f, .80f, .08f, .50f, 0f, 100, 6, new[]{ 1f, .5f, .2f, .05f, .02f, .01f }, 0));
            _settings.Biomes = set;
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

        private Biome MakeBiome(string name, float t, float f, float c, float e, float pv, float offset, float amp, float[] weights, float j){
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

        [Test]
        public void TrySuggest_WithoutSplines_FailsWithMessage(){
            _settings.Splines = null;
            bool ok = BiomeFirstCalibration.TrySuggest(_settings, Seed, Origin, Size, Stride, out BiomeFirstCalibration.Suggestion[] s, out string error);
            Assert.IsFalse(ok);
            Assert.IsEmpty(s);
            StringAssert.Contains("TerrainSplineSet", error);
        }

        [Test]
        public void TrySuggest_PlaceholderWorld_SuggestsPerOccurringBiome(){
            float[] offsets = Snapshot(b => b.HeightOffset), amps = Snapshot(b => b.HeightAmplitude), jags = Snapshot(b => b.Jaggedness);

            bool ok = BiomeFirstCalibration.TrySuggest(_settings, Seed, Origin, Size, Stride, out BiomeFirstCalibration.Suggestion[] s, out string error);

            Assert.IsTrue(ok, error);
            Assert.AreEqual(GenArchitecture.BiomeFirst, _settings.Architecture, "architecture restored");
            Assert.AreEqual(6, s.Length);
            int total = 0;
            for (int b = 0; b < s.Length; b++){
                Assert.AreEqual(b, s[b].Biome);
                Assert.AreEqual(_settings.Biomes.Biomes[b].name, s[b].Name);
                Assert.AreEqual(offsets[b], s[b].CurrentOffset);
                Assert.AreEqual(amps[b], s[b].CurrentAmplitude);
                Assert.AreEqual(jags[b], s[b].CurrentJaggedness);
                total += s[b].Count;
                if (s[b].Count == 0) continue;
                Assert.That(s[b].Offset, Is.InRange(_settings.HeightMin, _settings.HeightMax), s[b].Name);
                Assert.IsTrue(s[b].AmplitudeAvailable, s[b].Name);
                Assert.That(s[b].Amplitude, Is.GreaterThan(0f).And.LessThan(200f), s[b].Name);
                Assert.IsFalse(float.IsNaN(s[b].Jaggedness), s[b].Name);
            }

            Assert.That(total, Is.GreaterThan(0).And.LessThanOrEqualTo(Size * Size));

            // Writes nothing.
            CollectionAssert.AreEqual(offsets, Snapshot(b => b.HeightOffset));
            CollectionAssert.AreEqual(amps, Snapshot(b => b.HeightAmplitude));
            CollectionAssert.AreEqual(jags, Snapshot(b => b.Jaggedness));
        }

        [Test]
        public void TrySuggest_JaggednessIsMeanJ_OffsetIsMeanHMinusRidgeMean(){
            Assert.IsTrue(BiomeFirstCalibration.TrySuggest(_settings, Seed, Origin, Size, Stride, out BiomeFirstCalibration.Suggestion[] s, out string error), error);

            // Recompute the per-biome means over the unclamped columns straight from the Terrain-first sampler.
            _settings.Architecture = GenArchitecture.TerrainFirst;
            WorldGenConfig cfg = _settings.ToConfig(Seed, null, Allocator.Temp);
            _settings.Architecture = GenArchitecture.BiomeFirst;
            try{
                double[] n = new double[s.Length], sumH = new double[s.Length], sumJ = new double[s.Length], sumP = new double[s.Length];
                for (int y = 0; y < Size; y++)
                for (int x = 0; x < Size; x++){
                    ColumnSample c = WorldSampler.SampleColumn(Origin.x + x * Stride, Origin.y + y * Stride, cfg);
                    if (c.Clamped || c.HardBiome < 0) continue;
                    n[c.HardBiome] += 1;
                    sumH[c.HardBiome] += c.Height;
                    sumJ[c.HardBiome] += c.TerrainParams.z;
                    sumP[c.HardBiome] += PeakNoise.Sample(new float2(Origin.x + x * Stride, Origin.y + y * Stride), cfg.PeakWavelength, cfg.Seed);
                }

                for (int b = 0; b < s.Length; b++){
                    Assert.AreEqual((int)n[b], s[b].Count, s[b].Name);
                    if (n[b] == 0) continue;
                    double meanJ = sumJ[b] / n[b];
                    Assert.AreEqual(meanJ, s[b].Jaggedness, 1e-4, $"{s[b].Name} jaggedness");
                    Assert.AreEqual(sumH[b] / n[b] - meanJ * (sumP[b] / n[b]), s[b].Offset, 1e-3, $"{s[b].Name} offset");
                }
            }
            finally{
                cfg.Dispose();
            }
        }

        [Test]
        public void TrySuggest_ConstantSplinesAndGlobalWeights_RecoverTheAmplitude(){
            // H = 120 + 9 * sum g_i N_i exactly; with the biome weights equal to the global weights the residual
            // spread divided by the biome's own detail spread must give the amplitude back.
            _settings.Splines.FromJson("{\"Offset\": 120, \"Amp\": 9, \"Jaggedness\": 0}");
            foreach (Biome b in _settings.Biomes.Biomes) b.OctaveWeights = (float[])_settings.GlobalOctaveWeights.Clone();

            Assert.IsTrue(BiomeFirstCalibration.TrySuggest(_settings, Seed, Origin, Size, Stride, out BiomeFirstCalibration.Suggestion[] s, out string error), error);

            int checkedBiomes = 0;
            foreach (BiomeFirstCalibration.Suggestion b in s){
                if (b.Count < 50) continue; // a handful of columns gives no stable variance
                checkedBiomes++;
                Assert.IsTrue(b.AmplitudeAvailable, b.Name);
                Assert.AreEqual(9f, b.Amplitude, 1e-3f, $"{b.Name} amplitude");
                Assert.AreEqual(0f, b.Jaggedness, 1e-5f, $"{b.Name} jaggedness");
                Assert.AreEqual(120f, b.Offset, 18f, $"{b.Name} offset about 120 + 9 * mean detail");
            }

            Assert.That(checkedBiomes, Is.GreaterThan(0));
        }

        private float[] Snapshot(Func<Biome, float> get){
            List<Biome> biomes = _settings.Biomes.Biomes;
            float[] result = new float[biomes.Count];
            for (int i = 0; i < biomes.Count; i++) result[i] = get(biomes[i]);
            return result;
        }
    }
}
