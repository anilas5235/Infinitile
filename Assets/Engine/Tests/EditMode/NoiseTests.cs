using System.Collections.Generic;
using System.Reflection;
using Engine.Scripts.WorldGen.Core;
using Engine.Scripts.WorldGen.Noise;
using Engine.Scripts.WorldGen.Settings;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Engine.Tests.EditMode{
    /// <summary>NoiseProfile and WarpedNoiseLayer: seeding, salts, redistribution and warp channels.</summary>
    public class NoiseTests{
        private readonly List<NoiseProfile> _profiles = new();
        private readonly List<WarpedNoiseLayer> _layers = new();
        private WorldGenSettings _settings;

        [SetUp]
        public void SetUp(){
            _settings = ScriptableObject.CreateInstance<WorldGenSettings>();
        }

        [TearDown]
        public void TearDown(){
            foreach (NoiseProfile profile in _profiles) profile.Dispose();
            foreach (WarpedNoiseLayer layer in _layers) layer.Dispose();
            _profiles.Clear();
            _layers.Clear();
            Object.DestroyImmediate(_settings);
        }

        private NoiseProfile Profile(int seed, float scale = 50f, int octaves = 4, uint salt = HashSalts.TemperatureNoise){
            NoiseProfile profile = new(new NoiseProfile.Settings{
                Seed = seed, Salt = salt, Scale = scale, Persistance = 0.5f, Lacunarity = 2f, Octaves = octaves
            });
            _profiles.Add(profile);
            return profile;
        }

        private WarpedNoiseLayer Layer(int seed, float2[] points, float warpStrength = 40f){
            WarpedNoiseLayer layer = new(new WarpedNoiseLayer.Settings{
                BaseNoise = new NoiseProfile.Settings{
                    Seed = seed, Salt = HashSalts.ContinentalNoise, Scale = 120f, Persistance = 0.5f, Lacunarity = 2f, Octaves = 3
                },
                WarpNoise = new NoiseProfile.Settings{
                    Seed = seed, Salt = HashSalts.ContinentalWarpX, Scale = 90f, Persistance = 0.5f, Lacunarity = 2f, Octaves = 2
                },
                WarpSaltY = HashSalts.ContinentalWarpY,
                WarpStrength = warpStrength,
                RedistributionPoints = points
            });
            _layers.Add(layer);
            return layer;
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

        #region NoiseProfile and WarpedNoiseLayer

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
            WarpedNoiseLayer a = Layer(7, points);
            WarpedNoiseLayer b = Layer(7, points);
            foreach (float2 position in SamplePositions()){
                float value = a.GetNoise(position);
                Assert.That(value, Is.InRange(0f, 1f), $"at {position}");
                Assert.AreEqual(value, b.GetNoise(position), $"at {position}");
            }
        }

        [Test]
        public void WarpedNoiseLayer_WithoutPoints_UsesIdentityCurve(){
            WarpedNoiseLayer identity = Layer(7, null, 0f);
            WarpedNoiseLayer emptyPoints = Layer(7, new float2[0], 0f);
            NoiseProfile baseNoise = WarpProfile(identity, "_baseNoise");
            foreach (float2 position in SamplePositions()){
                float expected = baseNoise.GetNoise(position);
                Assert.AreEqual(expected, identity.GetNoise(position), 1e-6f, $"null points at {position}");
                Assert.AreEqual(expected, emptyPoints.GetNoise(position), 1e-6f, $"empty points at {position}");
            }
        }

        [Test]
        public void WarpedNoiseLayer_Redistribution_ClampsOutsideAndIsPiecewiseLinearInside(){
            // The curve covers only [0.4, 0.6] of the raw range: below the first point it returns the first y, above
            // the last point the last y, and between points it interpolates linearly (3 points, 2 segments).
            float2[] points ={ new(0.4f, 0.1f), new(0.5f, 0.3f), new(0.6f, 0.9f) };
            WarpedNoiseLayer curved = Layer(7, points, 0f);
            NoiseProfile baseNoise = WarpProfile(Layer(7, null, 0f), "_baseNoise");
            int below = 0, above = 0, inside = 0;
            foreach (float2 position in SamplePositions()){
                float raw = baseNoise.GetNoise(position);
                float expected;
                if (raw <= 0.4f){
                    expected = 0.1f;
                    below++;
                }
                else if (raw >= 0.6f){
                    expected = 0.9f;
                    above++;
                }
                else{
                    expected = raw <= 0.5f ? math.lerp(0.1f, 0.3f, (raw - 0.4f) / 0.1f) : math.lerp(0.3f, 0.9f, (raw - 0.5f) / 0.1f);
                    inside++;
                }

                Assert.AreEqual(expected, curved.GetNoise(position), 1e-5f, $"raw {raw} at {position}");
            }

            Assert.That(below, Is.GreaterThan(0), "no sample below the curve");
            Assert.That(above, Is.GreaterThan(0), "no sample above the curve");
            Assert.That(inside, Is.GreaterThan(0), "no sample inside the curve");
        }

        [Test]
        public void WarpedNoiseLayer_WarpChannelsDiffer(){
            // The Y channel has its own salt, decorrelating the X and Y warp channels.
            WarpedNoiseLayer layer = Layer(7, null);
            NoiseProfile warpX = WarpProfile(layer, "_warpX");
            NoiseProfile warpY = WarpProfile(layer, "_warpY");
            bool anyDifferent = false;
            foreach (float2 position in SamplePositions())
                anyDifferent |= !Mathf.Approximately(warpX.GetNoise(position), warpY.GetNoise(position));
            Assert.IsTrue(anyDifferent);
        }

        [Test]
        public void NoiseProfile_DifferentSaltsGiveDifferentFields(){
            // Two profiles with identical seed and shape but different salts are different fields.
            NoiseProfile a = Profile(42);
            NoiseProfile b = Profile(42, salt: HashSalts.HumidityNoise);
            int same = 0, total = 0;
            foreach (float2 position in SamplePositions()){
                total++;
                if (Mathf.Approximately(a.GetNoise(position), b.GetNoise(position))) same++;
            }

            Assert.Less(same, total / 10, "salts must decorrelate the fields");
        }

        [Test]
        public void NoiseProfile_HashedOffsetIsBounded(){
            // |o| <= 10^4 for every seed and salt.
            for (uint seed = 0; seed < 2000; seed += 7){
                float2 offset = NoiseProfile.HashedOffset(seed, HashSalts.TemperatureNoise);
                Assert.LessOrEqual(math.abs(offset.x), NoiseProfile.MaxOffset);
                Assert.LessOrEqual(math.abs(offset.y), NoiseProfile.MaxOffset);
            }

            Assert.AreNotEqual(NoiseProfile.HashedOffset(1u, HashSalts.TemperatureNoise),
                NoiseProfile.HashedOffset(1u, HashSalts.HumidityNoise));
        }

        #endregion

        #region Warp channels

        [Test]
        public void WorldGenSettings_ContinentalWarpChannelsDiffer(){
            // Warp X and warp Y must be different noise fields (own salts), built the way WorldGenSettings.ToConfig
            // builds the continental layer.
            WarpedNoiseLayer layer = new(_settings.continentalLayer.ToStruct(TestFixtures.Seed,
                HashSalts.ContinentalNoise, HashSalts.ContinentalWarpX, HashSalts.ContinentalWarpY));
            _layers.Add(layer);
            NoiseProfile warpX = WarpProfile(layer, "_warpX");
            NoiseProfile warpY = WarpProfile(layer, "_warpY");
            bool anyDifferent = false;
            foreach (float2 position in SamplePositions())
                anyDifferent |= !Mathf.Approximately(warpX.GetNoise(position * 40f), warpY.GetNoise(position * 40f));
            Assert.IsTrue(anyDifferent, "warp X and Y must be decorrelated");
        }

        #endregion
    }
}
