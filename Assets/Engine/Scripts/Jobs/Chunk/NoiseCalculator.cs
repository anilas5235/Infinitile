using System;
using Engine.Scripts.Noise;
using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;

namespace Engine.Scripts.Jobs.Chunk
{
    [BurstCompile]
    public static class NoiseCalculator
    {
        private static readonly float2 HumidityOffset = new(-89f, 311f);
        private static readonly float2 TemperatureOffset = new(867f, -543f);
        private static readonly float2 ContinentalOffset = new(-916f, 823f);

        public static WorldNoiseOutput WorldNoise(float2 worldPos, ref NoiseParameters noiseParams)
        {
            float2 seed2D = new(-noiseParams.Seed, noiseParams.Seed);
            float2 noiseSamplePos = worldPos + seed2D;

            float humidity = GetNormalizedCNoise(noiseSamplePos, HumidityOffset, noiseParams.HumidityScale);
            float temperature = GetNormalizedCNoise(noiseSamplePos, TemperatureOffset, noiseParams.TemperatureScale);

            float continental = noiseParams.ContinentalLayer.GetNoise(worldPos + seed2D + ContinentalOffset);

            float rawElevation = noiseParams.ElevationProfile.GetNoise(noiseSamplePos);

            float elevation = math.saturate(continental * .6f+ rawElevation * .4f);

            return new WorldNoiseOutput
            {
                Humidity = humidity,
                Temperature = temperature,
                Continental = continental,
                Elevation = elevation
            };
        }

        public struct NoiseParameters : IDisposable
        {
            public int Seed;
            public float HumidityScale;
            public float TemperatureScale;

            [ReadOnly] public NoiseProfile ElevationProfile;

            [ReadOnly] public WarpedNoiseLayer ContinentalLayer;

            public void Dispose()
            {
                ElevationProfile.Dispose();
                ContinentalLayer.Dispose();
            }
        }

        public struct WorldNoiseOutput
        {
            public float Humidity;
            public float Temperature;
            public float Continental;
            public float Elevation;
        }

        public static BiomeCalculator.BiomSectionInput BiomSectionInput(this WorldNoiseOutput worldNoise)
        {
            return new BiomeCalculator.BiomSectionInput
            {
                Humidity = worldNoise.Humidity,
                Temperature = worldNoise.Temperature,
                Continental = worldNoise.Continental,
                Elevation = worldNoise.Elevation
            };
        }

        public static float GetNormalizedCNoise(float2 position, float2 seed, float scale)
        {
            float2 samplePos = (position + seed) * scale;
            return math.remap(-1f, 1f, 0f, 1f, noise.cnoise(samplePos));
        }

        [BurstCompile]
        public static float ApplyCurve(float raw, in NativeArray<float2> points)
        {
            if (raw <= points[0].x) return points[0].y;
            int last = points.Length - 1;
            if (raw >= points[last].x) return points[last].y;

            for (int i = 1; i < points.Length; i++)
            {
                if (raw > points[i].x) continue;
                float2 a = points[i - 1];
                float2 b = points[i];
                float t = math.unlerp(a.x, b.x, raw);
                return math.lerp(a.y, b.y, t);
            }

            return raw;
        }
    }
}