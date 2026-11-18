using Engine.Scripts.Jobs.Chunk;
using Engine.Scripts.Jobs.ColliderBake;
using Engine.Scripts.Jobs.ColliderMeshing;
using Engine.Scripts.Jobs.Light;
using Engine.Scripts.Settings;
using Engine.Scripts.Utils;
using Engine.Scripts.World;
using Unity.Mathematics;
using Unity.Profiling;

namespace Engine.Scripts.Jobs.Core{
    /// <summary>
    ///     Central scheduler that orchestrates data, light, mesh, and collider jobs. Every frame one stage gets a
    ///     turn (collect its finished batch, look for work, start the next batch); the stages take turns round
    ///     robin, and a stage that sleeps or still waits for its batch passes the frame on to the next one.
    ///     The main-thread time of a turn is bounded by <see cref="SchedulerSettings.mainThreadBudgetMs" />: a turn
    ///     that runs out of it continues in the next frame (see <see cref="FrameBudget" />, <see cref="StageRing" />).
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

        private readonly StageRing _ring;

        /// <summary>
        ///     Creates a new scheduler and initializes all job queues and state handlers.
        /// </summary>
        /// <param name="settings">The voxel engine settings.</param>
        /// <param name="cMeshBuildScheduler">The mesh build scheduler.</param>
        /// <param name="chunkScheduler">The chunk data generation scheduler.</param>
        /// <param name="chunkManager">The chunk manager.</param>
        /// <param name="colliderBakeScheduler">The collider bake scheduler.</param>
        /// <param name="chunkPool">The chunk pool.</param>
        /// <param name="lightScheduler">The light scheduler.</param>
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

            // One profiler marker per scheduler stage
            _ring = new StageRing(
                new IPipelineStage[]{ _dataJobHandler, _lightJobHandler, _meshJobHandler, _colliderJobHandler },
                new[]{
                    VoxelProfilerMarkers.SchedulerData, VoxelProfilerMarkers.SchedulerLight,
                    VoxelProfilerMarkers.SchedulerMesh, VoxelProfilerMarkers.SchedulerCollider
                });
            Budget = new FrameBudget(settings.Scheduler.mainThreadBudgetMs,
                capShare: settings.Scheduler.cappedFrameSpareShare,
                fastCapShare: settings.Scheduler.fastCapBudgetShare);

            _chunkManager.OnRemeshRequested += OnRemesh;
            _chunkManager.OnLightInvalidated += OnLightInvalidated;
            _chunkManager.OnChunkUnloaded += OnChunkUnloaded;

