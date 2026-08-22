using Engine.Scripts.Noise;
using Unity.Burst;
using Unity.Mathematics;

namespace Engine.Scripts.Jobs.Chunk
{
    [BurstCompile]
    public static class NoiseCalculator
    {
        private static readonly float2 HumidityOffset = new(-89f, 311f);
        private static readonly float2 TemperatureOffset = new(867f, -543f);
        private static readonly float2 ContinentalOffset = new(-916f, 823f);

        public static WorldNoiseOutput WorldNoise(float2 worldPos, ref NoiseParameters noiseParams,
            ref NoiseProfile noiseProfile)
        {
            float2 seed2D = new(-noiseParams.Seed, noiseParams.Seed);
            float2 noiseSamplePos = worldPos + seed2D;

            float humidity = GetNormalizedCNoise(noiseSamplePos, HumidityOffset, noiseParams.HumidityScale);
            float temperature = GetNormalizedCNoise(noiseSamplePos, TemperatureOffset, noiseParams.TemperatureScale);
            float continental = GetNormalizedCNoise(noiseSamplePos, ContinentalOffset, noiseParams.ContinentalScale);

            float rawElevation = noiseProfile.GetNoise(noiseSamplePos);

            float elevation = math.saturate(continental * 0.7f + rawElevation * 0.3f);

            return new WorldNoiseOutput
            {
                Humidity = humidity,
                Temperature = temperature,
                Continental = continental,
                Elevation = elevation
            };
        }

        public struct NoiseParameters
        {
            public int Seed;
            public float HumidityScale;
            public float TemperatureScale;
            public float ContinentalScale;
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
    }
}