using System;
using System.Collections.Generic;
using Engine.Scripts.Utils.Logger;
using Unity.Mathematics;
using static Engine.Scripts.Utils.VoxelConstants;

namespace Engine.Scripts.Render{
    /// <summary>
    ///     Represents a request to build render data for a partition, including collection of the neighboring chunks'
    ///     interval lists in the <see cref="ChunkIntervalStore" /> (§20.1).
    /// </summary>
    internal class PartitionBuildRequest{
        private static readonly int2[] RequiredChunks =
            { int2.zero, new(0, 1), new(1, 1), new(1, 0), new(1, -1), new(0, -1), new(-1, -1), new(-1, 0), new(-1, 1) };

        /// <summary>
        ///     The partition coordinates for this build request.
        /// </summary>
        public int3 Partition{ get; private set; }

        private readonly IntervalRange[] _voxelLists = new IntervalRange[RequiredChunks.Length];
        private readonly IntervalRange[] _lightLists = new IntervalRange[RequiredChunks.Length]; // LGT-08

        /// <summary>
        ///     Initializes a new instance of the PartitionBuildRequest class.
        /// </summary>
        /// <param name="partition">The partition coordinates.</param>
        public PartitionBuildRequest(int3 partition){
            Partition = partition;
        }


        /// <summary>
        ///     Gets a value indicating whether this build request has successfully collected all required lists.
        /// </summary>
        public bool IsValid{ get; private set; }

        /// <summary>
        ///     PERF-04: re-targets a pooled request so the renderer does not allocate one per build.
        /// </summary>
        public void Reset(int3 partition){
            Partition = partition;
            IsValid = false;
            Array.Clear(_voxelLists, 0, _voxelLists.Length);
            Array.Clear(_lightLists, 0, _lightLists.Length);
        }

        /// <summary>
        ///     Gets the voxel interval lists of all required chunks, or null if IsValid is false.
        /// </summary>
        public IntervalRange[] VoxelLists => IsValid ? _voxelLists : null;

        /// <summary>
        ///     LGT-08: gets the light interval lists of all required chunks (same order as <see cref="VoxelLists" />),
        ///     or null if IsValid is false.
        /// </summary>
        public IntervalRange[] LightLists => IsValid ? _lightLists : null;

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
        ///     Collects the voxel data and light lists of all required neighboring chunks.
        /// </summary>
        /// <param name="voxelLists">Dictionary mapping chunk positions to their voxel interval lists.</param>
        /// <param name="lightLists">LGT-08: dictionary mapping chunk positions to their light interval lists.</param>
        public void CollectLists(Dictionary<int2, IntervalRange> voxelLists, Dictionary<int2, IntervalRange> lightLists){
            IsValid = true;
            int2 mainChunkPos = PartitionToChunkPos(Partition);

            for (int i = 0; i < RequiredChunks.Length; i++){
                int2 chunkPos = mainChunkPos + RequiredChunks[i];
                bool success = voxelLists.TryGetValue(chunkPos, out _voxelLists[i]);
                if (!success){
                    VoxelEngineLogger.Error<PartitionBuildRequest>(
                        $"Neighbor voxel data for partition {Partition} not found at {chunkPos}.");
                }

                bool lit = lightLists.TryGetValue(chunkPos, out _lightLists[i]);
                if (!lit){
                    VoxelEngineLogger.Error<PartitionBuildRequest>(
                        $"Neighbor light for partition {Partition} not found at {chunkPos}.");
                }

                IsValid &= success && lit;
            }
        }
    }
}
