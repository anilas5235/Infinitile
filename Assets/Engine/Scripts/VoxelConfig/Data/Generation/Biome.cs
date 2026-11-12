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

        [Tooltip("Top block of columns whose surface lies under water, so lake and sea floors carry no grass. "
                 + "Empty uses UnderBlock.")]
        [SerializeField] public Voxel.Voxel UnderwaterTopBlock;

        [SerializeField] public Voxel.Voxel UnderBlock;

        [SerializeField] public Voxel.Voxel StoneBlock;

        [Tooltip("Top block of dry columns colder than the freeze temperature (snow caps, snowy shores). "
                 + "Empty: the biome never takes snow.")]
        [SerializeField] public Voxel.Voxel SnowTopBlock;

        [Tooltip("The water surface over columns of this biome freezes to ice where it is colder than the freeze "
                 + "temperature. Off: the biome's water never freezes.")]
        [SerializeField] public bool FreezesWater;

        [Tooltip("Replaces the stone block in horizontal bands (WorldGenSettings strata). Empty: uniform stone.")]
        [SerializeField] public Voxel.Voxel StrataBlock;

        [Tooltip("Further band blocks (at most three): the bands then cycle through StrataBlock and these, from the "
                 + "bottom up. Empty: every band is the StrataBlock.")]
        [SerializeField] public Voxel.Voxel[] StrataPalette;

        [Tooltip("Forest floor: replaces the top block on dry columns where the tree density noise is high, so dense "
                 + "forest stands on its own ground (litter). Empty: none. Beaches, snow and underwater ground keep their blocks.")]
        [SerializeField] public Voxel.Voxel ForestFloorBlock;

        [Tooltip("Factor of the tree density noise (0…2, mean 1) from which the forest floor block lies. Higher: smaller patches.")]
        [Range(0f, 2f)] [SerializeField] public float ForestFloorDensity = 1.5f;

        [Tooltip("Meadow: replaces the top block on dry columns below the tree line (WorldGenSettings tree line), so a "
                 + "biome of bare stone carries grass and trees on its lower slopes and rock above. The meadow is soil "
                 + "for flora rules that need soil. Empty: none. Beaches, forest floor, snow and underwater ground keep their blocks.")]
        [SerializeField] public Voxel.Voxel MeadowBlock;

        [Tooltip("Top block of beach columns just above the water surface, and the two blocks under it. Beaches are "
                 + "patchy (WorldGenSettings beaches). Empty: the biome has no beaches.")]
        [SerializeField] public Voxel.Voxel BeachBlock;

        [Tooltip("Largest beach height in blocks: a beach covers columns whose top block lies up to this many blocks "
                 + "above the water surface, minus one; the beach noise scales it down per column.")]
        [Min(0)] [SerializeField] public int BeachHeight = 3;

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
            def.underwaterTopBlock = voxelId(UnderwaterTopBlock ? UnderwaterTopBlock : UnderBlock);
            def.underBlock = voxelId(UnderBlock);
            def.stoneBlock = voxelId(StoneBlock);
            def.snowTopBlock = SnowTopBlock ? voxelId(SnowTopBlock) : (ushort)0;
            def.forestFloorBlock = ForestFloorBlock ? voxelId(ForestFloorBlock) : (ushort)0;
            def.meadowBlock = MeadowBlock ? voxelId(MeadowBlock) : (ushort)0;
            def.strataBlock = StrataBlock ? voxelId(StrataBlock) : (ushort)0;
            // The band palette: the strata block first, then the further blocks that are set.
            def.strataCount = StrataBlock ? (byte)1 : (byte)0;
            if (StrataBlock && StrataPalette != null)
                foreach (Voxel.Voxel band in StrataPalette){
                    if (!band || def.strataCount >= BiomeDef.MaxStrataBlocks) continue;
                    ushort id = voxelId(band);
                    switch (def.strataCount++){
                        case 1: def.strataBlock1 = id; break;
                        case 2: def.strataBlock2 = id; break;
                        default: def.strataBlock3 = id; break;
                    }
                }

            def.beachBlock = BeachBlock ? voxelId(BeachBlock) : (ushort)0;
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
                floraGround = TopBlock && TopBlock.meshLayer == Mesh.MeshLayer.Solid ? (byte)1 : (byte)0,
                // Bare stone: the top block is the stone block itself.
                soilGround = TopBlock && TopBlock != StoneBlock ? (byte)1 : (byte)0,
                meadowGround = MeadowBlock ? (byte)1 : (byte)0,
                freezesWater = FreezesWater ? (byte)1 : (byte)0,
                forestFloorDensity = ForestFloorDensity,
                beachHeight = BeachBlock ? (byte)Mathf.Clamp(BeachHeight, 0, 255) : (byte)0,
                beachFloraGround = BeachBlock && BeachBlock.meshLayer == Mesh.MeshLayer.Solid ? (byte)1 : (byte)0
            };
        }

        /// <summary>
        ///     Burst mirror of a biome. The per-octave coefficients live in <c>WorldGenConfig.OctaveCoefficients</c>,
        ///     the flora rules in a flattened rule array addressed by <see cref="floraRuleStart" />.
        /// </summary>
        public struct BiomeDef : IDisposable{
            public ushort topBlock;

            /// <summary>Top block of a column whose upper neighbour is water; 0 keeps <see cref="topBlock" />.</summary>
            public ushort underwaterTopBlock;

            public ushort underBlock;
            public ushort stoneBlock;

            /// <summary>Top block of a snow-covered dry column; 0 = the biome takes no snow.</summary>
            public ushort snowTopBlock;

            /// <summary>1 when the water surface over the biome's columns may freeze to ice.</summary>
            public byte freezesWater;

            /// <summary>Most blocks a band palette holds.</summary>
            public const int MaxStrataBlocks = 4;

            /// <summary>Block of the strata bands in the stone (the first of the palette); 0 = uniform stone.</summary>
            public ushort strataBlock;

            /// <summary>Further blocks of the band palette, used up to <see cref="strataCount" />.</summary>
            public ushort strataBlock1, strataBlock2, strataBlock3;

            /// <summary>Number of blocks the bands cycle through; 0 or 1 = every band is <see cref="strataBlock" />.</summary>
            public byte strataCount;

            /// <summary>Top block of a beach column and the two blocks under it; 0 without a registry.</summary>
            public ushort beachBlock;

            /// <summary>Top block of dry columns in dense forest (<c>SurfaceCover.IsForestFloor</c>); 0 = none.</summary>
            public ushort forestFloorBlock;

            /// <summary>Tree density factor from which the forest floor block lies.</summary>
            public float forestFloorDensity;

            /// <summary>Top block of dry columns below the tree line (<c>SurfaceCover.IsMeadow</c>); 0 = none, or no registry.</summary>
            public ushort meadowBlock;

            /// <summary>1 when the biome has a meadow block: its dry columns below the tree line are soil.</summary>
            public byte meadowGround;

            /// <summary>Largest beach height h_b,max in blocks; 0 = the biome has no beaches.</summary>
            public byte beachHeight;

            /// <summary>1 when the beach block is solid ground that carries plants.</summary>
            public byte beachFloraGround;

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

            /// <summary>1 when the top block is soil (not the biome's stone block): ground for flora rules that need soil.</summary>
            public byte soilGround;

            /// <summary>1 when a flora rule of the biome follows a density noise: its intervals are computed per column.</summary>
            public byte floraDensity;

            /// <summary>
            ///     Block of strata band number <paramref name="band" /> (<c>SurfaceCover.StrataBand</c>, any integer):
            ///     the palette repeats from the bottom up.
            /// </summary>
            public readonly ushort StrataBlockOf(int band){
                if (strataCount <= 1) return strataBlock;
                int i = band % strataCount;
                if (i < 0) i += strataCount;
                return i switch{
                    0 => strataBlock,
                    1 => strataBlock1,
                    2 => strataBlock2,
                    _ => strataBlock3
                };
            }

            public void Dispose(){ }
        }
    }
}
