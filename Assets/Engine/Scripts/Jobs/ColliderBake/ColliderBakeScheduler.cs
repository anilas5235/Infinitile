using System.Collections.Generic;
using Engine.Scripts.Behaviour;
using Engine.Scripts.Components;
using Engine.Scripts.Jobs.Core;
using Engine.Scripts.Utils;
using Engine.Scripts.Utils.Logger;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEngine;

namespace Engine.Scripts.Jobs.ColliderBake{
    /// <summary>
    ///     Schedules and manages jobs that build or update physics colliders for active chunk meshes.
    /// </summary>
    public class ColliderBakeScheduler : JobScheduler{
        private readonly ChunkManager _chunkManager;
        private readonly ChunkPool _chunkPool;
        // The mesh version at Start tells Complete whether the partition's mesh changed during the bake
        private readonly Dictionary<int3, (ChunkPartition partition, int meshVersion)> _meshes;

        /// <summary>
        ///     Indicates whether the scheduler is ready to accept a new batch of collider build jobs.
        /// </summary>
        internal bool IsReady = true;

        private JobHandle _handle;

        private NativeList<EntityId> _jobs;

        /// <summary>
        ///     Initializes a new instance of the <see cref="ColliderBakeScheduler" /> class.
        /// </summary>
        /// <param name="chunkManager">The chunk manager responsible for tracking chunk state and events.</param>
        /// <param name="chunkPool">The chunk pool providing access to active chunk meshes.</param>
        internal ColliderBakeScheduler(ChunkManager chunkManager, ChunkPool chunkPool){
            _chunkManager = chunkManager;
            _chunkPool = chunkPool;
            _meshes = new Dictionary<int3, (ChunkPartition, int)>();

            _jobs = new NativeList<EntityId>(Allocator.Persistent);

            // Physics.BakeMesh reads the collider mesh; finish it before the pool or the mesher rewrites one.
            _chunkPool.PartitionMeshWriting += CompleteJobIfRunning;
        }

        /// <summary>
        ///     Gets a value indicating whether the currently scheduled collider build jobs have completed.
        /// </summary>
        internal bool IsComplete => _handle.IsCompleted;

        /// <summary>
        ///     Starts scheduling collider build jobs for the given list of chunk positions.
        /// </summary>
        /// <param name="jobs">The list of chunk positions whose colliders should be built or updated.</param>
        internal void Start(HashSet<int3> jobs){
            using ProfilerMarker.AutoScope _ = VoxelProfilerMarkers.ColliderBakeSchedule.Auto();
            StartRecord();

            IsReady = false;

            _meshes.EnsureCapacity(jobs.Count); // no growth once the batch size is reached
            foreach (int3 partitionPos in jobs){
                ChunkPartition behaviour = _chunkPool.GetPartition(partitionPos);
                _meshes.Add(partitionPos, (behaviour, behaviour.MeshVersion));
                if (behaviour.ColliderMesh.vertexCount > 0)
                    // Avoid colliders for empty meshes
                    _jobs.Add(behaviour.ColliderMesh.GetEntityId());
            }

            ColliderBuildJob job = new(){
                MeshIDs = _jobs
            };

            _handle = job.Schedule(_jobs.Length, 1);
        }

        /// <summary>
        ///     Completes all scheduled collider build jobs and applies the resulting colliders to the associated chunks.
        /// </summary>
        internal void Complete(){
            using ProfilerMarker.AutoScope _ = VoxelProfilerMarkers.ColliderBakeComplete.Auto();
#if VOXEL_TIMING_LOGS
            double start = Time.realtimeSinceStartupAsDouble;
#endif
            _handle.Complete();

            foreach ((int3 position, (ChunkPartition behaviour, int meshVersion)) in _meshes){
                // The pool may have reclaimed (and possibly re-claimed, which reuses the same object) this
                // partition, or the mesher may have rewritten its mesh while the bake ran; the result is stale then.
                // A re-meshed partition keeps its re-collide flag, a reclaimed one is not collidable, so both re-bake.
                if (!_chunkPool.IsPartitionActive(position) || _chunkPool.GetPartition(position) != behaviour ||
                    behaviour.MeshVersion != meshVersion) continue;

                _chunkPool.ColliderBaked(position);
                _chunkManager.ReCollidedPartition(position);

                if (behaviour.ColliderMesh.vertexCount <= 0) continue;
                behaviour.ApplyColliderMesh();
            }

#if VOXEL_TIMING_LOGS
            double totalTime = (Time.realtimeSinceStartupAsDouble - start) * 1000;

            if (totalTime >= 0.8){
                VoxelEngineLogger.Info<ColliderBakeScheduler>(
                    $"Built {_jobs.Length} colliders, Collected Results in <color=red>{totalTime:0.000}</color>ms"
                );
            }
#endif

            _jobs.Clear();
            _meshes.Clear();

            IsReady = true;

            StopRecord();
        }

        /// <summary>
        ///     Waits for the running bake job; its results are still applied by <see cref="Complete" />.
        /// </summary>
        internal void CompleteJobIfRunning(){
            _handle.Complete();
        }

        /// <summary>
        ///     Disposes the scheduler, ensuring all jobs are completed and native resources are released.
        /// </summary>
        internal void Dispose(){
            _chunkPool.PartitionMeshWriting -= CompleteJobIfRunning;
            _handle.Complete();

            _jobs.Dispose();
        }
    }
}
