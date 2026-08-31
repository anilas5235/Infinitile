using System;
using System.Collections;
using System.Collections.Generic;
using Engine.Scripts.Components;
using Engine.Scripts.Data;
using Engine.Scripts.Jobs.Chunk;
using Engine.Scripts.Jobs.ColliderBake;
using Engine.Scripts.Jobs.ColliderMeshing;
using Engine.Scripts.Jobs.Core;
using Engine.Scripts.Noise;
using Engine.Scripts.Settings;
using Engine.Scripts.Utils;
using Engine.Scripts.Utils.Extensions;
using Engine.Scripts.VoxelConfig;
using Unity.Mathematics;
using UnityEngine;

namespace Engine.Scripts.World
{
    /// <summary>
    ///     Top-level world controller that wires together chunk generation, meshing and colliders
    ///     and exposes a simple API for querying and modifying voxels around a focus transform.
    /// </summary>
    [DefaultExecutionOrder(-101)]
    public class VoxelWorld : Singleton<VoxelWorld>
    {
        
        [SerializeField] private Transform focus;

        [SerializeField] private VoxelEngineSettings settings;
        
        [SerializeField] public bool ShowGizmos;

        private VoxelEngineScheduler _scheduler;
        private ChunkPool _chunkPool;
        private ChunkScheduler _chunkScheduler;
        private ColliderBakeScheduler _colliderBakeScheduler;
        private NoiseProfile _noiseProfile;

        private Coroutine _focusUpdateRoutine;

        private bool _isFocused;
        private bool _isShuttingDown;
        private CMeshBuildScheduler _cMeshBuildScheduler;

        internal event Action<Chunk> ChunkChanged;
        internal event Action<Chunk> ChunkDataReady;
        internal event Action<int2> ChunkEvicted;
        internal event Action<int3> PartitionEvicted;
        internal event Action<HashSet<int3>> PartitionBuildRequested;

        internal void RaiseChunkChanged(Chunk chunk) => ChunkChanged?.Invoke(chunk);

        internal void RaiseChunkDataReady(Chunk chunk)
            => ChunkDataReady?.Invoke(chunk);

        internal void RaiseChunkEvicted(int2 chunkPos) => ChunkEvicted?.Invoke(chunkPos);

        internal void RaisePartitionEvicted(int3 partitionPos) => PartitionEvicted?.Invoke(partitionPos);

        internal void RequestPartitionBuild(HashSet<int3> partitions) => PartitionBuildRequested?.Invoke(partitions);
       
        /// <summary>
        ///     Gets the voxel ID at the given world voxel position.
        /// </summary>
        /// <param name="position">World voxel position.</param>
        /// <returns>Voxel ID at the given position.</returns>
        public ushort GetVoxel(int3 position) => ChunkManager.GetVoxel(position);

        /// <summary>
        ///     Sets the voxel ID at a given world voxel position and optionally triggers a remesh.
        /// </summary>
        /// <param name="voxelId">Voxel ID to set.</param>
        /// <param name="position">World voxel position.</param>
        /// <param name="remesh">If true, the affected chunks will be re-meshed.</param>
        /// <returns><c>true</c> if the voxel could be set; otherwise, <c>false</c>.</returns>
        public bool SetVoxel(ushort voxelId, Vector3Int position, bool remesh = true) =>
            ChunkManager.SetVoxel(voxelId, position.Int3(), remesh);
        
        public bool IsCollidable(int3 pos) => _chunkPool.IsPartitionActive(pos) && _chunkPool.IsCollidable(pos);

        /// <summary>
        ///     Adjusts derived chunk settings such as load and update distance based on the
        ///     configured draw distance.
        /// </summary>
        private void ConfigureSettings()
        {
            settings.Chunk.LoadDistance = settings.Chunk.DrawDistance + 2;
            settings.Chunk.UpdateDistance = math.max(settings.Chunk.DrawDistance - 2, 2);
        }

