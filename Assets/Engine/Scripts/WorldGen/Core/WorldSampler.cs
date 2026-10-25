using Engine.Scripts.WorldGen.Blending;
using Engine.Scripts.WorldGen.Classify;
using Engine.Scripts.WorldGen.Flora;
using Engine.Scripts.WorldGen.Height;
using Engine.Scripts.WorldGen.Noise;
using Engine.Scripts.WorldGen.Settings;
using Unity.Burst;
using Unity.Mathematics;

namespace Engine.Scripts.WorldGen.Core{
    /// <summary>
    ///     The static, Burst-compiled sampler: one entry point per quantity, all pure functions of world
    ///     coordinates, seed and a read-only <see cref="WorldGenConfig" />. The chunk job and the preview window
    ///     both call it; the region API (<see cref="WorldGenApi" />) wraps <see cref="SampleColumn(int,int,in WorldGenConfig)" />
    ///     in a job.
    ///     <para>
    ///         Terrain-first: spline height, then 6D classification (<see cref="TerrainFirstHeight" />). Biome-first:
    ///         5D classification, then the height composed from the biome parameters (<see cref="BiomeFirstHeight" />).
    ///         Both then apply the per-stage blend methods (<see cref="BlendMethods" />), the dithered surface pick
    ///         (<see cref="SurfacePick" />) and the flora decision (<see cref="FloraPlacement" />).
    ///     </para>
    ///     <para>
    ///         The <c>out</c>-parameter overloads are Burst direct-call entry points: managed callers
    ///         (preview, tests) run the same compiled code as the jobs and get bit-identical results. Direct calls
    ///         cannot return structs or carry <c>bool</c> in their signature, hence the <c>out</c> shape; the
    ///         returning overloads are managed conveniences over them (and plain inlined calls inside jobs).
    ///         A compile error in one of these entry points disables Burst for every job in the Editor, which
    ///         <c>WorldSamplerTests.SampleColumn_CompilesWithBurst</c> guards.
    ///     </para>
    /// </summary>
    [BurstCompile]
    public static class WorldSampler{
        /// <summary>T, F, C, E, W, PV at world position <paramref name="p" />.</summary>
        [BurstCompile(CompileSynchronously = true, FloatMode = FloatMode.Strict)]
        public static void SampleFields(in float2 p, in WorldGenConfig cfg, out WorldFields fields){
            fields = ClimateFields.Sample(p, cfg.Climate);
        }

        /// <inheritdoc cref="SampleFields(in float2,in WorldGenConfig,out WorldFields)" />
        public static WorldFields SampleFields(in float2 p, in WorldGenConfig cfg){
            SampleFields(p, cfg, out WorldFields fields);
            return fields;
        }

        /// <summary>
        ///     Full column decision at block column (<paramref name="x" />, <paramref name="z" />): the pure function.
        ///     Spatial blend methods evaluate every kernel tap here, so this is the slow reference; jobs
        ///     pass a <see cref="BlendCache" /> to the overload below and get the same bits.
        /// </summary>
        [BurstCompile(CompileSynchronously = true, FloatMode = FloatMode.Strict)]
        public static void SampleColumn(int x, int z, in WorldGenConfig cfg, out ColumnSample sample){
            BlendCache noCache = default;
            SampleColumn(x, z, cfg, noCache, out sample);
        }

        /// <summary>
        ///     <see cref="SampleColumn(int,int,in WorldGenConfig,out ColumnSample)" /> with the padded lattices of the
        ///     surrounding block of columns; called from Burst jobs only. Taps outside the cache fall back to
        ///     direct evaluation, so the result never depends on the cache. Direct-call entry point,
        ///     so a managed caller such as a test of <c>PrepareChunkMaps</c> runs the job's code.
        /// </summary>
        [BurstCompile(CompileSynchronously = true, FloatMode = FloatMode.Strict)]
        public static void SampleColumn(int x, int z, in WorldGenConfig cfg, in BlendCache cache, out ColumnSample sample){
            SampleCore(x, z, cfg, cache, out sample);
            // The flora decision on top of the core sample.
            FloraPlacement.Decide(x, z, cfg, cache, sample, out ushort species, out int source);
            sample.FloraSpecies = species;
            sample.FloraSource = source;
        }

