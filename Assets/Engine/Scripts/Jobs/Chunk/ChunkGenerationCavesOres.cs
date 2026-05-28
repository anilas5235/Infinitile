using Engine.Scripts.Utils.Extensions;
using Unity.Collections;
using Unity.Mathematics;
using static Engine.Scripts.Utils.VoxelConstants;

namespace Engine.Scripts.Jobs.Chunk
{
    /// <summary>
    ///     Provides Burst-compiled helpers to carve caves and place ore veins inside generated terrain.
    /// </summary>
    internal partial struct ChunkJob
    {
        /// <summary>
        ///     Carves cave tunnels and pockets into the voxel buffer using layered 3D noise,
        ///     optionally filling lower regions with lava.
        /// </summary>
        /// <param name="vox">Voxel buffer that will be modified in place.</param>
        /// <param name="origin">World-space origin (in voxels) of the chunk.</param>
        /// <param name="chunkColumns">Per-column metadata providing terrain height.</param>
        /// <param name="config">Generator configuration providing voxel IDs (lava, stone, etc.).</param>
        /// <param name="randomSeed">Random seed used to jitter noise sampling.</param>
        /// <param name="caveScale">Scale factor applied to cave noise; higher values yield smaller features.</param>
        /// <param name="lavaLevel">Maximum Y level at which carved spaces are filled with lava.</param>
        public static void CarveCaves(NativeArray<ushort> vox, int3 origin,
            NativeArray<ChunkColumn> chunkColumns, GeneratorConfig config, int randomSeed,
            float caveScale, int lavaLevel)
        {
            for (int x = 0; x < ChunkWidth; x++)
            for (int z = 0; z < ChunkDepth; z++)
            {
                int height = chunkColumns[GetColumnIdx(x, z, ChunkDepth)].Height;
                for (int y = 2; y <= height; y++)
                {
                    int idx = ChunkSize.Flatten(x, y, z);

                    float3 noiseSamplePos = (origin + new float3(x + randomSeed, y - randomSeed, z + randomSeed)) *
                                            caveScale;

                    float sCaveNoise = noise.snoise(noiseSamplePos) * .5f + .5f;
                    float cellNoise = noise.cellular(noiseSamplePos).x * .5f + .5f;

                    bool sCarve = math.square(sCaveNoise) + math.square(cellNoise) >
                                  math.lerp(.8f, 1.3f, math.square(y / (float)height));

                    if (sCarve)
                    {
                        vox[idx] = 0;
                        if (y <= lavaLevel) vox[idx] = config.Lava;
                    }
                }
            }
        }

        /// <summary>
        ///     Replaces stone voxels in the buffer with different ore types based on depth and noise.
        /// </summary>
        /// <param name="vox">Voxel buffer to modify in place.</param>
        /// <param name="config">Generator configuration providing stone and ore voxel IDs.</param>
        /// <param name="randomSeed">Random seed used to offset ore noise for deterministic variety.</param>
        public static void PlaceOres(NativeArray<ushort> vox, GeneratorConfig config, int randomSeed)
        {
            ushort stone = config.Stone;
            ushort oreCoal = config.StoneCoalOre;
            ushort oreIron = config.StoneIronGreenOre;
            ushort oreGold = config.StoneGoldOre;
            ushort oreDiamond = config.StoneDiamondOre;

            for (int x = 1; x < ChunkWidth - 1; x++)
            for (int z = 1; z < ChunkDepth - 1; z++)
            for (int y = 2; y < ChunkHeight - 2; y++)
            {
                int idx = ChunkSize.Flatten(x, y, z);
                if (vox[idx] != stone) continue;

                float depthNorm = 1f - y / (float)ChunkHeight;
                float oreNoise = math.abs(noise.snoise(new float3(randomSeed + x, randomSeed + y,
                    randomSeed - z) * 0.12f));
                float roll = math.max(0f, oreNoise * depthNorm);

                vox[idx] = roll switch
                {
                    > 0.85f when y < ChunkHeight * 0.15f => oreDiamond,
                    > 0.7f when y < ChunkHeight * 0.35f => oreGold,
                    > 0.6f => oreIron,
                    > 0.45f => oreCoal,
                    _ => vox[idx]
                };
            }
        }
    }
}