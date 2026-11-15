using System.Collections.Generic;
using Engine.Scripts.Data;
using Engine.Scripts.Jobs.Core;
using Engine.Scripts.Settings;
using Engine.Scripts.Utils;
using Engine.Scripts.Utils.Extensions;
using Engine.Scripts.Utils.Logger;
using Engine.Scripts.World;
using Engine.Scripts.WorldGen.Settings;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEngine;

namespace Engine.Scripts.Jobs.Chunk{
    /// <summary>
    ///     Schedules and manages jobs that generate chunk data, wraps <see cref="ChunkJob" />,
    ///     tracks job lists and result maps, and forwards completed chunks to <see cref="ChunkManager" />.
    /// </summary>
    internal class ChunkScheduler : JobScheduler{
        private readonly ChunkManager _chunkManager;
        private readonly GeneratorConfig _config;
        private readonly VoxelWorld _world;

        /// <summary>
        ///     Indicates whether the scheduler is ready to start a new chunk generation batch.
        /// </summary>
        internal bool IsReady = true;

        private JobHandle _handle;
        private NativeList<int2> _jobs;
        private NativeParallelHashMap<int2, ChunkVoxelData> _results;

        // Complete after a cut: the batch's chunks are in the store, the upload continues at this job
        private bool _added;
        private int _uploaded;

        /// <summary>
        ///     Initializes a new instance of the <see cref="ChunkScheduler" /> class with its own result collection.
        /// </summary>
        /// <param name="settings">Voxel engine settings providing chunk dimensions and load distance.</param>
        /// <param name="chunkManager">Chunk manager that receives finished chunk data.</param>
        /// <param name="config">Generator configuration used by chunk jobs.</param>
        /// <param name="world">Voxel world that raises events when chunk data is ready.</param>
        internal ChunkScheduler(
            VoxelEngineSettings settings,
            ChunkManager chunkManager,
            GeneratorConfig config,
            VoxelWorld world
        ){
            _chunkManager = chunkManager;
            _config = config;
            _world = world;
            _jobs = new NativeList<int2>(Allocator.Domain);
            _results = new NativeParallelHashMap<int2, ChunkVoxelData>(
                settings.Chunk.LoadDistance.SquareSize(),
                Allocator.Domain
            );
        }


        /// <summary>The world-generation config the chunk jobs read; released with this scheduler.</summary>
        internal WorldGenConfig WorldGen => _config.WorldGen;

        /// <summary>
        ///     Gets a value indicating whether the scheduled job has completed.
        /// </summary>
        internal bool IsComplete => _handle.IsCompleted;

        /// <summary>
        ///     Starts a Burst-compiled chunk generation job for the given list of chunk world positions.
        /// </summary>
        /// <param name="jobs">List of chunk world positions to generate.</param>
        internal void Start(HashSet<int2> jobs){
            using ProfilerMarker.AutoScope _ = VoxelProfilerMarkers.DataGenSchedule.Auto();
            StartRecord();
            IsReady = false;
            foreach (int2 j in jobs) _jobs.Add(j);

            ChunkJob job = new(){
                Jobs = _jobs,
                Results = _results.AsParallelWriter(),
                Config = _config
            };

            _handle = job.Schedule(_jobs.Length, 1);
        }

        /// <summary>
        ///     Waits for the job to finish, transfers the results into the chunk store
        ///     and clears temporary containers.
        ///     Items of work for <paramref name="budget" />: adding the batch to the chunk store, and the upload of
        ///     every chunk (the world hands its voxel data to the renderer). When the budget runs out in between,
        ///     the next call continues with the next chunk.
        /// </summary>
        /// <param name="budget">The frame's budget; null means no limit.</param>
        /// <returns>True when the batch is applied; false when the budget cut the call.</returns>
        internal bool Complete(FrameBudget budget = null){
            budget ??= FrameBudget.Unlimited;
            using ProfilerMarker.AutoScope _ = VoxelProfilerMarkers.DataGenComplete.Auto();
#if VOXEL_TIMING_LOGS
            double start = Time.realtimeSinceStartupAsDouble;
#endif
            _handle.Complete();
            if (!_added){
                if (!budget.Next()) return false;

                _chunkManager.AddChunks(_results);
                _results.Clear(); // the store owns the chunk data from here on
                _added = true;
                _uploaded = 0;
            }

#if VOXEL_TIMING_LOGS
            double totalTime = (Time.realtimeSinceStartupAsDouble - start) * 1000;
            if (totalTime >= 1){
                VoxelEngineLogger.Info<ChunkScheduler>(
                    $"Built {_jobs.Length} ChunkData, Collected Results in <color=red>{totalTime:0.000}</color>ms"
                );
            }
#endif

            // A chunk that was unloaded again in the frames a cut call waited is skipped
            for (; _uploaded < _jobs.Length; _uploaded++){
                if (!budget.Next()) return false;

                if (_chunkManager.TryGetChunk(_jobs[_uploaded], out Data.Chunk chunk))
                    _world.RaiseChunkDataReady(chunk);
            }

            _jobs.Clear();
            _added = false;
            IsReady = true;
            StopRecord();
            return true;
        }

        /// <summary>
        ///     Waits for the running job without applying its results; <see cref="Complete" /> still applies them.
        /// </summary>
        internal void CompleteJob(){
            _handle.Complete();
        }

        /// <summary>
        ///     Disposes all native containers after ensuring that the scheduled job has completed; chunk data of a
        ///     batch that was never completed is released.
        /// </summary>
        internal void Dispose(){
            _handle.Complete();

            foreach (KeyValue<int2, ChunkVoxelData> r in _results) r.Value.Dispose();
            _jobs.Dispose();
            _results.Dispose();
            _config.Dispose(); // Releases the noise parameters created for this scheduler's config
        }
    }
}
