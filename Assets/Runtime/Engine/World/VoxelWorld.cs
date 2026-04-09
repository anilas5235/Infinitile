using Runtime.Engine.Components;
using Runtime.Engine.Jobs;
using Runtime.Engine.Jobs.Chunk;
using Runtime.Engine.Jobs.ColliderBake;
using Runtime.Engine.Jobs.Meshing;
using Runtime.Engine.Noise;
using Runtime.Engine.Settings;
using Runtime.Engine.Utils;
using Runtime.Engine.Utils.Extensions;
using Runtime.Engine.VoxelConfig.Data;
using Unity.Mathematics;
using UnityEngine;

namespace Runtime.Engine.World
{
    /// <summary>
    /// Top-level world controller that wires together chunk generation, meshing and colliders
    /// and exposes a simple API for querying and modifying voxels around a focus transform.
    /// </summary>
    public class VoxelWorld : Singleton<VoxelWorld>
    {
        [SerializeField] private Transform focus;

        [SerializeField] private VoxelEngineSettings settings;

        #region API

        /// <summary>
        /// The partition coordinates of the current focus position.
        /// </summary>
        private int3 FocusPartitionCoords { get; set; }

        private int3 FocusPosition { get; set; }

        /// <summary>
        /// Gets the central scheduler that coordinates chunk data, mesh and collider jobs.
        /// </summary>
        private VoxelEngineScheduler Scheduler { get; set; }

        /// <summary>
        /// Gets the noise profile used for procedural terrain generation.
        /// </summary>
        private NoiseProfile NoiseProfile { get; set; }

        /// <summary>
        /// Gets the chunk manager that stores and accesses chunk data.
        /// </summary>
        public ChunkManager ChunkManager { get; private set; }

        #endregion

        private ChunkPool _chunkPool;
        private ChunkScheduler _chunkScheduler;
        private ColliderBakeScheduler _colliderBakeScheduler;
        private MeshBuildScheduler _meshBuildScheduler;

        private bool _isFocused;

        private static VoxelEngineProvider Provider() => new();

        #region Unity

        /// <summary>
        /// Initializes the singleton, configures engine settings via the provider
        /// and constructs all core engine components.
        /// </summary>
        protected override void Awake()
        {
            base.Awake();
            VoxelEngineProvider.Initialize(Provider(), provider =>
            {
                ConfigureSettings();

                provider.Settings = settings;
            });

            ConstructEngineComponents();

            FocusPartitionCoords = new int3(1, 1, 1) * int.MinValue;
        }

        /// <summary>
        /// Initializes focus state once all objects are created.
        /// </summary>
        private void Start()
        {
            _isFocused = focus;
        }

        /// <summary>
        /// Updates the focus chunk coordinate and lets the scheduler perform focus and
        /// regular scheduling logic every frame.
        /// </summary>
        private void Update()
        {
            int3 newFocusCoords = _isFocused ? VoxelUtils.GetPartitionCoords(focus.position) : int3.zero;

            if (!(newFocusCoords == FocusPartitionCoords).AndReduce())
            {
                FocusPartitionCoords = newFocusCoords;
                Scheduler.FocusUpdate(FocusPartitionCoords);
            }

            Scheduler.ScheduleUpdate(FocusPartitionCoords);
        }

        private void FixedUpdate()
        {
            if (!_isFocused) return;

            FocusPosition = focus.position.Int3();
            //_occlusionCuller.OccUpdate(FocusPartitionCoords, FocusPosition,focus.forward.Float3());
        }

        /// <summary>
        /// Cleans up engine components and disposes schedulers on destruction.
        /// </summary>
        protected override void OnDestroy()
        {
            base.OnDestroy();
            Scheduler.Dispose();
            ChunkManager.Dispose();
        }

        #endregion

        /// <summary>
        /// Gets the voxel ID at the given world voxel position.
        /// </summary>
        /// <param name="position">World voxel position.</param>
        /// <returns>Voxel ID at the given position.</returns>
        public ushort GetVoxel(Vector3Int position) => ChunkManager.GetVoxel(position);

        /// <summary>
        /// Sets the voxel ID at a given world voxel position and optionally triggers a remesh.
        /// </summary>
        /// <param name="voxelId">Voxel ID to set.</param>
        /// <param name="position">World voxel position.</param>
        /// <param name="remesh">If true, the affected chunks will be re-meshed.</param>
        /// <returns><c>true</c> if the voxel could be set; otherwise, <c>false</c>.</returns>
        public bool SetVoxel(ushort voxelId, Vector3Int position, bool remesh = true) =>
            ChunkManager.SetVoxel(voxelId, position, remesh);

        /// <summary>
        /// Adjusts derived chunk settings such as load and update distance based on the
        /// configured draw distance.
        /// </summary>
        private void ConfigureSettings()
        {
            settings.Chunk.LoadDistance = settings.Chunk.DrawDistance + 2;
            settings.Chunk.UpdateDistance = math.max(settings.Chunk.DrawDistance - 2, 2);
        }

        /// <summary>
        /// Constructs all engine components (noise profile, chunk manager, pools and schedulers)
        /// via the <see cref="VoxelEngineProvider"/>.
        /// </summary>
        private void ConstructEngineComponents()
        {
            NoiseProfile = VoxelEngineProvider.Current.NoiseProfile();
            ChunkManager = VoxelEngineProvider.Current.ChunkManager();

            _chunkPool = VoxelEngineProvider.Current.ChunkPool(transform);

            _meshBuildScheduler = VoxelEngineProvider.Current.MeshBuildScheduler(
                ChunkManager,
                _chunkPool,
                VoxelDataImporter.Instance.VoxelRegistry
            );

            _colliderBakeScheduler = VoxelEngineProvider.Current.ColliderBakeScheduler(
                ChunkManager,
                _chunkPool
            );

            _chunkScheduler = VoxelEngineProvider.Current.ChunkDataScheduler(
                ChunkManager,
                NoiseProfile,
                VoxelDataImporter.Instance.CreateConfig()
            );

            Scheduler = VoxelEngineProvider.Current.VoxelEngineScheduler(
                _meshBuildScheduler,
                _chunkScheduler,
                _colliderBakeScheduler,
                ChunkManager,
                _chunkPool
            );
        }
    }
}