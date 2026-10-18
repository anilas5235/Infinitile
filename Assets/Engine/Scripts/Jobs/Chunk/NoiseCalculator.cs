using System;
using Engine.Scripts.Noise;
using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;

namespace Engine.Scripts.Jobs.Chunk{
    [BurstCompile]
    public static class NoiseCalculator{
        /// <summary>
        ///     Legacy climate sampler (INT-40). Every profile carries its own gradient salt and hashed sample offset
        ///     (GEN-05), so no seed or decorrelation offset is added to the position here any more (D-03, F-02).
        /// </summary>
        public static WorldNoiseOutput WorldNoise(float2 worldPos, ref NoiseParameters noiseParams){
            float continental = noiseParams.ContinentalLayer.GetNoise(worldPos);

            float rawHumidity = noiseParams.HumidityProfile.GetNoise(worldPos);
            float rawTemperature = noiseParams.TemperatureProfile.GetNoise(worldPos);

            // Continentality shapes climate the way it does in the real world: inland areas (high
            // continental) dry out and swing to more extreme temperatures in both directions, while
            // coasts (low continental) stay humid and mild. Elevation itself isn't factored in here
            // (that's a further refinement - e.g. altitude cooling - left for later).
            float humidity = math.saturate(rawHumidity - continental * noiseParams.ContinentalHumidityInfluence);

            float temperatureSpread = 1f + continental * noiseParams.ContinentalTemperatureInfluence;
            float temperature = math.saturate(0.5f + (rawTemperature - 0.5f) * temperatureSpread);

            float rawElevation = noiseParams.ElevationProfile.GetNoise(worldPos);

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
            /// <summary>World seed the profiles were built from (informational; the profiles carry it themselves).</summary>
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
