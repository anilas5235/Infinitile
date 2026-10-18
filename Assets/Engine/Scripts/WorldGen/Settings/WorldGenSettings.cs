using System;
using System.Collections.Generic;
using Engine.Scripts.Noise;
using Engine.Scripts.Settings;
using Engine.Scripts.VoxelConfig.Data.Generation;
using Engine.Scripts.VoxelConfig.Registry;
using Engine.Scripts.WorldGen.Core;
using Engine.Scripts.WorldGen.Flora;
using Engine.Scripts.WorldGen.Noise;
using Unity.Collections;
using UnityEngine;

namespace Engine.Scripts.WorldGen.Settings{
    /// <summary>
    ///     Spec world-generation settings (INT-01, §11.2): architecture, blend methods and widths, classification
    ///     weights, height range, detail octaves, the climate instances T, F, C, E, W with their couplings (independent
    ///     of the legacy <see cref="NoiseSettings" />, decision 2026-10-18),
    ///     the active <see cref="BiomeSet" /> and the Terrain-first <see cref="TerrainSplineSet" />.
    ///     <see cref="ToConfig" /> builds the Burst mirror (INT-03), <see cref="ToCanonicalJson()" /> the
    ///     experiment manifest (INT-02). Defaults are the spec defaults (§9, §6.4, §5.7, §19.1).
    /// </summary>
    [CreateAssetMenu(fileName = "WorldGenSettings", menuName = "Voxel/WorldGenSettings", order = 0)]
    public class WorldGenSettings : ScriptableObject{
        [Header("Architecture (§4)")]
        [Tooltip("Terrain-first, Biome-first or the legacy hybrid generator (INT-40).")]
        public GenArchitecture Architecture = GenArchitecture.Legacy;

        [Header("Blending (§9)")]
        public BlendMethod MaterialMethod = BlendMethod.None;

        [Tooltip("B_mat: material blend width in blocks (DistanceBlend, DistortedBoundary).")]
        public float MaterialBlendWidth = 32f;

        [Tooltip("σ_mat: material blend width in normalised parameter units (ParameterSpaceWeighting).")]
        public float MaterialSigma = 0.04f;

        [Tooltip("Biome-first only.")]
        public BlendMethod HeightMethod = BlendMethod.None;

        [Tooltip("B_h: height blend width in blocks (Biome-first).")]
        public float HeightBlendWidth = 32f;

        [Tooltip("σ_h: height blend width in normalised parameter units (Biome-first).")]
        public float HeightSigma = 0.04f;

        [Tooltip("A: boundary-warp amplitude in blocks, shared by both stages (DEC-05).")]
        public float WarpAmplitude = 24f;

        [Tooltip("λ_d: boundary-warp wavelength in blocks.")]
        public float WarpWavelength = 96f;

        [Tooltip("Boundary-warp octaves.")]
        public int WarpOctaves = 2;

        [Header("Classification weights λ (§6.4)")]
        public float LambdaTemperature = 1f;

        public float LambdaHumidity = 1f;
        public float LambdaContinental = 2f;
        public float LambdaErosion = 1f;
        public float LambdaPV = 0.5f;

        [Tooltip("λ_h: weight of normalised height, Terrain-first only.")]
        public float LambdaHeight = 1f;

        [Header("Height (§5.7)")]
        public float HeightMin = 40f;

        public float HeightMax = 250f;

        [Tooltip("Wavelength of detail octave i in blocks (GEN-09). Length = K.")]
        public float[] OctaveWavelengths ={ 512f, 256f, 128f, 64f, 32f, 16f };

        [Tooltip("Global octave weights g_i of Terrain-first (TRN-03). Length = K.")]
        public float[] GlobalOctaveWeights ={ 1f, 0.5f, 0.25f, 0.125f, 0.0625f, 0.03125f };

        [Tooltip("Wavelength of the peak noise P in blocks (GEN-10).")]
        public float PeakWavelength = 192f;

