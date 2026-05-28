using System.Collections.Generic;
using UnityEngine;

namespace Engine.Scripts.VoxelConfig.Data
{
    /// <summary>
    ///     ScriptableObject asset that groups multiple <see cref="VoxelDefinition" /> instances
    ///     under a common package prefix for registration.
    /// </summary>
    [CreateAssetMenu(fileName = "VoxelDataPackage", menuName = "Voxel/Voxel Data Package")]
    public class VoxelDataPackage : ScriptableObject
    {
        /// <summary>
        ///     Name prefix used when registering contained voxel definitions (e.g. "std" or "UserPackage").
        /// </summary>
        public string packagePrefix = "UserPackage";

        /// <summary>
        ///     Collection of voxel definitions included in this package.
        /// </summary>
        public List<VoxelDefinition> voxelTextures;
    }
}