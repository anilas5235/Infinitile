using System;
using System.Collections.Generic;
using Engine.Scripts.VoxelConfig.Data.Generation;
using Engine.Scripts.VoxelConfig.Registry;
using Engine.Scripts.WorldGen.Blending;
using Engine.Scripts.WorldGen.Core;
using Engine.Scripts.WorldGen.Flora;
using Engine.Scripts.WorldGen.Height;
using Engine.Scripts.WorldGen.Noise;
using Unity.Collections;
using UnityEngine;

namespace Engine.Scripts.WorldGen.Settings{
    /// <summary>
    ///     World-generation settings: architecture, blend methods and widths, classification
    ///     weights, height range, water level, detail octaves, the climate instances T, F, C, E, W with their
    ///     couplings, the active <see cref="BiomeSet" /> and the Terrain-first <see cref="TerrainSplineSet" />.
    ///     This asset is the only generation input. <see cref="ToConfig" /> builds the Burst mirror,
    ///     <see cref="ToCanonicalJson()" /> the experiment manifest.
    /// </summary>
    [CreateAssetMenu(fileName = "WorldGenSettings", menuName = "Voxel/WorldGenSettings", order = 0)]
    public class WorldGenSettings : ScriptableObject{
        [Header("Architecture")]
        [Tooltip("Terrain-first (spline height, 6D classification) or Biome-first (5D classification, per-biome height).")]
        public GenArchitecture Architecture = GenArchitecture.BiomeFirst;

        [Header("Blending")]
        public BlendMethod MaterialMethod = BlendMethod.None;

        [Tooltip("B_mat: material blend width in blocks (DistanceBlend only; material-stage DistortedBoundary is a one-hot warp).")]
        public float MaterialBlendWidth = 32f;

        [Tooltip("σ_mat: material blend width in normalised parameter units (ParameterSpaceWeighting).")]
        public float MaterialSigma = 0.04f;

        [Tooltip("Biome-first only.")]
        public BlendMethod HeightMethod = BlendMethod.None;

        [Tooltip("B_h: height blend width in blocks (Biome-first).")]
        public float HeightBlendWidth = 32f;

        [Tooltip("σ_h: height blend width in normalised parameter units (Biome-first).")]
        public float HeightSigma = 0.04f;

        [Tooltip("A: boundary-warp amplitude in blocks, shared by both stages.")]
        public float WarpAmplitude = 24f;

        [Tooltip("λ_d: boundary-warp wavelength in blocks.")]
        public float WarpWavelength = 96f;

        [Tooltip("Boundary-warp octaves.")]
        public int WarpOctaves = 2;

        [Header("Classification weights λ")]
        public float LambdaTemperature = 1f;

        public float LambdaHumidity = 1f;
        public float LambdaContinental = 2f;
        public float LambdaErosion = 1f;
        public float LambdaPV = 0.5f;

        [Tooltip("λ_h: weight of normalised height, Terrain-first only.")]
        public float LambdaHeight = 1f;

        [Header("Height")]
        public float HeightMin = 40f;

        public float HeightMax = 250f;

        [Tooltip("Water surface level in world Y. Water fills every column up to this level.")]
        public int WaterLevel = 124;

        [Tooltip("Wavelength of detail octave i in blocks. Length = K.")]
        public float[] OctaveWavelengths ={ 512f, 256f, 128f, 64f, 32f, 16f };

        [Tooltip("Global octave weights g_i of Terrain-first. Length = K.")]
        public float[] GlobalOctaveWeights ={ 1f, 0.5f, 0.25f, 0.125f, 0.0625f, 0.03125f };

        [Tooltip("Wavelength of the peak noise P in blocks.")]
        public float PeakWavelength = 192f;

        [Header("Climate")]
        [Tooltip("Gain applied to the zero-centred weirdness fBm before clamping to [-1,1].")]
        public float WGain = 1.6f;

        [Tooltip("Global frequency multiplier for T, F, C, E and W.")]
        public float ClimateFrequencyScale = 1f;

        [Tooltip("Temperature T: fBm, default scale 900.")]
        public NoiseProfileSettings temperatureProfile = new(){
            scale = 900f,
            persistance = 0.5f,
            lacunarity = 2f,
            octaves = 3
        };

        [Tooltip("Humidity F: fBm, default scale 700.")]
        public NoiseProfileSettings humidityProfile = new(){
            scale = 700f,
            persistance = 0.5f,
            lacunarity = 2f,
            octaves = 3
        };

        [Tooltip("Continentalness C: warped fBm with redistribution curve, default scale 2500, warp 400.")]
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

