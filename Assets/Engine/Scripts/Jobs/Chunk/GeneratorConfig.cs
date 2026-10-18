using System;
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
        ///     D-05 ownership: a view of <c>VoxelRegistry.VoxelMap</c>. The registry owns and disposes it; the
        ///     config never does.
        /// </summary>
        [NativeDisableParallelForRestriction] public NativeHashMap<FixedString32Bytes, Voxel.VoxelDef> Voxels;

        /// <summary>
        ///     Spec world-generation parameters (INT-03). D-05 ownership: built by <c>WorldGenSettings.ToConfig</c>
        ///     for this config (see <c>VoxelEngineProvider.ChunkDataScheduler</c>) and released by
        ///     <see cref="Dispose" />, which the <c>ChunkScheduler</c> calls when it is disposed. Must be created
        ///     whenever the config is handed to a job, because the job safety system rejects unconstructed nested
        ///     containers.
        /// </summary>
        public WorldGenConfig WorldGen;

        /// <summary>Releases the world-gen config. <see cref="Voxels" /> belongs to its registry (D-05).</summary>
        public void Dispose(){
            WorldGen.Dispose();
        }
    }
}
