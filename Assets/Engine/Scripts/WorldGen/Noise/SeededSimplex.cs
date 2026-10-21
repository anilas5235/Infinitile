using Engine.Scripts.WorldGen.Core;
using Unity.Burst;
using Unity.Mathematics;

namespace Engine.Scripts.WorldGen.Noise{
    /// <summary>
    ///     Seeded 2D simplex noise. Lattice gradients come from
    ///     <see cref="Hash.Hash32" />(i, j, seed, salt), so there is no permutation table and therefore no period:
    ///     Unity's <c>noise.snoise</c>/<c>cnoise</c> use a mod-289 permutation and tile every 289 noise units.
    ///     One <c>salt</c> per noise instance keeps the fields T, F, C, E, W, each detail octave, P and the warp
    ///     fields decorrelated without hand-picked position offsets.
    ///     <para>
    ///         Skew and corner evaluation follow Stefan Gustavson, "Simplex noise demystified" (2005), with the
    ///         8-direction 2D gradient set; the gradient hash is our own extension.
    ///     </para>
    ///     <para>
    ///         Precision: positions are float32. Beyond |x|, |z| ≈ 1e5–1e6 noise units the fractional part
    ///         of the sample position loses bits, so features start to stair-step. No special handling is done
    ///         for play; a world that large is not a target.
    ///     </para>
    /// </summary>
    [BurstCompile]
    public static class SeededSimplex{
        private const float SkewF2 = 0.3660254037844386f; // (sqrt(3) - 1) / 2
        private const float UnskewG2 = 0.21132486540518713f; // (3 - sqrt(3)) / 6

        /// <summary>
        ///     Scales the kernel sum to roughly [-1, 1]; Gustavson's value for the 2D kernel (0.5 - r²)⁴ with the
        ///     8-direction gradient set. The result is clamped so callers can rely on the range exactly.
        /// </summary>
        private const float OutputScale = 70f;

        /// <summary>Zero-centred simplex noise in [-1, 1] at <paramref name="p" /> (noise units) for one instance.</summary>
        public static float Noise(float2 p, uint seed, uint salt){
            float s = (p.x + p.y) * SkewF2;
            float2 skewed = p + s;
            float2 cellF = math.floor(skewed);
            int2 cell = (int2)cellF;

            float t = (cellF.x + cellF.y) * UnskewG2;
            float2 d0 = p - (cellF - t); // offset from the first (origin) corner

            int2 step = d0.x > d0.y ? new int2(1, 0) : new int2(0, 1); // lower or upper triangle
            float2 d1 = d0 - step + UnskewG2;
            float2 d2 = d0 - 1f + 2f * UnskewG2;

            float n = Corner(d0, cell, seed, salt)
                      + Corner(d1, cell + step, seed, salt)
                      + Corner(d2, cell + 1, seed, salt);

            return math.clamp(OutputScale * n, -1f, 1f);
        }

        private static float Corner(float2 d, int2 cell, uint seed, uint salt){
            float t = 0.5f - math.lengthsq(d);
            if (t < 0f) return 0f;
            t *= t;
            return t * t * math.dot(Gradient(Hash.Hash32(cell.x, cell.y, seed, salt)), d);
        }

        /// <summary>
        ///     One of the 8 directions (±1, ±1), (±1, 0), (0, ±1) picked from the top three (best mixed) hash bits.
        /// </summary>
        private static float2 Gradient(uint hash){
            uint g = hash >> 29;
            float s0 = (g & 1u) == 0u ? 1f : -1f;
            float s1 = (g & 2u) == 0u ? 1f : -1f;
            if ((g & 4u) == 0u) return new float2(s0, s1);
            return (g & 1u) == 0u ? new float2(s1, 0f) : new float2(0f, s1);
        }
    }
}