        [Tooltip("How much a high continental value (inland) dries out humidity.")]
        [Range(-1f, 1f)]
        public float ContinentalHumidityInfluence = 0.4f;

        [Tooltip("How much a high continental value widens the temperature swing around the middle.")]
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

        [Tooltip("Run the ice override after terrain fill.")]
        public bool EnableClimateOverrides = true;

        [Header("Vegetation")]
        [Tooltip("r_tree: anchors of multi-block flora (trees, cacti) are at least this many blocks apart (default 3).")]
        [Min(0)]
        public int TreeSpacingRadius = 3;

        [Header("Assets")]
        [Tooltip("Active biomes. Indices in jobs are indices into this set.")]
        public BiomeSet Biomes;

        [Tooltip("Terrain-first splines; required when Architecture is TerrainFirst.")]
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
            if (TreeSpacingRadius < 0)
                Debug.LogWarning($"{name}: TreeSpacingRadius must not be negative, got {TreeSpacingRadius}.", this);
            foreach ((string field, float value) in InvalidScales())
                Debug.LogWarning($"{name}: {field} must be positive and finite, got {value}.", this);
            foreach ((string field, float value) in InvalidBlendStrengths(HeightMethod))
                Debug.LogWarning($"{name}: {field} is invalid for the selected blend method, got {value}.", this);
        }

        /// <summary>
        ///     Every wavelength (in blocks) the samplers divide by, and the climate frequency multiplier, that is not
        ///     positive and finite. They are reported rather than clamped.
        /// </summary>
        private IEnumerable<(string field, float value)> InvalidScales(){
            if (OctaveWavelengths != null)
                for (int i = 0; i < OctaveWavelengths.Length; i++)
                    if (!IsPositiveFinite(OctaveWavelengths[i])) yield return ($"OctaveWavelengths[{i}]", OctaveWavelengths[i]);
            if (!IsPositiveFinite(PeakWavelength)) yield return (nameof(PeakWavelength), PeakWavelength);
            if (!IsPositiveFinite(WarpWavelength)) yield return (nameof(WarpWavelength), WarpWavelength);
            // Not a divisor, but 0 flattens every climate field and a negative value mirrors the world.
            if (!IsPositiveFinite(ClimateFrequencyScale)) yield return (nameof(ClimateFrequencyScale), ClimateFrequencyScale);

            (string field, NoiseProfileSettings profile)[] profiles ={
                (nameof(temperatureProfile), temperatureProfile),
                (nameof(humidityProfile), humidityProfile),
                (nameof(continentalLayer) + ".baseNoise", continentalLayer?.baseNoise),
                (nameof(continentalLayer) + ".warpNoise", continentalLayer?.warpNoise),
                (nameof(erosionProfile), erosionProfile),
                (nameof(weirdnessProfile), weirdnessProfile)
            };
            foreach ((string field, NoiseProfileSettings profile) in profiles)
                if (profile != null && !IsPositiveFinite(profile.scale)) yield return (field + ".scale", profile.scale);
        }

        /// <summary>
        ///     Blend strengths the selected methods use that would break them: B ≤ 0 leaves the kernel empty,
        ///     σ ≤ 0 divides by zero, a non-finite or negative A or fewer than one warp octave has no meaning. Unused
        ///     strengths are not checked. Reported, not clamped.
        /// </summary>
        private IEnumerable<(string field, float value)> InvalidBlendStrengths(BlendMethod heightMethod){
            bool biomeFirst = Architecture == GenArchitecture.BiomeFirst;
            bool heightUsed = biomeFirst && heightMethod != BlendMethod.None;
            if (MaterialMethod == BlendMethod.DistanceBlend && !IsPositiveFinite(MaterialBlendWidth))
                yield return (nameof(MaterialBlendWidth), MaterialBlendWidth);
            if (heightUsed && heightMethod is BlendMethod.DistanceBlend or BlendMethod.DistortedBoundary && !IsPositiveFinite(HeightBlendWidth))
                yield return (nameof(HeightBlendWidth), HeightBlendWidth);
            if (MaterialMethod == BlendMethod.ParameterSpaceWeighting && !IsPositiveFinite(MaterialSigma))
                yield return (nameof(MaterialSigma), MaterialSigma);
            if (heightUsed && heightMethod == BlendMethod.ParameterSpaceWeighting && !IsPositiveFinite(HeightSigma))
                yield return (nameof(HeightSigma), HeightSigma);
            bool warpUsed = MaterialMethod == BlendMethod.DistortedBoundary || heightUsed && heightMethod == BlendMethod.DistortedBoundary;
            if (warpUsed && (!(WarpAmplitude >= 0f) || float.IsPositiveInfinity(WarpAmplitude)))
                yield return (nameof(WarpAmplitude), WarpAmplitude);
            if (warpUsed && WarpOctaves < 1) yield return (nameof(WarpOctaves), WarpOctaves);
        }

