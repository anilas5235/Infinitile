using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;
using static Engine.Scripts.Utils.VoxelConstants;

namespace Engine.Scripts.Data{
    /// <summary>
    ///     Read-only accessor spanning multiple chunks and their neighbors (wrap-around for border traversal).
    ///     Provides utilities for neighbor checks and voxel lookups across chunk boundaries.
    /// </summary>
    [BurstCompile]
    internal readonly struct ChunkAccessor{
        private readonly NativeParallelHashMap<int2, ChunkVoxelData>.ReadOnly _chunks;

        /// <summary>
        ///     Constructs a new accessor.
        /// </summary>
        internal ChunkAccessor(NativeParallelHashMap<int2, ChunkVoxelData>.ReadOnly chunks){
            _chunks = chunks;
        }

        /// <summary>
        ///     Voxel lookup within a chunk; remaps out-of-range coordinates to neighbor chunks.
        /// </summary>
        internal ushort GetVoxelInPartition(in int3 partitionPos, int3 voxelPos){
            if (!ResolveChunk(partitionPos, ref voxelPos, out int2 chunkPos)) return 0;
            return TryGetChunk(chunkPos, out ChunkVoxelData chunk) ? chunk.GetVoxel(voxelPos) : (ushort)0;
        }

        /// <summary>
        ///     Converts a partition-relative voxel position into a chunk position and chunk-local voxel position.
        ///     Returns false outside the world height.
        /// </summary>
        private static bool ResolveChunk(in int3 partitionPos, ref int3 voxelPos, out int2 chunkPos){
            voxelPos.y += partitionPos.y * PartitionHeight;
            chunkPos = partitionPos.xz;
            if (voxelPos.y is >= ChunkHeight or < 0) return false;
            int2 chunkOffset = int2.zero;

            switch (voxelPos.x){
                case > MaxXChunkPos:
                    chunkOffset.x = voxelPos.x / ChunkWidth;
                    voxelPos.x %= ChunkWidth;
                    break;
                case < MinChunkPosXYZ:
                    chunkOffset.x = (voxelPos.x - ChunkWidth + 1) / ChunkWidth;
                    voxelPos.x = (voxelPos.x % ChunkWidth + ChunkWidth) % ChunkWidth;
                    break;
            }

            switch (voxelPos.z){
                case > MaxZChunkPos:
                    chunkOffset.y = voxelPos.z / ChunkDepth;
                    voxelPos.z %= ChunkDepth;
                    break;
                case < MinChunkPosXYZ:
                    chunkOffset.y = (voxelPos.z - ChunkDepth + 1) / ChunkDepth;
                    voxelPos.z = (voxelPos.z % ChunkDepth + ChunkDepth) % ChunkDepth;
                    break;
            }

            chunkPos += chunkOffset;
            return true;
        }

        /// <summary>
        ///     Attempts to get a chunk at a position.
        /// </summary>
        internal bool TryGetChunk(int2 pos, out ChunkVoxelData chunk){
            return _chunks.TryGetValue(pos, out chunk);
        }

        /// <summary>
        ///     Checks whether a chunk exists.
        /// </summary>
        internal bool ContainsChunk(int2 coord){
            return _chunks.ContainsKey(coord);
        }

        /// <summary>
        ///     Checks whether a voxel coordinate lies inside chunk bounds.
        /// </summary>
        public static bool InChunkBounds(int3 chunkLocalPos){
            return chunkLocalPos.x >= 0 && chunkLocalPos.x < ChunkWidth &&
                   chunkLocalPos.y >= 0 && chunkLocalPos.y < ChunkHeight &&
                   chunkLocalPos.z >= 0 && chunkLocalPos.z < ChunkDepth;
        }

        public static bool InPartitionBounds(int3 partitionLocalPos){
            return partitionLocalPos.x >= 0 && partitionLocalPos.x < PartitionWidth &&
                   partitionLocalPos.y >= 0 && partitionLocalPos.y < PartitionHeight &&
                   partitionLocalPos.z >= 0 && partitionLocalPos.z < PartitionDepth;
        }
    }
}
