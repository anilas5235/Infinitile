using Engine.Scripts.WorldGen.Core;
using Engine.Scripts.WorldGen.Settings;
using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;

namespace Engine.Scripts.WorldGen.Blending{
    /// <summary>
    ///     The four blend methods (§9), one per <see cref="BlendMethod" />, selected by an enum switch (R2). Every
    ///     method returns a normalised weight vector over the active biomes, Σ w = 1 (± 1e−5), w ≥ 0 (BLD-02).
    ///     <list type="bullet">
    ///         <item>1 None: one-hot hard class (§9.2).</item>
    ///         <item>2 DistanceBlend: compact kernel k(r) = (1 − r²/R²)² over nn(q) on a world-aligned lattice (§9.3).</item>
    ///         <item>
    ///             3 DistortedBoundary: classification at the warped position q + A·Ω(q) (§9.4). Material stage: warp
    ///             only, one-hot nn~(p). Height stage: warp + distance blend, i.e. the kernel of method 2 over nn~ with
    ///             the same B_hgt — a combination of two techniques after Fischer et al., "AutoBiomes" (BLD-22).
    ///         </item>
    ///         <item>4 ParameterSpaceWeighting: soft nearest neighbour, Gaussian over the parameter-space distance (§9.5).</item>
    ///     </list>
    /// </summary>
    [BurstCompile]
    public static class BlendMethods{
        /// <summary>
        ///     Weights of one stage at column <paramref name="p" /> (BLD-02). <paramref name="hard" /> and
        ///     <paramref name="distancesSq" /> are the column's own hard class and squared parameter-space distances
        ///     (computed once and shared, PERF-07). <paramref name="cache" /> may be default (pure path).
        /// </summary>
        public static BlendWeights Compute(BlendStage stage, BlendMethod method, int2 p, int hard,
            in BlendWeights distancesSq, in WorldGenConfig cfg, in BlendCache cache){
            if (hard < 0) return default; // no active biome
            bool height = stage == BlendStage.Height;
            switch (method){
                case BlendMethod.DistanceBlend:
                    return height
                        ? Kernel(p, cfg.HeightKernel, cfg.HeightTaps, cache.Unwarped, false, cfg)
                        : Kernel(p, cfg.MaterialKernel, cfg.MaterialTaps, cache.Unwarped, false, cfg);
                case BlendMethod.DistortedBoundary:
                    return height
                        ? Kernel(p, cfg.HeightKernel, cfg.HeightTaps, cache.Warped, true, cfg)
                        : BlendWeights.OneHot(WarpedClass(p, hard, cfg, cache.Warped));
                case BlendMethod.ParameterSpaceWeighting:
                    return ParameterSpace(distancesSq, hard, height ? cfg.HeightSigma : cfg.MaterialSigma, cfg.BiomeCount);
                default:
                    return BlendWeights.OneHot(hard);
            }
        }

        /// <summary>
        ///     Method 4 (§9.5, BLD-30): w_b = exp(−(d_b² − d_min²)/(2σ²)) / Σ_c exp(−(d_c² − d_min²)/(2σ²)), the shifted
        ///     form, so the hard class always gets exp(0) = 1 and argmax w = hard class (BLD-31). No neighbourhood
        ///     lookups.
        /// </summary>
        public static BlendWeights ParameterSpace(in BlendWeights distancesSq, int hard, float sigma, int count){
            float dMinSq = distancesSq[hard];
            float twoSigmaSq = 2f * sigma * sigma;
            BlendWeights w = default;
            float sum = 0f;
            for (int b = 0; b < count; b++){
                float e = math.exp(-(distancesSq[b] - dMinSq) / twoSigmaSq);
                w[b] = e;
                sum += e;
            }

            for (int b = 0; b < count; b++) w[b] /= sum;
            return w;
        }

        /// <summary>
        ///     Kernel blend (§9.3, also the method-3 height stage): w_b = Σ k·[nn(q) = b] / Σ k over the phase's taps.
        ///     The taps are read from <paramref name="lattice" /> when it covers the window (BLD-11) and evaluated
        ///     directly otherwise; both walk the same tap list in the same order (BLD-12), so the results are
        ///     bit-identical (GEN-02). A window without a boundary point returns one-hot weights without the sum
        ///     (PERF-07), which equals the full sum exactly.
        /// </summary>
        public static BlendWeights Kernel(int2 p, in KernelSet kernel, in NativeArray<KernelTap> taps,
            in BlendLattice lattice, bool warped, in WorldGenConfig cfg){
            int phase = kernel.Phase(p.x, p.y);
            int start = kernel.PhaseStart[phase];
            int end = start + kernel.PhaseCount[phase];

            BlendWeights acc = default;
            float total = 0f;
            if (lattice.Covers(p, kernel.Reach)){
                if (lattice.IsUniform(p, kernel.Reach, out int uniform)) return BlendWeights.OneHot(uniform);
                for (int t = start; t < end; t++){
                    KernelTap tap = taps[t];
                    Accumulate(ref acc, ref total, lattice.ClassAt(p + tap.Offset), tap.K);
                }
            }
            else{
                for (int t = start; t < end; t++){
                    KernelTap tap = taps[t];
                    float2 q = p + tap.Offset;
                    int cls = warped ? WorldSampler.ClassifyWarpedAt(q, cfg) : WorldSampler.ClassifyAt(q, cfg);
                    Accumulate(ref acc, ref total, cls, tap.K);
                }
            }

            int count = cfg.BiomeCount;
            for (int b = 0; b < count; b++) acc[b] /= total;
            return acc;
        }

        /// <summary>The one accumulation step both kernel paths share, so their float sums are identical.</summary>
        private static void Accumulate(ref BlendWeights acc, ref float total, int cls, float k){
            acc[cls] += k;
            total += k;
        }

        /// <summary>
        ///     nn~(p) for the method-3 material stage: from the warped lattice when it holds p, otherwise evaluated;
        ///     with A = 0 the warp is the identity, so nn~(p) is the column's own hard class (PERF-07).
        /// </summary>
        private static int WarpedClass(int2 p, int hard, in WorldGenConfig cfg, in BlendLattice lattice){
            if (cfg.WarpAmplitude == 0f) return hard;
            if (lattice.Contains(p)) return lattice.ClassAt(p);
            return WorldSampler.ClassifyWarpedAt(p, cfg);
        }
    }
}
