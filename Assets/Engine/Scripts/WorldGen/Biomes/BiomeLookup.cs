using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;

namespace Engine.Scripts.WorldGen.Biomes{
    /// <summary>
    ///     The compiled biome lookup (§6.3): a grid whose lines are the box edges, so every cell lies inside boxes or in a
    ///     gap. One binary search per parameter finds the cell; a cell inside boxes holds the winning box (the box of the
    ///     latest data source), a gap cell the boxes that can be nearest somewhere in it. The result equals
    ///     <see cref="BruteForce" /> on every point (W2-BIO-03). Burst and managed code give the same bits: the lookup
    ///     only compares, adds and multiplies floats.
    /// </summary>
    public unsafe struct BiomeLookup : IDisposable{
        /// <summary>Most grid cells (§6.3): 2^20.</summary>
        public const int MaxCells = 1 << 20;

        /// <summary>Sorted box edges per parameter, parameter after parameter.</summary>
        [ReadOnly] public NativeArray<float> Edges;

        /// <summary>[0…4] first edge, [5…9] edge count, [10…14] cell stride per parameter.</summary>
        [ReadOnly] public NativeArray<int> Axes;

        /// <summary>Per cell: the winning box (≥ 0) or −(gap + 1).</summary>
        [ReadOnly] public NativeArray<int> Cells;

        /// <summary>Per gap: first candidate in <see cref="GapBoxes" /> and the count.</summary>
        [ReadOnly] public NativeArray<int2> Gaps;

        [ReadOnly] public NativeArray<int> GapBoxes;

        /// <summary>Per box: its biome id.</summary>
        [ReadOnly] public NativeArray<byte> BoxBiome;

        /// <summary>Per box: 5 minima and 5 maxima (open ends as infinities).</summary>
        [ReadOnly] public NativeArray<float> BoxMin, BoxMax;

        /// <summary>Per box: the boxes of later sources that overlap it (first in <see cref="WinnerBoxes" />, count).</summary>
        [ReadOnly] public NativeArray<int2> Winners;

        [ReadOnly] public NativeArray<int> WinnerBoxes;

        /// <summary>Per box: its data source (for the brute force: the latest source wins).</summary>
        [ReadOnly] public NativeArray<int> BoxSource;

        public int BoxCount;

        public bool IsCreated => Cells.IsCreated;

        /// <summary>The box at the climate point <paramref name="p" /> (5 values), −1 when the layout has no box.</summary>
        public readonly int Lookup(float* p){
            if (BoxCount == 0) return -1;
            int cell = 0;
            for (int d = 0; d < BiomeBox.Parameters; d++) cell += Interval(d, p[d]) * Axes[10 + d];
            int c = Cells[cell];
            if (c >= 0) return c;
            int2 gap = Gaps[-c - 1];
            int best = -1;
            float bestDistance = 0f;
            for (int i = gap.x; i < gap.x + gap.y; i++){
                int box = GapBoxes[i];
                float distance = DistanceSq(box, p);
                if (best < 0 || Better(box, distance, best, bestDistance)) {
                    best = box;
                    bestDistance = distance;
                }
            }

            return best;
        }

        /// <summary>The biome id at <paramref name="p" />, or 255 without boxes.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly byte BiomeAt(float* p){
            int box = Lookup(p);
            return box < 0 ? (byte)255 : BoxBiome[box];
        }

        /// <summary>
        ///     The reference: inside boxes the box of the latest source (equal sources: the lower box index, which the
        ///     validator refuses anyway), outside every box the nearest by the sum of squared interval distances, ties to
        ///     the lower biome id, then the lower box index.
        /// </summary>
        public readonly int BruteForce(float* p){
            int inside = -1;
            for (int b = 0; b < BoxCount; b++)
                if (Contains(b, p) && (inside < 0 || BoxSource[b] > BoxSource[inside]))
                    inside = b;
            if (inside >= 0) return inside;
            int best = -1;
            float bestDistance = 0f;
            for (int b = 0; b < BoxCount; b++){
                float distance = DistanceSq(b, p);
                if (best < 0 || Better(b, distance, best, bestDistance)){
                    best = b;
                    bestDistance = distance;
                }
            }

            return best;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private readonly bool Better(int box, float distance, int best, float bestDistance){
            if (distance != bestDistance) return distance < bestDistance;
            byte a = BoxBiome[box], b = BoxBiome[best];
            return a != b ? a < b : box < best;
        }

        /// <summary>Index of the grid interval of <paramref name="v" /> on parameter <paramref name="d" />: the edges at or below it.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly int Interval(int d, float v){
            int lo = Axes[d], n = Axes[5 + d];
            int count = 0;
            // Upper bound by halving: the number of edges ≤ v.
            while (n > 0){
                int half = n >> 1;
                if (Edges[lo + half] <= v){
                    lo += half + 1;
                    count += half + 1;
                    n -= half + 1;
                }
                else n = half;
            }

            return count;
        }

