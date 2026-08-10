using Engine.Scripts.Utils.Extensions;
using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;
using static Engine.Scripts.Utils.VoxelConstants;
using Random = Unity.Mathematics.Random;

namespace Engine.Scripts.Jobs.Chunk
{
    /// <summary>
    ///     Provides Burst-compiled helpers to place biome-dependent vegetation such as trees,
    ///     grass, cacti, crops and mushrooms on top of generated terrain.
    /// </summary>
    internal partial struct ChunkJob
    {
        /// <summary>
        ///     Places vegetation for every suitable surface column in the chunk using biome information
        ///     and a deterministic random stream derived from chunk position and global seed.
        /// </summary>
        /// <param name="vox">Voxel buffer that will receive vegetation blocks.</param>
        /// <param name="chunkColumns">Per-column data including biome, height and climate.</param>
        /// <param name="chunkWordPos">World-space origin (in voxels) of the chunk.</param>
        /// <param name="randomSeed">Global seed used to derive the per-chunk random generator.</param>
        /// <param name="config">Generator configuration providing voxel IDs for vegetation blocks.</param>
        [BurstCompile]
        public static void PlaceVegetation(ref NativeArray<ushort> vox,
            ref NativeArray<ChunkColumn> chunkColumns,
            ref int3 chunkWordPos, int randomSeed, ref GeneratorConfig config)
        {
            uint seed = (uint)((chunkWordPos.x * 73856093) ^ (chunkWordPos.z * 19349663) ^ randomSeed ^ 0x85ebca6b);
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

        private static bool SurfaceHasNeighbor(ref NativeArray<ushort> vox, int cx, int cy, int cz, ushort waterId)
        {
            int neighbors = 0;
            int3 pos = new(cx - 1, cy, cz);
            if (InChunk(ref pos))
            {
                ushort voxel = vox[ChunkSize.Flatten(pos)];
                if (voxel != 0 && voxel != waterId)
                    neighbors++;
            }

            pos = new int3(cx + 1, cy, cz);
            if (InChunk(ref pos))
            {
                ushort voxel = vox[ChunkSize.Flatten(pos)];
                if (voxel != 0 && voxel != waterId)
                    neighbors++;
            }

            pos = new int3(cx, cy, cz + 1);
            if (InChunk(ref pos))
            {
                ushort voxel = vox[ChunkSize.Flatten(pos)];
                if (voxel != 0 && voxel != waterId)
                    neighbors++;
            }

            pos = new int3(cx, cy, cz - 1);
            if (InChunk(ref pos))
            {
                ushort voxel = vox[ChunkSize.Flatten(pos)];
                if (voxel != 0 && voxel != waterId)
                    neighbors++;
            }

            return neighbors > 0;
        }
    }
}