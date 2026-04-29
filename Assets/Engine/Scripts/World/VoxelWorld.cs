using System;
using System.Collections.Generic;
using Engine.Scripts.Data;
using Engine.Scripts.Components;
using Engine.Scripts.Jobs;
using Engine.Scripts.Jobs.Chunk;
using Engine.Scripts.Jobs.ColliderBake;
using Engine.Scripts.Jobs.Meshing;
using Engine.Scripts.Noise;
using Engine.Scripts.Settings;
using Engine.Scripts.Utils;
using Engine.Scripts.Utils.Extensions;
using Engine.Scripts.VoxelConfig.Data;
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

        private VoxelEngineScheduler _scheduler;
        private ChunkPool _chunkPool;
        private ChunkScheduler _chunkScheduler;
        private ColliderBakeScheduler _colliderBakeScheduler;
        private NoiseProfile _noiseProfile;


        private bool _isFocused;
        private bool _isShuttingDown;
        private MeshBuildScheduler _meshBuildScheduler;

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

        internal void RequestPartitionBuild(HashSet<int3> partitions)
        {
            PartitionBuildRequested?.Invoke(partitions);
        }

        /// <summary>
        ///     Gets the voxel ID at the given world voxel position.
        /// </summary>
        /// <param name="position">World voxel position.</param>
        /// <returns>Voxel ID at the given position.</returns>
        public ushort GetVoxel(Vector3Int position) => ChunkManager.GetVoxel(position);

        /// <summary>
        ///     Sets the voxel ID at a given world voxel position and optionally triggers a remesh.
        /// </summary>
        /// <param name="voxelId">Voxel ID to set.</param>
        /// <param name="position">World voxel position.</param>
        /// <param name="remesh">If true, the affected chunks will be re-meshed.</param>
        /// <returns><c>true</c> if the voxel could be set; otherwise, <c>false</c>.</returns>
        public bool SetVoxel(ushort voxelId, Vector3Int position, bool remesh = true) =>
            ChunkManager.SetVoxel(voxelId, position, remesh);

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
            _noiseProfile = VoxelEngineProvider.Current.NoiseProfile();
            ChunkManager = VoxelEngineProvider.Current.ChunkManager();
            ChunkManager.OnChunkChange += RaiseChunkChanged;

            _chunkPool = VoxelEngineProvider.Current.ChunkPool(transform);
            _chunkPool.OnChunkEvicted += HandleChunkEvicted;
            _chunkPool.OnPartitionEvicted += HandlePartitionEvicted;

            _meshBuildScheduler = VoxelEngineProvider.Current.MeshBuildScheduler(
                ChunkManager,
                _chunkPool,
                VoxelDataImporter.Instance.VoxelRegistry,
                this
            );

            _colliderBakeScheduler = VoxelEngineProvider.Current.ColliderBakeScheduler(
                ChunkManager,
                _chunkPool
            );

            _chunkScheduler = VoxelEngineProvider.Current.ChunkDataScheduler(
                ChunkManager,
                _noiseProfile,
                VoxelDataImporter.Instance.CreateConfig(),
                this
            );

            _scheduler = VoxelEngineProvider.Current.VoxelEngineScheduler(
                _meshBuildScheduler,
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

        #region API

        /// <summary>
        ///     The partition coordinates of the current focus position.
        /// </summary>
        public int3 FocusPartitionCoords { get; private set; }

        public int3 FocusPosition { get; private set; }

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

            FocusPartitionCoords = new int3(1, 1, 1) * int.MinValue;
        }

        /// <summary>
        ///     Initializes focus state once all objects are created.
        /// </summary>
        private void Start()
        {
            _isFocused = focus;
        }

        /// <summary>
        ///     Updates the focus chunk coordinate and lets the scheduler perform focus and
        ///     regular scheduling logic every frame.
        /// </summary>
        private void Update()
        {
            int3 newFocusCoords = _isFocused ? VoxelUtils.GetPartitionCoords(focus.position) : int3.zero;

            if (!(newFocusCoords == FocusPartitionCoords).AndReduce())
            {
                FocusPartitionCoords = newFocusCoords;
                _scheduler.FocusUpdate(FocusPartitionCoords);
            }

            _scheduler.ScheduleUpdate(FocusPartitionCoords);
        }

        private void FixedUpdate()
        {
            if (!_isFocused) return;

            FocusPosition = focus.position.Int3();
            //_occlusionCuller.OccUpdate(FocusPartitionCoords, FocusPosition,focus.forward.Float3());
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
            ChunkManager.Dispose();
        }

        #endregion
    }
}