        public readonly bool Contains(int box, float* p){
            for (int d = 0; d < BiomeBox.Parameters; d++){
                float v = p[d];
                if (!(v >= BoxMin[box * 5 + d] && v < BoxMax[box * 5 + d])) return false;
            }

            return true;
        }

        /// <summary>Σ over the parameters of the squared distance to the interval (0 inside it), in parameter order.</summary>
        public readonly float DistanceSq(int box, float* p){
            float sum = 0f;
            for (int d = 0; d < BiomeBox.Parameters; d++){
                float v = p[d], lo = BoxMin[box * 5 + d], hi = BoxMax[box * 5 + d];
                float g = v < lo ? lo - v : v > hi ? v - hi : 0f;
                sum += g * g;
            }

            return sum;
        }

        /// <summary>
        ///     Whether every point in the box [<paramref name="lo" />, <paramref name="hi" />] takes box
        ///     <paramref name="box" />: it lies inside the box by the bounds margin, and no box of a later source overlaps
        ///     it (the uniform-cell test of §6.3).
        /// </summary>
        public readonly bool HoldsBounds(int box, float* lo, float* hi){
            for (int d = 0; d < BiomeBox.Parameters; d++){
                float min = BoxMin[box * 5 + d], max = BoxMax[box * 5 + d];
                if (!(lo[d] - Margin(lo[d]) >= min) || !(hi[d] + Margin(hi[d]) < max)) return false;
            }

            int2 w = Winners[box];
            for (int i = w.x; i < w.x + w.y; i++){
                int o = WinnerBoxes[i];
                bool overlaps = true;
                for (int d = 0; d < BiomeBox.Parameters && overlaps; d++)
                    overlaps = hi[d] + Margin(hi[d]) >= BoxMin[o * 5 + d] && lo[d] - Margin(lo[d]) < BoxMax[o * 5 + d];
                if (overlaps) return false;
            }

            return true;
        }

