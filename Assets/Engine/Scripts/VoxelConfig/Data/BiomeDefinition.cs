using UnityEngine;

namespace Engine.Scripts.VoxelConfig.Data
{
    /// <summary>
    ///     Defines a biome with its associated structures and configurations.
    ///     Replaces the Biome enum as the source of truth for available biomes.
    /// </summary>
    [CreateAssetMenu(fileName = "BiomeDefinition", menuName = "Infinitile/Biome/BiomeDefinition")]
    public class BiomeDefinition : ScriptableObject
    {
        /// <summary>
        ///     Unique identifier for this biome (e.g., "Plains", "Desert", "Ocean").
        /// </summary>
        [SerializeField]
        public string BiomeId;

        /// <summary>
        ///     Human-readable display name for the biome.
        /// </summary>
        [SerializeField]
        public string DisplayName;

        /// <summary>
        ///     Optional: Voxel ID for surface material specific to this biome.
        /// </summary>
        [SerializeField]
        public VoxelDefinition SurfaceMaterial;

        /// <summary>
        ///     Optional: Voxel ID for subsurface material specific to this biome.
        /// </summary>
        [SerializeField]
        public VoxelDefinition SubsurfaceMaterial;

        private void OnValidate()
        {
            if (string.IsNullOrEmpty(BiomeId))
                BiomeId = name.Replace(" ", "");

            if (string.IsNullOrEmpty(DisplayName))
                DisplayName = name;
        }
    }
}
