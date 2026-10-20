using Engine.Scripts.Components;
using Engine.Scripts.Jobs.Chunk;
using Engine.Scripts.Jobs.ColliderBake;
using Engine.Scripts.Jobs.ColliderMeshing;
using Engine.Scripts.Jobs.Core;
using Engine.Scripts.Settings;
using Engine.Scripts.Utils.Provider;
using Engine.Scripts.VoxelConfig;
using Engine.Scripts.VoxelConfig.Registry;
using Engine.Scripts.World;
using Engine.Scripts.WorldGen.Settings;
using UnityEngine;

namespace Engine.Scripts.Utils{
    /// <summary>
    ///     Factory/provider for core voxel engine subsystems (manager, schedulers, pools, noise profile).
    ///     Supplies configured instances based on <see cref="VoxelEngineSettings" />.
    /// </summary>
    public class VoxelEngineProvider : Provider<VoxelEngineProvider>{
        /// <summary>
        ///     Global engine configuration (seed, noise, chunk, renderer, scheduler settings).
        /// </summary>
        public VoxelEngineSettings Settings{ get; set; }


        /// <summary>
        ///     Allocates a new <see cref="ChunkManager" /> responsible for chunk data in memory.
        /// </summary>
        internal ChunkManager ChunkManager(){
            return new ChunkManager(Settings);
        }

        /// <summary>
        ///     Allocates a new <see cref="ChunkPool" /> for recycling chunk render objects.
        /// </summary>
        /// <param name="transform">Parent transform for pooled chunk game objects.</param>
        internal ChunkPool ChunkPool(Transform transform){
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
        ){
            return new VoxelEngineScheduler(Settings, cMeshBuildScheduler, chunkScheduler, chunkManager,
                colliderBakeScheduler, chunkPool);
        }

        /// <summary>
        ///     Creates a configured <see cref="ChunkScheduler" /> for data generation jobs. Fills missing config fields.
        ///     <paramref name="worldGen" /> replaces <see cref="VoxelEngineSettings.WorldGen" /> for this scheduler
        ///     (INT-30); its values are copied into the config, so the caller may destroy it afterwards.
        /// </summary>
        internal ChunkScheduler ChunkDataScheduler(
            ChunkManager chunkManager,
            GeneratorConfig generatorConfig,
            VoxelWorld world,
            WorldGenSettings worldGen = null
        ){
            if (!worldGen) worldGen = Settings.WorldGen;
            // INT-01/INT-40: since Phase 2 the WorldGenSettings asset is the only generation input.
            if (!worldGen)
                throw new System.InvalidOperationException(
                    $"{Settings.name}: a WorldGenSettings asset must be assigned (VoxelEngineSettings.WorldGen).");

            GeneratorConfig cfg = generatorConfig;
            cfg.WaterLevel = worldGen.WaterLevel;
            cfg.GlobalSeed = Settings.Seed;
            // INT-03: the Burst mirror of the spec settings (owned by the config, D-05).
            cfg.WorldGen = worldGen.ToConfig(Settings.Seed, DataImporter.Instance.VoxelRegistry);
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
        ){
            return new CMeshBuildScheduler(Settings, chunkManager, chunkPool, voxelRegistry, world);
        }

        internal ColliderBakeScheduler ColliderBakeScheduler(ChunkManager chunkManager, ChunkPool chunkPool){
            return new ColliderBakeScheduler(chunkManager, chunkPool);
        }
    }
}
