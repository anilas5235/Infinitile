using Engine.Scripts.Components;
using Engine.Scripts.Jobs.Chunk;
using Engine.Scripts.Jobs.ColliderBake;
using Engine.Scripts.Jobs.ColliderMeshing;
using Engine.Scripts.Jobs.Core;
using Engine.Scripts.Noise;
using Engine.Scripts.Settings;
using Engine.Scripts.Utils.Provider;
using Engine.Scripts.VoxelConfig.Registry;
using Engine.Scripts.World;
using UnityEngine;

namespace Engine.Scripts.Utils
{
    /// <summary>
    ///     Factory/provider for core voxel engine subsystems (manager, schedulers, pools, noise profile).
    ///     Supplies configured instances based on <see cref="VoxelEngineSettings" />.
    /// </summary>
    public class VoxelEngineProvider : Provider<VoxelEngineProvider>
    {
        /// <summary>
        ///     Global engine configuration (seed, noise, chunk, renderer, scheduler settings).
        /// </summary>
        public VoxelEngineSettings Settings { get; set; }

        
        private NoiseCalculator.NoiseParameters NoiseParameters()
        {
            return new NoiseCalculator.NoiseParameters
            {
                Seed = Settings.Seed,
                HumidityScale = Settings.Noise.HumidityScale,
                TemperatureScale = Settings.Noise.TemperatureScale,
                ElevationProfile = new NoiseProfile(Settings.Noise.elevationProfile.ToStruct(Settings.Seed)),
                ContinentalLayer = new WarpedNoiseLayer(Settings.Noise.continentalLayer.ToStruct(Settings.Seed))
            };
        }

        /// <summary>
        ///     Allocates a new <see cref="ChunkManager" /> responsible for chunk data in memory.
        /// </summary>
        internal ChunkManager ChunkManager()
        {
            return new ChunkManager(Settings);
        }

        /// <summary>
        ///     Allocates a new <see cref="ChunkPool" /> for recycling chunk render objects.
        /// </summary>
        /// <param name="transform">Parent transform for pooled chunk game objects.</param>
        internal ChunkPool ChunkPool(Transform transform)
        {
            return new ChunkPool(transform, Settings);
        }

        /// <summary>
        ///     Creates the top-level <see cref="VoxelEngineScheduler" /> coordinating all sub-schedulers.
        /// </summary>
        internal VoxelEngineScheduler VoxelEngineScheduler(
            CMeshBuildScheduler cMeshBuildScheduler,
            ChunkScheduler chunkScheduler,
            ColliderBakeScheduler colliderBakeScheduler,
            ChunkManager chunkManager,
            ChunkPool chunkPool
        )
        {
            return new VoxelEngineScheduler(Settings, cMeshBuildScheduler, chunkScheduler, chunkManager,
                colliderBakeScheduler, chunkPool);
        }

        /// <summary>
        ///     Creates a configured <see cref="ChunkScheduler" /> for data generation jobs. Fills missing config fields.
        /// </summary>
        internal ChunkScheduler ChunkDataScheduler(
            ChunkManager chunkManager,
            GeneratorConfig generatorConfig,
            VoxelWorld world
        )
        {
            GeneratorConfig cfg = generatorConfig;
            cfg.WaterLevel = Settings.Noise.WaterLevel;
            cfg.GlobalSeed = Settings.Seed;
            cfg.NoiseParams = NoiseParameters();
            return new ChunkScheduler(Settings, chunkManager, cfg, world);
        }

        /// <summary>
        ///     Creates the <see cref="MeshBuildScheduler" /> for building chunk meshes.
        /// </summary>
        internal CMeshBuildScheduler MeshBuildScheduler(
            ChunkManager chunkManager,
            ChunkPool chunkPool,
            VoxelRegistry voxelRegistry,
            VoxelWorld world
        )
        {
            return new CMeshBuildScheduler(Settings, chunkManager, chunkPool, voxelRegistry, world);
        }

        internal ColliderBakeScheduler ColliderBakeScheduler(ChunkManager chunkManager, ChunkPool chunkPool)
        {
            return new ColliderBakeScheduler(chunkManager, chunkPool);
        }
    }
}