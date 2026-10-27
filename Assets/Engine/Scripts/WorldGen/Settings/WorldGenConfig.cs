using System;
using System.Runtime.InteropServices;
using Engine.Scripts.VoxelConfig.Data.Generation;
using Engine.Scripts.WorldGen.Blending;
using Engine.Scripts.WorldGen.Caves;
using Engine.Scripts.WorldGen.Flora;
using Engine.Scripts.WorldGen.Height;
using Engine.Scripts.WorldGen.Noise;
using Unity.Collections;
using Unity.Mathematics;

namespace Engine.Scripts.WorldGen.Settings{
    /// <summary>
    ///     Burst-side mirror of <see cref="WorldGenSettings" />: enums, floats and flattened NativeArrays,
    ///     read-only inside jobs. Built by <see cref="WorldGenSettings.ToConfig" />.
    ///     <para>
    ///         Ownership: every container in this struct is created by <c>ToConfig</c> for this instance and
    ///         released by <see cref="Dispose" />, which <c>GeneratorConfig.Dispose</c> calls. Nothing here is a view
    ///         of registry memory. A default instance (<see cref="IsCreated" /> false) owns nothing and may be disposed,
    ///         but it cannot travel inside a job struct (the job safety system rejects unconstructed containers), so
    ///         every job-carried config comes from <c>ToConfig</c>.
    ///     </para>
    /// </summary>
    public struct WorldGenConfig : IDisposable{
        /// <summary>World seed as the hash functions take it.</summary>
        public uint Seed;

        public GenArchitecture Architecture;

        // --- Blending -----------------------------------------------------------------------------------------------
        public BlendMethod MaterialMethod;

        /// <summary>B_mat in blocks (DistanceBlend only; the material-stage DistortedBoundary is a one-hot warp).</summary>
        public float MaterialBlendWidth;

        /// <summary>σ_mat in normalised parameter units (method 4).</summary>
        public float MaterialSigma;

        public BlendMethod HeightMethod;
        public float HeightBlendWidth;
        public float HeightSigma;

        /// <summary>Shared boundary-warp amplitude A in blocks, used by both stages.</summary>
        public float WarpAmplitude;

        /// <summary>Boundary-warp wavelength λ_d in blocks.</summary>
        public float WarpWavelength;

        public int WarpOctaves;

        /// <summary>
        ///     Biome-first with both stages on the same method and strength (None; DistanceBlend with B_mat = B_hgt;
        ///     ParameterSpaceWeighting with σ_mat = σ_hgt): the height weights are the material weights, computed
        ///     once. Never set for DistortedBoundary, whose stages differ (warp only vs. warp + blend).
        /// </summary>
        [MarshalAs(UnmanagedType.U1)] public bool StagesShareWeights;

        /// <summary>Material-stage kernel (DistanceBlend, B_mat); not created for other methods.</summary>
        public KernelSet MaterialKernel;

        /// <summary>Taps of <see cref="MaterialKernel" />; empty when unused. Owned.</summary>
        [ReadOnly] public NativeArray<KernelTap> MaterialTaps;

        /// <summary>Height-stage kernel (Biome-first DistanceBlend or DistortedBoundary, B_hgt); not created otherwise.</summary>
        public KernelSet HeightKernel;

        /// <summary>Taps of <see cref="HeightKernel" />; empty when unused. Owned.</summary>
        [ReadOnly] public NativeArray<KernelTap> HeightTaps;

        // --- Classification weights λ ------------------------------------------------------------------------------
        public float LambdaTemperature;
        public float LambdaHumidity;
        public float LambdaContinental;
        public float LambdaErosion;
        public float LambdaPV;

        /// <summary>λ_h; Terrain-first only.</summary>
        public float LambdaHeight;

        // --- Height -------------------------------------------------------------------------------------------------
        public float HeightMin;
        public float HeightMax;
        public float PeakWavelength;

        /// <summary>
        ///     Water surface level in world Y: water fills y &lt; WaterLevel. Mirrors
        ///     <c>WorldGenSettings.WaterLevel</c> for the flora conditions.
        /// </summary>
        public int WaterLevel;

        /// <summary>Voxel id of the water that fills the columns up to <see cref="WaterLevel" />.</summary>
        public ushort WaterVoxel;

        /// <summary>Voxel id of the frozen water surface.</summary>
        public ushort IceVoxel;

