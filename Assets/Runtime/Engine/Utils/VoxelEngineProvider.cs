using Runtime.Engine.Components;
using Runtime.Engine.Jobs;
using Runtime.Engine.Jobs.Chunk;
using Runtime.Engine.Jobs.ColliderBake;
using Runtime.Engine.Jobs.Meshing;
using Runtime.Engine.Noise;
using Runtime.Engine.Settings;
using Runtime.Engine.Utils.Provider;
using Runtime.Engine.VoxelConfig.Data;
using UnityEngine;

namespace Runtime.Engine.Utils
{
    /// <summary>
    /// Factory/provider for core voxel engine subsystems (manager, schedulers, pools, noise profile).
    /// Supplies configured instances based on <see cref="VoxelEngineSettings"/>.
    /// </summary>
    public class VoxelEngineProvider : Provider<VoxelEngineProvider>
    {
        /// <summary>
        /// Global engine configuration (seed, noise, chunk, renderer, scheduler settings).
        /// </summary>
        public VoxelEngineSettings Settings { get; set; }

        /// <summary>
        /// Creates a new noise profile from current <see cref="Settings"/>.
        /// </summary>
        internal NoiseProfile NoiseProfile() => new(
            new NoiseProfile.Settings
            {
                Seed = Settings.Seed,
                Scale = Settings.Noise.Scale,
                Lacunarity = Settings.Noise.Lacunarity,
                Persistance = Settings.Noise.Persistance,
                Octaves = Settings.Noise.Octaves,
            }
        );

        /// <summary>
        /// Allocates a new <see cref="ChunkManager"/> responsible for chunk data in memory.
        /// </summary>
        internal ChunkManager ChunkManager() => new(Settings);

        /// <summary>
        /// Allocates a new <see cref="ChunkPool"/> for recycling chunk render objects.
        /// </summary>
        /// <param name="transform">Parent transform for pooled chunk game objects.</param>
        internal ChunkPool ChunkPool(Transform transform) => new(transform, Settings);

        /// <summary>
        /// Creates the top-level <see cref="VoxelEngineScheduler"/> coordinating all sub-schedulers.
        /// </summary>
        internal VoxelEngineScheduler VoxelEngineScheduler(
            MeshBuildScheduler meshBuildScheduler,
            ChunkScheduler chunkScheduler,
            ColliderBakeScheduler colliderBakeScheduler,
            ChunkManager chunkManager,
            ChunkPool chunkPool
        ) => new(Settings, meshBuildScheduler, chunkScheduler, chunkManager, colliderBakeScheduler, chunkPool);

        /// <summary>
        /// Creates a configured <see cref="ChunkScheduler"/> for data generation jobs. Fills missing config fields.
        /// </summary>
        internal ChunkScheduler ChunkDataScheduler(
            ChunkManager chunkManager,
            NoiseProfile noiseProfile,
            GeneratorConfig generatorConfig
        )
        {
            GeneratorConfig cfg = generatorConfig;
            cfg.WaterLevel = Settings.Noise.WaterLevel;
            cfg.GlobalSeed = Settings.Seed;
            return new ChunkScheduler(Settings, chunkManager, noiseProfile, cfg);
        }

        /// <summary>
        /// Creates the <see cref="MeshBuildScheduler"/> for building chunk meshes.
        /// </summary>
        internal MeshBuildScheduler MeshBuildScheduler(
            ChunkManager chunkManager,
            ChunkPool chunkPool,
            VoxelRegistry voxelRegistry
        ) => new(Settings, chunkManager, chunkPool, voxelRegistry);

        internal ColliderBakeScheduler ColliderBakeScheduler(ChunkManager chunkManager, ChunkPool chunkPool) =>
            new(chunkManager, chunkPool);
    }
}