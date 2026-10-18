using System;
using System.Collections.Generic;
using Engine.Scripts.VoxelConfig.Registry;
using Engine.Scripts.WorldGen.Flora;
using UnityEngine;

namespace Engine.Scripts.VoxelConfig.Data.Generation{
    /// <summary>
    ///     Defines a biome: surface blocks, its classification target in parameter space (§6.2), its Biome-first
    ///     height parameters (§8.2) and its flora rules (VEG-03). Which biomes are active is decided by a
    ///     <c>BiomeSet</c>, not by the data package (GEN-20).
    /// </summary>
    [CreateAssetMenu(fileName = "BiomeDefinition", menuName = "Infinitile/Generation/Biome")]
    public class Biome : ScriptableObject{
        [SerializeField] public Voxel.Voxel TopBlock;

        [SerializeField] public Voxel.Voxel UnderBlock;

        [SerializeField] public Voxel.Voxel StoneBlock;

        // --- Classification target (§6.2) ---------------------------------------------------------------------------

        [Range(0f, 1f)] [SerializeField] public float TargetHumidity = 0.5f;

        [Range(0f, 1f)] [SerializeField] public float TargetTemperature = 0.5f;

        [Range(0f, 1f)] [SerializeField] public float TargetContinental = 0.5f;

        [Tooltip("Target erosion E in [0,1]; low = rugged, high = flat.")]
        [Range(0f, 1f)] [SerializeField] public float TargetErosion = 0.5f;

        [Tooltip("Target peaks-and-valleys PV in [-1,1]; -1 = valley floor, +1 = peak.")]
        [Range(-1f, 1f)] [SerializeField] public float TargetPV;

        /// <summary>Target normalised height h in [0,1] (Terrain-first only). The serialized name is kept for existing assets.</summary>
        [Tooltip("Target normalised height h in [0,1]; only used by the Terrain-first classifier.")]
        [Range(0f, 1f)] [SerializeField] public float TargetHeight = 0.5f;

        // --- Biome-first height parameters (§8.2) -------------------------------------------------------------------

        [Tooltip("Base height Offset_b in blocks (§5.7).")]
        [SerializeField] public float HeightOffset = 136f;

        [Tooltip("Overall detail amplitude in blocks; multiplied with OctaveWeights to give a_{b,i} (GEN-22).")]
        [SerializeField] public float HeightAmplitude = 6f;

        [Tooltip("Relative weight per detail octave (512, 256, 128, 64, 32, 16 blocks). Missing entries count as 0.")]
        [SerializeField] public float[] OctaveWeights ={ 1f, 0.6f, 0.3f, 0.12f, 0.05f, 0.02f };

        [Tooltip("Jaggedness J_b: amplitude of the peak noise in blocks (§5.7).")]
        [SerializeField] public float Jaggedness;

        // --- Flora (VEG-03) ------------------------------------------------------------------------------------------

        [Tooltip("Flora rules of this biome; evaluated in order, probabilities are cumulative.")]
        [SerializeField] public List<FloraRule> FloraRules = new();

        [SerializeField] public Color RepresentativeColor = Color.magenta;

        public BiomeDef ToStruct(VoxelRegistry voxelRegistry){
            BiomeDef def = ToStruct();
            def.topBlock = voxelRegistry.GetIdOrThrow(TopBlock.GetFullName());
            def.underBlock = voxelRegistry.GetIdOrThrow(UnderBlock.GetFullName());
            def.stoneBlock = voxelRegistry.GetIdOrThrow(StoneBlock.GetFullName());
            return def;
        }

        /// <summary>Conversion without block ids (tests, preview). Flora rule ranges are assigned by the config builder.</summary>
        public BiomeDef ToStruct(){
            return new BiomeDef{
                targetHumidity = TargetHumidity,
                targetTemperature = TargetTemperature,
                targetContinental = TargetContinental,
                targetErosion = TargetErosion,
                targetPV = TargetPV,
                targetElevation = TargetHeight,
                heightOffset = HeightOffset,
                heightAmplitude = HeightAmplitude,
                jaggedness = Jaggedness,
                floraRuleStart = 0,
                floraRuleCount = 0
            };
        }

        /// <summary>
        ///     Burst mirror of a biome. The per-octave coefficients live in <c>WorldGenConfig.OctaveCoefficients</c>
        ///     (GEN-22), the flora rules in a flattened rule array addressed by <see cref="floraRuleStart" />.
        /// </summary>
        public struct BiomeDef : IDisposable{
            public ushort topBlock;
            public ushort underBlock;
            public ushort stoneBlock;
            public float targetHumidity;
            public float targetTemperature;
            public float targetContinental;
            public float targetErosion;
            public float targetPV;

            /// <summary>Target normalised height h in [0,1] (§6.2).</summary>
            public float targetElevation;

            public float heightOffset;
            public float heightAmplitude;
            public float jaggedness;
            public int floraRuleStart;
            public int floraRuleCount;

            public void Dispose(){ }
        }
    }
}
