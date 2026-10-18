using Engine.Scripts.WorldGen.Blending;
using Engine.Scripts.WorldGen.Classify;
using Engine.Scripts.WorldGen.Height;
using Engine.Scripts.WorldGen.Noise;
using Engine.Scripts.WorldGen.Settings;
using Unity.Burst;
using Unity.Mathematics;

namespace Engine.Scripts.WorldGen.Core{
    /// <summary>
    ///     The static, Burst-compiled sampler (GEN-01): one entry point per quantity, all pure functions of world
    ///     coordinates, seed and a read-only <see cref="WorldGenConfig" /> (R3). The chunk job and the preview window
    ///     both call it; the region API (<see cref="WorldGenApi" />) wraps <see cref="SampleColumn(int,int,in WorldGenConfig)" />
    ///     in a job.
    ///     <para>
    ///         Phase 0: fields, hard 5D classification, one-hot weights and the common height formula §5.7.
    ///         Phase 1: the Terrain-first path (spline height, then 6D classification, §7). Phase 2: the
    ///         Biome-first path (5D classification, then the height composed from the biome parameters, §8,
    ///         <see cref="BiomeFirstHeight" />). Phase 3 adds the blend methods and the surface pick, Phase 4 flora.
    ///     </para>
    ///     <para>
    ///         The <c>out</c>-parameter overloads are Burst direct-call entry points (GEN-16): managed callers
    ///         (preview, tests) run the same compiled code as the jobs and get bit-identical results. Direct calls
    ///         cannot return structs or carry <c>bool</c> in their signature, hence the <c>out</c> shape; the
    ///         returning overloads are managed conveniences over them (and plain inlined calls inside jobs).
    ///         A compile error in one of these entry points disables Burst for every job in the Editor, which
    ///         <c>WorldSamplerTests.SampleColumn_CompilesWithBurst</c> guards.
    ///     </para>
    /// </summary>
    [BurstCompile]
    public static class WorldSampler{
        /// <summary>T, F, C, E, W, PV at world position <paramref name="p" /> (GEN-07).</summary>
        [BurstCompile(CompileSynchronously = true, FloatMode = FloatMode.Strict)]
        public static void SampleFields(in float2 p, in WorldGenConfig cfg, out WorldFields fields){
            fields = ClimateFields.Sample(p, cfg.Climate);
        }

        /// <inheritdoc cref="SampleFields(in float2,in WorldGenConfig,out WorldFields)" />
        public static WorldFields SampleFields(in float2 p, in WorldGenConfig cfg){
            SampleFields(p, cfg, out WorldFields fields);
            return fields;
        }

        /// <summary>Full column decision at block column (<paramref name="x" />, <paramref name="z" />).</summary>
        [BurstCompile(CompileSynchronously = true, FloatMode = FloatMode.Strict)]
        public static void SampleColumn(int x, int z, in WorldGenConfig cfg, out ColumnSample sample){
            float2 p = new(x, z);
            ColumnSample s = default;
            SampleFields(p, cfg, out s.Fields);

            float unclamped;
            if (cfg.Architecture == GenArchitecture.TerrainFirst){
                // Terrain-first (§7): the spline height comes first and does not depend on any biome (TRN-03);
                // the normalised height h then takes part in the 6D classification (TRN-10). The height stage does
                // not exist here (TRN-21).
                TerrainFirstHeight.Sample(p, s.Fields, cfg, out s.TerrainParams, out s.Height, out unclamped);
                s.NormalizedHeight = math.saturate((s.Height - cfg.HeightMin) / (cfg.HeightMax - cfg.HeightMin));
                s.HardBiome = BiomeClassifier.Classify(s.Fields, s.NormalizedHeight, cfg);

                // Phase 0/1: material method 1 (None) (§9.2); Phase 3 routes through the per-stage methods (BLD-02).
                s.MaterialWeights = BlendWeights.OneHot(s.HardBiome);
                s.HeightWeights = s.MaterialWeights;
                s.DominantMaterialBiome = s.HardBiome;
            }
            else{
                // Biome-first (§8): hard 5D classification (BIO-01, no height dimension), then the height is
                // composed from the height-stage weights (BIO-02). Phase 2 has only method None, so both weight
                // sets are one-hot and the height jumps at biome boundaries; Phase 3 adds the blend methods
                // (material and height stage separately, BLD-02).
                s.HardBiome = BiomeClassifier.Classify(s.Fields, cfg);
                s.MaterialWeights = BlendWeights.OneHot(s.HardBiome);
                s.HeightWeights = s.MaterialWeights;
                s.DominantMaterialBiome = s.HardBiome;
                BiomeFirstHeight.Sample(p, s.HeightWeights, cfg, out s.Height, out unclamped);
                s.NormalizedHeight = math.saturate((s.Height - cfg.HeightMin) / (cfg.HeightMax - cfg.HeightMin));
            }

            // Surface pick (§9.6) arrives in Phase 3; until then the dominant material biome supplies the blocks.
            s.SurfaceSource = s.DominantMaterialBiome;

            s.Clamped = s.Height != unclamped;
            s.ColumnHeight = (int)math.floor(s.Height);

            // Flora (§10) arrives in Phase 4.
            s.FloraSpecies = 0;
            s.FloraSource = -1;
            sample = s;
        }

        /// <inheritdoc cref="SampleColumn(int,int,in WorldGenConfig,out ColumnSample)" />
        public static ColumnSample SampleColumn(int x, int z, in WorldGenConfig cfg){
            SampleColumn(x, z, cfg, out ColumnSample sample);
            return sample;
        }
    }
}