        [Header("Climate (§5.5)")]
        [Tooltip("Gain applied to the zero-centred weirdness fBm before clamping to [-1,1].")]
        public float WGain = 1.6f;

        [Tooltip("Global frequency multiplier for T, F, C, E and W (GEN-07).")]
        public float ClimateFrequencyScale = 1f;

        [Tooltip("Temperature T: fBm, spec default scale 900 (§5.5).")]
        public NoiseProfileSettings temperatureProfile = new(){
            scale = 900f,
            persistance = 0.5f,
            lacunarity = 2f,
            octaves = 3
        };

        [Tooltip("Humidity F: fBm, spec default scale 700 (§5.5).")]
        public NoiseProfileSettings humidityProfile = new(){
            scale = 700f,
            persistance = 0.5f,
            lacunarity = 2f,
            octaves = 3
        };

        [Tooltip("Continentalness C: warped fBm with redistribution curve, spec default scale 2500, warp 400 (§5.5).")]
        public WarpedNoiseLayerSettings continentalLayer = new(){
            baseNoise = new NoiseProfileSettings{
                scale = 2500f,
                persistance = 0.5f,
                lacunarity = 2f,
                octaves = 5
            },
            warpNoise = new NoiseProfileSettings{
                scale = 1800f,
                persistance = 0.5f,
                lacunarity = 2f,
                octaves = 2
            },
            warpStrength = 400f,
            redistributionCurve = new AnimationCurve(
                new Keyframe(0f, 0.02f),
                new Keyframe(0.3f, 0.12f),
                new Keyframe(0.4f, 0.28f),
                new Keyframe(0.5f, 0.42f),
                new Keyframe(0.6f, 0.55f),
                new Keyframe(0.8f, 0.78f),
                new Keyframe(1f, 1f)
            )
        };

        [Tooltip("How much a high continental value (inland) dries out humidity (DEC-06 coupling).")]
        [Range(-1f, 1f)]
        public float ContinentalHumidityInfluence = 0.4f;

        [Tooltip("How much a high continental value widens the temperature swing around the middle (DEC-06 coupling).")]
        [Range(0f, 2f)]
        public float ContinentalTemperatureInfluence = 0.6f;

        public NoiseProfileSettings erosionProfile = new(){
            scale = 1200f,
            persistance = 0.5f,
            lacunarity = 2f,
            octaves = 4
        };

        public NoiseProfileSettings weirdnessProfile = new(){
            scale = 500f,
            persistance = 0.5f,
            lacunarity = 2f,
            octaves = 3
        };

        [Tooltip("D-06: run the ice override after terrain fill.")]
        public bool EnableClimateOverrides = true;

        [Header("Assets")]
        [Tooltip("Active biomes (GEN-20). Indices in jobs are indices into this set.")]
        public BiomeSet Biomes;

        [Tooltip("Terrain-first splines (TRN-05). Optional in Phase 0.")]
        public TerrainSplineSet Splines;

        /// <summary>Number of detail octaves K.</summary>
        public int OctaveCount => OctaveWavelengths?.Length ?? 0;

        private void OnValidate(){
            continentalLayer?.EnsureSevenLinearKeys();
            if (OctaveWavelengths == null || OctaveWavelengths.Length == 0)
                Debug.LogWarning($"{name}: OctaveWavelengths must hold at least one octave.", this);
            else if (OctaveWavelengths.Length > DetailOctaves.MaxOctaves)
                Debug.LogWarning($"{name}: at most {DetailOctaves.MaxOctaves} detail octaves are supported.", this);
            if (GlobalOctaveWeights != null && OctaveWavelengths != null && GlobalOctaveWeights.Length != OctaveWavelengths.Length)
                Debug.LogWarning($"{name}: GlobalOctaveWeights ({GlobalOctaveWeights.Length}) and OctaveWavelengths ({OctaveWavelengths.Length}) differ in length.", this);
            if (HeightMin >= HeightMax)
                Debug.LogWarning($"{name}: HeightMin must be below HeightMax.", this);
        }

