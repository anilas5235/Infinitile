using System;
using System.Runtime.InteropServices;
using Engine.Scripts.Noise;
using Engine.Scripts.VoxelConfig.Data.Generation;
using Engine.Scripts.WorldGen.Core;
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
    ///         but it cannot travel inside a job struct: use <see cref="CreateEmpty" /> for the legacy path.
    ///     </para>
    /// </summary>
    public struct WorldGenConfig : IDisposable{
        /// <summary>World seed as the hash functions take it.</summary>
        public uint Seed;

        public GenArchitecture Architecture;

        // --- Blending (§9) -------------------------------------------------------------------------------------------
        public BlendMethod MaterialMethod;

        /// <summary>B_mat in blocks (methods 2 and 3).</summary>
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

        /// <summary>Number of detail octaves K.</summary>
        public int OctaveCount => OctaveWavelengths.IsCreated ? OctaveWavelengths.Length : 0;

        /// <summary>Number of active biomes.</summary>
        public int BiomeCount => Biomes.IsCreated ? Biomes.Length : 0;

        /// <summary>False for a default instance; true for <see cref="CreateEmpty" /> and <c>ToConfig</c> results.</summary>
        public bool IsCreated => OctaveWavelengths.IsCreated;

        /// <summary>
        ///     A created but empty config for the legacy path (INT-40): no biomes, no octaves, 1-octave placeholder
        ///     climate profiles, spec default scalars, <see cref="Architecture" /> = Legacy. <c>GeneratorConfig</c>
        ///     travels inside job structs (ChunkJob, preview jobs), and the job safety system rejects default
        ///     (unconstructed) containers even when the job never reads them, so a config without
        ///     <c>WorldGenSettings</c> must still hold constructed arrays. Owned by the caller like any other instance
        ///     (D-05); <see cref="Dispose" /> releases it.
        /// </summary>
        public static WorldGenConfig CreateEmpty(uint seed = 0u, Allocator allocator = Allocator.Persistent){
            return new WorldGenConfig{
                Seed = seed,
                Architecture = GenArchitecture.Legacy,
                MaterialMethod = BlendMethod.None,
                MaterialBlendWidth = 32f,
                MaterialSigma = 0.04f,
                HeightMethod = BlendMethod.None,
                HeightBlendWidth = 32f,
                HeightSigma = 0.04f,
                WarpAmplitude = 24f,
                WarpWavelength = 96f,
                WarpOctaves = 2,
                LambdaTemperature = 1f,
                LambdaHumidity = 1f,
                LambdaContinental = 2f,
                LambdaErosion = 1f,
                LambdaPV = 0.5f,
                LambdaHeight = 1f,
                HeightMin = 40f,
                HeightMax = 250f,
                PeakWavelength = 192f,
                EnableClimateOverrides = true,
                Climate = new ClimateParams{
                    Temperature = new NoiseProfile(UnitProfile(seed, HashSalts.TemperatureNoise)),
                    Humidity = new NoiseProfile(UnitProfile(seed, HashSalts.HumidityNoise)),
                    Continental = new WarpedNoiseLayer(new WarpedNoiseLayer.Settings{
                        BaseNoise = UnitProfile(seed, HashSalts.ContinentalNoise),
                        WarpNoise = UnitProfile(seed, HashSalts.ContinentalWarpX),
                        WarpSaltY = HashSalts.ContinentalWarpY,
                        WarpStrength = 0f,
                        RedistributionPoints = null
                    }),
                    Erosion = new NoiseProfile(UnitProfile(seed, HashSalts.ErosionNoise)),
                    Weirdness = new NoiseProfile(UnitProfile(seed, HashSalts.WeirdnessNoise)),
                    ContinentalHumidityInfluence = 0f,
                    ContinentalTemperatureInfluence = 0f,
                    WGain = 1.6f,
                    FrequencyScale = 1f
                },
                OctaveWavelengths = new NativeArray<float>(0, allocator),
                GlobalOctaveWeights = new NativeArray<float>(0, allocator),
                Biomes = new NativeArray<Biome.BiomeDef>(0, allocator),
                OctaveCoefficients = new NativeArray<float>(0, allocator)
            };
        }

        /// <summary>Single-octave unit-scale profile used by <see cref="CreateEmpty" />.</summary>
        private static NoiseProfile.Settings UnitProfile(uint seed, uint salt){
            return new NoiseProfile.Settings{
                Seed = (int)seed,
                Salt = salt,
                Scale = 1f,
                Persistance = 0.5f,
                Lacunarity = 2f,
                Octaves = 1
            };
        }

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
        }
    }
}
