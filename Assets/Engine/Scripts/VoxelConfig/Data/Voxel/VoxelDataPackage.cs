using System.Collections.Generic;
using UnityEngine;

namespace Engine.Scripts.VoxelConfig.Data.Voxel
{
    /// <summary>
    ///     ScriptableObject asset that groups multiple <see cref="Voxel" /> instances
    ///     under a common package prefix for registration.
    /// </summary>
    [CreateAssetMenu(fileName = "VoxelDataPackage", menuName = "Infinitile/Voxel/VoxelDataPackage")]
    public class VoxelDataPackage : ScriptableObject
    {
        /// <summary>
        ///     Name prefix used when registering contained voxel definitions (e.g. "std" or "UserPackage").
        /// </summary>
        public string packagePrefix = "Custom";

        /// <summary>
        ///     Collection of voxel definitions included in this package.
        /// </summary>
        public List<Voxel> voxelTextures;
    }
}