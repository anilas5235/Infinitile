using System;
using Engine.Scripts.VoxelConfig.Data.Generation;
using UnityEngine;

namespace Engine.Scripts.WorldGen.Flora{
    /// <summary>
    ///     One flora rule of a biome (VEG-03): a species (single voxel or multi-block structure), its probability per
    ///     column and placement conditions. The Burst mirror and the placement logic follow in Phase 4 (FloraPlacement).
    /// </summary>
    [Serializable]
    public class FloraRule{
        [Tooltip("Single-voxel species (grass, flowers, mushrooms, rocks). Leave empty when SpeciesStructure is set.")]
        public VoxelConfig.Data.Voxel.Voxel SpeciesVoxel;

        [Tooltip("Multi-block species (tree, cactus). Leave empty when SpeciesVoxel is set.")]
        public VoxelStructure SpeciesStructure;

        [Tooltip("Probability per column that this species is rolled, in [0,1]. The rules of one biome are cumulative.")]
        [Range(0f, 1f)]
        public float Probability = 0.05f;

        [Tooltip("When true, the plant is suppressed if the column's surface source biome differs from its flora source biome.")]
        public bool RequiresSurfaceFromSameBiome;

        [Tooltip("Minimum column height above water level for this species (VEG-03, default 1).")]
        public int MinHeightAboveWater = 1;

        /// <summary>True when the rule names exactly one species.</summary>
        public bool IsValid => (SpeciesVoxel != null) ^ (SpeciesStructure != null);
    }
}