            // Under a frame budget the data and mesh stages look whether a job they would wait for still runs and
            // try again in the next frame, instead of blocking the main thread on it.
            _chunkScheduler.EvictionReadersRunning = EvictionReadersRunning;
            _cMeshBuildScheduler.BakeRunning = BakeRunning;
        }

        /// <summary>
        ///     Whether a job that the eviction of <paramref name="position" /> would wait for still runs: the
        ///     collider mesh job or the light job reading its voxel data, or the collider bake while the pool
        ///     renders the chunk (the eviction reclaims it and clears its collider meshes).
        /// </summary>
        private bool EvictionReadersRunning(int2 position){
            return (_chunkManager.IsReadByAccessor(position) && _cMeshBuildScheduler.Running) ||
                   (_chunkManager.IsReadByLightJob(position) && _lightScheduler.Running) ||
                   (_chunkPool.IsChunkActive(position) && _colliderBakeScheduler.Running);
        }

        private bool BakeRunning(){
            return _colliderBakeScheduler.Running;
        }

        /// <summary>
        ///     The main-thread time the stages may spend per frame, from
        ///     <see cref="SchedulerSettings.mainThreadBudgetMs" /> and
        ///     <see cref="SchedulerSettings.cappedFrameSpareShare" /> and <see cref="SchedulerSettings.fastCapBudgetShare" />
        ///     when the world is built; under a frame cap it follows the cap that is in force in the frame
        ///     (<see cref="FrameCap" />): it grows under a cap slower than 120 frames per second and shrinks under a
        ///     faster one. The focus update is not
        ///     part of it: it runs outside the tick, from a check every 0.5 s, when the focus moved to another
        ///     partition or its forward vector (quantised to 45°) changed.
        /// </summary>
        internal FrameBudget Budget{ get; set; }

        /// <summary>The state handlers and job schedulers of the four stages, for measurements of the pipeline.</summary>
        internal DataJobStateHandler DataHandler => _dataJobHandler;

        internal LightJobStateHandler LightHandler => _lightJobHandler;
        internal MeshJobStateHandler MeshHandler => _meshJobHandler;
        internal ColliderJobStateHandler ColliderHandler => _colliderJobHandler;
        internal ChunkScheduler DataJobs => _chunkScheduler;
        internal LightScheduler LightJobs => _lightScheduler;
        internal CMeshBuildScheduler MeshJobs => _cMeshBuildScheduler;
        internal ColliderBakeScheduler BakeJobs => _colliderBakeScheduler;

        /// <summary>
        ///     Executes a scheduler tick: the first stage in round-robin order that has main-thread work gets the
        ///     frame, and the stage after it is first in line on the next tick. So at most one stage applies a batch
        ///     per frame, no frame is left unused while any stage has work, and a busy stage cannot starve the others.
        ///     A turn that uses up the frame's budget stops between two items of work and continues on the next
        ///     tick, before any other stage.
        /// </summary>
        /// <param name="focus">The focus position (e.g., player chunk root).</param>
        internal void ScheduleUpdate(PriorityUtil.Focus focus){
            // The frame cap is read every frame: a settings menu can change it while the world runs
            Budget.BeginFrame(Budget.FollowsCap ? FrameCap.PeriodMs() : 0f);
            LastStage = _ring.Turn(focus, Budget);
            Budget.EndFrame();
        }

        /// <summary>
        ///     The stage that had the last frame's turn (0 data, 1 light, 2 mesh, 3 bake), or -1 when no stage had
        ///     work; for measurements.
        /// </summary>
        internal int LastStage{ get; private set; } = -1;

        /// <summary>
        ///     Updates priorities of all queues and delegates focus update to managers/pool.
        /// </summary>
        /// <param name="focus">The new focus position.</param>
        internal void FocusUpdate(PriorityUtil.Focus focus){
            using ProfilerMarker.AutoScope _ = VoxelProfilerMarkers.FocusUpdate.Auto();
            using (VoxelProfilerMarkers.FocusData.Auto()) _dataJobHandler.FocusUpdate(focus);
            using (VoxelProfilerMarkers.FocusLight.Auto()) _lightJobHandler.FocusUpdate(focus);
            using (VoxelProfilerMarkers.FocusMesh.Auto()) _meshJobHandler.FocusUpdate(focus);
            using (VoxelProfilerMarkers.FocusCollider.Auto()) _colliderJobHandler.FocusUpdate(focus);
            using (VoxelProfilerMarkers.FocusChunkStore.Auto()) _chunkManager.FocusUpdate(focus);
            using (VoxelProfilerMarkers.FocusPool.Auto()) _chunkPool.FocusUpdate(focus);
        }

        /// <summary>
        ///     Handles remesh requests by waking up the mesh job handler.
        /// </summary>
        private void OnRemesh(){
            _meshJobHandler.WakeUp();
        }

        /// <summary>
        ///     A chunk's data was unloaded, e.g. by the pool reclaiming it; one inside the load distance must be
        ///     generated again even if the focus does not move.
        /// </summary>
        private void OnChunkUnloaded(int2 _){
            _dataJobHandler.WakeUp();
        }

        /// <summary>A chunk lost its light (neighbour unloaded, edit next to unlit chunks).</summary>
        private void OnLightInvalidated(){
            _lightJobHandler.WakeUp();
        }

        /// <summary>
        ///     Completes and applies every running batch (data, light, mesh, collider) so the scheduler can be disposed
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
            _chunkScheduler.EvictionReadersRunning = null;
            _cMeshBuildScheduler.BakeRunning = null;
            _chunkScheduler.Dispose();
            _lightScheduler.Dispose();
            _cMeshBuildScheduler.Dispose();
            _colliderBakeScheduler.Dispose();

            if (_chunkManager == null) return;
            _chunkManager.OnRemeshRequested -= OnRemesh;
            _chunkManager.OnLightInvalidated -= OnLightInvalidated;
            _chunkManager.OnChunkUnloaded -= OnChunkUnloaded;
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
        ///     Gets the average execution time of light jobs.
        /// </summary>
        public float LightAvgTiming => _lightScheduler.AvgTime;

        /// <summary>
        ///     Gets the number of chunks in the light queue.
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
}
