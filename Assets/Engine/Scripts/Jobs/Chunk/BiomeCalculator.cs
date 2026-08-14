using Engine.Scripts.VoxelConfig.Data.Generation;
using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;

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
                float dh = input.Humidity - biome.targetHumidity;
                float dt = input.Temperature - biome.targetTemperature;
                float dc = input.Continental - biome.targetContinental;
                float dHeight = input.Height - biome.targetHeight;
                float distance = math.lengthsq(new float4(dh, dt, dc, dHeight));

                if (distance >= bestDistance)
                {
                    continue;
                }

                bestDistance = distance;
                bestIndex = i;
            }

            return bestIndex;
        }
    }
}