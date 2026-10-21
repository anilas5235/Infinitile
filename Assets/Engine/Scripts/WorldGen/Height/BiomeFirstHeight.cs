using Engine.Scripts.WorldGen.Blending;
using Engine.Scripts.WorldGen.Noise;
using Engine.Scripts.WorldGen.Settings;
using Unity.Burst;
using Unity.Mathematics;

namespace Engine.Scripts.WorldGen.Height{
    /// <summary>
    ///     Biome-first height. The height-stage weights w_b blend the per-biome constants:
    ///     Offset = Σ w_b·Offset_b, a_i = Σ w_b·a_{b,i}, J = Σ w_b·J_b, with the effective octave
    ///     coefficients a_{b,i} = HeightAmplitude_b × OctaveWeights_{b,i} that <c>WorldGenSettings.ToConfig</c>
    ///     flattened. Then H = Offset + Σ_i a_i·N_i + J·P with one evaluation of every N_i and of P per
    ///     column: all biomes sample the same shared noises, only their coefficients differ. The result
    ///     equals Σ w_b·H_b by linearity, without evaluating any H_b. Frequencies are never blended:
    ///     the octave wavelengths are global.
    ///     <para>
    ///         With one-hot weights (method None) H is the hard biome's own formula and the height jumps
    ///         at biome boundaries; the height-stage blend methods turn the weights continuous.
    ///     </para>
    /// </summary>
    [BurstCompile]
    public static class BiomeFirstHeight{
        /// <summary>
        ///     H at world position <paramref name="p" /> for the height-stage <paramref name="weights" />.
        ///     <paramref name="height" /> is H clamped to [Hmin, Hmax]; <paramref name="unclamped" /> is H before the
        ///     clamp, so the clamp engaged exactly when the two differ. Burst direct-call entry point.
        /// </summary>
        [BurstCompile(CompileSynchronously = true, FloatMode = FloatMode.Strict)]
        public static void Sample(in float2 p, in BlendWeights weights, in WorldGenConfig cfg,
            out float height, out float unclamped){
            int biomeCount = cfg.BiomeCount;
            int octaveCount = cfg.OctaveCount;

            // Blended Offset and J.
            float offset = 0f;
            float jaggedness = 0f;
            for (int b = 0; b < biomeCount; b++){
                float w = weights[b];
                if (w == 0f) continue;
                offset += w * cfg.Biomes[b].heightOffset;
                jaggedness += w * cfg.Biomes[b].jaggedness;
            }

            // Blended a_i, then exactly one N_i sample per octave for the whole column.
            float h = offset;
            for (int i = 0; i < octaveCount; i++){
                float a = 0f;
                for (int b = 0; b < biomeCount; b++){
                    float w = weights[b];
                    if (w != 0f) a += w * cfg.OctaveCoefficients[b * octaveCount + i];
                }

                if (a != 0f) h += a * DetailOctaves.Sample(i, p, cfg.OctaveWavelengths[i], cfg.Seed);
            }

            // One P sample per column.
            if (jaggedness != 0f) h += jaggedness * PeakNoise.Sample(p, cfg.PeakWavelength, cfg.Seed);

            unclamped = h;
            height = math.clamp(h, cfg.HeightMin, cfg.HeightMax);
        }

        /// <inheritdoc cref="Sample(in float2,in BlendWeights,in WorldGenConfig,out float,out float)" />
        /// <param name="clamped">Whether the clamp to [Hmin, Hmax] engaged.</param>
        public static float Sample(in float2 p, in BlendWeights weights, in WorldGenConfig cfg, out bool clamped){
            Sample(p, weights, cfg, out float height, out float unclamped);
            clamped = height != unclamped;
            return height;
        }
    }
}
