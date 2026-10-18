using System;
using Engine.Scripts.WorldGen.Core;
using Engine.Scripts.WorldGen.Noise;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;

namespace Engine.Tests.EditMode{
    /// <summary>TST-11 (hash quality) and TST-13 (noise periodicity) for the Phase 0 noise basis.</summary>
    public class HashAndSimplexTests{
        private const uint Seed = 1337u;
        private const uint Salt = HashSalts.TemperatureNoise;

        #region TST-11

        [Test]
        public void Hash01_MeanIsHalf_AndUncorrelatedWithSwappedAndShiftedInputs(){
            // 10^6 samples over a 1000 x 1000 window straddling the origin.
            const int half = 500;
            const int count = 2 * half * 2 * half;
            double sum = 0, sumSwap = 0, sumShift = 0;
            double sumSq = 0, sumSqSwap = 0, sumSqShift = 0;
            double crossSwap = 0, crossShift = 0;
            for (int x = -half; x < half; x++)
            for (int z = -half; z < half; z++){
                double a = Hash.Hash01(x, z, Seed, Salt);
                double swapped = Hash.Hash01(z, x, Seed, Salt);
                double shifted = Hash.Hash01(x + 1, z, Seed, Salt);
                if (a < 0.0 || a >= 1.0) Assert.Fail($"Hash01 out of [0,1) at ({x},{z}): {a}");
                sum += a;
                sumSwap += swapped;
                sumShift += shifted;
                sumSq += a * a;
                sumSqSwap += swapped * swapped;
                sumSqShift += shifted * shifted;
                crossSwap += a * swapped;
                crossShift += a * shifted;
            }

            double mean = sum / count;
            Assert.AreEqual(0.5, mean, 0.002, "Hash01 mean");

            double corrSwap = Correlation(count, sum, sumSwap, sumSq, sumSqSwap, crossSwap);
            double corrShift = Correlation(count, sum, sumShift, sumSq, sumSqShift, crossShift);
            Debug.Log($"TST-11: mean {mean:F5}, corr(x,z)/(z,x) {corrSwap:E2}, corr(x,z)/(x+1,z) {corrShift:E2}");
            Assert.Less(Math.Abs(corrSwap), 0.01, "correlation between (x,z) and (z,x)");
            Assert.Less(Math.Abs(corrShift), 0.01, "correlation between (x,z) and (x+1,z)");
        }

        [Test]
        public void Hash32_FlippingOneInputBit_FlipsAboutHalfTheOutputBits(){
            // Avalanche: over many inputs, each single-bit flip of x, z, seed or salt changes ~16 of 32 output bits.
            double flipped = 0;
            int trials = 0;
            for (int i = 0; i < 2000; i++){
                int x = i * 7919 - 1000003;
                int z = i * 104729 + 17;
                uint baseline = Hash.Hash32(x, z, Seed, Salt);
                for (int bit = 0; bit < 32; bit++){
                    flipped += math.countbits(baseline ^ Hash.Hash32(x ^ (1 << bit), z, Seed, Salt));
                    flipped += math.countbits(baseline ^ Hash.Hash32(x, z ^ (1 << bit), Seed, Salt));
                    flipped += math.countbits(baseline ^ Hash.Hash32(x, z, Seed ^ (1u << bit), Salt));
                    flipped += math.countbits(baseline ^ Hash.Hash32(x, z, Seed, Salt ^ (1u << bit)));
                    trials += 4;
                }
            }

            double average = flipped / trials;
            Debug.Log($"TST-11: avalanche {average:F3} bits per flipped input bit");
            Assert.That(average, Is.InRange(15.5, 16.5));
        }

        [Test]
        public void Hash32_DifferentSaltsGiveDifferentFields(){
            int same = 0;
            for (int i = 0; i < 1000; i++)
                if (Hash.Hash32(i, -i, Seed, HashSalts.TemperatureNoise) == Hash.Hash32(i, -i, Seed, HashSalts.HumidityNoise))
                    same++;
            Assert.Less(same, 3);
        }

        [Test]
        public void HashSalts_DetailOctaveAndFbmOctave_AreDistinct(){
            for (int i = 0; i < 8; i++)
            for (int j = i + 1; j < 8; j++){
                Assert.AreNotEqual(HashSalts.DetailOctave(i), HashSalts.DetailOctave(j));
                Assert.AreNotEqual(HashSalts.FbmOctave(Salt, i), HashSalts.FbmOctave(Salt, j));
            }
        }

