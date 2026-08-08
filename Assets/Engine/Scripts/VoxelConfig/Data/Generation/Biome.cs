using System;
using Engine.Scripts.VoxelConfig.Registry;
using UnityEngine;

namespace Engine.Scripts.VoxelConfig.Data.Generation
{
    /// <summary>
    ///     Defines a biome with its associated structures and configurations.
    /// </summary>
    [CreateAssetMenu(fileName = "BiomeDefinition", menuName = "Infinitile/Generation/Biome")]
    public class Biome : ScriptableObject
    {
        [SerializeField] public Voxel.Voxel SurfaceMaterial;

        [SerializeField] public Voxel.Voxel SubsurfaceMaterial;

        public BiomeDef ToStruct(VoxelRegistry voxelRegistry)
        {
            return new BiomeDef
            {
                surfaceMaterial = voxelRegistry.GetIdOrThrow(SurfaceMaterial.name),
                subsurfaceMaterial = voxelRegistry.GetIdOrThrow(SubsurfaceMaterial.name)
            };
        }

        public struct BiomeDef: IDisposable
        {
            public ushort surfaceMaterial;
            public ushort subsurfaceMaterial;

            public void Dispose()
            {
            }
        }
    }
}