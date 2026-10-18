using System;
using Unity.Mathematics;

namespace Engine.Tests.EditMode{
    /// <summary>Shared statistics for the TST-08/TST-13 noise tests.</summary>
    internal static class NoiseStatistics{
        /// <summary>The four TST-13 directions: x, z and both diagonals.</summary>
        internal static readonly float2[] Directions ={
            new(1f, 0f), new(0f, 1f),
            math.normalize(new float2(1f, 1f)), math.normalize(new float2(1f, -1f))
        };

        /// <summary>
        ///     Largest |autocorrelation| of <paramref name="sample" /> along <paramref name="direction" /> for lags
        ///     from one <paramref name="wavelength" /> up to 8k blocks, estimated over 16k-block lines (TST-13).
        ///     One line holds only 16384 / wavelength independent features, so the estimate is averaged over parallel
        ///     lines (3 wavelengths apart, i.e. independent) until it covers at least <paramref name="minFeatures" />.
        /// </summary>
        internal static float MaxAutocorrelationBeyondWavelength(Func<float2, float> sample, float2 direction,
            float wavelength, int minFeatures, out int peakLag){
            const int samples = 16384;
            int lines = math.max(1, (int)math.ceil(wavelength * minFeatures / samples));
            float2 perpendicular = new(-direction.y, direction.x);
            int lagStep = math.max(1, (int)(wavelength / 4f));
            int minLag = (int)math.ceil(wavelength);
            int lagCount = (samples / 2 - minLag) / lagStep + 1;
            double[] covariance = new double[lagCount];
            double variance = 0;
            float[] values = new float[samples];

            for (int line = 0; line < lines; line++){
                float2 origin = new float2(12345.5f, -6789.25f) + perpendicular * (line * 3f * wavelength);
                double mean = 0;
                for (int i = 0; i < samples; i++){
                    values[i] = sample(origin + direction * i);
                    mean += values[i];
                }

                mean /= samples;
                for (int i = 0; i < samples; i++){
                    values[i] -= (float)mean;
                    variance += values[i] * values[i] / samples;
                }

                for (int l = 0; l < lagCount; l++){
                    int lag = minLag + l * lagStep;
                    int n = samples - lag;
                    double cov = 0;
                    for (int i = 0; i < n; i++) cov += values[i] * values[i + lag];
                    covariance[l] += cov / n;
                }
            }

            float peak = 0f;
            peakLag = 0;
            for (int l = 0; l < lagCount; l++){
                float corr = (float)(covariance[l] / variance); // both sums run over the same number of lines
                if (math.abs(corr) <= peak) continue;
                peak = math.abs(corr);
                peakLag = minLag + l * lagStep;
            }

            return peak;
        }

        /// <summary>Histogram of <paramref name="values" /> over [<paramref name="min" />, <paramref name="max" />] as a log line.</summary>
        internal static string Histogram(float[] values, float min, float max, int bins = 10){
            int[] counts = new int[bins];
            foreach (float v in values){
                int bin = math.clamp((int)((v - min) / (max - min) * bins), 0, bins - 1);
                counts[bin]++;
            }

            string[] parts = new string[bins];
            for (int i = 0; i < bins; i++) parts[i] = (100f * counts[i] / values.Length).ToString("F1") + "%";
            return string.Join(" ", parts);
        }
    }
}
