using System.Runtime.CompilerServices;
using Engine.Scripts.WorldGen.Core;
using Engine.Scripts.WorldGen.Noise;
using Unity.Burst;
using Unity.Mathematics;

namespace Engine.Scripts.WorldGen.Blending{
    /// <summary>
    ///     Dithered pick from a weight vector: u = Hash01(x, z, seed, salt), S = Σ w, result = smallest
    ///     b with Σ_{c≤b} w_c &gt; u·S, falling back to the last b with w_b &gt; 0 against rounding. White-noise
    ///     hashing per column, no ordered dithering (it would imprint a grid). One-hot weights return their biome.
    ///     The flora biome pick uses this routine with its own salt. The surface pick
    ///     (<see cref="PickPatchy" />) replaces the white noise by a low-frequency noise, so two materials meet in
    ///     tongues and patches instead of a per-block mix.
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

        /// <summary>Share of the broad octave in the border noise; the rest is the detail octave.</summary>
        private const float BroadWeight = .7f;

        /// <summary>Frequency of the detail octave relative to the broad one.</summary>
        private const float DetailFrequency = 3f;

        /// <summary>Spread of the border noise around .5; larger moves the border further through the blend zone.</summary>
        private const float Gain = .8f;

        /// <summary>
        ///     Bounds of the threshold: a biome whose weight share is below <see cref="MinThreshold" /> never takes a
        ///     column, so no patch lies at the far edge of a blend zone.
        /// </summary>
        public const float MinThreshold = .04f, MaxThreshold = .96f;

        /// <summary>
        ///     The surface pick: like <see cref="Pick" />, but the threshold is <see cref="Threshold01" />, a noise of
        ///     wavelength <paramref name="wavelength" /> in blocks. Neighbouring columns share nearly the same
        ///     threshold, so the border between two materials is the contour where the weight crosses it: a wavy line
        ///     with tongues and islands inside the blend zone. Columns with one biome never evaluate the noise.
        ///     Wavelength 0 is the white-noise dither of <see cref="Pick" /> with <see cref="HashSalts.SurfacePick" />.
        /// </summary>
        public static int PickPatchy(int x, int z, uint seed, float wavelength, in BlendWeights weights, int count){
            float sum = 0f;
            int positive = 0, last = -1;
            for (int b = 0; b < count; b++){
                float w = weights[b];
                if (!(w > 0f)) continue;
                sum += w;
                positive++;
                last = b;
            }

            if (positive <= 1) return last;
            float threshold = Threshold01(x, z, seed, wavelength) * sum;
            float cumulative = 0f;
            for (int b = 0; b < count; b++){
                float w = weights[b];
                if (!(w > 0f)) continue;
                cumulative += w;
                if (cumulative > threshold) return b;
            }

            return last;
        }

        /// <summary>
        ///     Threshold of the surface pick in 0…1: .5 plus two octaves of simplex noise (the broad one at
        ///     <paramref name="wavelength" /> blocks, a detail octave at a third of it), clamped to
        ///     <see cref="MinThreshold" />…<see cref="MaxThreshold" />. Symmetric around .5, so two biomes keep equal
        ///     shares of their blend zone. Wavelength 0: white noise per column. A separate method, so the sampling
        ///     stays small.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static float Threshold01(int x, int z, uint seed, float wavelength){
            if (!(wavelength > 0f)) return Hash.Hash01(x, z, seed, HashSalts.SurfacePick);
            float2 p = new float2(x, z) / wavelength;
            float n = BroadWeight * SeededSimplex.Noise(p, seed, HashSalts.MaterialBorder) +
                      (1f - BroadWeight) * SeededSimplex.Noise(p * DetailFrequency, seed, HashSalts.MaterialBorderDetail);
            return math.clamp(.5f + Gain * n, MinThreshold, MaxThreshold);
        }
    }
}
