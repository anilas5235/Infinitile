using System;
using System.Runtime.InteropServices;
using Engine.Scripts.Noise;
using Engine.Scripts.VoxelConfig.Data.Generation;
using Engine.Scripts.WorldGen.Blending;
using Engine.Scripts.WorldGen.Core;
using Engine.Scripts.WorldGen.Height;
using Engine.Scripts.WorldGen.Noise;
using Unity.Collections;

namespace Engine.Scripts.WorldGen.Settings{
    /// <summary>
    ///     Burst-side mirror of <see cref="WorldGenSettings" /> (INT-03): enums, floats and flattened NativeArrays,
    ///     read-only inside jobs. Built by <see cref="WorldGenSettings.ToConfig" />.
    ///     <para>
    ///         Ownership (D-05): every container in this struct is created by <c>ToConfig</c> for this instance and
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

        // --- Blending (§9) -------------------------------------------------------------------------------------------
        public BlendMethod MaterialMethod;

        /// <summary>B_mat in blocks (DistanceBlend only; the material-stage DistortedBoundary is a one-hot warp, §9.7).</summary>
        public float MaterialBlendWidth;

        /// <summary>σ_mat in normalised parameter units (method 4).</summary>
        public float MaterialSigma;

        public BlendMethod HeightMethod;
        public float HeightBlendWidth;
        public float HeightSigma;

        /// <summary>Shared boundary-warp amplitude A in blocks (DEC-05).</summary>
        public float WarpAmplitude;

        /// <summary>Boundary-warp wavelength λ_d in blocks.</summary>
        public float WarpWavelength;

        public int WarpOctaves;

        /// <summary>
        ///     Biome-first with both stages on the same method and strength (None; DistanceBlend with B_mat = B_hgt;
        ///     ParameterSpaceWeighting with σ_mat = σ_hgt): the height weights are the material weights, computed
        ///     once (PERF-07). Never set for DistortedBoundary, whose stages differ (warp only vs. warp + blend).
        /// </summary>
        [MarshalAs(UnmanagedType.U1)] public bool StagesShareWeights;

        /// <summary>Material-stage kernel (DistanceBlend, B_mat); not created for other methods (BLD-12).</summary>
        public KernelSet MaterialKernel;

        /// <summary>Taps of <see cref="MaterialKernel" />; empty when unused. Owned.</summary>
        [ReadOnly] public NativeArray<KernelTap> MaterialTaps;

        /// <summary>Height-stage kernel (Biome-first DistanceBlend or DistortedBoundary, B_hgt); not created otherwise.</summary>
        public KernelSet HeightKernel;

        /// <summary>Taps of <see cref="HeightKernel" />; empty when unused. Owned.</summary>
        [ReadOnly] public NativeArray<KernelTap> HeightTaps;

        // --- Classification weights λ (§6.4) -----------------------------------------------------------------------------
        public float LambdaTemperature;
        public float LambdaHumidity;
        public float LambdaContinental;
        public float LambdaErosion;
        public float LambdaPV;

        /// <summary>λ_h; Terrain-first only.</summary>
        public float LambdaHeight;

        // --- Height (§5.7) --------------------------------------------------------------------------------------------
        public float HeightMin;
        public float HeightMax;
        public float PeakWavelength;

        /// <summary>D-06: run the ice override after terrain fill.</summary>
        [MarshalAs(UnmanagedType.U1)] public bool EnableClimateOverrides; // blittable for the Burst direct calls

        /// <summary>Climate noise instances (T, F, C, E, W) and couplings. Owned.</summary>
        public ClimateParams Climate;

        /// <summary>Wavelength of detail octave i in blocks (GEN-09); Length = K. Owned.</summary>
        [ReadOnly] public NativeArray<float> OctaveWavelengths;

        /// <summary>Global octave weights g_i of Terrain-first (TRN-03); Length = K. Owned.</summary>
        [ReadOnly] public NativeArray<float> GlobalOctaveWeights;

        /// <summary>Active biomes in BiomeSet order (GEN-21). Owned.</summary>
        [ReadOnly] public NativeArray<Biome.BiomeDef> Biomes;

        /// <summary>
        ///     Effective Biome-first octave coefficients a_{b,i} = HeightAmplitude_b × OctaveWeights_{b,i} (GEN-22),
        ///     flattened as [b × K + i]. Owned.
        /// </summary>
        [ReadOnly] public NativeArray<float> OctaveCoefficients;

        /// <summary>Flattened Terrain-first spline trees (TRN-01, TRN-05), pre-ordered per root. Owned.</summary>
        [ReadOnly] public NativeArray<SplineNode> SplineNodes;

        /// <summary>Control points of <see cref="SplineNodes" />. Owned.</summary>
        [ReadOnly] public NativeArray<SplinePoint> SplinePoints;

        /// <summary>Offset(C,E,PV) root; NodeCount 0 when no spline set is assigned.</summary>
        public SplineRoot OffsetSpline;

        /// <summary>Amp(C,E,PV) root.</summary>
        public SplineRoot AmpSpline;

        /// <summary>J(C,E,PV) root (own extension, TRN-04).</summary>
        public SplineRoot JaggednessSpline;

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
        }
    }
}
