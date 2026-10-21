using Engine.Scripts.Data;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using static Engine.Scripts.Utils.VoxelConstants;

namespace Engine.Scripts.Jobs.Light{
    /// <summary>
    ///     LGT-04: initial light of chunks. Each index lights one chunk from the voxel data of its 3 × 3
    ///     neighbourhood over the box chunk ± 15 in x and z (full height) and writes only that chunk's light.
    ///     Light loses at least 1 per step, so nothing outside the box can reach the chunk (exact), and no job writes
    ///     another chunk's light. Seeds: the sky column scan and the frontier (LGT-05), emitters; then a BFS.
    /// </summary>
    [BurstCompile]
    internal struct LightJob : IJobParallelFor{
        /// <summary>Box margin around the chunk: light reaches at most 14 voxels from its source.</summary>
        internal const int Margin = MaxLightLevel;

        /// <summary>Box size per axis: chunk + 2 margins + 2 sentinel columns.</summary>
        internal const int BoxSize = ChunkWidth + 2 * Margin + 2;

        [ReadOnly] public NativeList<int2> Jobs;
        [ReadOnly] public NativeParallelHashMap<int2, ChunkVoxelData>.ReadOnly Chunks;
        [ReadOnly] public VoxelLightTable Table;

        [WriteOnly] public NativeParallelHashMap<int2, ChunkLightData>.ParallelWriter Results;

        public void Execute(int index){
            int2 chunkPos = Jobs[index];
            Results.TryAdd(chunkPos, Compute(chunkPos, Chunks, Table));
        }

        /// <summary>Lights one chunk; missing neighbour chunks count as opaque.</summary>
        internal static ChunkLightData Compute(int2 chunkPos,
            in NativeParallelHashMap<int2, ChunkVoxelData>.ReadOnly chunks, in VoxelLightTable table){
            int2 chunkOrigin = chunkPos * ChunkSizeXZ;
            LightRegion region = new(BoxSize, BoxSize, chunkOrigin - (Margin + 1), Allocator.Temp);

            for (int dx = -1; dx <= 1; dx++)
            for (int dz = -1; dz <= 1; dz++){
                int2 neighbour = chunkPos + new int2(dx, dz);
                if (chunks.TryGetValue(neighbour, out ChunkVoxelData data))
                    region.FillVoxels(data.Data, neighbour * ChunkSizeXZ, table);
            }

            NativeQueue<int> queue = new(Allocator.Temp);
            region.SeedSky(ref queue);
            region.SeedEmitters(ref queue);
            region.Propagate(ref queue);

            ChunkLightData result = ChunkLightData.CreateEmpty(1024);
            region.WriteChunk(new int2(Margin + 1), ref result);

            queue.Dispose();
            region.Dispose();
            return result;
        }
    }
}
