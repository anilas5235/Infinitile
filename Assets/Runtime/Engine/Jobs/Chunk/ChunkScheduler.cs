using System.Collections.Generic;
using Runtime.Engine.Components;
using Runtime.Engine.Jobs.Core;
using Runtime.Engine.Noise;
using Runtime.Engine.Settings;
using Runtime.Engine.Utils.Extensions;
using Runtime.Engine.Utils.Logger;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace Runtime.Engine.Jobs.Chunk
{
    /// <summary>
    /// Schedules and manages jobs that generate chunk data, wraps <see cref="ChunkJob"/>, 
    /// tracks job lists and result maps, and forwards completed chunks to <see cref="ChunkManager"/>.
    /// </summary>
    internal class ChunkScheduler : JobScheduler
    {
        private readonly ChunkManager _chunkManager;
        private readonly NoiseProfile _noiseProfile;
        private JobHandle _handle;
        private NativeList<int2> _jobs;
        private NativeParallelHashMap<int2, Data.ChunkVoxelData> _results;
        private readonly GeneratorConfig _config;

        /// <summary>
        /// Initializes a new instance of the <see cref="ChunkScheduler"/> class with its own result collection.
        /// </summary>
        /// <param name="settings">Voxel engine settings providing chunk dimensions and load distance.</param>
        /// <param name="chunkManager">Chunk manager that receives finished chunk data.</param>
        /// <param name="noiseProfile">Noise profile used for terrain height generation.</param>
        /// <param name="config">Generator configuration used by chunk jobs.</param>
        internal ChunkScheduler(
            VoxelEngineSettings settings,
            ChunkManager chunkManager,
            NoiseProfile noiseProfile,
            GeneratorConfig config
        )
        {
            _chunkManager = chunkManager;
            _noiseProfile = noiseProfile;
            _config = config;
            _jobs = new NativeList<int2>(Allocator.Domain);
            _results = new NativeParallelHashMap<int2, Data.ChunkVoxelData>(
                settings.Chunk.LoadDistance.SquareSize(),
                Allocator.Domain
            );
        }

        /// <summary>
        /// Indicates whether the scheduler is ready to start a new chunk generation batch.
        /// </summary>
        internal bool IsReady = true;


        /// <summary>
        /// Gets a value indicating whether the scheduled job has completed.
        /// </summary>
        internal bool IsComplete => _handle.IsCompleted;

        /// <summary>
        /// Starts a Burst-compiled chunk generation job for the given list of chunk world positions.
        /// </summary>
        /// <param name="jobs">List of chunk world positions to generate.</param>
        internal void Start(List<int2> jobs)
        {
            StartRecord();
            IsReady = false;
            foreach (int2 j in jobs) _jobs.Add(j);

            ChunkJob job = new()
            {
                Jobs = _jobs,
                NoiseProfile = _noiseProfile,
                Results = _results.AsParallelWriter(),
                RandomSeed = _config.GlobalSeed,
                Config = _config
            };

            _handle = job.Schedule(_jobs.Length, 1);
        }

        /// <summary>
        /// Waits for the job to finish, transfers the results into the chunk store
        /// and clears temporary containers.
        /// </summary>
        internal void Complete()
        {
            double start = Time.realtimeSinceStartupAsDouble;
            _handle.Complete();
            _chunkManager.AddChunks(_results);
            double totalTime = (Time.realtimeSinceStartupAsDouble - start) * 1000;
            if (totalTime >= 1)
            {
                VoxelEngineLogger.Info<ChunkScheduler>(
                    $"Built {_jobs.Length} ChunkData, Collected Results in <color=red>{totalTime:0.000}</color>ms"
                );
            }

            _jobs.Clear();
            _results.Clear();
            IsReady = true;
            StopRecord();
        }

        /// <summary>
        /// Disposes all native containers after ensuring that the scheduled job has completed.
        /// </summary>
        internal void Dispose()
        {
            _handle.Complete();

            _jobs.Dispose();
            _results.Dispose();
        }
    }
}