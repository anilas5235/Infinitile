using Engine.Scripts.VoxelConfig.Data.Voxel;
using UnityEngine;

namespace Engine.Scripts.VoxelConfig.Data.Generation
{
    /// <summary>
    ///     Defines a biome with its associated structures and configurations.
    /// </summary>
    [CreateAssetMenu(fileName = "BiomeDefinition", menuName = "Infinitile/Generation/Biome")]
    public class BiomeDefinition : ScriptableObject
    {
        [SerializeField] public VoxelDefinition SurfaceMaterial;

        [SerializeField] public VoxelDefinition SubsurfaceMaterial;
    }
}