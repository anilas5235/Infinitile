using System;
using System.Collections;
using System.Collections.Generic;
using Engine.Scripts.Data;
using Engine.Scripts.Jobs.Chunk;
using Engine.Scripts.Jobs.ColliderBake;
using Engine.Scripts.Jobs.ColliderMeshing;
using Engine.Scripts.Jobs.Core;
using Engine.Scripts.Jobs.Light;
using Engine.Scripts.Settings;
using Engine.Scripts.Utils;
using Engine.Scripts.Utils.Extensions;
using Engine.Scripts.VoxelConfig;
using Engine.Scripts.WorldGen.Settings;
using Unity.Mathematics;
using UnityEngine;

namespace Engine.Scripts.World{
    /// <summary>
    ///     Top-level world controller that wires together chunk generation, meshing and colliders
    ///     and exposes a simple API for querying and modifying voxels around a focus transform.
    /// </summary>
    [DefaultExecutionOrder(-101)]
    public class VoxelWorld : Singleton<VoxelWorld>{
        [SerializeField] private Transform focus;

        [SerializeField] private VoxelEngineSettings settings;

        [SerializeField] public bool ShowGizmos;
        private CMeshBuildScheduler _cMeshBuildScheduler;
        private ChunkPool _chunkPool;
        private ChunkScheduler _chunkScheduler;
        private ColliderBakeScheduler _colliderBakeScheduler;
        private LightScheduler _lightScheduler;

        private Coroutine _focusUpdateRoutine;

        private bool _isFocused;
        private bool _isShuttingDown;

        private VoxelEngineScheduler _scheduler;

        internal event Action<Chunk> ChunkChanged;
        internal event Action<Chunk> ChunkDataReady;

        /// <summary>A chunk's light was computed or changed (the renderer uploads it).</summary>
        internal event Action<Chunk> ChunkLightChanged;
        internal event Action<int2> ChunkEvicted;
        internal event Action<int3> PartitionEvicted;
        internal event Action<HashSet<int3>> PartitionBuildRequested;

        internal void RaiseChunkChanged(Chunk chunk){
            ChunkChanged?.Invoke(chunk);
        }

        internal void RaiseChunkDataReady(Chunk chunk){
            ChunkDataReady?.Invoke(chunk);
        }

        internal void RaiseChunkLightChanged(Chunk chunk){
            ChunkLightChanged?.Invoke(chunk);
        }

        internal void RaiseChunkEvicted(int2 chunkPos){
            ChunkEvicted?.Invoke(chunkPos);
        }

        internal void RaisePartitionEvicted(int3 partitionPos){
            PartitionEvicted?.Invoke(partitionPos);
        }

        internal void RequestPartitionBuild(HashSet<int3> partitions){
            PartitionBuildRequested?.Invoke(partitions);
        }

        /// <summary>
        ///     Gets the voxel ID at the given world voxel position.
        /// </summary>
        /// <param name="position">World voxel position.</param>
        /// <returns>Voxel ID at the given position.</returns>
        public ushort GetVoxel(int3 position){
            return ChunkManager.GetVoxel(position);
        }

        /// <summary>
        ///     Sets the voxel ID at a given world voxel position and optionally triggers a remesh.
        /// </summary>
        /// <param name="voxelId">Voxel ID to set.</param>
        /// <param name="position">World voxel position.</param>
        /// <param name="remesh">If true, the affected chunks will be re-meshed.</param>
        /// <returns><c>true</c> if the voxel could be set; otherwise, <c>false</c>.</returns>
        public bool SetVoxel(ushort voxelId, Vector3Int position, bool remesh = true){
            return ChunkManager.SetVoxel(voxelId, position.Int3(), remesh);
        }

        public bool IsCollidable(int3 pos){
            return _chunkPool.IsPartitionActive(pos) && _chunkPool.IsCollidable(pos);
        }

