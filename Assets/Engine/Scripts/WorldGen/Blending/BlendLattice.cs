using System;
using Engine.Scripts.WorldGen.Core;
using Engine.Scripts.WorldGen.Settings;
using Unity.Collections;
using Unity.Mathematics;

namespace Engine.Scripts.WorldGen.Blending{
    /// <summary>
    ///     Padded, world-aligned lattice of hard classes nn(q) (or nn~(q) for the warped kind) over a block of
    ///     columns: points q = Origin + (i, j)·Stride, each classified once by
    ///     <see cref="WorldSampler.ClassifyAt" /> / <see cref="WorldSampler.ClassifyWarpedAt" />, the same functions the
    ///     pure sampler calls per tap, so a cached tap equals the pure one bit for bit.
    ///     <para>
    ///         Early-out: a lattice point is a boundary point when one of its 8 neighbours (inside the
    ///         lattice) has another class; <see cref="BoundarySat" /> is the summed-area table of that mask. A kernel
    ///         window whose bounding box holds no boundary point is a single class (the box is connected and lies
    ///         inside the lattice, so two different classes in it would need two differing neighbours inside the
    ///         lattice, both flagged), and the kernel blend of a single class is exactly one-hot (Σk / Σk = 1). No
    ///         margin beyond the kernel reach is therefore needed.
    ///     </para>
    ///     Lives in Allocator.Temp inside the job that builds it.
    /// </summary>
    public struct BlendLattice : IDisposable{
        /// <summary>World position of element (0, 0); a multiple of <see cref="Stride" />.</summary>
        public int2 Origin;

        /// <summary>Points along x and z.</summary>
        public int2 Size;

        /// <summary>Lattice stride in blocks; divides the stride of every kernel that reads it.</summary>
        public int Stride;

        /// <summary>Class per point, element [j × Size.x + i].</summary>
        public NativeArray<sbyte> Classes;

        /// <summary>Summed-area table of the boundary mask, (Size.x + 1) × (Size.y + 1), row 0 and column 0 zero.</summary>
        public NativeArray<int> BoundarySat;

        public bool IsCreated => Classes.IsCreated;

        /// <summary>
        ///     Classifies every lattice point that a kernel with reach <paramref name="reach" /> can touch from a
        ///     column in [<paramref name="minColumn" />, <paramref name="maxColumn" />] (inclusive).
        /// </summary>
        public static BlendLattice Build(int2 minColumn, int2 maxColumn, int reach, int stride, bool warped,
            in WorldGenConfig cfg, Allocator allocator){
            int2 lo = FloorDiv(minColumn - reach, stride);
            int2 hi = FloorDiv(maxColumn + reach, stride);
            BlendLattice lattice = new(){
                Origin = lo * stride,
                Size = hi - lo + 1,
                Stride = stride
            };
            int count = lattice.Size.x * lattice.Size.y;
            lattice.Classes = new NativeArray<sbyte>(count, allocator, NativeArrayOptions.UninitializedMemory);
            lattice.BoundarySat = new NativeArray<int>((lattice.Size.x + 1) * (lattice.Size.y + 1), allocator);

            for (int j = 0; j < lattice.Size.y; j++)
            for (int i = 0; i < lattice.Size.x; i++){
                float2 q = lattice.Origin + new int2(i, j) * stride;
                int cls = warped ? WorldSampler.ClassifyWarpedAt(q, cfg) : WorldSampler.ClassifyAt(q, cfg);
                lattice.Classes[j * lattice.Size.x + i] = (sbyte)cls;
            }

            lattice.BuildBoundarySat();
            return lattice;
        }

        private void BuildBoundarySat(){
            int w = Size.x, h = Size.y, satW = w + 1;
            for (int j = 0; j < h; j++){
                int rowSum = 0;
                for (int i = 0; i < w; i++){
                    sbyte c = Classes[j * w + i];
                    int boundary = 0;
                    for (int dj = -1; dj <= 1 && boundary == 0; dj++){
                        int nj = j + dj;
                        if (nj < 0 || nj >= h) continue;
                        for (int di = -1; di <= 1; di++){
                            int ni = i + di;
                            if (ni < 0 || ni >= w) continue;
                            if (Classes[nj * w + ni] != c){
                                boundary = 1;
                                break;
                            }
                        }
                    }

                    rowSum += boundary;
                    BoundarySat[(j + 1) * satW + i + 1] = BoundarySat[j * satW + i + 1] + rowSum;
                }
            }
        }

        /// <summary>True when every lattice point within <paramref name="reach" /> blocks of <paramref name="p" /> is stored.</summary>
        public bool Covers(int2 p, int reach){
            if (!Classes.IsCreated) return false;
            int2 lo = FloorDiv(p - reach - Origin, Stride);
            int2 hi = FloorDiv(p + reach - Origin, Stride);
            return math.all(lo >= 0) && math.all(hi < Size);
        }

        /// <summary>Class at lattice-aligned world position <paramref name="q" /> (must be covered).</summary>
        public int ClassAt(int2 q){
            int2 ij = (q - Origin) / Stride;
            return Classes[ij.y * Size.x + ij.x];
        }

