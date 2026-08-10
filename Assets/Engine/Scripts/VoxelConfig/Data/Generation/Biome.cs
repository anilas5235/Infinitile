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
        [SerializeField] public Voxel.Voxel TopBlock;

        [SerializeField] public Voxel.Voxel UnderBlock;
        
        [SerializeField] public Voxel.Voxel StoneBlock;
        
        

        public BiomeDef ToStruct(VoxelRegistry voxelRegistry)
        {
            return new BiomeDef
            {
                topBlock = voxelRegistry.GetIdOrThrow(TopBlock.GetFullName()),
                underBlock = voxelRegistry.GetIdOrThrow(UnderBlock.GetFullName()),
                stoneBlock = voxelRegistry.GetIdOrThrow(StoneBlock.GetFullName()),
            };
        }

        public struct BiomeDef: IDisposable
        {
            public ushort topBlock;
            public ushort underBlock;
            public ushort stoneBlock;

            public void Dispose()
            {
            }
        }
    }
}