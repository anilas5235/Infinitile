using System.Collections.Generic;
using Engine.Scripts.Utils.Logger;
using Unity.Mathematics;
using UnityEngine;
using static Engine.Scripts.Utils.VoxelConstants;

namespace Engine.Scripts.Render
{
    internal class PartitionBuildRequest
    {
        public readonly int3 Partition;
        private readonly GraphicsBuffer[] _buffers = new GraphicsBuffer[RequiredChunks.Length];


        public bool IsValid { get; private set; }

        public PartitionBuildRequest(int3 partition)
        {
            Partition = partition;
        }

        public GraphicsBuffer[] Buffers => IsValid ? _buffers : null;

        public PartitionMetadata GetMetadata()
        {
            return new PartitionMetadata()
            {
                PartitionPos = Partition,
                PartitionWorldPos = PartitionToWorldPos(Partition)
            };
        }
        
        private static readonly int2[] RequiredChunks = 
            {int2.zero ,new(0, 1), new(1, 1), new(1, 0), new(1, -1), new(0, -1), new(-1, -1), new(-1, 0), new(-1, 1) };

        public void CollectBuffers(Dictionary<int2, GraphicsBuffer> voxelDataBuffers)
        {
            IsValid = true;
            int2 mainChunkPos = PartitionToChunkPos(Partition);

            for (int i = 0; i < RequiredChunks.Length; i++)
            {
                int2 chunkPos = mainChunkPos + RequiredChunks[i];
                bool success = voxelDataBuffers.TryGetValue(chunkPos, out _buffers[i]);
                if (!success)  VoxelEngineLogger.Error<PartitionBuildRequest>(
                    $"Neighbor voxel data buffer for partition {Partition} not found at {chunkPos}.");
                IsValid &= success;
            }
        }
    }
}