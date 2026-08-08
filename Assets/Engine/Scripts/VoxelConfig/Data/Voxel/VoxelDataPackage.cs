using System.Collections.Generic;
using Engine.Scripts.VoxelConfig.Data.Generation;
using UnityEngine;
using UnityEngine.Serialization;

namespace Engine.Scripts.VoxelConfig.Data.Voxel
{
    [CreateAssetMenu(fileName = "VoxelDataPackage", menuName = "Infinitile/Voxel/VoxelDataPackage")]
    public class VoxelDataPackage : ScriptableObject
    {
        /// <summary>
        ///     Name prefix used when registering contained definitions (e.g. "std" or "UserPackage").
        /// </summary>
        public string packagePrefix = "Custom";

        [FormerlySerializedAs("voxelTextures")] public List<Voxel> voxel;
        
        public List<Biome> biomes;
        
        public List<VoxelStructure> structures;
    }
}