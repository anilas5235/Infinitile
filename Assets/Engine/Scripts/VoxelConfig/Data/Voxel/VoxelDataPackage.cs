using System.Collections.Generic;
using System.Linq;
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

        [FormerlySerializedAs("voxelTextures")]
        public List<Voxel> voxel;

        public List<Biome> biomes;

        public List<VoxelStructure> structures;

        private void OnValidate()
        {
            DeduplicateLists();
            MarkAllVoxels();
        }

        private void DeduplicateLists()
        {
            if (voxel != null)
            {
                HashSet<int> seen = new();
                List<Voxel> newVoxels = new();
                foreach (Voxel v in voxel)
                {
                    if (!v) continue;
                    int id = v.GetInstanceID();
                    if (seen.Add(id)) newVoxels.Add(v);
                }
                if (newVoxels.Count != voxel.Count) voxel = newVoxels;
            }

            if (biomes != null)
            {
                HashSet<int> seenB = new();
                List<Biome> newBiomes = new();
                foreach (Biome b in biomes)
                {
                    if (!b) continue;
                    int id = b.GetInstanceID();
                    if (seenB.Add(id)) newBiomes.Add(b);
                }
                if (newBiomes.Count != biomes.Count) biomes = newBiomes;
            }

            if (structures != null)
            {
                HashSet<int> seenS = new();
                List<VoxelStructure> newStructs = new();
                foreach (VoxelStructure s in structures)
                {
                    if (!s) continue;
                    int id = s.GetInstanceID();
                    if (seenS.Add(id)) newStructs.Add(s);
                }
                if (newStructs.Count != structures.Count) structures = newStructs;
            }
        }

        private void MarkAllVoxels()
        {
            if (voxel == null) return;
            foreach (Voxel v in voxel.Where(v => v)) v.package = this;
        }
    }
}