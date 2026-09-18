using System;
using Engine.Scripts.Noise;
using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;

namespace Engine.Scripts.Jobs.Chunk{
    [BurstCompile]
    public static class NoiseCalculator{
        // Decorrelation offsets for each noise channel. Perlin/Simplex noise stays visibly correlated
        // (or anti-correlated) with itself under small translations - a shift needs to be well beyond
        // the largest scale/wavelength in play (continental goes up to ~2500) before two channels sampled
        // from the same base noise function actually behave like independent fields. Keep these an order
        // of magnitude past the biggest NoiseProfile/WarpedNoiseLayer scale you configure.
        private static readonly float2 HumidityOffset = new(15731f, -28411f);
        private static readonly float2 TemperatureOffset = new(-19207f, 33461f);
        private static readonly float2 ContinentalOffset = new(27851f, 12907f);
        private static readonly float2 ElevationOffset = new(-8461f, -19603f);

        public static WorldNoiseOutput WorldNoise(float2 worldPos, ref NoiseParameters noiseParams){
            float2 seed2D = new(-noiseParams.Seed, noiseParams.Seed);
            float2 noiseSamplePos = worldPos + seed2D;

            float continental = noiseParams.ContinentalLayer.GetNoise(worldPos + seed2D + ContinentalOffset);

            float rawHumidity = noiseParams.HumidityProfile.GetNoise(noiseSamplePos + HumidityOffset);
            float rawTemperature = noiseParams.TemperatureProfile.GetNoise(noiseSamplePos + TemperatureOffset);

            // Continentality shapes climate the way it does in the real world: inland areas (high
            // continental) dry out and swing to more extreme temperatures in both directions, while
            // coasts (low continental) stay humid and mild. Elevation itself isn't factored in here
            // (that's a further refinement - e.g. altitude cooling - left for later).
            float humidity = math.saturate(rawHumidity - continental * noiseParams.ContinentalHumidityInfluence);

            float temperatureSpread = 1f + continental * noiseParams.ContinentalTemperatureInfluence;
            float temperature = math.saturate(0.5f + (rawTemperature - 0.5f) * temperatureSpread);

            float rawElevation = noiseParams.ElevationProfile.GetNoise(noiseSamplePos + ElevationOffset);

            // Continental (already contrast-shaped via its redistribution curve) defines the large-scale
            // height shape. Elevation is applied as a centered detail offset on top of it rather than
            // averaged in, so it can't wash out continental's contrast back towards the middle of the range.
            float elevationDetail = (rawElevation - 0.5f) * noiseParams.ElevationDetailStrength;
            float elevation = math.saturate(continental + elevationDetail);

            return new WorldNoiseOutput{
                Humidity = humidity,
                Temperature = temperature,
                Continental = continental,
                Elevation = elevation
            };
        }

        public static BiomeCalculator.BiomSectionInput BiomSectionInput(this WorldNoiseOutput worldNoise){
            return new BiomeCalculator.BiomSectionInput{
                Humidity = worldNoise.Humidity,
                Temperature = worldNoise.Temperature,
                Continental = worldNoise.Continental,
                Elevation = worldNoise.Elevation
            };
        }

        [BurstCompile]
        public static float ApplyCurve(float raw, in NativeArray<float2> points){
            if (raw <= points[0].x) return points[0].y;
            int last = points.Length - 1;
            if (raw >= points[last].x) return points[last].y;

            for (int i = 1; i < points.Length; i++){
                if (raw > points[i].x) continue;
                float2 a = points[i - 1];
                float2 b = points[i];
                float t = math.unlerp(a.x, b.x, raw);
                return math.lerp(a.y, b.y, t);
            }

            return raw;
        }

        public struct NoiseParameters : IDisposable{
            public int Seed;
            public float ElevationDetailStrength;

            /// <summary>How much a high continental value (inland) dries the climate out.</summary>
            public float ContinentalHumidityInfluence;

            /// <summary>How much a high continental value (inland) widens temperature swings around 0.5.</summary>
            public float ContinentalTemperatureInfluence;

            [ReadOnly] public NoiseProfile ElevationProfile;

            [ReadOnly] public NoiseProfile HumidityProfile;

            [ReadOnly] public NoiseProfile TemperatureProfile;

            [ReadOnly] public WarpedNoiseLayer ContinentalLayer;

            public void Dispose(){
                ElevationProfile.Dispose();
                HumidityProfile.Dispose();
                TemperatureProfile.Dispose();
                ContinentalLayer.Dispose();
            }
        }

        public struct WorldNoiseOutput{
            public float Humidity;
            public float Temperature;
            public float Continental;
            public float Elevation;
        }
    }
}
