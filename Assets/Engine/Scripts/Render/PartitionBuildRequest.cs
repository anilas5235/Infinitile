using System;
using System.Collections.Generic;
using Engine.Scripts.Utils.Logger;
using Unity.Mathematics;
using UnityEngine;
using static Engine.Scripts.Utils.VoxelConstants;

namespace Engine.Scripts.Render{
    /// <summary>
    ///     Represents a request to build render data for a partition, including collection of neighboring chunk buffers.
    /// </summary>
    internal class PartitionBuildRequest{
        private static readonly int2[] RequiredChunks =
            { int2.zero, new(0, 1), new(1, 1), new(1, 0), new(1, -1), new(0, -1), new(-1, -1), new(-1, 0), new(-1, 1) };

        /// <summary>
        ///     The partition coordinates for this build request.
        /// </summary>
        public int3 Partition{ get; private set; }

        private readonly GraphicsBuffer[] _buffers = new GraphicsBuffer[RequiredChunks.Length];
        private readonly GraphicsBuffer[] _lightBuffers = new GraphicsBuffer[RequiredChunks.Length]; // LGT-08

        /// <summary>
        ///     Initializes a new instance of the PartitionBuildRequest class.
        /// </summary>
        /// <param name="partition">The partition coordinates.</param>
        public PartitionBuildRequest(int3 partition){
            Partition = partition;
        }


        /// <summary>
        ///     Gets a value indicating whether this build request has successfully collected all required buffers.
        /// </summary>
        public bool IsValid{ get; private set; }

        /// <summary>
        ///     PERF-04: re-targets a pooled request so the renderer does not allocate one per build.
        /// </summary>
        public void Reset(int3 partition){
            Partition = partition;
            IsValid = false;
            Array.Clear(_buffers, 0, _buffers.Length);
            Array.Clear(_lightBuffers, 0, _lightBuffers.Length);
        }

        /// <summary>
        ///     Gets the graphics buffers for all required chunks, or null if IsValid is false.
        /// </summary>
        public GraphicsBuffer[] Buffers => IsValid ? _buffers : null;

        /// <summary>
        ///     LGT-08: gets the light buffers for all required chunks (same order as <see cref="Buffers" />), or null
        ///     if IsValid is false.
        /// </summary>
        public GraphicsBuffer[] LightBuffers => IsValid ? _lightBuffers : null;

        /// <summary>
        ///     Gets the metadata for this partition.
        /// </summary>
        /// <returns>A PartitionMetadata struct containing partition position information.</returns>
        public PartitionMetadata GetMetadata(){
            return new PartitionMetadata{
                PartitionPos = Partition,
                PartitionWorldPos = PartitionToWorldPos(Partition)
            };
        }

        /// <summary>
        ///     Collects the voxel data and light buffers for all required neighboring chunks.
        /// </summary>
        /// <param name="voxelDataBuffers">Dictionary mapping chunk positions to their voxel data buffers.</param>
        /// <param name="lightDataBuffers">LGT-08: dictionary mapping chunk positions to their light buffers.</param>
        public void CollectBuffers(Dictionary<int2, GraphicsBuffer> voxelDataBuffers,
            Dictionary<int2, GraphicsBuffer> lightDataBuffers){
            IsValid = true;
            int2 mainChunkPos = PartitionToChunkPos(Partition);

            for (int i = 0; i < RequiredChunks.Length; i++){
                int2 chunkPos = mainChunkPos + RequiredChunks[i];
                bool success = voxelDataBuffers.TryGetValue(chunkPos, out _buffers[i]);
                if (!success){
                    VoxelEngineLogger.Error<PartitionBuildRequest>(
                        $"Neighbor voxel data buffer for partition {Partition} not found at {chunkPos}.");
                }

                bool lit = lightDataBuffers.TryGetValue(chunkPos, out _lightBuffers[i]);
                if (!lit){
                    VoxelEngineLogger.Error<PartitionBuildRequest>(
                        $"Neighbor light buffer for partition {Partition} not found at {chunkPos}.");
                }

                IsValid &= success && lit;
            }
        }
    }
}
