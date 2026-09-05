using Engine.Scripts.Noise;
using Engine.Scripts.Utils.Extensions;
using Engine.Scripts.VoxelConfig.Data.Generation;
using Unity.Collections;
using Unity.Mathematics;
using static Engine.Scripts.Jobs.Chunk.NoiseCalculator;
using static Engine.Scripts.Utils.VoxelConstants;

namespace Engine.Scripts.Jobs.Chunk
{
    /// <summary>
    ///     Provides Burst-compiled helpers to prepare biome-aware terrain metadata and fill voxel buffers
    ///     for a single chunk based on noise, configuration and climate.
    /// </summary>
    internal partial struct ChunkJob
    {
        private const float BiomeScale = 0.0012f;

        /// <summary>
        ///     Computes climate values, terrain height and biome information for every column of a chunk.
        /// </summary>
        /// <param name="noiseProfile">Noise profile used to sample base terrain height.</param>
        /// <param name="randomSeed">Random seed used to offset noise sampling for deterministic variation.</param>
        /// <param name="config">Generator configuration providing water level and voxel IDs.</param>
        /// <param name="chunkWordPos">World-space origin (in voxels) of the chunk.</param>
        /// <param name="chunkColumns">Output array that will receive per-column height, biome and climate data.</param>
        public static void PrepareChunkMaps(int randomSeed, ref GeneratorConfig config, ref int3 chunkWordPos,
            NativeArray<ChunkColumn> chunkColumns)
        {
            for (int x = 0; x < ChunkWidth; x++)
            for (int z = 0; z < ChunkDepth; z++)
            {
                int i = GetColumnIdx(x, z, ChunkDepth);
                float2 worldPos = new(chunkWordPos.x + x, chunkWordPos.z + z);

                WorldNoiseOutput worldNoise = WorldNoise(worldPos, ref config.NoiseParams);

                const int minY = 100;
                const int maxY = ChunkHeight - 1;
                const int rangeY = maxY - minY;
                int height = math.clamp(minY + (int)(worldNoise.Elevation * rangeY), 0, maxY);

                BiomeCalculator.BiomSectionInput input = worldNoise.BiomSectionInput();
                ChunkColumn col = new()
                {
                    Height = height,
                    Biome = BiomeCalculator.SelectBiome(ref input, ref config),
                    Temperature = worldNoise.Temperature,
                    Humidity = worldNoise.Humidity
                };
                uint seed = (uint)((chunkWordPos.x + x) ^ (chunkWordPos.z + z) ^ randomSeed ^ 0x85ebca6b);
                Random rng = new(seed == 0 ? 1u : seed);
                SelectSurfaceMaterials(ref config, ref col, ref rng);
                chunkColumns[i] = col;
            }
        }

        /// <summary>
        ///     Fills the voxel buffer for a chunk with terrain blocks based on the prepared column data
        ///     and configuration values.
        /// </summary>
        /// <param name="vox">Voxel buffer to write to (one entry per voxel).</param>
        /// <param name="waterLevel">Global water level used to place water or surface blocks.</param>
        /// <param name="chunkColumns">Per-column terrain metadata produced by <see cref="PrepareChunkMaps" />.</param>
        /// <param name="config">Generator configuration providing voxel IDs for stone, dirt, grass, etc.</param>
        public static void FillTerrain(NativeArray<ushort> vox, NativeArray<ChunkColumn> chunkColumns,
            ref GeneratorConfig config)
        {
            const ushort air = 0;
            ushort waterBlock = config.Voxels["std:Water"].Id;
            ushort ice = config.Voxels["std:Ice"].Id;
            int waterLevel = config.WaterLevel;

            for (int x = 0; x < ChunkWidth; x++)
            for (int z = 0; z < ChunkDepth; z++)
            {
                int i = GetColumnIdx(x, z, ChunkDepth);

                ChunkColumn col = chunkColumns[i];

                ushort st = col.StoneBlock;
                ushort under = col.UnderBlock;
                ushort top = col.TopBlock;


                int gy = col.Height;

                for (int y = 0; y < ChunkHeight; y++)
                {
                    ushort v;
                    if (y < gy - 4) v = st;
                    else if (y < gy) v = under;
                    else if (y == gy) v = gy < waterLevel ? waterBlock : top;
                    else v = y < waterLevel ? waterBlock : air;

                    if (y == waterLevel && v == waterBlock && col.Temperature < .2f) v = ice;

                    vox[ChunkSize.Flatten(x, y, z)] = v;
                }
            }
        }

        private static void SelectSurfaceMaterials(ref GeneratorConfig config, ref ChunkColumn col, ref Random rng)
        {
            Biome.BiomeDef biomDef = config.BiomeDefs[col.Biome];

            col.TopBlock = biomDef.topBlock;
            col.UnderBlock = biomDef.underBlock;
            col.StoneBlock = biomDef.stoneBlock;
        }
    }
}