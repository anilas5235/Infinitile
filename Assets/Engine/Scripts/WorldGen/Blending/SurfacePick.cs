using Engine.Scripts.WorldGen.Core;
using Unity.Burst;

namespace Engine.Scripts.WorldGen.Blending{
    /// <summary>
    ///     Dithered pick from a weight vector (§9.6, BLD-40): u = Hash01(x, z, seed, salt), S = Σ w, result = smallest
    ///     b with Σ_{c≤b} w_c &gt; u·S, falling back to the last b with w_b &gt; 0 against rounding (TST-14). White-noise
    ///     hashing per column, no ordered dithering (it would imprint a grid). One-hot weights return their biome
    ///     (BLD-41). The surface pick uses <see cref="HashSalts.SurfacePick" />; the flora biome pick (VEG-02) uses the
    ///     same routine with its own salt.
    /// </summary>
    [BurstCompile]
    public static class SurfacePick{
        /// <summary>Picked biome index, or -1 when every weight is zero (no active biome).</summary>
        public static int Pick(int x, int z, uint seed, uint salt, in BlendWeights weights, int count){
            float sum = 0f;
            for (int b = 0; b < count; b++) sum += weights[b];
            float threshold = Hash.Hash01(x, z, seed, salt) * sum;

            // Zero weights are skipped: adding 0 leaves the running sum unchanged, and a zero-weight biome is never picked.
            float cumulative = 0f;
            int last = -1;
            for (int b = 0; b < count; b++){
                float w = weights[b];
                if (!(w > 0f)) continue;
                cumulative += w;
                last = b;
                if (cumulative > threshold) return b;
            }

            return last;
        }
    }
}