        // --- INT-03: Burst mirror -------------------------------------------------------------------------------------

        /// <summary>
        ///     Builds the Burst-side <see cref="WorldGenConfig" />. The caller owns the result and must dispose it
        ///     (D-05); <c>GeneratorConfig.Dispose</c> does so for the engine.
        /// </summary>
        /// <param name="seed">World seed.</param>
        /// <param name="registry">Voxel registry for block ids; null leaves the block ids at 0 (tests, preview).</param>
        /// <param name="allocator">Allocator of the flattened arrays.</param>
        public WorldGenConfig ToConfig(int seed, VoxelRegistry registry = null, Allocator allocator = Allocator.Persistent){
            int octaveCount = OctaveCount;
            if (octaveCount == 0) throw new InvalidOperationException($"{name}: OctaveWavelengths is empty.");
            if (octaveCount > DetailOctaves.MaxOctaves)
                throw new InvalidOperationException($"{name}: more than {DetailOctaves.MaxOctaves} detail octaves.");

            List<Biome> active = ActiveBiomes();

            WorldGenConfig cfg = new(){
                Seed = (uint)seed,
                Architecture = Architecture,
                MaterialMethod = MaterialMethod,
                MaterialBlendWidth = MaterialBlendWidth,
                MaterialSigma = MaterialSigma,
                HeightMethod = HeightMethod,
                HeightBlendWidth = HeightBlendWidth,
                HeightSigma = HeightSigma,
                WarpAmplitude = WarpAmplitude,
                WarpWavelength = WarpWavelength,
                WarpOctaves = WarpOctaves,
                LambdaTemperature = LambdaTemperature,
                LambdaHumidity = LambdaHumidity,
                LambdaContinental = LambdaContinental,
                LambdaErosion = LambdaErosion,
                LambdaPV = LambdaPV,
                LambdaHeight = LambdaHeight,
                HeightMin = HeightMin,
                HeightMax = HeightMax,
                PeakWavelength = PeakWavelength,
                EnableClimateOverrides = EnableClimateOverrides,
                Climate = BuildClimate(seed),
                OctaveWavelengths = new NativeArray<float>(OctaveWavelengths, allocator),
                GlobalOctaveWeights = new NativeArray<float>(octaveCount, allocator),
                Biomes = new NativeArray<Biome.BiomeDef>(active.Count, allocator),
                OctaveCoefficients = new NativeArray<float>(active.Count * octaveCount, allocator)
            };

            for (int i = 0; i < octaveCount; i++)
                cfg.GlobalOctaveWeights[i] = GlobalOctaveWeights != null && i < GlobalOctaveWeights.Length
                    ? GlobalOctaveWeights[i]
                    : 0f;

            for (int b = 0; b < active.Count; b++){
                Biome biome = active[b];
                cfg.Biomes[b] = registry != null ? biome.ToStruct(registry) : biome.ToStruct();
                for (int i = 0; i < octaveCount; i++){
                    // GEN-22: a_{b,i} = HeightAmplitude_b × OctaveWeights_{b,i}; missing weights count as 0.
                    float w = biome.OctaveWeights != null && i < biome.OctaveWeights.Length ? biome.OctaveWeights[i] : 0f;
                    cfg.OctaveCoefficients[b * octaveCount + i] = biome.HeightAmplitude * w;
                }
            }

            return cfg;
        }