        /// <summary>Run the snow and ice overrides after terrain fill.</summary>
        [MarshalAs(UnmanagedType.U1)] public bool EnableClimateOverrides; // blittable for the Burst direct calls

        // --- Snow, ice, strata and beaches (SurfaceCover) ----------------------------------------------------------
        public float FreezeTemperature;
        public float SnowLapse;
        public float FreezeNoiseAmplitude;
        public float FreezeNoiseWavelength;
        public int StrataPeriod;
        public int StrataThickness;
        public float StrataWarpAmplitude;
        public float StrataWarpWavelength;

        /// <summary>Wavelength of the beach noise in blocks.</summary>
        public float BeachWavelength;

        /// <summary>Beach noise value (0…1) below which a shore has no beach.</summary>
        public float BeachThreshold;

        /// <summary>Rise of the beach noise above the threshold to the full beach height; positive.</summary>
        public float BeachRamp;

        /// <summary>Largest distance in blocks from a beach column to an under-water water probe; 0 = no test.</summary>
        public int BeachWaterDistance;

        /// <summary>Some biome has beaches (beach height above 0).</summary>
        [MarshalAs(UnmanagedType.U1)] public bool HasBeaches;

        // --- Flora density noises (FloraPlacement) ------------------------------------------------------------------
        /// <summary>Wavelength in blocks of each flora density noise, indexed by <see cref="FloraDensity" />.</summary>
        public float4 FloraDensityWavelength;

        /// <summary>Contrast c of each density noise: its factor is clamp(1 + c · D, 0, 2) for the noise D in −1…1.</summary>
        public float4 FloraDensityContrast;

        /// <summary>Climate noise instances (T, F, C, E, W) and couplings. Owned.</summary>
        public ClimateParams Climate;

        /// <summary>Wavelength of detail octave i in blocks; Length = K. Owned.</summary>
        [ReadOnly] public NativeArray<float> OctaveWavelengths;

        /// <summary>Global octave weights g_i of Terrain-first; Length = K. Owned.</summary>
        [ReadOnly] public NativeArray<float> GlobalOctaveWeights;

        /// <summary>Active biomes in BiomeSet order. Owned.</summary>
        [ReadOnly] public NativeArray<Biome.BiomeDef> Biomes;

        /// <summary>
        ///     Effective Biome-first octave coefficients a_{b,i} = HeightAmplitude_b × OctaveWeights_{b,i},
        ///     flattened as [b × K + i]. Owned.
        /// </summary>
        [ReadOnly] public NativeArray<float> OctaveCoefficients;

        /// <summary>Flattened Terrain-first spline trees, pre-ordered per root. Owned.</summary>
        [ReadOnly] public NativeArray<SplineNode> SplineNodes;

        /// <summary>Control points of <see cref="SplineNodes" />. Owned.</summary>
        [ReadOnly] public NativeArray<SplinePoint> SplinePoints;

        /// <summary>Offset(C,E,PV) root; NodeCount 0 when no spline set is assigned.</summary>
        public SplineRoot OffsetSpline;

        /// <summary>Amp(C,E,PV) root.</summary>
        public SplineRoot AmpSpline;

        /// <summary>J(C,E,PV) root (own extension).</summary>
        public SplineRoot JaggednessSpline;

        // --- Flora -------------------------------------------------------------------------------------------------
        /// <summary>r_tree: spacing radius of structure and stack species (default 3); tree variants have their own.</summary>
        public int TreeSpacingRadius;

        /// <summary>Largest horizontal reach of a multi-block species from its anchor column (canopy, branches, lean).</summary>
        public int FloraReach;

        /// <summary>Largest spacing radius of any multi-block species (<see cref="TreeSpacingRadius" /> or a variant's).</summary>
        public int FloraSpacingMax;

        /// <summary>
        ///     Columns a chunk evaluates beyond its border for multi-block flora: <see cref="FloraReach" /> +
        ///     <see cref="FloraSpacingMax" />, 0 when no biome has a multi-block rule.
        /// </summary>
        public int FloraPadding;

        /// <summary>Flora rules of every active biome, addressed by <c>BiomeDef.floraRuleStart/floraRuleCount</c>. Owned.</summary>
        [ReadOnly] public NativeArray<FloraRuleDef> FloraRules;

        /// <summary>Structure species, indexed by <see cref="FloraRuleDef.Structure" />. Owned.</summary>
        [ReadOnly] public NativeArray<FloraStructureDef> FloraStructures;

