using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using Engine.Scripts.Jobs.Chunk;
using Engine.Scripts.Settings;
using Engine.Scripts.VoxelConfig.Data.Generation;
using Engine.Scripts.WorldGen.Core;
using Engine.Scripts.WorldGen.Noise;
using Engine.Scripts.WorldGen.Settings;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Engine.Tests.EditMode{
    /// <summary>INT-01…03, GEN-20…22: settings asset, canonical JSON, Burst mirror and biome set.</summary>
    public class WorldGenSettingsTests{
        private readonly List<Object> _created = new();

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

        private Biome Biome(string name, float amplitude, float[] weights, float erosion = 0.5f, float pv = 0f){
            Biome b = Create<Biome>(name);
            b.HeightAmplitude = amplitude;
            b.OctaveWeights = weights;
            b.TargetErosion = erosion;
            b.TargetPV = pv;
            return b;
        }

        private WorldGenSettings Settings(params Biome[] biomes){
            BiomeSet set = Create<BiomeSet>("Set");
            set.Biomes.AddRange(biomes);
            WorldGenSettings s = Create<WorldGenSettings>("WG");
            s.Biomes = set;
            return s;
        }

        // --- INT-02 ------------------------------------------------------------------------------------------------------

        [Test]
        public void CanonicalJson_SortsKeysAndUsesInvariantRoundTripFloats(){
            string json = CanonicalJson.Serialize(new Dictionary<string, object>{
                ["b"] = 1, ["a"] = 0.1f, ["c"] = new[]{ 2f, 0.5f }, ["d"] = GenArchitecture.BiomeFirst, ["e"] = (string)null
            });
            Assert.AreEqual("{\n  \"a\": 0.1,\n  \"b\": 1,\n  \"c\": [\n    2,\n    0.5\n  ],\n  \"d\": \"BiomeFirst\",\n  \"e\": null\n}\n", json);
        }

        [Test]
        public void CanonicalJson_FloatsRoundTrip(){
            float[] values ={ 0.1f, 1f / 3f, 0.04f, 1.6f, 136.25f, -0.2f, 1e-7f, 3.4e38f };
            foreach (float v in values)
                Assert.AreEqual(v, float.Parse(CanonicalJson.Float(v), CultureInfo.InvariantCulture), $"{v}");
        }

        [Test]
        public void ToCanonicalJson_IsDeterministicAndCultureIndependent(){
            WorldGenSettings a = Settings(Biome("Plains", 6f, new[]{ 1f, 0.6f }), Biome("Desert", 8f, new[]{ 1f, 0.8f }));
            WorldGenSettings b = Settings(Biome("Plains", 6f, new[]{ 1f, 0.6f }), Biome("Desert", 8f, new[]{ 1f, 0.8f }));

            string first = a.ToCanonicalJson();
            CultureInfo previous = Thread.CurrentThread.CurrentCulture;
            try{
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                Assert.AreEqual(first, a.ToCanonicalJson(), "same instance, German culture");
                Assert.AreEqual(first, b.ToCanonicalJson(), "second instance with identical values");
            }
            finally{
                Thread.CurrentThread.CurrentCulture = previous;
            }

            StringAssert.Contains("\"Name\": \"Desert\"", first, "BiomeSet contents are inlined");
            StringAssert.Contains("\"Architecture\": \"Legacy\"", first);
            StringAssert.Contains("\"TemperatureProfile\"", first, "own climate profiles are part of the manifest");
            StringAssert.Contains("\"RedistributionCurve\"", first);
            Assert.IsFalse(first.Contains("0,6"), "no culture-specific decimal separator");
        }

        [Test]
        public void ToCanonicalJson_ChangesWhenAnyParameterChanges(){
            WorldGenSettings s = Settings(Biome("Plains", 6f, new[]{ 1f }));
            string baseline = s.ToCanonicalJson();

            s.LambdaPV = 0.75f;
            string changedLambda = s.ToCanonicalJson();
            Assert.AreNotEqual(baseline, changedLambda);

            s.Biomes.Biomes[0].Jaggedness = 3f;
            Assert.AreNotEqual(changedLambda, s.ToCanonicalJson(), "inlined biome fields take part");

            Assert.AreNotEqual(s.ToCanonicalJson(), s.ToCanonicalJson(42), "seed overload adds the seed");
            StringAssert.Contains("\"Seed\": 42", s.ToCanonicalJson(42));
        }

        // --- GEN-20/21 ----------------------------------------------------------------------------------------------

        [Test]
        public void BiomeSet_MapsIndexToNameInAssetOrder(){
            Biome plains = Biome("Plains", 6f, new[]{ 1f });
            Biome ocean = Biome("Ocean", 6f, new[]{ 1f });
            WorldGenSettings s = Settings(plains, ocean);

            Assert.AreEqual(2, s.Biomes.Count);
            Assert.AreEqual("Plains", s.Biomes.NameOf(0));
            Assert.AreEqual("Ocean", s.Biomes.NameOf(1));
            Assert.AreEqual(1, s.Biomes.IndexOf(ocean));
            Assert.AreEqual("<7>", s.Biomes.NameOf(7), "out of range gives a placeholder");
            CollectionAssert.AreEqual(new[]{ "Plains", "Ocean" }, s.Biomes.Names());
        }

        // --- INT-03 / GEN-22 ------------------------------------------------------------------------------------------

        [Test]
        public void ToConfig_FlattensBiomesAndOctaveCoefficients(){
            WorldGenSettings s = Settings(
                Biome("Plains", 6f, new[]{ 1f, 0.6f, 0.3f, 0.12f, 0.05f, 0.02f }, 0.7f, -0.2f),
                Biome("Mountain", 22f, new[]{ 0.7f, 0.7f, 0.6f }, 0.15f, 0.6f)); // short weight list → zeros
            WorldGenConfig cfg = s.ToConfig(1337, allocator: Allocator.Temp);
            try{
                Assert.IsTrue(cfg.IsCreated);
                Assert.AreEqual(1337u, cfg.Seed);
                Assert.AreEqual(GenArchitecture.Legacy, cfg.Architecture);
                Assert.AreEqual(6, cfg.OctaveCount);
                Assert.AreEqual(2, cfg.BiomeCount);
                CollectionAssert.AreEqual(new[]{ 512f, 256f, 128f, 64f, 32f, 16f }, cfg.OctaveWavelengths.ToArray());
                CollectionAssert.AreEqual(new[]{ 1f, 0.5f, 0.25f, 0.125f, 0.0625f, 0.03125f }, cfg.GlobalOctaveWeights.ToArray());

                Assert.AreEqual(0.7f, cfg.Biomes[0].targetErosion);
                Assert.AreEqual(-0.2f, cfg.Biomes[0].targetPV);
                Assert.AreEqual(136f, cfg.Biomes[0].heightOffset);
                Assert.AreEqual(0.6f, cfg.Biomes[1].targetPV);
                Assert.AreEqual(22f, cfg.Biomes[1].heightAmplitude);

                Assert.AreEqual(6f * 0.6f, cfg.OctaveCoefficient(0, 1), 1e-6f, "a_{0,1} = Amp × w");
                Assert.AreEqual(6f * 0.02f, cfg.OctaveCoefficient(0, 5), 1e-6f);
                Assert.AreEqual(22f * 0.7f, cfg.OctaveCoefficient(1, 0), 1e-6f);
                Assert.AreEqual(0f, cfg.OctaveCoefficient(1, 3), "missing weights are 0");
                Assert.AreEqual(12, cfg.OctaveCoefficients.Length);

                Assert.AreEqual(1.6f, cfg.Climate.WGain);
                Assert.AreEqual(0.4f, cfg.Climate.ContinentalHumidityInfluence, "coupling comes from WorldGenSettings itself");
                WorldFields f = ClimateFields.Sample(new float2(123f, -456f), cfg.Climate);
                Assert.That(f.E, Is.InRange(0f, 1f));
                Assert.That(f.PV, Is.InRange(-1f, 1f));
            }
            finally{
                cfg.Dispose();
            }
        }

        [Test]
        public void ToConfig_ThrowsOnEmptyBiomeSlot(){
            WorldGenSettings s = Settings(Biome("Plains", 6f, new[]{ 1f }), null);
            Assert.Throws<System.InvalidOperationException>(() => s.ToConfig(1, allocator: Allocator.Temp));
        }

        [Test]
        public void ToConfig_RejectsInvalidScales(){
            WorldGenSettings octave = Create<WorldGenSettings>("WG");
            octave.OctaveWavelengths = new[]{ 512f, 0f };
            WorldGenSettings peak = Create<WorldGenSettings>("WG");
            peak.PeakWavelength = float.NaN;
            WorldGenSettings warp = Create<WorldGenSettings>("WG");
            warp.WarpWavelength = float.PositiveInfinity;
            WorldGenSettings climate = Create<WorldGenSettings>("WG");
            climate.continentalLayer.warpNoise.scale = -1800f;
            WorldGenSettings frequency = Create<WorldGenSettings>("WG");
            frequency.ClimateFrequencyScale = 0f;

            StringAssert.Contains("OctaveWavelengths[1]",
                Assert.Throws<System.InvalidOperationException>(() => octave.ToConfig(1, allocator: Allocator.Temp)).Message);
            StringAssert.Contains("PeakWavelength",
                Assert.Throws<System.InvalidOperationException>(() => peak.ToConfig(1, allocator: Allocator.Temp)).Message);
            StringAssert.Contains("WarpWavelength",
                Assert.Throws<System.InvalidOperationException>(() => warp.ToConfig(1, allocator: Allocator.Temp)).Message);
            StringAssert.Contains("continentalLayer.warpNoise.scale",
                Assert.Throws<System.InvalidOperationException>(() => climate.ToConfig(1, allocator: Allocator.Temp)).Message);
            StringAssert.Contains("ClimateFrequencyScale",
                Assert.Throws<System.InvalidOperationException>(() => frequency.ToConfig(1, allocator: Allocator.Temp)).Message);
        }

        [Test]
        public void ToConfig_WithoutBiomeSetGivesEmptyBiomes(){
            WorldGenSettings s = Create<WorldGenSettings>("WG");
            WorldGenConfig cfg = s.ToConfig(1, allocator: Allocator.Temp);
            Assert.AreEqual(0, cfg.BiomeCount);
            Assert.AreEqual(0, cfg.OctaveCoefficients.Length);
            cfg.Dispose();
        }

        [Test]
        public void DefaultClimate_MatchesTheTst08Fixture(){
            // TST-08 runs on ClimateFieldTests.CreateClimate; it must stay the climate WorldGenSettings ships (INT-01).
            WorldGenSettings s = Create<WorldGenSettings>("WG");
            WorldGenConfig cfg = s.ToConfig(1337, allocator: Allocator.Temp);
            ClimateParams fixture = ClimateFieldTests.CreateClimate(1337u);
            try{
                for (int i = 0; i < 64; i++){
                    float2 p = new(i * 173.1f - 4000f, i * 59.3f + 900f);
                    WorldFields a = ClimateFields.Sample(p, cfg.Climate);
                    WorldFields b = ClimateFields.Sample(p, fixture);
                    Assert.AreEqual(b.T, a.T, $"T at {p}");
                    Assert.AreEqual(b.F, a.F, $"F at {p}");
                    Assert.AreEqual(b.C, a.C, $"C at {p}");
                    Assert.AreEqual(b.E, a.E, $"E at {p}");
                    Assert.AreEqual(b.W, a.W, $"W at {p}");
                }
            }
            finally{
                fixture.Dispose();
                cfg.Dispose();
            }
        }

        [Test]
        public void SampleRegion_RejectsAShortBufferBeforeScheduling(){
            WorldGenSettings s = Create<WorldGenSettings>("WG");
            WorldGenConfig cfg = s.ToConfig(1, allocator: Allocator.TempJob);
            // Clamped is bound last: the request must fail before the dummies of the earlier buffers are allocated.
            NativeArray<byte> clamped = new(15, Allocator.TempJob);
            try{
                WorldGenRequest req = new(){ Config = cfg, Origin = int2.zero, Size = new int2(4, 4) };
                RegionBuffers buffers = new(){ Clamped = clamped };
                Assert.Throws<System.ArgumentException>(() => WorldGenApi.SampleRegion(req, ref buffers));
            }
            finally{
                clamped.Dispose();
                cfg.Dispose();
            }
        }

        [Test]
        public void CreateEmpty_IsConstructedSchedulableAndBiomeless(){
            WorldGenConfig cfg = WorldGenConfig.CreateEmpty(7u);
            try{
                Assert.IsTrue(cfg.IsCreated, "0-length arrays count as constructed");
                Assert.AreEqual(0, cfg.BiomeCount);
                Assert.AreEqual(0, cfg.OctaveCount);
                Assert.AreEqual(GenArchitecture.Legacy, cfg.Architecture);
                Assert.AreEqual(40f, cfg.HeightMin);

                // The mirror must survive a job schedule (ChunkJob carries it even on the legacy path).
                WorldGenRequest req = new(){ Config = cfg, Origin = int2.zero, Size = new int2(4, 4) };
                NativeArray<int> biome = new(16, Allocator.TempJob);
                NativeArray<float> height = new(16, Allocator.TempJob);
                RegionBuffers buffers = new(){ HardBiome = biome, Height = height };
                WorldGenApi.SampleRegion(req, ref buffers).Complete();
                Assert.AreEqual(-1, biome[5], "no biome active");
                Assert.AreEqual(40f, height[5], "no biome → height clamps to Hmin");
                biome.Dispose();
                height.Dispose();
            }
            finally{
                cfg.Dispose();
            }
        }

        [Test]
        public void DefaultConfigs_DisposeSafely(){
            // D-05: a GeneratorConfig without WorldGenSettings carries a default WorldGenConfig that owns nothing.
            default(WorldGenConfig).Dispose();
            GeneratorConfig generator = default;
            Assert.IsFalse(generator.WorldGen.IsCreated);
            generator.Dispose();
        }

        [Test]
        public void BiomeDef_CarriesTheExtendedFields(){
            Biome b = Biome("Snow", 14f, new[]{ 0.8f }, 0.35f, 0.3f);
            b.HeightOffset = 158f;
            b.Jaggedness = 15f;
            b.TargetHeight = 0.85f;
            Biome.BiomeDef def = b.ToStruct();
            Assert.AreEqual(0.35f, def.targetErosion);
            Assert.AreEqual(0.3f, def.targetPV);
            Assert.AreEqual(0.85f, def.targetElevation);
            Assert.AreEqual(158f, def.heightOffset);
            Assert.AreEqual(14f, def.heightAmplitude);
            Assert.AreEqual(15f, def.jaggedness);
            Assert.AreEqual(0, def.floraRuleCount);
        }
    }
}