        /// <summary>
        ///     Climate instances T, F, C, E, W from this asset; the legacy <see cref="NoiseSettings" /> no longer takes
        ///     part. Each instance gets its own gradient salt (GEN-05).
        /// </summary>
        private ClimateParams BuildClimate(int seed){
            return new ClimateParams{
                Temperature = new NoiseProfile(temperatureProfile.ToStruct(seed, HashSalts.TemperatureNoise)),
                Humidity = new NoiseProfile(humidityProfile.ToStruct(seed, HashSalts.HumidityNoise)),
                Continental = new WarpedNoiseLayer(continentalLayer.ToStruct(seed,
                    HashSalts.ContinentalNoise, HashSalts.ContinentalWarpX, HashSalts.ContinentalWarpY)),
                Erosion = new NoiseProfile(erosionProfile.ToStruct(seed, HashSalts.ErosionNoise)),
                Weirdness = new NoiseProfile(weirdnessProfile.ToStruct(seed, HashSalts.WeirdnessNoise)),
                ContinentalHumidityInfluence = ContinentalHumidityInfluence,
                ContinentalTemperatureInfluence = ContinentalTemperatureInfluence,
                WGain = WGain,
                FrequencyScale = ClimateFrequencyScale
            };
        }

        /// <summary>The active biomes in index order (GEN-21), at most <see cref="BiomeSet.MaxBiomes" />. Null slots throw.</summary>
        private List<Biome> ActiveBiomes(){
            List<Biome> result = new();
            if (Biomes == null || Biomes.Biomes == null) return result;
            int count = Mathf.Min(Biomes.Count, BiomeSet.MaxBiomes);
            for (int i = 0; i < count; i++){
                Biome biome = Biomes.Biomes[i];
                if (!biome) throw new InvalidOperationException($"{Biomes.name}: biome slot {i} is empty.");
                result.Add(biome);
            }

            return result;
        }

        // --- INT-02: canonical JSON -----------------------------------------------------------------------------------

        /// <summary>
        ///     Deterministic JSON of every parameter that influences generation, with the <see cref="BiomeSet" /> and
        ///     <see cref="TerrainSplineSet" /> contents inlined (INT-02). Sorted keys, invariant culture, round-trip
        ///     floats: identical settings give identical text.
        /// </summary>
        public string ToCanonicalJson(){
            return CanonicalJson.Serialize(ToTree(null));
        }

        /// <summary>Same as <see cref="ToCanonicalJson()" /> plus the world seed, for experiment manifests.</summary>
        public string ToCanonicalJson(int seed){
            return CanonicalJson.Serialize(ToTree(seed));
        }

        private Dictionary<string, object> ToTree(int? seed){
            Dictionary<string, object> tree = new(){
                ["Architecture"] = Architecture,
                ["MaterialMethod"] = MaterialMethod,
                ["MaterialBlendWidth"] = MaterialBlendWidth,
                ["MaterialSigma"] = MaterialSigma,
                ["HeightMethod"] = HeightMethod,
                ["HeightBlendWidth"] = HeightBlendWidth,
                ["HeightSigma"] = HeightSigma,
                ["WarpAmplitude"] = WarpAmplitude,
                ["WarpWavelength"] = WarpWavelength,
                ["WarpOctaves"] = WarpOctaves,
                ["LambdaTemperature"] = LambdaTemperature,
                ["LambdaHumidity"] = LambdaHumidity,
                ["LambdaContinental"] = LambdaContinental,
                ["LambdaErosion"] = LambdaErosion,
                ["LambdaPV"] = LambdaPV,
                ["LambdaHeight"] = LambdaHeight,
                ["HeightMin"] = HeightMin,
                ["HeightMax"] = HeightMax,
                ["OctaveWavelengths"] = OctaveWavelengths ?? Array.Empty<float>(),
                ["GlobalOctaveWeights"] = GlobalOctaveWeights ?? Array.Empty<float>(),
                ["PeakWavelength"] = PeakWavelength,
                ["WGain"] = WGain,
                ["ClimateFrequencyScale"] = ClimateFrequencyScale,
                ["TemperatureProfile"] = ProfileTree(temperatureProfile),
                ["HumidityProfile"] = ProfileTree(humidityProfile),
                ["ContinentalLayer"] = LayerTree(continentalLayer),
                ["ContinentalHumidityInfluence"] = ContinentalHumidityInfluence,
                ["ContinentalTemperatureInfluence"] = ContinentalTemperatureInfluence,
                ["ErosionProfile"] = ProfileTree(erosionProfile),
                ["WeirdnessProfile"] = ProfileTree(weirdnessProfile),
                ["EnableClimateOverrides"] = EnableClimateOverrides,
                ["Biomes"] = BiomeSetTree(),
                ["Splines"] = Splines
                    ? new Dictionary<string, object>{ ["Name"] = Splines.name, ["Notes"] = Splines.Notes ?? "" }
                    : null
            };
            if (seed.HasValue) tree["Seed"] = seed.Value;
            return tree;
        }

