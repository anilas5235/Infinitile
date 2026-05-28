using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;
using static Engine.Scripts.Utils.VoxelConstants;

namespace Engine.Scripts.Data
{
    /// <summary>
    ///     Read-only accessor spanning multiple chunks and their neighbors (wrap-around for border traversal).
    ///     Provides utilities for neighbor checks and voxel lookups across chunk boundaries.
    /// </summary>
    [BurstCompile]
    internal readonly struct ChunkAccessor
    {
        private readonly NativeParallelHashMap<int2, ChunkVoxelData>.ReadOnly _chunks;
        private readonly NativeParallelHashMap<int2, ChunkLightData>.ReadOnly _lightData;

        /// <summary>
        ///     Constructs a new accessor.
        /// </summary>
        internal ChunkAccessor(NativeParallelHashMap<int2, ChunkVoxelData>.ReadOnly chunks,
            NativeParallelHashMap<int2, ChunkLightData>.ReadOnly lightData)
        {
            _chunks = chunks;
            _lightData = lightData;
        }

        /// <summary>
        ///     Voxel lookup within a chunk; remaps out-of-range coordinates to neighbor chunks.
        /// </summary>
        internal ushort GetVoxelInPartition(in int3 partitionPos, int3 voxelPos)
        {
            voxelPos.y += partitionPos.y * PartitionHeight;
            if (voxelPos.y is >= ChunkHeight or < 0) return 0;
            int2 chunkOffset = int2.zero;

            switch (voxelPos.x)
            {
                case > MaxXChunkPos:
                    chunkOffset.x = voxelPos.x / ChunkWidth;
                    voxelPos.x %= ChunkWidth;
                    break;
                case < MinChunkPosXYZ:
                    chunkOffset.x = (voxelPos.x - ChunkWidth + 1) / ChunkWidth;
                    voxelPos.x = (voxelPos.x % ChunkWidth + ChunkWidth) % ChunkWidth;
                    break;
            }

            switch (voxelPos.z)
            {
                case > MaxZChunkPos:
                    chunkOffset.y = voxelPos.z / ChunkDepth;
                    voxelPos.z %= ChunkDepth;
                    break;
                case < MinChunkPosXYZ:
                    chunkOffset.y = (voxelPos.z - ChunkDepth + 1) / ChunkDepth;
                    voxelPos.z = (voxelPos.z % ChunkDepth + ChunkDepth) % ChunkDepth;
                    break;
            }

            return TryGetChunk(partitionPos.xz + chunkOffset, out ChunkVoxelData chunk)
                ? chunk.GetVoxel(voxelPos)
                : (ushort)0;
        }

        /// <summary>
        ///     Attempts to get a chunk at a position.
        /// </summary>
        internal bool TryGetChunk(int2 pos, out ChunkVoxelData chunk)
        {
            return _chunks.TryGetValue(pos, out chunk);
        }

        internal byte GetLightInPartition(int3 partitionPos, int3 voxelPos)
        {
            FindPartitionAndLocalPos(ref partitionPos, ref voxelPos);

            return TryGetLightData(partitionPos, out PartitionLightData lightData)
                ? lightData.GetLight(voxelPos)
                : (byte)0;
        }

        private static void FindPartitionAndLocalPos(ref int3 partitionPos, ref int3 voxelPos)
        {
            int3 partitionOffset = int3.zero;

            switch (voxelPos.x)
            {
                case > MaxXPartitionPos:
                    partitionOffset.x = voxelPos.x / PartitionWidth;
                    voxelPos.x %= PartitionWidth;
                    break;
                case < MinPartitionPosXYZ:
                    partitionOffset.x = (voxelPos.x - PartitionWidth + 1) / PartitionWidth;
                    voxelPos.x = (voxelPos.x % PartitionWidth + PartitionWidth) % PartitionWidth;
                    break;
            }

            switch (voxelPos.y)
            {
                case > MaxYPartitionPos:
                    partitionOffset.y = voxelPos.y / PartitionHeight;
                    voxelPos.y %= PartitionHeight;
                    break;
                case < MinPartitionPosXYZ:
                    partitionOffset.y = (voxelPos.y - PartitionHeight + 1) / PartitionHeight;
                    voxelPos.y = (voxelPos.y % PartitionHeight + PartitionHeight) % PartitionHeight;
                    break;
            }

            switch (voxelPos.z)
            {
                case > MaxZPartitionPos:
                    partitionOffset.z = voxelPos.z / PartitionDepth;
                    voxelPos.z %= PartitionDepth;
                    break;
                case < MinPartitionPosXYZ:
                    partitionOffset.z = (voxelPos.z - PartitionDepth + 1) / PartitionDepth;
                    voxelPos.z = (voxelPos.z % PartitionDepth + PartitionDepth) % PartitionDepth;
                    break;
            }

            partitionPos += partitionOffset;
        }

        internal bool TryGetLightData(int3 partitionPos, out PartitionLightData lightData)
        {
            lightData = default;
            return _lightData.TryGetValue(partitionPos.xz, out ChunkLightData chunkLightData) &&
                   chunkLightData.TryGetPartitionLight(partitionPos.y, out lightData);
        }

        /// <summary>
        ///     Checks whether a chunk exists.
        /// </summary>
        internal bool ContainsChunk(int2 coord)
        {
            return _chunks.ContainsKey(coord);
        }

        /// <summary>
        ///     Checks whether a voxel coordinate lies inside chunk bounds.
        /// </summary>
        public static bool InChunkBounds(int3 chunkLocalPos)
        {
            return chunkLocalPos.x >= 0 && chunkLocalPos.x < ChunkWidth &&
                   chunkLocalPos.y >= 0 && chunkLocalPos.y < ChunkHeight &&
                   chunkLocalPos.z >= 0 && chunkLocalPos.z < ChunkDepth;
        }

        public static bool InPartitionBounds(int3 partitionLocalPos)
        {
            return partitionLocalPos.x >= 0 && partitionLocalPos.x < PartitionWidth &&
                   partitionLocalPos.y >= 0 && partitionLocalPos.y < PartitionHeight &&
                   partitionLocalPos.z >= 0 && partitionLocalPos.z < PartitionDepth;
        }
    }
}