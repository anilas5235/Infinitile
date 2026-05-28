using System.Collections.Generic;
using Engine.Scripts.Behaviour;
using Engine.Scripts.Components;
using Engine.Scripts.Jobs.Core;
using Engine.Scripts.Utils.Logger;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace Engine.Scripts.Jobs.ColliderBake
{
    /// <summary>
    ///     Schedules and manages jobs that build or update physics colliders for active chunk meshes.
    /// </summary>
    public class ColliderBakeScheduler : JobScheduler
    {
        private readonly ChunkManager _chunkManager;
        private readonly ChunkPool _chunkPool;

        private JobHandle _handle;

        private NativeList<int> _jobs;
        private readonly Dictionary<int3, ChunkPartition> _meshes;

        /// <summary>
        ///     Indicates whether the scheduler is ready to accept a new batch of collider build jobs.
        /// </summary>
        internal bool IsReady = true;

        /// <summary>
        ///     Initializes a new instance of the <see cref="ColliderBakeScheduler" /> class.
        /// </summary>
        /// <param name="chunkManager">The chunk manager responsible for tracking chunk state and events.</param>
        /// <param name="chunkPool">The chunk pool providing access to active chunk meshes.</param>
        internal ColliderBakeScheduler(ChunkManager chunkManager, ChunkPool chunkPool)
        {
            _chunkManager = chunkManager;
            _chunkPool = chunkPool;
            _meshes = new Dictionary<int3, ChunkPartition>();

            _jobs = new NativeList<int>(Allocator.Persistent);
        }

        /// <summary>
        ///     Gets a value indicating whether the currently scheduled collider build jobs have completed.
        /// </summary>
        internal bool IsComplete => _handle.IsCompleted;

        /// <summary>
        ///     Starts scheduling collider build jobs for the given list of chunk positions.
        /// </summary>
        /// <param name="jobs">The list of chunk positions whose colliders should be built or updated.</param>
        internal void Start(List<int3> jobs)
        {
            StartRecord();

            IsReady = false;

            foreach (int3 partitionPos in jobs)
            {
                ChunkPartition behaviour = _chunkPool.GetPartition(partitionPos);
                _meshes.Add(partitionPos, behaviour);
                if (behaviour.ColliderMesh.vertexCount > 0)
                    // Avoid colliders for empty meshes
                    _jobs.Add(behaviour.ColliderMesh.GetInstanceID());
            }

            ColliderBuildJob job = new()
            {
                MeshIDs = _jobs
            };

            _handle = job.Schedule(_jobs.Length, 1);
        }

        /// <summary>
        ///     Completes all scheduled collider build jobs and applies the resulting colliders to the associated chunks.
        /// </summary>
        internal void Complete()
        {
            double start = Time.realtimeSinceStartupAsDouble;
            _handle.Complete();

            foreach ((int3 position, ChunkPartition behaviour) in _meshes)
            {
                _chunkPool.ColliderBaked(position);
                _chunkManager.ReCollidedPartition(position);

                if (behaviour.ColliderMesh.vertexCount <= 0) continue;
                behaviour.ApplyColliderMesh();
            }

            double totalTime = (Time.realtimeSinceStartupAsDouble - start) * 1000;

            if (totalTime >= 0.8)
                VoxelEngineLogger.Info<ColliderBakeScheduler>(
                    $"Built {_jobs.Length} colliders, Collected Results in <color=red>{totalTime:0.000}</color>ms"
                );

            _jobs.Clear();
            _meshes.Clear();

            IsReady = true;

            StopRecord();
        }

        /// <summary>
        ///     Disposes the scheduler, ensuring all jobs are completed and native resources are released.
        /// </summary>
        internal void Dispose()
        {
            _handle.Complete();

            _jobs.Dispose();
        }
    }
}