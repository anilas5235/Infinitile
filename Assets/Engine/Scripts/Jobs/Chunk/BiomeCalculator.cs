using Engine.Scripts.VoxelConfig.Data.Generation;
using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;
using static Engine.Scripts.Jobs.Chunk.NoiseCalculator;

namespace Engine.Scripts.Jobs.Chunk
{
    [BurstCompile]
    public static class BiomeCalculator
    {
        public struct BiomSectionInput
        {
            public float Humidity;
            public float Temperature;
            public float Continental;
            public float Height;
        }
        public static ushort SelectBiome(ref BiomSectionInput input, ref GeneratorConfig config)
        {
            NativeArray<Biome.BiomeDef> bioms = config.BiomeDefs;
            if (bioms.Length == 0)
            {
                return 0;
            }

            ushort bestIndex = 0;
            float bestDistance = float.MaxValue;

            for (ushort i = 0; i < bioms.Length; i++)
            {
                Biome.BiomeDef biome = bioms[i];
                float distance = ClimateDistanceSq(
                    input.Humidity,
                    input.Temperature,
                    input.Continental,
                    biome.targetHumidity,
                    biome.targetTemperature,
                    biome.targetContinental);

                if (distance >= bestDistance)
                {
                    continue;
                }

                bestDistance = distance;
                bestIndex = i;
            }

            return bestIndex;
        }

        public static float ClimateDistanceSq(
            float humidity,
            float temperature,
            float continental,
            float targetHumidity,
            float targetTemperature,
            float targetContinental)
        {
            float dh = humidity - targetHumidity;
            float dt = temperature - targetTemperature;
            float dc = continental - targetContinental;
            return math.lengthsq(new float3(dh, dt, dc));
        }
    }
}