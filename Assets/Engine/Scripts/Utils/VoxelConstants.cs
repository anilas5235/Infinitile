using Engine.Scripts.Utils.Extensions;
using Unity.Mathematics;
using UnityEngine.Rendering;

namespace Engine.Scripts.Utils
{
    /// <summary>
    /// Defines the core voxel and partition dimensions used throughout the engine.
    /// </summary>
    public static class VoxelConstants
    {
        internal const int ChunkWidth = 32;
        internal const int ChunkHeight = 256;
        internal const int ChunkDepth = 32;

        internal const int MinChunkPosXYZ = 0;
        internal const int MaxXChunkPos = ChunkWidth - 1;
        internal const int MaxYChunkPos = ChunkHeight - 1;
        internal const int MaxZChunkPos = ChunkDepth - 1;

        internal const int VoxelsPerChunk = ChunkWidth * ChunkHeight * ChunkDepth;


        internal const int PartitionWidth = 32;
        internal const int PartitionHeight = 32;
        internal const int PartitionDepth = 32;

        internal const int MinPartitionPosXYZ = 0;
        internal const int MaxXPartitionPos = PartitionWidth - 1;
        internal const int MaxYPartitionPos = PartitionHeight - 1;
        internal const int MaxZPartitionPos = PartitionDepth - 1;

        internal const int VoxelsPerPartition = PartitionWidth * PartitionHeight * PartitionDepth;
        internal const int PartitionsPerChunk = ChunkHeight / PartitionHeight;

        internal const MeshUpdateFlags MeshFlags = MeshUpdateFlags.DontRecalculateBounds |
                                                   MeshUpdateFlags.DontValidateIndices |
                                                   MeshUpdateFlags.DontResetBoneBounds;

        internal const byte MaxLightLevel = 15;

        internal static readonly int3 ChunkSize = new(ChunkWidth, ChunkHeight, ChunkDepth);
        internal static readonly int2 ChunkSizeXZ = new(ChunkWidth, ChunkDepth);

        internal static readonly int3 PartitionSize = new(PartitionWidth, PartitionHeight, PartitionDepth);

        /// <summary>
        /// Converts a partition position to its corresponding world-space origin.
        /// </summary>
        /// <param name="partition">The partition position in partition coordinates.</param>
        /// <returns>The world-space origin of the partition.</returns>
        public static int3 PartitionToWorldPos(int3 partition)
        {
            return PartitionSize.MemberMultiply(partition);
        }

        /// <summary>
        /// Converts a world-space position to its containing partition coordinates.
        /// </summary>
        /// <param name="worldPos">The world-space position.</param>
        /// <returns>The partition coordinates containing the world position.</returns>
        public static int3 WorldToPartitionPos(int3 worldPos)
        {
            return PartitionSize.MemberDivide(worldPos);
        }

        /// <summary>
        /// Converts a partition position to its corresponding chunk coordinates in XZ space.
        /// </summary>
        /// <param name="partition">The partition position.</param>
        /// <returns>The chunk coordinates for the partition.</returns>
        public static int2 PartitionToChunkPos(int3 partition)
        {
            return partition.xz;
        }
    }
}