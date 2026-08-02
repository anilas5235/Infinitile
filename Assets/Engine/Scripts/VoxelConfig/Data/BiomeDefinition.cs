using UnityEngine;

namespace Engine.Scripts.VoxelConfig.Data
{
    /// <summary>
    ///     Defines a biome with its associated structures and configurations.
    /// </summary>
    [CreateAssetMenu(fileName = "BiomeDefinition", menuName = "Infinitile/Biome/BiomeDefinition")]
    public class BiomeDefinition : ScriptableObject
    {
        [SerializeField] public VoxelDefinition SurfaceMaterial;

        [SerializeField] public VoxelDefinition SubsurfaceMaterial;
    }
}