using System.Collections.Generic;
using System.Reflection;
using Engine.Scripts.Jobs.Chunk;
using Engine.Scripts.Noise;
using Engine.Scripts.Settings;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Engine.Tests.EditMode{
    /// <summary>TST-E10 (NoiseProfile, WarpedNoiseLayer, ApplyCurve) and TST-E14 (seeds F-02, warp channels F-06).</summary>
    public class NoiseTests{
        private readonly List<NoiseProfile> _profiles = new();
        private readonly List<WarpedNoiseLayer> _layers = new();
        private readonly List<NoiseCalculator.NoiseParameters> _parameters = new();
        private readonly List<NativeArray<float2>> _arrays = new();
        private NoiseSettings _settings;

        [SetUp]
        public void SetUp(){
            _settings = ScriptableObject.CreateInstance<NoiseSettings>();
        }

        [TearDown]
        public void TearDown(){
            foreach (NoiseProfile profile in _profiles) profile.Dispose();
            foreach (WarpedNoiseLayer layer in _layers) layer.Dispose();
            foreach (NoiseCalculator.NoiseParameters parameters in _parameters) parameters.Dispose();
            foreach (NativeArray<float2> array in _arrays) array.Dispose();
            _profiles.Clear();
            _layers.Clear();
            _parameters.Clear();
            _arrays.Clear();
            Object.DestroyImmediate(_settings);
        }

        private NoiseProfile Profile(int seed, float scale = 50f, int octaves = 4){
            NoiseProfile profile = new(new NoiseProfile.Settings{
                Seed = seed, Scale = scale, Persistance = 0.5f, Lacunarity = 2f, Octaves = octaves
            });
            _profiles.Add(profile);
            return profile;
        }

        private WarpedNoiseLayer Layer(int seed, int warpSeedOffsetY, float2[] points, float warpStrength = 40f){
            WarpedNoiseLayer layer = new(new WarpedNoiseLayer.Settings{
                BaseNoise = new NoiseProfile.Settings{ Seed = seed, Scale = 120f, Persistance = 0.5f, Lacunarity = 2f, Octaves = 3 },
                WarpNoise = new NoiseProfile.Settings{ Seed = seed, Scale = 90f, Persistance = 0.5f, Lacunarity = 2f, Octaves = 2 },
                WarpStrength = warpStrength,
                WarpSeedOffsetY = warpSeedOffsetY,
                RedistributionPoints = points
            });
            _layers.Add(layer);
            return layer;
        }

        private NativeArray<float2> Points(params float2[] points){
            NativeArray<float2> array = new(points, Allocator.Persistent);
            _arrays.Add(array);
            return array;
        }

        private NoiseCalculator.NoiseParameters Parameters(int seed){
            NoiseCalculator.NoiseParameters parameters = _settings.ToNoiseParameters(seed);
            _parameters.Add(parameters);
            return parameters;
        }

        private static IEnumerable<float2> SamplePositions(){
            for (int x = -6; x <= 6; x++)
            for (int y = -6; y <= 6; y++)
                yield return new float2(x * 37.3f + 0.5f, y * 53.1f - 0.25f);
        }

        private static NoiseProfile WarpProfile(WarpedNoiseLayer layer, string field){
            FieldInfo info = typeof(WarpedNoiseLayer).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(info, $"WarpedNoiseLayer.{field} not found");
            return (NoiseProfile)info.GetValue(layer);
        }

        #region TST-E10

        [Test]
        public void NoiseProfile_OutputIsInUnitRange(){
            NoiseProfile profile = Profile(42);
            foreach (float2 position in SamplePositions()){
                float value = profile.GetNoise(position);
                Assert.That(value, Is.InRange(0f, 1f), $"at {position}");
            }
        }

        [Test]
        public void NoiseProfile_IsDeterministicAndSeedDependent(){
            NoiseProfile a = Profile(42);
            NoiseProfile b = Profile(42);
            NoiseProfile other = Profile(43);
            bool anyDifferent = false;
            foreach (float2 position in SamplePositions()){
                Assert.AreEqual(a.GetNoise(position), b.GetNoise(position), $"at {position}");
                anyDifferent |= !Mathf.Approximately(a.GetNoise(position), other.GetNoise(position));
            }

            Assert.IsTrue(anyDifferent, "a different seed must change the field");
        }

        [Test]
        public void NoiseProfile_NonPositiveScale_StaysFinite(){
            NoiseProfile profile = Profile(1, 0f);
            float value = profile.GetNoise(new float2(3.5f, -7.25f));
            Assert.IsTrue(float.IsFinite(value));
            Assert.That(value, Is.InRange(0f, 1f));
        }

        [Test]
        public void WarpedNoiseLayer_OutputIsInUnitRangeAndDeterministic(){
            float2[] points ={ new(0f, 0.02f), new(0.5f, 0.42f), new(1f, 1f) };
            WarpedNoiseLayer a = Layer(7, 1000, points);
            WarpedNoiseLayer b = Layer(7, 1000, points);
            foreach (float2 position in SamplePositions()){
                float value = a.GetNoise(position);
                Assert.That(value, Is.InRange(0f, 1f), $"at {position}");
                Assert.AreEqual(value, b.GetNoise(position), $"at {position}");
            }
        }

        [Test]
        public void WarpedNoiseLayer_WithoutPoints_UsesIdentityCurve(){
            WarpedNoiseLayer identity = Layer(7, 1000, null, 0f);
            WarpedNoiseLayer emptyPoints = Layer(7, 1000, new float2[0], 0f);
            NoiseProfile baseNoise = WarpProfile(identity, "_baseNoise");
            foreach (float2 position in SamplePositions()){
                float expected = baseNoise.GetNoise(position);
                Assert.AreEqual(expected, identity.GetNoise(position), 1e-6f, $"null points at {position}");
                Assert.AreEqual(expected, emptyPoints.GetNoise(position), 1e-6f, $"empty points at {position}");
            }
        }

        [Test]
        public void WarpedNoiseLayer_WarpChannelsDiffer(){
            // F-06 intent at struct level: an explicit WarpSeedOffsetY decorrelates the X and Y warp channels.
            WarpedNoiseLayer layer = Layer(7, 1000, null);
            NoiseProfile warpX = WarpProfile(layer, "_warpX");
            NoiseProfile warpY = WarpProfile(layer, "_warpY");
            bool anyDifferent = false;
            foreach (float2 position in SamplePositions())
                anyDifferent |= !Mathf.Approximately(warpX.GetNoise(position), warpY.GetNoise(position));
            Assert.IsTrue(anyDifferent);
        }

        [Test]
        public void ApplyCurve_IdentityReturnsInput(){
            NativeArray<float2> identity = Points(new float2(0f, 0f), new float2(1f, 1f));
            for (float raw = 0f; raw <= 1f; raw += 0.125f)
                Assert.AreEqual(raw, NoiseCalculator.ApplyCurve(raw, identity), 1e-6f);
        }

        [Test]
        public void ApplyCurve_ClampsBelowAndAbove(){
            NativeArray<float2> curve = Points(new float2(0.2f, 0.1f), new float2(0.8f, 0.9f));
            Assert.AreEqual(0.1f, NoiseCalculator.ApplyCurve(-1f, curve), 1e-6f);
            Assert.AreEqual(0.1f, NoiseCalculator.ApplyCurve(0.2f, curve), 1e-6f);
            Assert.AreEqual(0.9f, NoiseCalculator.ApplyCurve(0.8f, curve), 1e-6f);
            Assert.AreEqual(0.9f, NoiseCalculator.ApplyCurve(5f, curve), 1e-6f);
        }

        [Test]
        public void ApplyCurve_IsPiecewiseLinear(){
            NativeArray<float2> curve = Points(new float2(0f, 0f), new float2(0.5f, 1f), new float2(1f, 0f));
            Assert.AreEqual(0.5f, NoiseCalculator.ApplyCurve(0.25f, curve), 1e-6f);
            Assert.AreEqual(1f, NoiseCalculator.ApplyCurve(0.5f, curve), 1e-6f);
            Assert.AreEqual(0.5f, NoiseCalculator.ApplyCurve(0.75f, curve), 1e-6f);
            Assert.AreEqual(0.2f, NoiseCalculator.ApplyCurve(0.1f, curve), 1e-6f);
        }

        [Test]
        public void WorldNoise_OutputIsInUnitRange(){
            NoiseCalculator.NoiseParameters parameters = Parameters(TestFixtures.Seed);
            foreach (float2 position in SamplePositions()){
                NoiseCalculator.WorldNoiseOutput output = NoiseCalculator.WorldNoise(position * 40f, ref parameters);
                Assert.That(output.Humidity, Is.InRange(0f, 1f));
                Assert.That(output.Temperature, Is.InRange(0f, 1f));
                Assert.That(output.Continental, Is.InRange(0f, 1f));
                Assert.That(output.Elevation, Is.InRange(0f, 1f));
            }
        }

        #endregion

        #region TST-E14

        [Category("KnownBug")] // F-02
        [Test]
        public void WorldNoise_SeedIsNotAPlainTranslation(){
            // F-02: the seed offsets (S, S) from the profiles and (-S, S) from WorldNoise add up to (0, 2S),
            // so WorldNoise(p, S) currently equals WorldNoise(p + (0, 2S), 0).
            const int seed = 123;
            NoiseCalculator.NoiseParameters seeded = Parameters(seed);
            NoiseCalculator.NoiseParameters unseeded = Parameters(0);
            bool anyDifferent = false;
            foreach (float2 position in SamplePositions()){
                NoiseCalculator.WorldNoiseOutput a = NoiseCalculator.WorldNoise(position, ref seeded);
                NoiseCalculator.WorldNoiseOutput b =
                    NoiseCalculator.WorldNoise(position + new float2(0f, 2f * seed), ref unseeded);
                anyDifferent |= !Mathf.Approximately(a.Elevation, b.Elevation)
                                || !Mathf.Approximately(a.Humidity, b.Humidity)
                                || !Mathf.Approximately(a.Temperature, b.Temperature)
                                || !Mathf.Approximately(a.Continental, b.Continental);
            }

            Assert.IsTrue(anyDifferent, "seed S must not be the same world as seed 0 shifted by (0, 2S)");
        }

        [Category("KnownBug")] // F-06
        [Test]
        public void NoiseSettings_ToStruct_CopiesWarpSeedOffsetY(){
            // F-06: WarpedNoiseLayerSettings.ToStruct never copies warpSeedOffsetY.
            _settings.continentalLayer.warpSeedOffsetY = 1000;
            WarpedNoiseLayer.Settings layerSettings = _settings.continentalLayer.ToStruct(TestFixtures.Seed);
            Assert.AreEqual(1000, layerSettings.WarpSeedOffsetY);
        }

        [Category("KnownBug")] // F-06
        [Test]
        public void NoiseSettings_ContinentalWarpChannelsDiffer(){
            // F-06: with the offset dropped, warp X and warp Y are the same noise field.
            NoiseCalculator.NoiseParameters parameters = Parameters(TestFixtures.Seed);
            NoiseProfile warpX = WarpProfile(parameters.ContinentalLayer, "_warpX");
            NoiseProfile warpY = WarpProfile(parameters.ContinentalLayer, "_warpY");
            bool anyDifferent = false;
            foreach (float2 position in SamplePositions())
                anyDifferent |= !Mathf.Approximately(warpX.GetNoise(position * 40f), warpY.GetNoise(position * 40f));
            Assert.IsTrue(anyDifferent, "warp X and Y must be decorrelated");
        }

        #endregion
    }
}