        private static Dictionary<string, object> ProfileTree(NoiseProfileSettings p){
            if (p == null) return null;
            return new Dictionary<string, object>{
                ["Scale"] = p.scale,
                ["Persistance"] = p.persistance,
                ["Lacunarity"] = p.lacunarity,
                ["Octaves"] = p.octaves
            };
        }

        private static Dictionary<string, object> LayerTree(WarpedNoiseLayerSettings l){
            if (l == null) return null;
            List<object> curve = new();
            if (l.redistributionCurve != null)
                foreach (Keyframe k in l.redistributionCurve.keys) curve.Add(new[]{ k.time, k.value });
            return new Dictionary<string, object>{
                ["BaseNoise"] = ProfileTree(l.baseNoise),
                ["WarpNoise"] = ProfileTree(l.warpNoise),
                ["WarpStrength"] = l.warpStrength,
                ["RedistributionCurve"] = curve
            };
        }

        private List<object> BiomeSetTree(){
            List<object> list = new();
            if (Biomes == null || Biomes.Biomes == null) return list;
            foreach (Biome biome in Biomes.Biomes) list.Add(biome ? BiomeTree(biome) : null);
            return list;
        }

        private static Dictionary<string, object> BiomeTree(Biome b){
            List<object> flora = new();
            if (b.FloraRules != null)
                foreach (FloraRule rule in b.FloraRules)
                    flora.Add(new Dictionary<string, object>{
                        ["SpeciesVoxel"] = VoxelName(rule.SpeciesVoxel),
                        ["SpeciesStructure"] = rule.SpeciesStructure ? rule.SpeciesStructure.name : null,
                        ["Probability"] = rule.Probability,
                        ["RequiresSurfaceFromSameBiome"] = rule.RequiresSurfaceFromSameBiome,
                        ["MinHeightAboveWater"] = rule.MinHeightAboveWater
                    });

            return new Dictionary<string, object>{
                ["Name"] = b.name,
                ["TopBlock"] = VoxelName(b.TopBlock),
                ["UnderBlock"] = VoxelName(b.UnderBlock),
                ["StoneBlock"] = VoxelName(b.StoneBlock),
                ["TargetTemperature"] = b.TargetTemperature,
                ["TargetHumidity"] = b.TargetHumidity,
                ["TargetContinental"] = b.TargetContinental,
                ["TargetErosion"] = b.TargetErosion,
                ["TargetPV"] = b.TargetPV,
                ["TargetHeight"] = b.TargetHeight,
                ["HeightOffset"] = b.HeightOffset,
                ["HeightAmplitude"] = b.HeightAmplitude,
                ["OctaveWeights"] = b.OctaveWeights ?? Array.Empty<float>(),
                ["Jaggedness"] = b.Jaggedness,
                ["FloraRules"] = flora
            };
        }

        private static string VoxelName(VoxelConfig.Data.Voxel.Voxel voxel){
            if (!voxel) return null;
            return voxel.package ? voxel.GetFullName().ToString() : voxel.name;
        }
    }
}