        private static bool IsPositiveFinite(float value){
            // NaN fails the comparison.
            return value > 0f && !float.IsPositiveInfinity(value);
        }

        // --- Burst mirror ------------------------------------------------------------------------------------------

        /// <summary>
        ///     Builds the Burst-side <see cref="WorldGenConfig" />. The caller owns the result and must dispose it;
        ///     <c>GeneratorConfig.Dispose</c> does so for the engine.
        /// </summary>
        /// <param name="seed">World seed.</param>
        /// <param name="registry">Voxel registry for block ids; null leaves the block ids at 0 (tests, preview).</param>
        /// <param name="allocator">Allocator of the flattened arrays.</param>
        public WorldGenConfig ToConfig(int seed, VoxelRegistry registry = null, Allocator allocator = Allocator.Persistent){
            Func<VoxelConfig.Data.Voxel.Voxel, ushort> voxelId = null;
            if (registry != null) voxelId = v => registry.GetIdOrThrow(v.GetFullName());
            return ToConfigWithVoxelIds(seed, voxelId, allocator);
        }

        /// <summary>
        ///     <see cref="ToConfig(int,VoxelRegistry,Allocator)" /> with block ids from <paramref name="voxelId" />
        ///     (the registry, or a test's own id table); null leaves every block and flora voxel id at 0.
        /// </summary>
        internal WorldGenConfig ToConfigWithVoxelIds(int seed, Func<VoxelConfig.Data.Voxel.Voxel, ushort> voxelId,
            Allocator allocator = Allocator.Persistent){
            int octaveCount = OctaveCount;
            if (octaveCount == 0) throw new InvalidOperationException($"{name}: OctaveWavelengths is empty.");
            if (octaveCount > DetailOctaves.MaxOctaves)
                throw new InvalidOperationException($"{name}: more than {DetailOctaves.MaxOctaves} detail octaves.");
            foreach ((string field, float value) in InvalidScales())
                throw new InvalidOperationException($"{name}: {field} must be positive and finite, got {value}.");
            // h = (H - Hmin)/(Hmax - Hmin) is a classification dimension: an empty range gives NaN.
            if (!(HeightMin < HeightMax) || float.IsInfinity(HeightMin) || float.IsInfinity(HeightMax))
                throw new InvalidOperationException($"{name}: HeightMin ({HeightMin}) must be finite and below HeightMax ({HeightMax}).");
            if (TreeSpacingRadius < 0)
                throw new InvalidOperationException($"{name}: TreeSpacingRadius must not be negative, got {TreeSpacingRadius}.");

            List<Biome> active = ActiveBiomes();

            // The height stage does not exist in Terrain-first; a configured method is reported and ignored.
            BlendMethod heightMethod = HeightMethod;
            if (Architecture == GenArchitecture.TerrainFirst && heightMethod != BlendMethod.None){
                Debug.LogWarning($"{name}: HeightMethod {heightMethod} is ignored in Terrain-first; the height is already continuous.", this);
                heightMethod = BlendMethod.None;
            }

            if (Architecture == GenArchitecture.TerrainFirst && !Splines)
                throw new InvalidOperationException($"{name}: Terrain-first needs a TerrainSplineSet.");
            foreach ((string field, float value) in InvalidBlendStrengths(heightMethod))
                throw new InvalidOperationException($"{name}: {field} is invalid for the selected blend method, got {value}.");

            WorldGenConfig cfg = new(){
                Seed = (uint)seed,
                Architecture = Architecture,
                MaterialMethod = MaterialMethod,
                MaterialBlendWidth = MaterialBlendWidth,
                MaterialSigma = MaterialSigma,
                HeightMethod = heightMethod,
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
                WaterLevel = WaterLevel,
                EnableClimateOverrides = EnableClimateOverrides,
                Climate = BuildClimate(seed),
                OctaveWavelengths = new NativeArray<float>(OctaveWavelengths, allocator),
                GlobalOctaveWeights = new NativeArray<float>(octaveCount, allocator),
                Biomes = new NativeArray<Biome.BiomeDef>(active.Count, allocator),
                OctaveCoefficients = new NativeArray<float>(active.Count * octaveCount, allocator)
            };

            // Biome.ToStruct and TerrainSplineSet.Flatten throw on bad data; free what was allocated above before
            // rethrowing.
            try{
                if (Splines){
                    Splines.Flatten(allocator, out cfg.SplineNodes, out cfg.SplinePoints,
                        out cfg.OffsetSpline, out cfg.AmpSpline, out cfg.JaggednessSpline);
                }
                else{
                    cfg.SplineNodes = new NativeArray<SplineNode>(0, allocator);
                    cfg.SplinePoints = new NativeArray<SplinePoint>(0, allocator);
                }

                // Kernel taps once per settings change; empty arrays for stages without a spatial kernel.
                bool biomeFirst = Architecture == GenArchitecture.BiomeFirst;
                if (MaterialMethod == BlendMethod.DistanceBlend)
                    cfg.MaterialKernel = KernelSet.Build(MaterialBlendWidth, allocator, out cfg.MaterialTaps);
                else cfg.MaterialTaps = new NativeArray<KernelTap>(0, allocator);
                if (biomeFirst && heightMethod is BlendMethod.DistanceBlend or BlendMethod.DistortedBoundary)
                    cfg.HeightKernel = KernelSet.Build(HeightBlendWidth, allocator, out cfg.HeightTaps);
                else cfg.HeightTaps = new NativeArray<KernelTap>(0, allocator);
                cfg.StagesShareWeights = biomeFirst && MaterialMethod == heightMethod && MaterialMethod switch{
                    BlendMethod.None => true,
                    BlendMethod.DistanceBlend => MaterialBlendWidth == HeightBlendWidth,
                    BlendMethod.ParameterSpaceWeighting => MaterialSigma == HeightSigma,
                    _ => false // DistortedBoundary: warp only (material) vs. warp + distance blend (height)
                };

                for (int i = 0; i < octaveCount; i++)
                    cfg.GlobalOctaveWeights[i] = GlobalOctaveWeights != null && i < GlobalOctaveWeights.Length
                        ? GlobalOctaveWeights[i]
                        : 0f;

                for (int b = 0; b < active.Count; b++){
                    Biome biome = active[b];
                    cfg.Biomes[b] = voxelId != null ? biome.ToStruct(voxelId) : biome.ToStruct();
                    for (int i = 0; i < octaveCount; i++){
                        // a_{b,i} = HeightAmplitude_b × OctaveWeights_{b,i}; missing weights count as 0.
                        float w = biome.OctaveWeights != null && i < biome.OctaveWeights.Length ? biome.OctaveWeights[i] : 0f;
                        cfg.OctaveCoefficients[b * octaveCount + i] = biome.HeightAmplitude * w;
                    }
                }

                // Flora rules, species, structures.
                FloraConfigBuilder.Build(active, voxelId, TreeSpacingRadius, allocator, ref cfg, this);
            }
            catch{
                cfg.Dispose();
                throw;
            }

            return cfg;
        }

