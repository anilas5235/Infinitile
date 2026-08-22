using System;
using Engine.Scripts.Noise;
using Engine.Scripts.VoxelConfig.Data.Generation;
using Engine.Scripts.VoxelConfig.Data.Voxel;
using Unity.Collections;

namespace Engine.Scripts.Jobs.Chunk
{
    /// <summary>
    ///     Holds voxel IDs and global parameters used by chunk generation jobs to create terrain,
    ///     caves, ores, vegetation and structures.
    /// </summary>
    public struct GeneratorConfig : IDisposable
    {
        /// <summary>
        ///     Vertical world Y level that represents the global water surface used for oceans, lakes and rivers.
        /// </summary>
        public int WaterLevel;

        /// <summary>
        ///     Global deterministic seed passed to generation jobs to keep world generation reproducible.
        /// </summary>
        public int GlobalSeed;
        
        [NativeDisableParallelForRestriction]
        public NativeArray<Biome.BiomeDef> BiomeDefs;
        
        [NativeDisableParallelForRestriction]
        public NativeHashMap<FixedString32Bytes, Voxel.VoxelDef> Voxels;
        
        public NoiseCalculator.NoiseParameters NoiseParams;

        public void Dispose()
        {
            if (BiomeDefs.IsCreated) BiomeDefs.Dispose();
            if (Voxels.IsCreated) Voxels.Dispose();
            NoiseParams.Dispose();
        }
    }
}