        /// <summary>
        ///     Adjusts derived chunk settings such as load and update distance based on the
        ///     configured draw distance.
        /// </summary>
        private void ConfigureSettings(){
            // data -> light (needs 3 x 3 data) -> mesh (needs 3 x 3 light): one extra ring for the light stage
            settings.Chunk.LoadDistance = settings.Chunk.DrawDistance + 3;
            settings.Chunk.UpdateDistance = math.max(settings.Chunk.DrawDistance - 2, 2);
        }

        /// <summary>
        ///     Constructs all engine components (chunk manager, pool and schedulers)
        ///     via the <see cref="VoxelEngineProvider" />.
        /// </summary>
        private void ConstructEngineComponents(){
            ChunkManager = VoxelEngineProvider.Current.ChunkManager();
            ChunkManager.OnChunkChange += RaiseChunkChanged;
            ChunkManager.OnChunkUnloaded += HandleChunkUnloaded;

            _chunkPool = VoxelEngineProvider.Current.ChunkPool(transform);
            _chunkPool.OnChunkEvicted += HandleChunkEvicted;
            _chunkPool.OnPartitionEvicted += HandlePartitionEvicted;

            ConstructSchedulers(VoxelEngineProvider.Current.ChunkDataScheduler(
                ChunkManager,
                DataImporter.Instance.CreateConfig(),
                this
            ));
        }

        /// <summary>
        ///     Constructs the job schedulers around the existing chunk manager and pool; <see cref="Regenerate" />
        ///     rebuilds them with another data scheduler.
        /// </summary>
        private void ConstructSchedulers(ChunkScheduler chunkScheduler){
            _chunkScheduler = chunkScheduler;

            _cMeshBuildScheduler = VoxelEngineProvider.Current.MeshBuildScheduler(
                ChunkManager,
                _chunkPool,
                DataImporter.Instance.VoxelRegistry,
                this
            );

            _colliderBakeScheduler = VoxelEngineProvider.Current.ColliderBakeScheduler(
                ChunkManager,
                _chunkPool
            );

            _lightScheduler = VoxelEngineProvider.Current.LightScheduler(
                ChunkManager,
                DataImporter.Instance.VoxelRegistry,
                this
            );

            _scheduler = VoxelEngineProvider.Current.VoxelEngineScheduler(
                _cMeshBuildScheduler,
                _chunkScheduler,
                _colliderBakeScheduler,
                ChunkManager,
                _chunkPool,
                _lightScheduler
            );
        }

        private void HandleChunkEvicted(int2 chunkPos){
            if (_isShuttingDown) return;

            // UnloadChunk raises ChunkEvicted itself (through OnChunkUnloaded); only a chunk the manager no
            // longer holds needs the event raised here.
            if (!ChunkManager.UnloadChunk(chunkPos)) RaiseChunkEvicted(chunkPos);
        }

        private void HandleChunkUnloaded(int2 chunkPos){
            // A pooled chunk whose data is gone keeps its partitions active, and they would never be meshed again
            // after a reload. Reclaiming it raises ChunkEvicted through HandleChunkEvicted.
            if (!_isShuttingDown && _chunkPool.TryReclaimChunk(chunkPos)) return;
            RaiseChunkEvicted(chunkPos);
        }

        private void HandlePartitionEvicted(int3 partitionPos){
            if (_isShuttingDown) return;

            RaisePartitionEvicted(partitionPos);
        }

        /// <summary>
        ///     Waits for the running generation, light, mesh and bake jobs, e.g. before the <see cref="DataImporter" />
        ///     releases the registries they read. Destruction order between the two is not defined.
        /// </summary>
        internal void WaitForJobs(){
            _scheduler?.WaitForJobs();
        }

        /// <summary>A focus no real position equals, so the next focus update always reaches the schedulers.</summary>
        private static PriorityUtil.Focus UnsetFocus => new(new int3(1, 1, 1) * int.MinValue, new float3(0, 0, 1));

        private void TryFocusUpdate(){
            if (!_isFocused) return;
            PriorityUtil.Focus newFocus = new(VoxelConstants.WorldToPartitionPos(focus.position.Int3()),
                focus.forward.Float3());

            if (newFocus.Equals(Focus)) return;
            Focus = newFocus;
            _scheduler.FocusUpdate(Focus);
        }