        /// <summary>
        ///     Everything of <see cref="SampleColumn(int,int,in WorldGenConfig,in BlendCache,out ColumnSample)" /> but the
        ///     flora decision (FloraSpecies 0, FloraSource -1): the input of <see cref="FloraPlacement" />, which samples
        ///     neighbouring columns with it, and of the chunk job's padded flora grid. Direct-call entry point,
        ///     so a managed caller of <c>PrepareChunkMaps</c> gets the job's bits.
        /// </summary>
        [BurstCompile(CompileSynchronously = true, FloatMode = FloatMode.Strict)]
        public static void SampleCore(int x, int z, in WorldGenConfig cfg, in BlendCache cache, out ColumnSample sample){
            float2 p = new(x, z);
            int2 column = new(x, z);
            int biomeCount = cfg.BiomeCount;
            ColumnSample s = default;
            SampleFields(p, cfg, out s.Fields);

            float unclamped;
            BlendWeights distancesSq;
            if (cfg.Architecture == GenArchitecture.TerrainFirst){
                // Terrain-first: the spline height comes first and does not depend on any biome (identical for
                // every material method); the normalised height h then takes part in the 6D classification.
                // The height stage does not exist here.
                TerrainFirstHeight.Sample(p, s.Fields, cfg, out s.TerrainParams, out s.Height, out unclamped);
                s.NormalizedHeight = NormalizedHeight(s.Height, cfg);
                s.HardBiome = BiomeClassifier.Distances(s.Fields, s.NormalizedHeight, true, cfg, out distancesSq);
                s.MaterialWeights = BlendMethods.Compute(BlendStage.Material, cfg.MaterialMethod, column, s.HardBiome,
                    distancesSq, cfg, cache);
                s.HeightWeights = BlendWeights.OneHot(s.HardBiome);
            }
            else{
                // Biome-first: 5D classification (no height dimension); each stage computes its weights with its
                // own method, and the height is composed from the height-stage weights.
                s.HardBiome = BiomeClassifier.Distances(s.Fields, 0f, false, cfg, out distancesSq);
                s.MaterialWeights = BlendMethods.Compute(BlendStage.Material, cfg.MaterialMethod, column, s.HardBiome,
                    distancesSq, cfg, cache);
                // The same method with the same strength gives the same weights; compute them once.
                s.HeightWeights = cfg.StagesShareWeights
                    ? s.MaterialWeights
                    : BlendMethods.Compute(BlendStage.Height, cfg.HeightMethod, column, s.HardBiome, distancesSq, cfg, cache);
                BiomeFirstHeight.Sample(p, s.HeightWeights, cfg, out s.Height, out unclamped);
                s.NormalizedHeight = NormalizedHeight(s.Height, cfg);
            }

            if (cfg.MaterialMethod == BlendMethod.None){
                // One-hot weights: argmax and the dithered pick are the hard class itself, so skip both.
                s.DominantMaterialBiome = s.HardBiome;
                s.SurfaceSource = s.HardBiome;
            }
            else{
                s.DominantMaterialBiome = biomeCount > 0 ? s.MaterialWeights.ArgMax(biomeCount) : -1;
                // Dithered surface pick from the material weights.
                s.SurfaceSource = SurfacePick.Pick(x, z, cfg.Seed, HashSalts.SurfacePick, s.MaterialWeights, biomeCount);
            }

            s.Clamped = s.Height != unclamped;
            s.ColumnHeight = (int)math.floor(s.Height);

            s.FloraSpecies = 0;
            s.FloraSource = -1;
            sample = s;
        }

        /// <summary>
        ///     The terrain height H of <see cref="SampleCore" /> alone, bit for bit: Terrain-first skips the classification
        ///     and the material stage, Biome-first the surface pick. Direct-call entry point, so a managed caller gets the
        ///     jobs' bits.
        /// </summary>
        [BurstCompile(CompileSynchronously = true, FloatMode = FloatMode.Strict)]
        public static void SampleHeight(int x, int z, in WorldGenConfig cfg, in BlendCache cache, out float height){
            float2 p = new(x, z);
            SampleFields(p, cfg, out WorldFields fields);
            if (cfg.Architecture == GenArchitecture.TerrainFirst){
                TerrainFirstHeight.Sample(p, fields, cfg, out float3 _, out height, out float _);
                return;
            }

            int2 column = new(x, z);
            int hard = BiomeClassifier.Distances(fields, 0f, false, cfg, out BlendWeights distancesSq);
            BlendWeights weights = cfg.StagesShareWeights
                ? BlendMethods.Compute(BlendStage.Material, cfg.MaterialMethod, column, hard, distancesSq, cfg, cache)
                : BlendMethods.Compute(BlendStage.Height, cfg.HeightMethod, column, hard, distancesSq, cfg, cache);
            BiomeFirstHeight.Sample(p, weights, cfg, out height, out float _);
        }

        /// <summary>h = saturate((H − Hmin)/(Hmax − Hmin)).</summary>
        public static float NormalizedHeight(float height, in WorldGenConfig cfg){
            return math.saturate((height - cfg.HeightMin) / (cfg.HeightMax - cfg.HeightMin));
        }

        /// <summary>
        ///     Hard classification nn(q) at world position <paramref name="q" />: the architecture's own vector,
        ///     5D in Biome-first, 6D with h(q) in Terrain-first. At an integer column it equals that column's
        ///     <see cref="ColumnSample.HardBiome" />; -1 without active biomes. Direct-call entry point: the
        ///     lattice of a managed <c>BlendCache.Build</c> classifies with the jobs' code.
        /// </summary>
        [BurstCompile(CompileSynchronously = true, FloatMode = FloatMode.Strict)]
        public static int ClassifyAt(in float2 q, in WorldGenConfig cfg){
            WorldFields f = ClimateFields.Sample(q, cfg.Climate);
            if (cfg.Architecture != GenArchitecture.TerrainFirst) return BiomeClassifier.Classify(f, cfg);
            TerrainFirstHeight.Sample(q, f, cfg, out float3 _, out float height, out float _);
            return BiomeClassifier.Classify(f, NormalizedHeight(height, cfg), cfg);
        }

        /// <summary>
        ///     nn~(q) = nn(q + A·Ω(q)): every field, and in Terrain-first h, sampled at the warped position.
        /// </summary>
        [BurstCompile(CompileSynchronously = true, FloatMode = FloatMode.Strict)]
        public static int ClassifyWarpedAt(in float2 q, in WorldGenConfig cfg){
            return ClassifyAt(WarpField.Warp(q, cfg), cfg);
        }

        /// <inheritdoc cref="SampleColumn(int,int,in WorldGenConfig,out ColumnSample)" />
        public static ColumnSample SampleColumn(int x, int z, in WorldGenConfig cfg){
            SampleColumn(x, z, cfg, out ColumnSample sample);
            return sample;
        }
    }
}
