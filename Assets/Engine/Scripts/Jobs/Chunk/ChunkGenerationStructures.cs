using Engine.Scripts.Utils.Extensions;
using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;
using static Engine.Scripts.Utils.VoxelConstants;
using Random = Unity.Mathematics.Random;

namespace Engine.Scripts.Jobs.Chunk
{
    /// <summary>
    ///     Provides Burst-compiled helpers to place rare world structures such as pyramids, oases,
    ///     igloos, shipwrecks and mineshafts on or within generated terrain.
    /// </summary>
    internal partial struct ChunkJob
    {
        /// <summary>
        ///     Places biome-dependent structures into the voxel buffer using per-column metadata.
        ///     Structures are rare and are positioned deterministically based on chunk position and seed.
        /// </summary>
        /// <param name="vox">Voxel buffer to modify with structure blocks.</param>
        /// <param name="chunkColumns">Per-column data including biome and terrain height.</param>
        /// <param name="chunkWordPos">World-space origin (in voxels) of the chunk.</param>
        /// <param name="randomSeed">Global seed used to derive the per-chunk random stream.</param>
        /// <param name="config">Generator configuration providing voxel IDs for structure materials.</param>
        [BurstCompile]
        public static void PlaceStructures(ref NativeArray<ushort> vox,
            ref NativeArray<ChunkColumn> chunkColumns, ref int3 chunkWordPos,
            int randomSeed, ref GeneratorConfig config)
        {
            uint seed = (uint)((chunkWordPos.x * 43524) ^ (chunkWordPos.z * 7856) ^ randomSeed ^ 0x85ebca6b);
            Random rng = new(seed == 0 ? 1u : seed);

            for (int x = 1; x < ChunkWidth - 1; x++)
            for (int z = 1; z < ChunkDepth - 1; z++)
            {
                int gi = GetColumnIdx(x, z, ChunkDepth);
                ChunkColumn chunkCol = chunkColumns[gi];
                int gy = chunkCol.Height;
                if (gy <= 0 || gy >= ChunkHeight - 2) continue;
            }
        }
    }
}