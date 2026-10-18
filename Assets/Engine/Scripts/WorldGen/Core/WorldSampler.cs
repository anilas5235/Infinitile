using Engine.Scripts.WorldGen.Blending;
using Engine.Scripts.WorldGen.Classify;
using Engine.Scripts.WorldGen.Noise;
using Engine.Scripts.WorldGen.Settings;
using Unity.Burst;
using Unity.Mathematics;

namespace Engine.Scripts.WorldGen.Core{
    /// <summary>
    ///     The static, Burst-compiled sampler (GEN-01): one entry point per quantity, all pure functions of world
    ///     coordinates, seed and a read-only <see cref="WorldGenConfig" /> (R3). The chunk job and the preview window
    ///     both call it; the region API (<see cref="WorldGenApi" />) wraps <see cref="SampleColumn" /> in a job.
    ///     <para>
    ///         Phase 0 skeleton: fields, hard 5D classification, one-hot weights and the common height formula §5.7
    ///         composed from the classified biome's parameters (§8.2 with one-hot weights). Phase 1 adds the
    ///         Terrain-first spline path and 6D classification, Phase 2 the blend methods, Phase 3 the surface pick,
    ///         Phase 4 flora.
    ///     </para>
    /// </summary>
    [BurstCompile]
    public static class WorldSampler{
        /// <summary>T, F, C, E, W, PV at world position <paramref name="p" /> (GEN-07).</summary>
        [BurstCompile]
        public static WorldFields SampleFields(in float2 p, in WorldGenConfig cfg){
            return ClimateFields.Sample(p, cfg.Climate);
        }

        /// <summary>
        ///     Full column decision at block column (<paramref name="x" />, <paramref name="z" />). Marked for Burst
        ///     direct call, so managed callers (preview, tests) run the same compiled code as the jobs and get
        ///     bit-identical results (GEN-16).
        /// </summary>
        [BurstCompile]
        public static ColumnSample SampleColumn(int x, int z, in WorldGenConfig cfg){
            float2 p = new(x, z);
            ColumnSample s = default;
            s.Fields = SampleFields(p, cfg);

            // Phase 0: hard 5D classification for every architecture. Terrain-first (6D with h) arrives with the
            // spline height in Phase 1 (TRN-01…06).
            s.HardBiome = BiomeClassifier.Classify(s.Fields, cfg);

            // Phase 0: method 1 (None) in both stages (§9.2). Phase 2 routes through the per-stage methods (BLD-02).
            s.MaterialWeights = BlendWeights.OneHot(s.HardBiome);
            s.HeightWeights = s.MaterialWeights;
            s.DominantMaterialBiome = s.HardBiome;

            // Surface pick (§9.6) arrives in Phase 3; until then the dominant material biome supplies the blocks.
            s.SurfaceSource = s.DominantMaterialBiome;

            s.Height = SampleBiomeHeight(p, s.HeightWeights, cfg, out s.Clamped);
            s.ColumnHeight = (int)math.floor(s.Height);

            // Flora (§10) arrives in Phase 4.
            s.FloraSpecies = 0;
            s.FloraSource = -1;
            return s;
        }

        /// <summary>
        ///     Biome-first height (§8.2, §5.7): Offset, a_i and J are weighted sums of the per-biome constants with
        ///     <paramref name="weights" />, then H = Offset + Σ a_i N_i + J P, clamped to [Hmin, Hmax].
        ///     <paramref name="clamped" /> reports whether the clamp engaged (GEN-12).
        /// </summary>
        [BurstCompile]
        public static float SampleBiomeHeight(in float2 p, in BlendWeights weights, in WorldGenConfig cfg, out bool clamped){
            int biomeCount = cfg.BiomeCount;
            int octaveCount = cfg.OctaveCount;

            float offset = 0f;
            float jaggedness = 0f;
            for (int b = 0; b < biomeCount; b++){
                float w = weights[b];
                if (w == 0f) continue;
                offset += w * cfg.Biomes[b].heightOffset;
                jaggedness += w * cfg.Biomes[b].jaggedness;
            }

            float height = offset;
            for (int i = 0; i < octaveCount; i++){
                float a = 0f;
                for (int b = 0; b < biomeCount; b++){
                    float w = weights[b];
                    if (w != 0f) a += w * cfg.OctaveCoefficients[b * octaveCount + i];
                }

                if (a != 0f) height += a * DetailOctaves.Sample(i, p, cfg.OctaveWavelengths[i], cfg.Seed);
            }

            if (jaggedness != 0f) height += jaggedness * PeakNoise.Sample(p, cfg.PeakWavelength, cfg.Seed);

            float result = math.clamp(height, cfg.HeightMin, cfg.HeightMax);
            clamped = result != height;
            return result;
        }
    }
}
