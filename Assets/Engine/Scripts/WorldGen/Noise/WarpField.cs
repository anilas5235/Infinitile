using Engine.Scripts.WorldGen.Core;
using Engine.Scripts.WorldGen.Settings;
using Unity.Burst;
using Unity.Mathematics;

namespace Engine.Scripts.WorldGen.Noise{
    /// <summary>
    ///     Boundary warp field Ω of method 3, DistortedBoundary: two independent simplex fBm channels
    ///     (salts <see cref="HashSalts.BlendWarpX" />, <see cref="HashSalts.BlendWarpY" />) at wavelength λ_d with
    ///     <c>WarpOctaves</c> octaves (default 2, persistence 0.5, lacunarity 2), normalised to [−1,1]. The
    ///     displacement d(q) = A·Ω(q) moves only the classification position; both blend stages use the
    ///     same field and the same amplitude A.
    /// </summary>
    [BurstCompile]
    public static class WarpField{
        /// <summary>Ω(p) ∈ [−1,1]² at world position <paramref name="p" /> (blocks).</summary>
        public static float2 Sample(float2 p, in WorldGenConfig cfg){
            float2 q = p / cfg.WarpWavelength;
            float2 sum = 0f;
            float amplitude = 1f;
            float frequency = 1f;
            float maxAmplitude = 0f;
            for (int i = 0; i < cfg.WarpOctaves; i++){
                float2 o = q * frequency;
                sum.x += amplitude * SeededSimplex.Noise(o, cfg.Seed, HashSalts.FbmOctave(HashSalts.BlendWarpX, i));
                sum.y += amplitude * SeededSimplex.Noise(o, cfg.Seed, HashSalts.FbmOctave(HashSalts.BlendWarpY, i));
                maxAmplitude += amplitude;
                amplitude *= 0.5f;
                frequency *= 2f;
            }

            return maxAmplitude > 0f ? math.clamp(sum / maxAmplitude, -1f, 1f) : float2.zero;
        }

        /// <summary>
        ///     The warped classification position q + A·Ω(q). With A = 0 the warp is skipped and q returned
        ///     unchanged, which is exactly what q + 0·Ω would give.
        /// </summary>
        public static float2 Warp(float2 q, in WorldGenConfig cfg){
            if (cfg.WarpAmplitude == 0f) return q;
            return q + cfg.WarpAmplitude * Sample(q, cfg);
        }
    }
}
