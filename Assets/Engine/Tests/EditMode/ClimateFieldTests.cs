using System;
using Engine.Scripts.Noise;
using Engine.Scripts.WorldGen.Core;
using Engine.Scripts.WorldGen.Noise;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Engine.Tests.EditMode{
    /// <summary>Field sanity and periodicity of the detail octaves, P and the climate fields.</summary>
    public class ClimateFieldTests{
        private const uint Seed = 1337u;
        private ClimateParams _climate;
        private NativeArray<float> _wavelengths;

        [SetUp]
        public void SetUp(){
            _climate = CreateClimate(Seed);
            _wavelengths = new NativeArray<float>(DetailOctaves.DefaultOctaveCount, Allocator.Persistent);
            for (int i = 0; i < _wavelengths.Length; i++) _wavelengths[i] = DetailOctaves.DefaultWavelength(i);
        }

        [TearDown]
        public void TearDown(){
            _climate.Dispose();
            _wavelengths.Dispose();
        }

        /// <summary>Default scales: T 900, F 700, C 2500/warp 400, E 1200 × 4, W 500 × 3, WGain 1.6.</summary>
        internal static ClimateParams CreateClimate(uint seed, float frequencyScale = 1f, float wGain = 1.6f){
            NoiseProfile.Settings Fbm(float scale, int octaves, uint salt){
                return new NoiseProfile.Settings{
                    Seed = (int)seed, Salt = salt, Scale = scale, Persistance = 0.5f, Lacunarity = 2f, Octaves = octaves
                };
            }

            return new ClimateParams{
                Temperature = new NoiseProfile(Fbm(900f, 3, HashSalts.TemperatureNoise)),
                Humidity = new NoiseProfile(Fbm(700f, 3, HashSalts.HumidityNoise)),
                Continental = new WarpedNoiseLayer(new WarpedNoiseLayer.Settings{
                    BaseNoise = Fbm(2500f, 5, HashSalts.ContinentalNoise),
                    WarpNoise = Fbm(1800f, 2, HashSalts.ContinentalWarpX),
                    WarpSaltY = HashSalts.ContinentalWarpY,
                    WarpStrength = 400f,
                    RedistributionPoints = new[]{
                        new float2(0f, 0.02f), new float2(0.3f, 0.12f), new float2(0.4f, 0.28f), new float2(0.5f, 0.42f),
                        new float2(0.6f, 0.55f), new float2(0.8f, 0.78f), new float2(1f, 1f)
                    }
                }),
                Erosion = new NoiseProfile(Fbm(1200f, 4, HashSalts.ErosionNoise)),
                Weirdness = new NoiseProfile(Fbm(500f, 3, HashSalts.WeirdnessNoise)),
                ContinentalHumidityInfluence = 0.4f,
                ContinentalTemperatureInfluence = 0.6f,
                WGain = wGain,
                FrequencyScale = frequencyScale
            };
        }

        #region Field sanity

        [Test]
        public void Fields_AreInRange_NotDegenerate_AndHistogrammed(){
            // 256 x 256 columns, 8 blocks apart: a 2048 x 2048 block window.
            const int n = 256;
            const float stride = 8f;
            float[] t = new float[n * n], f = new float[n * n], c = new float[n * n], e = new float[n * n],
                w = new float[n * n], pv = new float[n * n];
            int peakColumns = 0;
            for (int ix = 0; ix < n; ix++)
            for (int iz = 0; iz < n; iz++){
                int k = ix * n + iz;
                WorldFields s = ClimateFields.Sample(new float2(ix * stride - 1024f, iz * stride - 1024f), _climate);
                t[k] = s.T;
                f[k] = s.F;
                c[k] = s.C;
                e[k] = s.E;
                w[k] = s.W;
                pv[k] = s.PV;
                if (s.T < 0f || s.T > 1f || s.F < 0f || s.F > 1f || s.C < 0f || s.C > 1f || s.E < 0f || s.E > 1f)
                    Assert.Fail($"[0,1] field out of range at ({ix},{iz}): T {s.T} F {s.F} C {s.C} E {s.E}");
                if (s.W < -1f || s.W > 1f || s.PV < -1f || s.PV > 1f)
                    Assert.Fail($"[-1,1] field out of range at ({ix},{iz}): W {s.W} PV {s.PV}");
                if (math.abs(s.PV - ClimateFields.PeaksAndValleys(s.W)) > 1e-6f) Assert.Fail("PV must derive from W");
                if (math.abs(s.W) >= 2f / 3f) peakColumns++;
            }

            float peakShare = peakColumns / (float)(n * n);
            Debug.Log($"Climate histogram T  {NoiseStatistics.Histogram(t, 0f, 1f)}");
            Debug.Log($"Climate histogram F  {NoiseStatistics.Histogram(f, 0f, 1f)}");
            Debug.Log($"Climate histogram C  {NoiseStatistics.Histogram(c, 0f, 1f)}");
            Debug.Log($"Climate histogram E  {NoiseStatistics.Histogram(e, 0f, 1f)}");
            Debug.Log($"Climate histogram W  {NoiseStatistics.Histogram(w, -1f, 1f)}");
            Debug.Log($"Climate histogram PV {NoiseStatistics.Histogram(pv, -1f, 1f)}");
            Debug.Log($"Climate fields: share of |W| >= 2/3 (peak columns): {peakShare:P2}");

            AssertSpread(t, "T");
            AssertSpread(f, "F");
            AssertSpread(c, "C");
            AssertSpread(e, "E");
            AssertSpread(w, "W");
            Assert.Greater(peakShare, 0.01f, "with WGain 1.6 some columns must reach |W| >= 2/3");
        }

        private static void AssertSpread(float[] values, string name){
            double mean = 0, sq = 0;
            foreach (float v in values){
                mean += v;
                sq += v * v;
            }

            mean /= values.Length;
            double std = Math.Sqrt(Math.Max(0, sq / values.Length - mean * mean));
            Assert.Greater(std, 0.05, $"{name} is degenerate (std {std:F4})");
        }

        [Test]
        public void PeaksAndValleys_MatchesTheMinecraftIdentities(){
            Assert.AreEqual(-1f, ClimateFields.PeaksAndValleys(0f), 1e-6f);
            Assert.AreEqual(1f, ClimateFields.PeaksAndValleys(2f / 3f), 1e-6f);
            Assert.AreEqual(1f, ClimateFields.PeaksAndValleys(-2f / 3f), 1e-6f);
            Assert.AreEqual(0f, ClimateFields.PeaksAndValleys(1f / 3f), 1e-6f);
            Assert.AreEqual(0f, ClimateFields.PeaksAndValleys(-1f / 3f), 1e-6f);
            Assert.AreEqual(0f, ClimateFields.PeaksAndValleys(1f), 1e-6f);
            Assert.AreEqual(0f, ClimateFields.PeaksAndValleys(-1f), 1e-6f);
        }

        [Test]
        public void Fields_AreDeterministicAndSeedDependent(){
            ClimateParams same = CreateClimate(Seed);
            ClimateParams other = CreateClimate(Seed + 1u);
            try{
                int differ = 0;
                for (int i = 0; i < 200; i++){
                    float2 p = new(i * 97.3f - 5000f, i * 41.7f + 300f);
                    WorldFields a = ClimateFields.Sample(p, _climate);
                    WorldFields b = ClimateFields.Sample(p, same);
                    Assert.AreEqual(a.T, b.T);
                    Assert.AreEqual(a.F, b.F);
                    Assert.AreEqual(a.C, b.C);
                    Assert.AreEqual(a.E, b.E);
                    Assert.AreEqual(a.W, b.W);
                    WorldFields o = ClimateFields.Sample(p, other);
                    if (!Mathf.Approximately(a.E, o.E) && !Mathf.Approximately(a.W, o.W)) differ++;
                }

                Assert.Greater(differ, 150);
            }
            finally{
                same.Dispose();
                other.Dispose();
            }
        }

        [Test]
        public void ClimateFrequencyScale_ScalesThePosition(){
            // Scale 2 at p equals scale 1 at 2p for every field.
            ClimateParams doubled = CreateClimate(Seed, 2f);
            try{
                for (int i = 0; i < 100; i++){
                    float2 p = new(i * 53.1f - 2000f, -i * 29.7f + 900f);
                    WorldFields a = ClimateFields.Sample(p, doubled);
                    WorldFields b = ClimateFields.Sample(p * 2f, _climate);
                    Assert.AreEqual(b.T, a.T, 1e-6f);
                    Assert.AreEqual(b.F, a.F, 1e-6f);
                    Assert.AreEqual(b.C, a.C, 1e-6f);
                    Assert.AreEqual(b.E, a.E, 1e-6f);
                    Assert.AreEqual(b.W, a.W, 1e-6f);
                }
            }
            finally{
                doubled.Dispose();
            }
        }

        [Test]
        public void DetailOctaves_AreZeroCentredInRange_AndSampleAllMatchesSample(){
            double[] sums = new double[_wavelengths.Length];
            const int n = 128;
            for (int ix = 0; ix < n; ix++)
            for (int iz = 0; iz < n; iz++){
                float2 p = new(ix * 7.3f - 300f, iz * 5.1f + 100f);
                FixedList64Bytes<float> all = DetailOctaves.SampleAll(p, _wavelengths, Seed);
                Assert.AreEqual(_wavelengths.Length, all.Length);
                for (int i = 0; i < all.Length; i++){
                    float single = DetailOctaves.Sample(i, p, _wavelengths[i], Seed);
                    if (all[i] != single) Assert.Fail($"SampleAll differs from Sample for octave {i}");
                    if (single < -1f || single > 1f) Assert.Fail($"octave {i} out of range: {single}");
                    sums[i] += single;
                }
            }

            for (int i = 0; i < sums.Length; i++)
                Assert.AreEqual(0.0, sums[i] / (n * n), 0.05, $"octave {i} is zero-centred");
        }

        [Test]
        public void PeakNoise_IsInUnitRange_AndReachesRidges(){
            float max = 0f, min = 1f;
            for (int ix = 0; ix < 256; ix++)
            for (int iz = 0; iz < 256; iz++){
                float p = PeakNoise.Sample(new float2(ix * 3.7f, iz * 2.9f), PeakNoise.DefaultWavelength, Seed);
                if (p < 0f || p > 1f) Assert.Fail($"P out of range: {p}");
                max = math.max(max, p);
                min = math.min(min, p);
            }

            Assert.Greater(max, 0.95f, "ridges (n near 0) must reach P near 1");
            Assert.Less(min, 0.1f, "away from ridges P must drop towards 0");
        }

        #endregion

        #region Periodicity

        [Test]
        public void DetailOctavesAndPeak_HaveNoSecondaryAutocorrelationPeak(){
            for (int i = 0; i < _wavelengths.Length; i++){
                int octave = i;
                float wavelength = _wavelengths[i];
                AssertNoSecondaryPeak($"N{octave} ({wavelength})", wavelength, 1024,
                    p => DetailOctaves.Sample(octave, p, wavelength, Seed));
            }

            AssertNoSecondaryPeak("P (192)", PeakNoise.DefaultWavelength, 1024,
                p => PeakNoise.Sample(p, PeakNoise.DefaultWavelength, Seed));
        }

        [Test]
        public void Fields_HaveNoSecondaryAutocorrelationPeak(){
            // Base-octave scales as in CreateClimate. Each field is evaluated through its own noise instance (the continental
            // coupling of T and F only rescales values and cannot add a period), and the large-scale fields use fewer
            // features, so the whole test stays within a few seconds in Mono.
            AssertNoSecondaryPeak("T (900)", 900f, 256, p => _climate.Temperature.GetNoise(p));
            AssertNoSecondaryPeak("F (700)", 700f, 256, p => _climate.Humidity.GetNoise(p));
            AssertNoSecondaryPeak("E (1200)", 1200f, 256, p => _climate.Erosion.GetNoise(p));
            AssertNoSecondaryPeak("W (500)", 500f, 256, p => _climate.Weirdness.GetNoise(p));
            AssertNoSecondaryPeak("C (2500)", 2500f, 128, p => _climate.Continental.GetNoise(p));
        }

        private static void AssertNoSecondaryPeak(string name, float wavelength, int minFeatures, Func<float2, float> sample){
            foreach (float2 direction in NoiseStatistics.Directions){
                float peak = NoiseStatistics.MaxAutocorrelationBeyondWavelength(sample, direction, wavelength,
                    minFeatures, out int lag);
                Debug.Log($"Autocorrelation {name} dir {direction}: max |autocorr| {peak:F3} at lag {lag}");
                Assert.Less(peak, 0.2f, $"{name}: secondary autocorrelation peak along {direction} at lag {lag}");
            }
        }

        #endregion
    }
}
