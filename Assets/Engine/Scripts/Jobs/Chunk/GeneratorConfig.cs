using System;
using Engine.Scripts.VoxelConfig.Data.Generation;
using Engine.Scripts.VoxelConfig.Data.Voxel;
using Engine.Scripts.WorldGen.Settings;
using Unity.Collections;

namespace Engine.Scripts.Jobs.Chunk{
    /// <summary>
    ///     Holds voxel IDs and global parameters used by chunk generation jobs to create terrain,
    ///     caves, ores, vegetation and structures.
    /// </summary>
    public struct GeneratorConfig : IDisposable{
        /// <summary>
        ///     Vertical world Y level that represents the global water surface used for oceans, lakes and rivers.
        /// </summary>
        public int WaterLevel;

        /// <summary>
        ///     Global deterministic seed passed to generation jobs to keep world generation reproducible.
        /// </summary>
        public int GlobalSeed;

        /// <summary>
        ///     D-05 ownership: a view of <c>BiomeRegistry.BiomeArray</c>. The registry owns and disposes it; the
        ///     config never does.
        /// </summary>
        [NativeDisableParallelForRestriction] public NativeArray<Biome.BiomeDef> BiomeDefs;

        /// <summary>
        ///     D-05 ownership: a view of <c>VoxelRegistry.VoxelMap</c>. The registry owns and disposes it; the
        ///     config never does.
        /// </summary>
        [NativeDisableParallelForRestriction] public NativeHashMap<FixedString32Bytes, Voxel.VoxelDef> Voxels;

        /// <summary>
        ///     D-05 ownership: created for this config (see <c>VoxelEngineProvider.ChunkDataScheduler</c>) and
        ///     released by <see cref="Dispose" />, which the <c>ChunkScheduler</c> calls when it is disposed.
        /// </summary>
        public NoiseCalculator.NoiseParameters NoiseParams;

        /// <summary>
        ///     Spec world-generation parameters (INT-03). D-05 ownership: built by <c>WorldGenSettings.ToConfig</c> (or
        ///     <c>WorldGenConfig.CreateEmpty</c> for the legacy path without settings) for this config and released by
        ///     <see cref="Dispose" />. Must be created whenever the config is handed to a job, because the job safety
        ///     system rejects unconstructed nested containers.
        /// </summary>
        public WorldGenConfig WorldGen;

        /// <summary>
        ///     Releases the noise parameters and the world-gen config. <see cref="BiomeDefs" /> and <see cref="Voxels" /> belong
        ///     to their registries (D-05).
        /// </summary>
        public void Dispose(){
            NoiseParams.Dispose();
            WorldGen.Dispose();
        }
    }
}
