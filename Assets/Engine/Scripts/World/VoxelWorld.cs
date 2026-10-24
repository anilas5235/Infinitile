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
using Engine.Scripts.VoxelConfig.Data.Generation;
using Engine.Scripts.WorldGen.Core;
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

        private string[] _biomeNames = Array.Empty<string>(); // of the config the chunk jobs use

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

        /// <summary>True once the chunk column holding the world voxel position has its data generated.</summary>
        /// <param name="position">World voxel position; only x and z are used.</param>
        public bool IsChunkLoaded(int3 position){
            return ChunkManager.IsChunkLoaded(VoxelConstants.WorldToChunkPos(position));
        }

        /// <summary>True if the partition at <paramref name="pos" /> is active and has its collider baked.</summary>
        /// <param name="pos">Partition position.</param>
        public bool IsCollidable(int3 pos){
            return _chunkPool.IsPartitionActive(pos) && _chunkPool.IsCollidable(pos);
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
            RememberBiomeNames(settings.WorldGen);
        }

        /// <summary>Biome names in config order, for <see cref="BiomeName" />.</summary>
        private void RememberBiomeNames(WorldGenSettings worldGen){
            List<Biome> biomes = worldGen.ActiveBiomes();
            _biomeNames = new string[biomes.Count];
            for (int i = 0; i < biomes.Count; i++) _biomeNames[i] = biomes[i] ? biomes[i].name : null;
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
            RememberBiomeNames(worldGen ? worldGen : settings.WorldGen);
            Focus = UnsetFocus;
            TryFocusUpdate();
        }

        /// <summary>
        ///     Samples column (<paramref name="x" />, <paramref name="z" />) with the generation config the chunks are
        ///     built from (after <see cref="Regenerate" /> the new one): climate fields, height, biomes and flora as
        ///     <see cref="WorldSampler.SampleColumn(int,int,in WorldGenConfig,out ColumnSample)" /> gives them. Biome
        ///     indices resolve with <see cref="BiomeName" />. False while the world is not running.
        /// </summary>
        public bool TrySampleColumn(int x, int z, out ColumnSample sample){
            sample = default;
            if (_isShuttingDown || _chunkScheduler == null) return false;
            WorldGenConfig cfg = _chunkScheduler.WorldGen;
            if (!cfg.IsCreated) return false;
            WorldSampler.SampleColumn(x, z, cfg, out sample);
            return true;
        }

        /// <summary>Name of biome <paramref name="index" /> of <see cref="TrySampleColumn" />; null when out of range.</summary>
        public string BiomeName(int index){
            return index >= 0 && index < _biomeNames.Length ? _biomeNames[index] : null;
        }

        /// <summary>
        ///     Architecture and blend methods of the generation config in use; false while the world is not
        ///     running.
        /// </summary>
        public bool TryGetGenerationMethods(out GenArchitecture architecture, out BlendMethod material,
            out BlendMethod height){
            architecture = default;
            material = height = default;
            if (_isShuttingDown || _chunkScheduler == null || !_chunkScheduler.WorldGen.IsCreated) return false;
            WorldGenConfig cfg = _chunkScheduler.WorldGen;
            architecture = cfg.Architecture;
            material = cfg.MaterialMethod;
            height = cfg.HeightMethod;
            return true;
        }

        /// <summary>Loaded chunks and job queue lengths; zero while the world is not running.</summary>
        public WorldStats Stats{
            get{
                if (_isShuttingDown || _scheduler == null || ChunkManager == null) return default;
                return new WorldStats{
                    LoadedChunks = ChunkManager.ChunkCount(),
                    DataQueue = _scheduler.DataQueueCount,
                    LightQueue = _scheduler.LightQueueCount,
                    MeshQueue = _scheduler.MeshQueueCount,
                    BakeQueue = _scheduler.BakeQueueCount
                };
            }
        }

        /// <summary>
        ///     The partition coordinates of the current focus position.
        /// </summary>
        public PriorityUtil.Focus Focus{ get; private set; }

        /// <summary>
        ///     Gets the chunk manager that stores and accesses chunk data.
        /// </summary>
        internal ChunkManager ChunkManager{ get; private set; }

        public VoxelEngineSettings Settings => settings;

        #endregion

        #region Unity

        /// <summary>
        ///     Initializes the singleton, configures engine settings via the provider
        ///     and constructs all core engine components.
        /// </summary>
        protected override void Awake(){
            base.Awake();
            if (Instance != this) return; // a duplicate; base.Awake destroys it

            VoxelEngineProvider.Initialize(new VoxelEngineProvider(), provider => provider.Settings = settings);

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
            _isFocused = focus && _scheduler != null;
            TryFocusUpdate();
        }

        private void Update(){
            _scheduler?.ScheduleUpdate(Focus);
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
            _scheduler?.Dispose();
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
