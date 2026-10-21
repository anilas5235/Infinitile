using Engine.Scripts.Components;
using Engine.Scripts.Jobs.Chunk;
using Engine.Scripts.Jobs.ColliderBake;
using Engine.Scripts.Jobs.ColliderMeshing;
using Engine.Scripts.Jobs.Light;
using Engine.Scripts.Settings;
using Engine.Scripts.Utils;

namespace Engine.Scripts.Jobs.Core{
    /// <summary>
    ///     Central scheduler that orchestrates data, light, mesh, and collider jobs as a round-robin state machine.
    ///     Manages separate priority queues and selects batches according to scheduler settings.
    /// </summary>
    public class VoxelEngineScheduler{
        private readonly CMeshBuildScheduler _cMeshBuildScheduler;
        private readonly ChunkManager _chunkManager;
        private readonly ChunkPool _chunkPool;
        private readonly ChunkScheduler _chunkScheduler;
        private readonly ColliderBakeScheduler _colliderBakeScheduler;
        private readonly ColliderJobStateHandler _colliderJobHandler;

        private readonly DataJobStateHandler _dataJobHandler;
        private readonly LightJobStateHandler _lightJobHandler;
        private readonly LightScheduler _lightScheduler;
        private readonly MeshJobStateHandler _meshJobHandler;

        private SchedulerUpdate _currentUpdate;

        /// <summary>
        ///     Creates a new scheduler and initializes all job queues and state handlers.
        /// </summary>
        /// <param name="settings">The voxel engine settings.</param>
        /// <param name="cMeshBuildScheduler">The mesh build scheduler.</param>
        /// <param name="chunkScheduler">The chunk data generation scheduler.</param>
        /// <param name="chunkManager">The chunk manager.</param>
        /// <param name="colliderBakeScheduler">The collider bake scheduler.</param>
        /// <param name="chunkPool">The chunk pool.</param>
        /// <param name="lightScheduler">The light scheduler (LGT-06).</param>
        internal VoxelEngineScheduler(VoxelEngineSettings settings,
            CMeshBuildScheduler cMeshBuildScheduler,
            ChunkScheduler chunkScheduler,
            ChunkManager chunkManager,
            ColliderBakeScheduler colliderBakeScheduler,
            ChunkPool chunkPool,
            LightScheduler lightScheduler){
            _cMeshBuildScheduler = cMeshBuildScheduler;
            _lightScheduler = lightScheduler;
            _chunkScheduler = chunkScheduler;
            _colliderBakeScheduler = colliderBakeScheduler;
            _chunkManager = chunkManager;
            _chunkPool = chunkPool;

            _colliderJobHandler = new ColliderJobStateHandler(settings, chunkManager, chunkPool, colliderBakeScheduler);
            _meshJobHandler = new MeshJobStateHandler(settings, chunkManager, chunkPool, cMeshBuildScheduler,
                _colliderJobHandler);
            _lightJobHandler = new LightJobStateHandler(settings, chunkManager, chunkPool, lightScheduler,
                _meshJobHandler);
            _dataJobHandler =
                new DataJobStateHandler(settings, chunkManager, chunkPool, chunkScheduler, _lightJobHandler);

            _currentUpdate = SchedulerUpdate.Data;

            _chunkManager.OnRemeshRequested += OnRemesh;
            _chunkManager.OnLightInvalidated += OnLightInvalidated;
        }

        /// <summary>
        ///     Executes a scheduler tick. Processes queue updates, job dispatching, or result collection
        ///     depending on the current step in the round-robin cycle.
        /// </summary>
        /// <param name="focus">The focus position (e.g., player chunk root).</param>
        internal void ScheduleUpdate(PriorityUtil.Focus focus){
            switch (_currentUpdate){
                // PERF-01: one marker per scheduler stage
                case SchedulerUpdate.Data:
                    using (VoxelProfilerMarkers.SchedulerData.Auto()) _dataJobHandler.Update(focus);
                    break;
                case SchedulerUpdate.Light:
                    using (VoxelProfilerMarkers.SchedulerLight.Auto()) _lightJobHandler.Update(focus);
                    break;
                case SchedulerUpdate.Mesh:
                    using (VoxelProfilerMarkers.SchedulerMesh.Auto()) _meshJobHandler.Update(focus);
                    break;
                case SchedulerUpdate.Collider:
                    using (VoxelProfilerMarkers.SchedulerCollider.Auto()) _colliderJobHandler.Update(focus);
                    break;
            }

            _currentUpdate = (SchedulerUpdate)(((byte)_currentUpdate + 1) % SchedulerUpdateCount);
        }

