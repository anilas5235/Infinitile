using System;
using Engine.Scripts.Noise;
using Unity.Burst;
using Unity.Mathematics;

namespace Engine.Scripts.WorldGen.Noise{
    /// <summary>The five climate fields plus the derived PV of one column (§5.5). All a pure function of (x, z, seed, config).</summary>
    public struct WorldFields{
        /// <summary>Temperature in [0,1].</summary>
        public float T;

        /// <summary>Humidity in [0,1].</summary>
        public float F;

        /// <summary>Continentalness in [0,1] (0 = deep ocean, 1 = far inland).</summary>
        public float C;

        /// <summary>Erosion in [0,1]; low = rugged, high = flat (Minecraft convention).</summary>
        public float E;

        /// <summary>Weirdness in [-1,1].</summary>
        public float W;

        /// <summary>Peaks and valleys in [-1,1], derived from <see cref="W" /> (not an own noise).</summary>
        public float PV;
    }

    /// <summary>
    ///     Burst-side noise instances and couplings for <see cref="ClimateFields.Sample" />. Built once per settings
    ///     change by <c>WorldGenSettings.ToConfig</c>; owns its profiles and releases them in <see cref="Dispose" />
    ///     (D-05). A default instance (no profiles created) owns nothing and may be disposed.
    /// </summary>
    public struct ClimateParams : IDisposable{
        public NoiseProfile Temperature;
        public NoiseProfile Humidity;
        public WarpedNoiseLayer Continental;
        public NoiseProfile Erosion;
        public NoiseProfile Weirdness;

        /// <summary>How much a high continental value (inland) dries the climate out (DEC-06, kept from the legacy path).</summary>
        public float ContinentalHumidityInfluence;

        /// <summary>How much a high continental value (inland) widens temperature swings around 0.5 (DEC-06).</summary>
        public float ContinentalTemperatureInfluence;

        /// <summary>Gain applied to the zero-centred weirdness fBm before clamping (§5.5, default 1.6).</summary>
        public float WGain;

        /// <summary>Global frequency multiplier for T, F, C, E and W (GEN-07, default 1).</summary>
        public float FrequencyScale;

        public void Dispose(){
            Temperature.Dispose();
            Humidity.Dispose();
            Continental.Dispose();
            Erosion.Dispose();
            Weirdness.Dispose();
        }
    }

    /// <summary>
    ///     Computes T, F, C, E, W and PV once per column (GEN-07). Identical for both architectures and every blend
    ///     method, given seed and settings; the legacy path (INT-40) keeps its own Elevation field in NoiseCalculator
    ///     until GEN-08 retires it.
    /// </summary>
    [BurstCompile]
    public static class ClimateFields{
        /// <summary>Samples all fields at world position <paramref name="p" /> (blocks).</summary>
        public static WorldFields Sample(float2 p, in ClimateParams c){
            // GEN-07: ClimateFrequencyScale multiplies the frequency of every climate field, i.e. scales the position.
            float2 q = p * c.FrequencyScale;

            float continental = c.Continental.GetNoise(q);

            // Continentality shapes climate the way it does in the real world: inland areas (high C) dry out and swing
            // to more extreme temperatures in both directions, coasts stay humid and mild (DEC-06).
            float rawHumidity = c.Humidity.GetNoise(q);
            float humidity = math.saturate(rawHumidity - continental * c.ContinentalHumidityInfluence);

            float rawTemperature = c.Temperature.GetNoise(q);
            float temperatureSpread = 1f + continental * c.ContinentalTemperatureInfluence;
            float temperature = math.saturate(0.5f + (rawTemperature - 0.5f) * temperatureSpread);

            float erosion = c.Erosion.GetNoise(q);

            // W = clamp((2n - 1) * WGain, -1, 1): the gain compensates fBm's concentration around 0 so |W| reaches 2/3
            // often enough to create peaks (checked in TST-08).
            float weirdness = math.clamp((2f * c.Weirdness.GetNoise(q) - 1f) * c.WGain, -1f, 1f);

            return new WorldFields{
                T = temperature,
                F = humidity,
                C = continental,
                E = erosion,
                W = weirdness,
                PV = PeaksAndValleys(weirdness)
            };
        }

        /// <summary>
        ///     PV = 1 − |3·|W| − 2| after Minecraft Wiki, "World generation" (peaks and valleys).
        ///     W = 0 → −1 (valley); |W| = 1/3 → 0; |W| = 2/3 → 1 (peak); |W| = 1 → 0.
        /// </summary>
        public static float PeaksAndValleys(float weirdness){
            return 1f - math.abs(3f * math.abs(weirdness) - 2f);
        }
    }
}
