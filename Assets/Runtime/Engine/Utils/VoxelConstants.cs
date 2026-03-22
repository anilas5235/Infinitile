using Runtime.Engine.Utils.Extensions;
using Unity.Mathematics;
using UnityEngine.Rendering;

namespace Runtime.Engine.Utils
{
    public static class VoxelConstants
    {
        internal const int ChunkWidth = 32;
        internal const int ChunkHeight = 256;
        internal const int ChunkDepth = 32;
        
        internal const int MinChunkPosXYZ = 0;
        internal const int MaxXChunkPos = ChunkWidth - 1;
        internal const int MaxYChunkPos = ChunkHeight - 1;
        internal const int MaxZChunkPos = ChunkDepth - 1;
        
        internal static readonly int3 ChunkSize = new(ChunkWidth, ChunkHeight, ChunkDepth);
        internal static readonly int2 ChunkSizeXZ = new(ChunkWidth, ChunkDepth);

        internal const int VoxelsPerChunk = ChunkWidth * ChunkHeight * ChunkDepth;


        internal const int PartitionWidth = 32;
        internal const int PartitionHeight = 32;
        internal const int PartitionDepth = 32;
        
        internal const int MinPartitionPosXYZ = 0;
        internal const int MaxXPartitionPos = PartitionWidth - 1;
        internal const int MaxYPartitionPos = PartitionHeight - 1;
        internal const int MaxZPartitionPos = PartitionDepth - 1;

        internal static readonly int3 PartitionSize = new(PartitionWidth, PartitionHeight, PartitionDepth);

        internal const int VoxelsPerPartition = PartitionWidth * PartitionHeight * PartitionDepth;
        internal const int PartitionsPerChunk = ChunkHeight / PartitionHeight;

        internal const MeshUpdateFlags MeshFlags = MeshUpdateFlags.DontRecalculateBounds |
                                                   MeshUpdateFlags.DontValidateIndices |
                                                   MeshUpdateFlags.DontResetBoneBounds;
        
        internal const byte MaxLightLevel = 15;
        
        public static int3 PartitionToWorldPos(int3 partition) => PartitionSize.MemberMultiply(partition);
        
        public static int3 WorldToPartitionPos(int3 worldPos) => PartitionSize.MemberDivide(worldPos);

        public static int2 PartitionToChunkPos(int3 partition) => partition.xz;
    }
}