        #region API

        /// <summary>
        ///     Regenerates the world with <paramref name="worldGen" /> (null = the asset of the engine
        ///     settings), e.g. to compare configurations at the same spot. Running jobs are completed, every loaded
        ///     chunk is evicted through the normal events (the renderer releases its buffers, colliders are cleared)
        ///     and the schedulers are rebuilt; loading restarts around the focus. The settings are copied into the
        ///     generation config, so the caller keeps ownership of <paramref name="worldGen" /> and may destroy it
        ///     afterwards. Edits made to the world are lost. Throws like <see cref="WorldGenSettings.ToConfig" />
        ///     before anything is torn down.
        /// </summary>
        public void Regenerate(WorldGenSettings worldGen = null){
            if (_isShuttingDown) return;

            // Built first, so invalid settings throw while the running world is still intact.
            ChunkScheduler chunkScheduler = VoxelEngineProvider.Current.ChunkDataScheduler(
                ChunkManager,
                DataImporter.Instance.CreateConfig(),
                this,
                worldGen
            );

            _scheduler.CompleteRunning();
            _scheduler.Dispose(); // unsubscribes the job-completion listeners and releases the old config
            _chunkPool.ReclaimAll(); // pool chunks: partitions, colliders and chunk data, with eviction events
            ChunkManager.UnloadAll(); // the load ring outside the draw distance

            ConstructSchedulers(chunkScheduler);
            Focus = UnsetFocus;
            TryFocusUpdate();
        }

        /// <summary>
        ///     The partition coordinates of the current focus position.
        /// </summary>
        public PriorityUtil.Focus Focus{ get; private set; }

        /// <summary>
        ///     Gets the chunk manager that stores and accesses chunk data.
        /// </summary>
        public ChunkManager ChunkManager{ get; private set; }

        public VoxelEngineSettings Settings => settings;

        #endregion

        #region Unity

        /// <summary>
        ///     Initializes the singleton, configures engine settings via the provider
        ///     and constructs all core engine components.
        /// </summary>
        protected override void Awake(){
            base.Awake();
            VoxelEngineProvider.Initialize(new VoxelEngineProvider(), provider => {
                ConfigureSettings();
                provider.Settings = settings;
            });

            ConstructEngineComponents();

            Focus = UnsetFocus;
        }

        private void OnEnable(){
            if (_focusUpdateRoutine != null) StopCoroutine(_focusUpdateRoutine);
            _focusUpdateRoutine = StartCoroutine(FocusUpdateRoutine());
        }

        private void OnDisable(){
            if (_focusUpdateRoutine != null) StopCoroutine(_focusUpdateRoutine);
            _focusUpdateRoutine = null;
        }

        /// <summary>
        ///     Initializes focus state once all objects are created.
        /// </summary>
        private void Start(){
            _isFocused = focus;
            TryFocusUpdate();
        }

        private void Update(){
            _scheduler.ScheduleUpdate(Focus);
        }

        /// <summary>
        ///     Cleans up engine components and disposes schedulers on destruction.
        /// </summary>
        protected override void OnDestroy(){
            _isShuttingDown = true;
            if (ChunkManager != null){
                ChunkManager.OnChunkChange -= RaiseChunkChanged;
                ChunkManager.OnChunkUnloaded -= HandleChunkUnloaded;
            }

            if (_chunkPool != null){
                _chunkPool.OnChunkEvicted -= HandleChunkEvicted;
                _chunkPool.OnPartitionEvicted -= HandlePartitionEvicted;
                _chunkPool.Dispose();
            }

            base.OnDestroy();
            _scheduler.Dispose();
            ChunkManager?.Dispose();
        }

        private IEnumerator FocusUpdateRoutine(){
            while (true){
                yield return new WaitForSeconds(.5f);
                TryFocusUpdate();
            }
        }

        #endregion
    }
}
