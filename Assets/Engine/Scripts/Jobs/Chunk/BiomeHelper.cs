using Unity.Burst;
using Unity.Mathematics;
#if UNITY_EDITOR
#endif

namespace Engine.Scripts.Jobs.Chunk
{
    /// <summary>
    ///     Helper utilities to select a biome based on climate parameters and to sample coverage (editor diagnostics).
    /// </summary>
    internal partial struct ChunkJob
    {
        /// <summary>
        ///     Selects a biome given temperature, humidity, elevation, ground height, water level threshold,
        ///     continentality and mountain mask, and returns the resulting biome classification.
        /// </summary>
        [BurstCompile]
        private static ushort SelectBiome(float temp, float hum, float elev, int groundY, int waterThreshold,
            float continentality, ref GeneratorConfig config)
        {
            var bioms = config.BiomeDefs;
            return 0;
        }
    }
}