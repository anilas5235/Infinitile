using Engine.Scripts.Utils.Extensions;
using Unity.Mathematics;
using UnityEngine;
using static Engine.Scripts.Utils.VoxelConstants;

namespace Engine.Scripts.Utils
{
    /// <summary>
    /// Utility methods for converting between world, chunk, partition, and voxel coordinates.
    /// </summary>
    public static class VoxelUtils
    {
        /// <summary>
        ///     Gets the chunk origin coordinates for a world position specified as <see cref="Vector3Int" />.
        /// </summary>
        /// <param name="position">World position in integer voxel coordinates.</param>
        /// <returns>Chunk origin coordinates in voxel space.</returns>
        public static int2 GetChunkCoords(Vector3Int position)
        {
            return GetChunkCoords(position.Int3());
        }

        /// <summary>
        ///     Computes chunk origin coordinates (bottom-left in XZ) for the given world position,
        ///     correctly handling negative coordinates.
        /// </summary>
        /// <param name="position">World position in voxel coordinates.</param>
        /// <returns>Chunk origin coordinates this position belongs to.</returns>
        public static int2 GetChunkCoords(int3 position)
        {
            int2 cCoords = new(
                FloorDivide(position.x, ChunkWidth),
                FloorDivide(position.z, ChunkDepth)
            );
            return cCoords;
        }

        /// <summary>
        /// Gets the partition coordinates for a world position specified as <see cref="Vector3" />.
        /// </summary>
        /// <param name="position">World position in floating-point voxel coordinates.</param>
        /// <returns>Partition coordinates containing the given position.</returns>
        public static int3 GetPartitionCoords(Vector3 position)
        {
            return GetPartitionCoords(position.Int3());
        }

        /// <summary>
        /// Gets the partition coordinates for a world position specified as <see cref="int3" />.
        /// </summary>
        /// <param name="position">World position in integer voxel coordinates.</param>
        /// <returns>Partition coordinates containing the given position.</returns>
        public static int3 GetPartitionCoords(int3 position)
        {
            int2 cCoords = GetChunkCoords(position);
            return new int3(
                cCoords.x,
                position.y / PartitionHeight,
                cCoords.y
            );
        }

        /// <summary>
        /// Converts a world position to voxel coordinates local to a partition.
        /// </summary>
        /// <param name="partitionPosition">The partition position in partition coordinates.</param>
        /// <param name="worldPosition">The world position in integer voxel coordinates.</param>
        /// <returns>Local voxel coordinates inside the partition.</returns>
        public static int3 GetPartitionLocalVoxelCoords(int3 partitionPosition, int3 worldPosition)
        {
            int3 localVoxelPos = worldPosition - new int3(
                partitionPosition.x * ChunkWidth,
                partitionPosition.y * PartitionHeight,
                partitionPosition.z * ChunkDepth
            );
            return localVoxelPos;
        }


        /// <summary>
        /// Gets the local voxel coordinates within its chunk for a world position.
        /// </summary>
        /// <param name="position">World position in integer voxel coordinates.</param>
        /// <returns>Local voxel coordinates within the chunk.</returns>
        public static int3 GetLocalVoxelCoords(Vector3Int position)
        {
            int2 chunkCoords = GetChunkCoords(position);
            return position.Int3() - ChunkSize.MemberMultiply(chunkCoords.x, 0, chunkCoords.y);
        }

        private static int FloorDivide(int value, int divisor)
        {
            return value / divisor - (value < 0 && value % divisor != 0 ? 1 : 0);
        }
    }
}