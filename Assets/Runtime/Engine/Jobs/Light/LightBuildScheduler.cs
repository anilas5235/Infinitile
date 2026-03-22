using System.Collections.Generic;
using Runtime.Engine.Components;
using Runtime.Engine.Data;
using Runtime.Engine.Jobs.Core;
using Runtime.Engine.Jobs.Meshing;
using Runtime.Engine.Settings;
using Runtime.Engine.Utils.Extensions;
using Runtime.Engine.Utils.Logger;
using Runtime.Engine.VoxelConfig.Data;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace Runtime.Engine.Jobs.Light
{
    /// <summary>
    /// Schedules and executes mesh build jobs for chunks, generating both render and collider meshes
    /// using a greedy meshing algorithm and applying the results to chunk behaviors.
    /// </summary>
    internal class LightBuildScheduler : JobScheduler
    {
        private readonly ChunkManager _chunkManager;
        private readonly ChunkPool _chunkPool;
        private readonly VoxelRegistry _voxelRegistry;

        private JobHandle _handle;

        private NativeList<int3> _jobs;
        private ChunkAccessor _chunkAccessor;
        private NativeParallelHashMap<int3, PartitionLightData> _results;

        private readonly NativeArray<int3> _neighborOffsets;

        /// <summary>
        /// Initializes a new instance of the <see cref="MeshBuildScheduler"/> class.
        /// </summary>
        /// <param name="settings">Voxel engine settings providing chunk size and draw distance.</param>
        /// <param name="chunkManager">Chunk manager used to access chunk data and state.</param>
        /// <param name="chunkPool">Pool providing reusable chunk behaviours and meshes.</param>
        /// <param name="voxelRegistry">Voxel registry providing render generation data for blocks.</param>
        internal LightBuildScheduler(
            VoxelEngineSettings settings,
            ChunkManager chunkManager,
            ChunkPool chunkPool,
            VoxelRegistry voxelRegistry
        )
        {
            _chunkManager = chunkManager;
            _chunkPool = chunkPool;
            _voxelRegistry = voxelRegistry;

            _neighborOffsets = new NativeArray<int3>(VectorConstants.Int3Directions, Allocator.Domain);

            _results = new NativeParallelHashMap<int3, PartitionLightData>(
                settings.Chunk.DrawDistance.SquareSize(), Allocator.Domain);
            _jobs = new NativeList<int3>(Allocator.Domain);
        }

        /// <summary>
        /// Indicates whether the scheduler is ready to accept a new list of light build jobs.
        /// </summary>
        internal bool IsReady = true;

        /// <summary>
        /// Gets a value indicating whether the currently scheduled light build jobs have completed.
        /// </summary>
        internal bool IsComplete => _handle.IsCompleted;

        /// <summary>
        /// Starts a light build job for the given list of partition positions.
        /// </summary>
        /// <param name="jobs">List of partition positions whose light should be generated or updated.</param>
        internal void Start(List<int3> jobs)
        {
            StartRecord();

            IsReady = false;

            _chunkAccessor = _chunkManager.GetAccessor(jobs);

            foreach (int3 j in jobs)
            {
                _jobs.Add(j);
            }

            LightBuildJob job = new()
            {
                Accessor = _chunkAccessor,
                Jobs = _jobs,
                NeighborOffsets = _neighborOffsets,
                RenderGenData = _voxelRegistry.GetVoxelGenData(),
                Results = _results.AsParallelWriter()
            };

            _handle = job.Schedule(_jobs.Length, 1);
        }

        /// <summary>
        /// Completes the scheduled mesh build jobs, applies the generated mesh data to chunk meshes
        /// and colliders, recalculates bounds and logs timing information.
        /// </summary>
        internal void Complete()
        {
            double start = Time.realtimeSinceStartupAsDouble;
            _handle.Complete();

            //TODO: Get light data from results and apply to chunk behaviours, then recalculate lighting on neighboring chunks if needed

            double totalTime = (Time.realtimeSinceStartupAsDouble - start) * 1000;
            if (totalTime >= 4)
            {
                VoxelEngineLogger.Warn<MeshBuildScheduler>(
                    $"Built {_jobs.Length} meshes, Collected Results in <color=red>{totalTime:0.000}</color>ms"
                );
            }

            _results.Clear();
            _jobs.Clear();

            IsReady = true;
            long totalJobTime = StopRecord();
            if (totalJobTime > 100)
            {
                VoxelEngineLogger.Warn<MeshBuildScheduler>(
                    $"Total Mesh Build Time for {_jobs.Length} jobs: <color=red>{totalJobTime:0.000}</color>ms"
                );
            }
        }

        /// <summary>
        /// Disposes all native containers and completes any running jobs before releasing resources.
        /// </summary>
        internal void Dispose()
        {
            _handle.Complete();

            _results.Dispose();
            _jobs.Dispose();
        }
    }
}