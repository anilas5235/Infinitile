using System;
using System.Collections.Generic;
using Engine.Scripts.VoxelConfig.Registry;
using Engine.Scripts.WorldGen.Flora;
using UnityEngine;

namespace Engine.Scripts.VoxelConfig.Data.Generation{
    /// <summary>
    ///     Defines a biome: surface blocks, its classification target in parameter space, its Biome-first
    ///     height parameters and its flora rules. Which biomes are active is decided by a <c>BiomeSet</c>, not by
    ///     the data package.
    /// </summary>
    [CreateAssetMenu(fileName = "BiomeDefinition", menuName = "Infinitile/Generation/Biome")]
    public class Biome : ScriptableObject{
        [SerializeField] public Voxel.Voxel TopBlock;

        [SerializeField] public Voxel.Voxel UnderBlock;

        [SerializeField] public Voxel.Voxel StoneBlock;

        // --- Classification target ------------------------------------------------------------------------------------

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

        // --- Biome-first height parameters ----------------------------------------------------------------------------

        [Tooltip("Base height Offset_b in blocks.")]
        [SerializeField] public float HeightOffset = 136f;

        [Tooltip("Overall detail amplitude in blocks; multiplied with OctaveWeights to give a_{b,i}.")]
        [SerializeField] public float HeightAmplitude = 6f;

        [Tooltip("Relative weight per detail octave (512, 256, 128, 64, 32, 16 blocks). Missing entries count as 0.")]
        [SerializeField] public float[] OctaveWeights ={ 1f, 0.6f, 0.3f, 0.12f, 0.05f, 0.02f };

        [Tooltip("Jaggedness J_b: amplitude of the peak noise in blocks.")]
        [SerializeField] public float Jaggedness;

        // --- Flora ----------------------------------------------------------------------------------------------------

        [Tooltip("Flora rules of this biome; evaluated in order, probabilities are cumulative.")]
        [SerializeField] public List<FloraRule> FloraRules = new();

        [SerializeField] public Color RepresentativeColor = Color.magenta;

        public BiomeDef ToStruct(VoxelRegistry voxelRegistry){
            return ToStruct(v => voxelRegistry.GetIdOrThrow(v.GetFullName()));
        }

        /// <summary>Conversion with block ids from <paramref name="voxelId" /> (the registry, or a test's own table).</summary>
        public BiomeDef ToStruct(Func<Voxel.Voxel, ushort> voxelId){
            if (!TopBlock || !UnderBlock || !StoneBlock)
                throw new InvalidOperationException($"Biome {name}: TopBlock, UnderBlock and StoneBlock must be set");
            BiomeDef def = ToStruct();
            def.topBlock = voxelId(TopBlock);
            def.underBlock = voxelId(UnderBlock);
            def.stoneBlock = voxelId(StoneBlock);
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
                floraRuleCount = 0,
                // Plants need solid ground; the mesh layer of the top block decides, no registry needed.
                floraGround = TopBlock && TopBlock.meshLayer == Mesh.MeshLayer.Solid ? (byte)1 : (byte)0
            };
        }

        /// <summary>
        ///     Burst mirror of a biome. The per-octave coefficients live in <c>WorldGenConfig.OctaveCoefficients</c>,
        ///     the flora rules in a flattened rule array addressed by <see cref="floraRuleStart" />.
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

            /// <summary>Target normalised height h in [0,1].</summary>
            public float targetElevation;

            public float heightOffset;
            public float heightAmplitude;
            public float jaggedness;
            public int floraRuleStart;
            public int floraRuleCount;

            /// <summary>1 when the top block is solid ground that carries plants.</summary>
            public byte floraGround;

            public void Dispose(){ }
        }
    }
}
