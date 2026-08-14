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

        [Range(0f, 1f)] [SerializeField] public float TargetHumidity = 0.5f;

        [Range(0f, 1f)] [SerializeField] public float TargetTemperature = 0.5f;

        [Range(0f, 1f)] [SerializeField] public float TargetContinental = 0.5f;

        [Range(0f, 1f)] [SerializeField] public float TargetHeight = 0.5f;

        [SerializeField] public Color RepresentativeColor = Color.magenta;

        public BiomeDef ToStruct(VoxelRegistry voxelRegistry)
        {
            return new BiomeDef
            {
                topBlock = voxelRegistry.GetIdOrThrow(TopBlock.GetFullName()),
                underBlock = voxelRegistry.GetIdOrThrow(UnderBlock.GetFullName()),
                stoneBlock = voxelRegistry.GetIdOrThrow(StoneBlock.GetFullName()),
                targetHumidity = TargetHumidity,
                targetTemperature = TargetTemperature,
                targetContinental = TargetContinental,
                targetHeight = TargetHeight
            };
        }

        public struct BiomeDef : IDisposable
        {
            public ushort topBlock;
            public ushort underBlock;
            public ushort stoneBlock;
            public float targetHumidity;
            public float targetTemperature;
            public float targetContinental;
            public float targetHeight;

            public void Dispose()
            {
            }
        }
    }
}