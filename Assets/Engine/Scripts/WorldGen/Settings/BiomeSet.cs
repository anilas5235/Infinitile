using System.Collections.Generic;
using Engine.Scripts.VoxelConfig.Data.Generation;
using UnityEngine;

namespace Engine.Scripts.WorldGen.Settings{
    /// <summary>
    ///     The active biomes (GEN-20). The classifier only considers biomes in this list; biome indices in jobs are
    ///     indices into it, in asset order (GEN-21). Changing the set needs no code change. Biomes that stay in a
    ///     VoxelDataPackage but not in the set (e.g. Jungle, Swamp) are inactive.
    /// </summary>
    [CreateAssetMenu(fileName = "BiomeSet", menuName = "Voxel/BiomeSet", order = 1)]
    public class BiomeSet : ScriptableObject{
        /// <summary>Upper bound of active biomes: BlendWeights holds 16 entries (BLD-02).</summary>
        public const int MaxBiomes = 16;

        [Tooltip("Active biomes in index order. At most 16.")]
        public List<Biome> Biomes = new();

        /// <summary>Number of active biomes (null entries included, so indices stay stable).</summary>
        public int Count => Biomes?.Count ?? 0;

        /// <summary>Index of <paramref name="biome" /> in the active set, or -1.</summary>
        public int IndexOf(Biome biome){
            return Biomes == null ? -1 : Biomes.IndexOf(biome);
        }

        /// <summary>GEN-21: index → name. Out-of-range or null entries give a placeholder, never an exception.</summary>
        public string NameOf(int index){
            if (Biomes == null || index < 0 || index >= Biomes.Count) return $"<{index}>";
            Biome biome = Biomes[index];
            return biome ? biome.name : $"<null {index}>";
        }

        /// <summary>GEN-21: the full index → name table.</summary>
        public string[] Names(){
            string[] names = new string[Count];
            for (int i = 0; i < names.Length; i++) names[i] = NameOf(i);
            return names;
        }

        private void OnValidate(){
            if (Biomes == null) return;
            if (Biomes.Count > MaxBiomes)
                Debug.LogWarning($"{name}: {Biomes.Count} biomes exceed the maximum of {MaxBiomes}; only the first {MaxBiomes} are used.", this);
            for (int i = 0; i < Biomes.Count; i++)
                if (!Biomes[i]) Debug.LogWarning($"{name}: biome slot {i} is empty.", this);
        }
    }
}