        /// <summary>
        ///     True and the class when no boundary point lies in the bounding box of the window
        ///     [p − reach, p + reach]. The window must be covered.
        /// </summary>
        public bool IsUniform(int2 p, int reach, out int cls){
            int2 lo = FloorDiv(p - reach - Origin, Stride);
            int2 hi = FloorDiv(p + reach - Origin, Stride);
            int satW = Size.x + 1;
            int boundaries = BoundarySat[(hi.y + 1) * satW + hi.x + 1] - BoundarySat[lo.y * satW + hi.x + 1]
                             - BoundarySat[(hi.y + 1) * satW + lo.x] + BoundarySat[lo.y * satW + lo.x];
            cls = Classes[lo.y * Size.x + lo.x];
            return boundaries == 0;
        }

        /// <summary>True when world position <paramref name="q" /> is a lattice point inside this lattice.</summary>
        public bool Contains(int2 q){
            if (!Classes.IsCreated) return false;
            int2 d = q - Origin;
            if (math.any(d < 0) || d.x % Stride != 0 || d.y % Stride != 0) return false;
            return math.all(d / Stride < Size);
        }

        private static int2 FloorDiv(int2 value, int divisor){
            return new int2(FloorDiv(value.x, divisor), FloorDiv(value.y, divisor));
        }

        private static int FloorDiv(int value, int divisor){
            int q = value / divisor;
            return q * divisor > value ? q - 1 : q; // divisor > 0: truncation rounded a negative quotient up
        }

        public void Dispose(){
            if (Classes.IsCreated) Classes.Dispose();
            if (BoundarySat.IsCreated) BoundarySat.Dispose();
        }
    }

    /// <summary>
    ///     The lattices a block of columns needs for the configured spatial methods: one unwarped
    ///     lattice of nn(q) for DistanceBlend (material and/or height stage, shared when both use it) and one warped
    ///     lattice of nn~(q) for the DistortedBoundary height stage. Built per chunk by <c>ChunkJob</c> and per tile by
    ///     <see cref="WorldGenApi" />; a default instance means "no cache", and the sampler then evaluates every tap
    ///     itself (the pure path) with the same result.
    /// </summary>
    public struct BlendCache : IDisposable{
        public BlendLattice Unwarped;
        public BlendLattice Warped;

        /// <summary>
        ///     Builds the lattices for the columns in [<paramref name="minColumn" />, <paramref name="maxColumn" />],
        ///     sampled every <paramref name="columnStep" /> blocks. A lattice is skipped when the columns are so far
        ///     apart that the pure per-tap evaluation is cheaper ((step / s)² lattice points per column vs. the tap
        ///     count), e.g. for a zoomed-out preview; the result is the same either way.
        /// </summary>
        public static BlendCache Build(in WorldGenConfig cfg, int2 minColumn, int2 maxColumn, int columnStep, Allocator allocator){
            BlendCache cache = default;
            if (cfg.BiomeCount == 0) return cache;

            bool biomeFirst = cfg.Architecture == GenArchitecture.BiomeFirst;
            int unwarpedStride = int.MaxValue, unwarpedReach = 0, unwarpedTaps = int.MaxValue;
            int warpedStride = int.MaxValue, warpedReach = 0, warpedTaps = int.MaxValue;

            if (cfg.MaterialMethod == BlendMethod.DistanceBlend)
                Add(cfg.MaterialKernel, ref unwarpedStride, ref unwarpedReach, ref unwarpedTaps);
            if (biomeFirst && cfg.HeightMethod == BlendMethod.DistanceBlend)
                Add(cfg.HeightKernel, ref unwarpedStride, ref unwarpedReach, ref unwarpedTaps);
            if (biomeFirst && cfg.HeightMethod == BlendMethod.DistortedBoundary)
                Add(cfg.HeightKernel, ref warpedStride, ref warpedReach, ref warpedTaps);

            if (Worthwhile(unwarpedStride, unwarpedTaps, columnStep))
                cache.Unwarped = BlendLattice.Build(minColumn, maxColumn, unwarpedReach, unwarpedStride, false, cfg, allocator);
            if (Worthwhile(warpedStride, warpedTaps, columnStep))
                cache.Warped = BlendLattice.Build(minColumn, maxColumn, warpedReach, warpedStride, true, cfg, allocator);
            return cache;
        }

        private static void Add(in KernelSet kernel, ref int stride, ref int reach, ref int taps){
            // A stride-1 lattice holds every stride-2 point, so the finer stride serves both kernels.
            stride = math.min(stride, kernel.Stride);
            reach = math.max(reach, kernel.Reach);
            taps = math.min(taps, kernel.PhaseCount[0]);
        }

        private static bool Worthwhile(int stride, int taps, int columnStep){
            if (stride == int.MaxValue) return false;
            float pointsPerColumn = (float)columnStep / stride;
            return pointsPerColumn * pointsPerColumn < taps;
        }

        public void Dispose(){
            Unwarped.Dispose();
            Warped.Dispose();
        }
    }
}