        /// <summary>
        ///     Updates priorities of all queues and delegates focus update to managers/pool.
        /// </summary>
        /// <param name="focus">The new focus position.</param>
        internal void FocusUpdate(PriorityUtil.Focus focus){
            _dataJobHandler.FocusUpdate(focus);
            _lightJobHandler.FocusUpdate(focus);
            _meshJobHandler.FocusUpdate(focus);
            _colliderJobHandler.FocusUpdate(focus);
            _chunkManager.FocusUpdate(focus);
            _chunkPool.FocusUpdate(focus);
        }

        /// <summary>
        ///     Handles remesh requests by waking up the mesh job handler.
        /// </summary>
        private void OnRemesh(){
            _meshJobHandler.WakeUp();
        }

        /// <summary>LGT-06: a chunk lost its light (neighbour unloaded, edit next to unlit chunks).</summary>
        private void OnLightInvalidated(){
            _lightJobHandler.WakeUp();
        }

        /// <summary>
        ///     INT-30: completes and applies every running batch (data, light, mesh, collider) so the scheduler can be disposed
        ///     without dropping a writable mesh-data array or a job that still reads chunk data.
        /// </summary>
        internal void CompleteRunning(){
            if (!_chunkScheduler.IsReady) _chunkScheduler.Complete();
            _lightScheduler.Complete(); // returns at once when idle
            _cMeshBuildScheduler.Complete(); // returns at once when idle
            if (!_colliderBakeScheduler.IsReady) _colliderBakeScheduler.Complete();
        }

        /// <summary>
        ///     Waits for every running job without applying results, before data the jobs read (registries) is released.
        /// </summary>
        internal void WaitForJobs(){
            _chunkScheduler.CompleteJob();
            _lightScheduler.CompleteJob();
            _cMeshBuildScheduler.CompleteIfRunning();
            _colliderBakeScheduler.CompleteJobIfRunning();
        }

        /// <summary>
        ///     Cleans up all sub-scheduler resources and event handlers.
        /// </summary>
        internal void Dispose(){
            _chunkScheduler.Dispose();
            _lightScheduler.Dispose();
            _cMeshBuildScheduler.Dispose();
            _colliderBakeScheduler.Dispose();

            if (_chunkManager == null) return;
            _chunkManager.OnRemeshRequested -= OnRemesh;
            _chunkManager.OnLightInvalidated -= OnLightInvalidated;
        }

        private const int SchedulerUpdateCount = 4;

        /// <summary>
        ///     Enum representing the four job types in the round-robin scheduling cycle.
        /// </summary>
        private enum SchedulerUpdate : byte{
            /// <summary>Data generation job update.</summary>
            Data,

            /// <summary>Light job update (LGT-06).</summary>
            Light,

            /// <summary>Mesh building job update.</summary>
            Mesh,

            /// <summary>Collider baking job update.</summary>
            Collider
        }

        #region RuntimeStatsAPI

        /// <summary>
        ///     Gets the average execution time of data generation jobs.
        /// </summary>
        public float DataAvgTiming => _chunkScheduler.AvgTime;

        /// <summary>
        ///     Gets the average execution time of mesh building jobs.
        /// </summary>
        public float MeshAvgTiming => _cMeshBuildScheduler.AvgTime;

        /// <summary>
        ///     Gets the average execution time of light jobs (LGT-06).
        /// </summary>
        public float LightAvgTiming => _lightScheduler.AvgTime;

        /// <summary>
        ///     Gets the number of chunks in the light queue (LGT-06).
        /// </summary>
        public int LightQueueCount => _lightJobHandler.QueueCount;

        /// <summary>
        ///     Gets the number of chunks in the data generation queue.
        /// </summary>
        public int DataQueueCount => _dataJobHandler.QueueCount;

        /// <summary>
        ///     Gets the number of partitions in the mesh building queue.
        /// </summary>
        public int MeshQueueCount => _meshJobHandler.QueueCount;

        /// <summary>
        ///     Gets the number of partitions in the collider baking queue.
        /// </summary>
        public int BakeQueueCount => _colliderJobHandler.QueueCount;

        #endregion
    }

    /// <summary>
    ///     Enum representing the three steps in the scheduler state machine.
    /// </summary>
    internal enum SchedulerStep{
        /// <summary>Updating and enqueuing new work items.</summary>
        UpdateQueues,

        /// <summary>Dispatching jobs to sub-schedulers.</summary>
        JobUpdate,

        /// <summary>Collecting and processing results.</summary>
        CollectResults
    }
}