        /// <summary>
        ///     Constructs all engine components (noise profile, chunk manager, pools and schedulers)
        ///     via the <see cref="VoxelEngineProvider" />.
        /// </summary>
        private void ConstructEngineComponents()
        {
            ChunkManager = VoxelEngineProvider.Current.ChunkManager();
            ChunkManager.OnChunkChange += RaiseChunkChanged;

            _chunkPool = VoxelEngineProvider.Current.ChunkPool(transform);
            _chunkPool.OnChunkEvicted += HandleChunkEvicted;
            _chunkPool.OnPartitionEvicted += HandlePartitionEvicted;

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

            _chunkScheduler = VoxelEngineProvider.Current.ChunkDataScheduler(
                ChunkManager,
                DataImporter.Instance.CreateConfig(),
                this
            );

            _scheduler = VoxelEngineProvider.Current.VoxelEngineScheduler(
                _cMeshBuildScheduler,
                _chunkScheduler,
                _colliderBakeScheduler,
                ChunkManager,
                _chunkPool
            );
        }

        private void HandleChunkEvicted(int2 chunkPos)
        {
            if (_isShuttingDown) return;

            ChunkManager.UnloadChunk(chunkPos);
            RaiseChunkEvicted(chunkPos);
        }

        private void HandlePartitionEvicted(int3 partitionPos)
        {
            if (_isShuttingDown) return;

            RaisePartitionEvicted(partitionPos);
        }

        private void TryFocusUpdate()
        {
            if (!_isFocused) return;
            PriorityUtil.Focus newFocus = new(VoxelConstants.WorldToPartitionPos(focus.position.Int3()),
                focus.forward.Float3());

            if (newFocus.Equals(Focus)) return;
            Focus = newFocus;
            _scheduler.FocusUpdate(Focus);
        }

        #region API

        /// <summary>
        ///     The partition coordinates of the current focus position.
        /// </summary>
        public PriorityUtil.Focus Focus { get; private set; }

        /// <summary>
        ///     Gets the chunk manager that stores and accesses chunk data.
        /// </summary>
        public ChunkManager ChunkManager { get; private set; }

        public VoxelEngineSettings Settings => settings;

        #endregion

        #region Unity

        /// <summary>
        ///     Initializes the singleton, configures engine settings via the provider
        ///     and constructs all core engine components.
        /// </summary>
        protected override void Awake()
        {
            base.Awake();
            VoxelEngineProvider.Initialize(new VoxelEngineProvider(), provider =>
            {
                ConfigureSettings();
                provider.Settings = settings;
            });

            ConstructEngineComponents();

            Focus = new PriorityUtil.Focus(new int3(1, 1, 1) * int.MinValue, new float3(0, 0, 1));
        }

        private void OnEnable()
        {
            if (_focusUpdateRoutine != null) StopCoroutine(_focusUpdateRoutine);
            _focusUpdateRoutine = StartCoroutine(FocusUpdateRoutine());
        }

        private void OnDisable()
        {
            if (_focusUpdateRoutine != null) StopCoroutine(_focusUpdateRoutine);
            _focusUpdateRoutine = null;
        }

        /// <summary>
        ///     Initializes focus state once all objects are created.
        /// </summary>
        private void Start()
        {
            _isFocused = focus;
            TryFocusUpdate();
        }

        private void Update()
        {
            _scheduler.ScheduleUpdate(Focus);
        }

        /// <summary>
        ///     Cleans up engine components and disposes schedulers on destruction.
        /// </summary>
        protected override void OnDestroy()
        {
            _isShuttingDown = true;
            if (ChunkManager != null) ChunkManager.OnChunkChange -= RaiseChunkChanged;
            if (_chunkPool != null)
            {
                _chunkPool.OnChunkEvicted -= HandleChunkEvicted;
                _chunkPool.OnPartitionEvicted -= HandlePartitionEvicted;
                _chunkPool.Dispose();
            }

            base.OnDestroy();
            _scheduler.Dispose();
            ChunkManager?.Dispose();
        }

        private IEnumerator FocusUpdateRoutine()
        {
            while (true)
            {
                yield return new WaitForSeconds(.5f);
                TryFocusUpdate();
            }
        }

        #endregion
    }
}