        private static double Correlation(int n, double sumA, double sumB, double sumA2, double sumB2, double sumAb){
            double cov = sumAb / n - sumA / n * (sumB / n);
            double varA = sumA2 / n - sumA / n * (sumA / n);
            double varB = sumB2 / n - sumB / n * (sumB / n);
            return cov / Math.Sqrt(varA * varB);
        }

        #endregion

        #region TST-13

        [Test]
        public void Simplex_OutputIsInRangeZeroCentredAndDeterministic(){
            double sum = 0;
            int count = 0;
            float min = 1f, max = -1f;
            for (int x = -200; x < 200; x++)
            for (int z = -200; z < 200; z++){
                float2 p = new float2(x, z) * 0.137f + 0.013f;
                float a = SeededSimplex.Noise(p, Seed, Salt);
                if (a != SeededSimplex.Noise(p, Seed, Salt)) Assert.Fail($"not deterministic at {p}");
                if (a < -1f || a > 1f) Assert.Fail($"out of range at {p}: {a}");
                sum += a;
                min = math.min(min, a);
                max = math.max(max, a);
                count++;
            }

            Debug.Log($"TST-13: simplex mean {sum / count:F4}, min {min:F3}, max {max:F3}");
            Assert.AreEqual(0.0, sum / count, 0.02, "simplex is zero-centred");
            Assert.Less(min, -0.6f, "simplex reaches well into the negative range");
            Assert.Greater(max, 0.6f, "simplex reaches well into the positive range");
        }

        [Test]
        public void Simplex_SeedAndSaltChangeTheField(){
            int sameSeed = 0, sameSalt = 0;
            for (int i = 0; i < 500; i++){
                float2 p = new float2(i * 0.73f, -i * 0.41f);
                float a = SeededSimplex.Noise(p, Seed, Salt);
                if (Mathf.Approximately(a, SeededSimplex.Noise(p, Seed + 1u, Salt))) sameSeed++;
                if (Mathf.Approximately(a, SeededSimplex.Noise(p, Seed, HashSalts.HumidityNoise))) sameSalt++;
            }

            Assert.Less(sameSeed, 25);
            Assert.Less(sameSalt, 25);
        }

        [Test]
        public void Simplex_IsNotATranslationUnderSeedChange(){
            // F-02 at the basis level: a new seed must not be the same field shifted along an axis.
            const int shiftRange = 64;
            for (int shift = -shiftRange; shift <= shiftRange; shift++){
                bool differsX = false, differsZ = false;
                for (int i = 0; i < 64 && !(differsX && differsZ); i++){
                    float2 p = new float2(i * 1.7f, i * 0.9f) / 16f;
                    float a = SeededSimplex.Noise(p, Seed, Salt);
                    differsX |= !Mathf.Approximately(a, SeededSimplex.Noise(p + new float2(shift, 0f), Seed + 1u, Salt));
                    differsZ |= !Mathf.Approximately(a, SeededSimplex.Noise(p + new float2(0f, shift), Seed + 1u, Salt));
                }

                Assert.IsTrue(differsX && differsZ, $"seed change equals a translation by {shift}");
            }
        }

        [TestCase(16f)]
        [TestCase(192f)]
        [TestCase(512f)]
        public void Simplex_HasNoSecondaryAutocorrelationPeak(float wavelength){
            // TST-13: 16k blocks along x, z and both diagonals. Beyond one wavelength the autocorrelation must stay
            // below 0.2 (a permutation-table noise shows a peak near 1 at its period, e.g. 289 noise units).
            foreach (float2 direction in NoiseStatistics.Directions){
                float peak = NoiseStatistics.MaxAutocorrelationBeyondWavelength(
                    p => SeededSimplex.Noise(p / wavelength, Seed, Salt), direction, wavelength, 1024, out int peakLag);
                Debug.Log($"TST-13: wavelength {wavelength}, dir {direction}: max |autocorr| {peak:F3} at lag {peakLag}");
                Assert.Less(peak, 0.2f, $"secondary autocorrelation peak along {direction} at lag {peakLag}");
            }
        }

        #endregion
    }
}