        /// <summary>Voxels of every structure in <see cref="FloraStructures" />. Owned.</summary>
        [ReadOnly] public NativeArray<FloraVoxel> FloraVoxels;

        /// <summary>Tree species, indexed by <see cref="FloraRuleDef.Tree" />. Owned.</summary>
        [ReadOnly] public NativeArray<TreeSpeciesDef> TreeSpecies;

        /// <summary>Variants of every species in <see cref="TreeSpecies" />. Owned.</summary>
        [ReadOnly] public NativeArray<TreeVariantDef> TreeVariants;

        /// <summary>
        ///     [start, end) of every multi-block rule's species interval over the biomes without density noise: a
        ///     column whose species roll lies in none of them, and in no multi-block interval of a biome in
        ///     <see cref="FloraDenseMultiBlockBiomes" /> at that column, cannot be a multi-block candidate, whatever its
        ///     flora biome (pre-filter). Owned.
        /// </summary>
        [ReadOnly] public NativeArray<float2> FloraMultiBlockIntervals;

        /// <summary>
        ///     Biomes with density noise and a multi-block rule. Their intervals depend on the column, so the
        ///     pre-filter computes them per column like the roll does. Owned.
        /// </summary>
        [ReadOnly] public NativeArray<int> FloraDenseMultiBlockBiomes;

        /// <summary>
        ///     Replace class (<see cref="FloraVoxelClass" />) per voxel id for the flora voxels; id 0 is air, ids not
        ///     covered (terrain, water) count as solid. Owned.
        /// </summary>
        [ReadOnly] public NativeArray<byte> VoxelClasses;

        /// <summary>Caves, lava, ores and the bedrock floor. Owns its ore rules.</summary>
        public CaveConfig Caves;

        /// <summary>True when at least one active biome has a flora rule.</summary>
        public bool HasFlora => FloraRules.IsCreated && FloraRules.Length > 0;

        /// <summary>Replace class of the voxel <paramref name="id" /> for flora stamping.</summary>
        public byte VoxelClassOf(ushort id){
            if (id == 0) return FloraVoxelClass.Air;
            return id < VoxelClasses.Length ? VoxelClasses[id] : FloraVoxelClass.Solid;
        }

        /// <summary>Number of detail octaves K.</summary>
        public int OctaveCount => OctaveWavelengths.IsCreated ? OctaveWavelengths.Length : 0;

        /// <summary>Number of active biomes.</summary>
        public int BiomeCount => Biomes.IsCreated ? Biomes.Length : 0;

        /// <summary>False for a default instance; true for <c>ToConfig</c> results.</summary>
        public bool IsCreated => OctaveWavelengths.IsCreated;

        /// <summary>a_{b,i} for biome <paramref name="biome" /> and octave <paramref name="octave" />.</summary>
        public float OctaveCoefficient(int biome, int octave){
            return OctaveCoefficients[biome * OctaveCount + octave];
        }

        public void Dispose(){
            Climate.Dispose();
            if (OctaveWavelengths.IsCreated) OctaveWavelengths.Dispose();
            if (GlobalOctaveWeights.IsCreated) GlobalOctaveWeights.Dispose();
            if (Biomes.IsCreated) Biomes.Dispose();
            if (OctaveCoefficients.IsCreated) OctaveCoefficients.Dispose();
            if (SplineNodes.IsCreated) SplineNodes.Dispose();
            if (SplinePoints.IsCreated) SplinePoints.Dispose();
            if (MaterialTaps.IsCreated) MaterialTaps.Dispose();
            if (HeightTaps.IsCreated) HeightTaps.Dispose();
            if (FloraRules.IsCreated) FloraRules.Dispose();
            if (FloraStructures.IsCreated) FloraStructures.Dispose();
            if (FloraVoxels.IsCreated) FloraVoxels.Dispose();
            if (TreeSpecies.IsCreated) TreeSpecies.Dispose();
            if (TreeVariants.IsCreated) TreeVariants.Dispose();
            if (FloraMultiBlockIntervals.IsCreated) FloraMultiBlockIntervals.Dispose();
            if (FloraDenseMultiBlockBiomes.IsCreated) FloraDenseMultiBlockBiomes.Dispose();
            if (VoxelClasses.IsCreated) VoxelClasses.Dispose();
            Caves.Dispose();
        }
    }
}
