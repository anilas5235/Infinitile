using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;

namespace Engine.Scripts.WorldGen.Blending{
    /// <summary>One kernel tap of the distance blend (§9.3): a lattice offset from the column and its weight k(r).</summary>
    public struct KernelTap{
        /// <summary>q − p in blocks; q lies on the world-aligned lattice of stride s (GEN-03).</summary>
        public int2 Offset;

        /// <summary>k(|q − p|) = (1 − r²/R²)², strictly positive (taps with r ≥ R are not stored).</summary>
        public float K;
    }

    /// <summary>
    ///     Precomputed kernel of one blend stage (BLD-10, BLD-12). The column p sees the lattice points
    ///     q = (s·i, s·j) with |q − p| &lt; R; which offsets those are depends only on the phase (p mod s), so the tap
    ///     list is stored once per phase (s² phases, at most 4) in a fixed order. The cached chunk path and the pure
    ///     sampler walk the same list, so the summation order and the float result are identical (GEN-02).
    /// </summary>
    public struct KernelSet{
        /// <summary>Lattice stride s = clamp(floor(R/4), 1, 2) (BLD-10).</summary>
        public int Stride;

        /// <summary>Kernel radius R = B/2 in blocks.</summary>
        public float Radius;

        /// <summary>max |offset| over all taps and both axes; the padding a cached lattice needs around a column.</summary>
        public int Reach;

        /// <summary>First tap of phase φ = (x mod s) + s·(z mod s) in the tap array.</summary>
        public int4 PhaseStart;

        /// <summary>Number of taps of phase φ.</summary>
        public int4 PhaseCount;

        /// <summary>False for a stage that does not use a spatial kernel.</summary>
        public bool IsCreated => Stride > 0;

        /// <summary>Phase index of column (<paramref name="x" />, <paramref name="z" />) (floor modulo, also for negative x/z).</summary>
        public int Phase(int x, int z){
            int s = Stride;
            int px = (x % s + s) % s;
            int pz = (z % s + s) % s;
            return px + s * pz;
        }

        /// <summary>Lattice stride for a blend width <paramref name="blendWidth" /> (BLD-10).</summary>
        public static int StrideFor(float blendWidth){
            float radius = blendWidth * 0.5f;
            return math.clamp((int)math.floor(radius / 4f), 1, 2);
        }

        /// <summary>
        ///     Builds the taps of a kernel with transition width <paramref name="blendWidth" /> (B, R = B/2) and
        ///     appends them to a new array (BLD-12; once per settings change, in <c>WorldGenSettings.ToConfig</c>).
        ///     k(r) = max(0, 1 − r²/R²)² (§9.3); r² is an exact integer, so every k is reproducible.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">B is not positive and finite.</exception>
        public static KernelSet Build(float blendWidth, Allocator allocator, out NativeArray<KernelTap> taps){
            if (!(blendWidth > 0f) || float.IsInfinity(blendWidth))
                throw new ArgumentOutOfRangeException(nameof(blendWidth), blendWidth, "The blend width B must be positive and finite.");

            float radius = blendWidth * 0.5f;
            float radiusSq = radius * radius;
            int stride = StrideFor(blendWidth);
            int bound = (int)math.ceil(radius) + stride;

            KernelSet set = new(){ Stride = stride, Radius = radius };
            List<KernelTap> list = new();
            for (int phase = 0; phase < stride * stride; phase++){
                int px = phase % stride;
                int pz = phase / stride;
                set.PhaseStart[phase] = list.Count;
                // Row-major over the lattice (z outer, x inner): the fixed tap order of BLD-12.
                for (int dz = -bound; dz <= bound; dz++){
                    if (((dz + pz) % stride + stride) % stride != 0) continue; // q.z = p.z + dz must be a multiple of s
                    for (int dx = -bound; dx <= bound; dx++){
                        if (((dx + px) % stride + stride) % stride != 0) continue;
                        int rSq = dx * dx + dz * dz;
                        if (rSq >= radiusSq) continue; // |q − p| < R
                        float t = 1f - rSq / radiusSq;
                        list.Add(new KernelTap{ Offset = new int2(dx, dz), K = t * t });
                        set.Reach = math.max(set.Reach, math.max(math.abs(dx), math.abs(dz)));
                    }
                }

                set.PhaseCount[phase] = list.Count - set.PhaseStart[phase];
                if (set.PhaseCount[phase] == 0)
                    throw new ArgumentOutOfRangeException(nameof(blendWidth), blendWidth,
                        $"The blend width B = {blendWidth} leaves phase {phase} without a lattice point inside R.");
            }

            taps = new NativeArray<KernelTap>(list.ToArray(), allocator);
            return set;
        }
    }
}
