using Engine.Scripts.VoxelConfig.Data.Generation;
using Unity.Burst;
using Unity.Collections;
using static Engine.Scripts.Jobs.Chunk.NoiseCalculator;

namespace Engine.Scripts.Jobs.Chunk
{
    [BurstCompile]
    public static class BiomeCalculator
    {
        public static ushort SelectBiome(ref WorldNoiseOutput worldNoise, ref GeneratorConfig config)
        {
            NativeArray<Biome.BiomeDef> bioms = config.BiomeDefs;
            return 0;
        }
    }
}