using Engine.Scripts.Components;
using Engine.Scripts.Jobs.Chunk;
using Engine.Scripts.Jobs.ColliderBake;
using Engine.Scripts.Jobs.Meshing;
using Engine.Scripts.Settings;
using Unity.Mathematics;

namespace Engine.Scripts.Jobs.Core
{
    /// <summary>
    /// Central scheduler that orchestrates data, mesh, and collider jobs as a round-robin state machine.
    /// Manages separate priority queues and selects batches according to scheduler settings.
    /// </summary>
    public class VoxelEngineScheduler
    {
        private readonly ChunkManager _chunkManager;
        private readonly ChunkPool _chunkPool;
        private readonly ChunkScheduler _chunkScheduler;
        private readonly ColliderBakeScheduler _colliderBakeScheduler;
        private readonly ColliderJobStateHandler _colliderJobHandler;

        private readonly DataJobStateHandler _dataJobHandler;
        private readonly MeshBuildScheduler _meshBuildScheduler;
        private readonly MeshJobStateHandler _meshJobHandler;

        private SchedulerUpdate _currentUpdate;

        /// <summary>
        /// Creates a new scheduler and initializes all job queues and state handlers.
        /// </summary>
        /// <param name="settings">The voxel engine settings.</param>
        /// <param name="meshBuildScheduler">The mesh build scheduler.</param>
        /// <param name="chunkScheduler">The chunk data generation scheduler.</param>
        /// <param name="chunkManager">The chunk manager.</param>
        /// <param name="colliderBakeScheduler">The collider bake scheduler.</param>
        /// <param name="chunkPool">The chunk pool.</param>
        internal VoxelEngineScheduler(VoxelEngineSettings settings,
            MeshBuildScheduler meshBuildScheduler,
            ChunkScheduler chunkScheduler,
            ChunkManager chunkManager,
            ColliderBakeScheduler colliderBakeScheduler,
            ChunkPool chunkPool)
        {
            _meshBuildScheduler = meshBuildScheduler;
            _chunkScheduler = chunkScheduler;
            _colliderBakeScheduler = colliderBakeScheduler;
            _chunkManager = chunkManager;
            _chunkPool = chunkPool;

            _colliderJobHandler = new ColliderJobStateHandler(settings, chunkManager, chunkPool, colliderBakeScheduler);
            _meshJobHandler = new MeshJobStateHandler(settings, chunkManager, chunkPool, meshBuildScheduler,
                _colliderJobHandler);
            _dataJobHandler =
                new DataJobStateHandler(settings, chunkManager, chunkPool, chunkScheduler, _meshJobHandler);

            _currentUpdate = SchedulerUpdate.Data;

            _chunkManager.OnRemeshRequested += OnRemesh;
        }

        /// <summary>
        /// Executes a scheduler tick. Processes queue updates, job dispatching, or result collection
        /// depending on the current step in the round-robin cycle.
        /// </summary>
        /// <param name="focus">The focus position (e.g., player chunk root).</param>
        internal void ScheduleUpdate(int3 focus)
        {
            switch (_currentUpdate)
            {
                case SchedulerUpdate.Data:
                    _dataJobHandler.Update(focus);
                    break;
                case SchedulerUpdate.Mesh:
                    _meshJobHandler.Update(focus);
                    break;
                case SchedulerUpdate.Collider:
                    _colliderJobHandler.Update(focus);
                    break;
            }

            _currentUpdate = (SchedulerUpdate)(((byte)_currentUpdate + 1) % 3);
        }

        /// <summary>
        /// Updates priorities of all queues and delegates focus update to managers/pool.
        /// </summary>
        /// <param name="focus">The new focus position.</param>
        internal void FocusUpdate(int3 focus)
        {
            _dataJobHandler.FocusUpdate(focus);
            _meshJobHandler.FocusUpdate(focus);
            _colliderJobHandler.FocusUpdate(focus);
            _chunkManager.FocusUpdate(focus);
            _chunkPool.FocusUpdate(focus);
        }

        /// <summary>
        /// Handles remesh requests by waking up the mesh job handler.
        /// </summary>
        private void OnRemesh()
        {
            _meshJobHandler.WakeUp();
        }

        /// <summary>
        /// Cleans up all sub-scheduler resources and event handlers.
        /// </summary>
        internal void Dispose()
        {
            _chunkScheduler.Dispose();
            _meshBuildScheduler.Dispose();
            _colliderBakeScheduler.Dispose();

            if (_chunkManager != null) _chunkManager.OnRemeshRequested -= OnRemesh;
        }

        /// <summary>
        /// Enum representing the three job types in the round-robin scheduling cycle.
        /// </summary>
        private enum SchedulerUpdate : byte
        {
            /// <summary>Data generation job update.</summary>
            Data,
            /// <summary>Mesh building job update.</summary>
            Mesh,
            /// <summary>Collider baking job update.</summary>
            Collider
        }

        #region RuntimeStatsAPI

        /// <summary>
        /// Gets the average execution time of data generation jobs.
        /// </summary>
        public float DataAvgTiming => _chunkScheduler.AvgTime;

        /// <summary>
        /// Gets the average execution time of mesh building jobs.
        /// </summary>
        public float MeshAvgTiming => _meshBuildScheduler.AvgTime;

        /// <summary>
        /// Gets the number of chunks in the data generation queue.
        /// </summary>
        public int DataQueueCount => _dataJobHandler.QueueCount;

        /// <summary>
        /// Gets the number of partitions in the mesh building queue.
        /// </summary>
        public int MeshQueueCount => _meshJobHandler.QueueCount;

        /// <summary>
        /// Gets the number of partitions in the collider baking queue.
        /// </summary>
        public int BakeQueueCount => _colliderJobHandler.QueueCount;

        #endregion
    }
    
    /// <summary>
    /// Enum representing the three steps in the scheduler state machine.
    /// </summary>
    internal enum SchedulerStep
    {
        /// <summary>Updating and enqueuing new work items.</summary>
        UpdateQueues,
        /// <summary>Dispatching jobs to sub-schedulers.</summary>
        JobUpdate,
        /// <summary>Collecting and processing results.</summary>
        CollectResults
    }
}