        /// <summary>The bounds margin of AD-30: 1e-4 · max(1, |v|).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Margin(float v){
            return 1e-4f * math.max(1f, math.abs(v));
        }

        public void Dispose(){
            if (Edges.IsCreated) Edges.Dispose();
            if (Axes.IsCreated) Axes.Dispose();
            if (Cells.IsCreated) Cells.Dispose();
            if (Gaps.IsCreated) Gaps.Dispose();
            if (GapBoxes.IsCreated) GapBoxes.Dispose();
            if (BoxBiome.IsCreated) BoxBiome.Dispose();
            if (BoxMin.IsCreated) BoxMin.Dispose();
            if (BoxMax.IsCreated) BoxMax.Dispose();
            if (Winners.IsCreated) Winners.Dispose();
            if (WinnerBoxes.IsCreated) WinnerBoxes.Dispose();
            if (BoxSource.IsCreated) BoxSource.Dispose();
        }

        /// <summary>Managed convenience for tests and tools: the box at <paramref name="p" />.</summary>
        public readonly int Lookup(float[] p){
            fixed (float* f = p) return Lookup(f);
        }

        public readonly int BruteForce(float[] p){
            fixed (float* f = p) return BruteForce(f);
        }

        /// <summary>Cells of the grid along each parameter.</summary>
        public readonly int CellsAlong(int d) => Axes[5 + d] + 1;
    }

    /// <summary>Statistics of a compiled lookup, for the validator and the Layout tab.</summary>
    public struct BiomeLookupStats{
        public long Cells;
        public int GapCells, GapCandidates, MaxCandidates;
    }

    /// <summary>
    ///     Compiles boxes into a <see cref="BiomeLookup" />. Managed code decides only which boxes a gap cell keeps (a
    ///     superset with a safety factor, in double); every distance that picks a biome is computed by the lookup in
    ///     float, so the result is the brute force's (AD-30).
    /// </summary>
    public static class BiomeLookupCompiler{
        /// <summary>
        ///     Compiles <paramref name="boxes" />; null with <paramref name="error" /> when the grid exceeds
        ///     <see cref="BiomeLookup.MaxCells" /> (the k-d tree of §6.3 is not built yet: the validator refuses such a
        ///     layout and names the files with the most edges).
        /// </summary>
        public static bool TryCompile(IReadOnlyList<LayoutBox> boxes, Allocator allocator, out BiomeLookup lookup, out BiomeLookupStats stats,
            out string error){
            const int P = BiomeBox.Parameters;
            lookup = default;
            stats = default;
            error = null;
            int n = boxes.Count;

            List<float>[] edges = new List<float>[P];
            for (int d = 0; d < P; d++){
                SortedSet<float> set = new();
                foreach (LayoutBox b in boxes){
                    if (!float.IsInfinity(b.Min[d])) set.Add(b.Min[d]);
                    if (!float.IsInfinity(b.Max[d])) set.Add(b.Max[d]);
                }

                edges[d] = new List<float>(set);
            }

            long cells = 1;
            int[] strides = new int[P];
            for (int d = 0; d < P; d++){
                strides[d] = (int)cells;
                cells *= edges[d].Count + 1;
                if (cells > BiomeLookup.MaxCells){
                    error = $"The biome lookup grid would have more than {BiomeLookup.MaxCells} cells (edges per parameter: " +
                            string.Join(", ", System.Array.ConvertAll(edges, e => e.Count.ToString())) + ").";
                    return false;
                }
            }

            stats.Cells = cells;
            int count = (int)cells;
            int[] winner = new int[count];
            for (int i = 0; i < count; i++) winner[i] = -1;

            // Each box marks the cells it covers: per parameter the interval range from its min edge to its max edge.
            int[] lo = new int[P], hi = new int[P];
            for (int b = 0; b < n; b++){
                LayoutBox box = boxes[b];
                bool empty = false;
                for (int d = 0; d < P; d++){
                    lo[d] = float.IsNegativeInfinity(box.Min[d]) ? 0 : edges[d].BinarySearch(box.Min[d]) + 1;
                    hi[d] = float.IsPositiveInfinity(box.Max[d]) ? edges[d].Count : edges[d].BinarySearch(box.Max[d]);
                    if (hi[d] < lo[d]) empty = true;
                }

                if (empty) continue;
                ForEachCell(lo, hi, strides, cell => {
                    int w = winner[cell];
                    if (w < 0 || boxes[b].Source > boxes[w].Source) winner[cell] = b;
                });
            }

            // Gap cells: the boxes that can be nearest somewhere in the cell, dmin_j ≤ min_k dmax_k with a safety factor.
            List<int2> gaps = new();
            List<int> gapBoxes = new();
            int[] index = new int[P];
            double[] cellLo = new double[P], cellHi = new double[P];
            double[] dmin = new double[n], dmax = new double[n];
            for (int cell = 0; cell < count; cell++){
                if (winner[cell] >= 0) continue;
                int rest = cell;
                for (int d = P - 1; d >= 0; d--){
                    index[d] = rest / strides[d];
                    rest -= index[d] * strides[d];
                }

                // The outer cells reach to infinity: climate values leave [−1, 1] (the graph bounds are wider), so a
                // box that is nearest only far out must stay a candidate there.
                for (int d = 0; d < P; d++){
                    cellLo[d] = index[d] == 0 ? double.NegativeInfinity : edges[d][index[d] - 1];
                    cellHi[d] = index[d] == edges[d].Count ? double.PositiveInfinity : edges[d][index[d]];
                }

                double bound = double.MaxValue;
                for (int b = 0; b < n; b++){
                    double sMin = 0, sMax = 0;
                    for (int d = 0; d < P; d++){
                        double m = boxes[b].Min[d], M = boxes[b].Max[d];
                        double gMin = cellHi[d] <= m ? m - cellHi[d] : cellLo[d] >= M ? cellLo[d] - M : 0;
                        double gLo = cellLo[d] < m ? m - cellLo[d] : cellLo[d] > M ? cellLo[d] - M : 0;
                        double gHi = cellHi[d] < m ? m - cellHi[d] : cellHi[d] > M ? cellHi[d] - M : 0;
                        sMin += gMin * gMin;
                        double gMax = Math.Max(gLo, gHi);
                        sMax += gMax * gMax;
                    }

                    dmin[b] = sMin;
                    dmax[b] = sMax;
                    bound = Math.Min(bound, sMax);
                }

                int start = gapBoxes.Count;
                double limit = bound * (1 + 1e-4) + 1e-9;
                for (int b = 0; b < n; b++)
                    if (dmin[b] <= limit)
                        gapBoxes.Add(b);
                int k = gapBoxes.Count - start;
                gaps.Add(new int2(start, k));
                winner[cell] = -gaps.Count;
                stats.GapCells++;
                stats.GapCandidates += k;
                stats.MaxCandidates = Math.Max(stats.MaxCandidates, k);
            }

            // Later-source boxes that overlap each box, for the uniform-cell test.
            List<int2> winners = new();
            List<int> winnerBoxes = new();
            for (int b = 0; b < n; b++){
                int start = winnerBoxes.Count;
                for (int o = 0; o < n; o++)
                    if (o != b && boxes[o].Source > boxes[b].Source && boxes[o].Overlaps(boxes[b]))
                        winnerBoxes.Add(o);
                winners.Add(new int2(start, winnerBoxes.Count - start));
            }

            List<float> allEdges = new();
            int[] axes = new int[16];
            for (int d = 0; d < P; d++){
                axes[d] = allEdges.Count;
                axes[5 + d] = edges[d].Count;
                axes[10 + d] = strides[d];
                allEdges.AddRange(edges[d]);
            }

            float[] mins = new float[n * P], maxs = new float[n * P];
            byte[] biome = new byte[n];
            int[] source = new int[n];
            for (int b = 0; b < n; b++){
                for (int d = 0; d < P; d++){
                    mins[b * P + d] = boxes[b].Min[d];
                    maxs[b * P + d] = boxes[b].Max[d];
                }

                biome[b] = (byte)Math.Clamp(boxes[b].Biome, 0, 255);
                source[b] = boxes[b].Source;
            }

            lookup = new BiomeLookup{
                Edges = Array(allEdges.ToArray(), allocator),
                Axes = new NativeArray<int>(axes, allocator),
                Cells = new NativeArray<int>(winner, allocator),
                Gaps = Array(gaps.ToArray(), allocator),
                GapBoxes = Array(gapBoxes.ToArray(), allocator),
                BoxBiome = Array(biome, allocator),
                BoxMin = Array(mins, allocator),
                BoxMax = Array(maxs, allocator),
                Winners = Array(winners.ToArray(), allocator),
                WinnerBoxes = Array(winnerBoxes.ToArray(), allocator),
                BoxSource = Array(source, allocator),
                BoxCount = n
            };
            return true;
        }

        // A zero-length NativeArray is not created; keep one element so the arrays are valid in jobs.
        private static NativeArray<T> Array<T>(T[] values, Allocator allocator) where T : struct{
            if (values.Length > 0) return new NativeArray<T>(values, allocator);
            return new NativeArray<T>(1, allocator);
        }

        private static void ForEachCell(int[] lo, int[] hi, int[] strides, Action<int> action){
            for (int a = lo[0]; a <= hi[0]; a++)
            for (int b = lo[1]; b <= hi[1]; b++)
            for (int c = lo[2]; c <= hi[2]; c++)
            for (int d = lo[3]; d <= hi[3]; d++)
            for (int e = lo[4]; e <= hi[4]; e++)
                action(a * strides[0] + b * strides[1] + c * strides[2] + d * strides[3] + e * strides[4]);
        }
    }
}