        /// <summary>
        ///     Climate instances T, F, C, E, W from this asset. Each instance gets its own gradient salt.
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

        /// <summary>
        ///     Flora species id → name; element 0 is "none", element i the species with id i (a voxel or a
        ///     structure asset name), as <c>ColumnSample.FloraSpecies</c> reports it.
        /// </summary>
        public string[] FloraSpeciesNames(){
            List<UnityEngine.Object> species = FloraConfigBuilder.CollectSpecies(ActiveBiomes());
            string[] names = new string[species.Count + 1];
            names[0] = "none";
            for (int i = 0; i < species.Count; i++) names[i + 1] = species[i].name;
            return names;
        }

        /// <summary>The active biomes in index order, at most <see cref="BiomeSet.MaxBiomes" />. Null slots throw.</summary>
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

        // --- Canonical JSON ----------------------------------------------------------------------------------------

        /// <summary>
        ///     Deterministic JSON of every parameter that influences generation, with the <see cref="BiomeSet" /> and
        ///     <see cref="TerrainSplineSet" /> contents inlined. Sorted keys, invariant culture, round-trip
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
                ["WaterLevel"] = WaterLevel,
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
                ["TreeSpacingRadius"] = TreeSpacingRadius,
                ["Biomes"] = BiomeSetTree(),
                ["Splines"] = Splines ? Splines.ToTree() : null
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
                        ["MinHeightAboveWater"] = rule.MinHeightAboveWater,
                        ["MinStackHeight"] = rule.MinStackHeight,
                        ["MaxStackHeight"] = rule.MaxStackHeight
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
