using System;
using System.Collections.Generic;
using Engine.Scripts.WorldGen.Settings;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Engine.Editor.WorldGen{
    /// <summary>
    ///     GEN-24: boundary length between every biome pair of a sampled view. A boundary is a 4-neighbour edge
    ///     between two samples with different biomes (the ANA-10 edge count); each edge stands for
    ///     <c>stride</c> blocks of boundary, so the length is in blocks and roughly independent of the zoom.
    ///     Pairs listed in <see cref="BiomeSet.ImplausibleNeighbours" /> are flagged.
    /// </summary>
    public static class BiomeBoundaryStats{
        /// <summary>Counts matrix side: one row/column per possible biome index (BLD-02).</summary>
        public const int Side = BiomeSet.MaxBiomes;

        /// <summary>Boundary between two biomes, A &lt; B.</summary>
        public struct PairLength{
            public int A;
            public int B;
            public string NameA;
            public string NameB;

            /// <summary>Boundary length in blocks (edges × stride).</summary>
            public long Length;

            /// <summary>Share of the total boundary length of the view.</summary>
            public float Share;

            /// <summary>The pair is on the implausible list (GEN-24).</summary>
            public bool Implausible;
        }

        /// <summary>
        ///     Counts the edges between different biomes into <c>Edges[min·Side + max]</c>. Indices outside
        ///     [0, Side) are ignored (no biome). Single job: the map is at most a few hundred thousand samples.
        /// </summary>
        [BurstCompile(CompileSynchronously = true)]
        public struct CountJob : IJob{
            [ReadOnly] public NativeArray<int> Biomes;
            public int Size;

            /// <summary>Side × Side counts; cleared by the job.</summary>
            public NativeArray<int> Edges;

            public void Execute(){
                for (int i = 0; i < Edges.Length; i++) Edges[i] = 0;
                for (int y = 0; y < Size; y++)
                for (int x = 0; x < Size; x++){
                    int b = Biomes[y * Size + x];
                    if (x + 1 < Size) Add(b, Biomes[y * Size + x + 1]);
                    if (y + 1 < Size) Add(b, Biomes[(y + 1) * Size + x]);
                }
            }

            private void Add(int a, int b){
                if (a == b || (uint)a >= Side || (uint)b >= Side) return;
                int lo = math.min(a, b), hi = math.max(a, b);
                Edges[lo * Side + hi]++;
            }
        }

        /// <summary>Schedules the edge count over a square map of <paramref name="size" />² biome indices.</summary>
        public static JobHandle Schedule(NativeArray<int> biomes, int size, NativeArray<int> edges, JobHandle dependency){
            if (biomes.Length != size * size) throw new ArgumentException($"Expected {size * size} biome samples, got {biomes.Length}.");
            if (edges.Length != Side * Side) throw new ArgumentException($"Edge matrix needs {Side * Side} entries, got {edges.Length}.");
            return new CountJob{ Biomes = biomes, Size = size, Edges = edges }.Schedule(dependency);
        }

        /// <summary>
        ///     Turns the edge counts into pair lengths in blocks, sorted by length (longest first). Implausible pairs
        ///     are flagged from <paramref name="set" /> (may be null: nothing flagged, names are indices).
        /// </summary>
        public static List<PairLength> Summarize(NativeArray<int> edges, int stride, BiomeSet set){
            List<PairLength> pairs = new();
            long total = 0;
            for (int a = 0; a < Side; a++)
            for (int b = a + 1; b < Side; b++){
                int count = edges[a * Side + b];
                if (count == 0) continue;
                long length = (long)count * stride;
                total += length;
                pairs.Add(new PairLength{
                    A = a, B = b,
                    NameA = set ? set.NameOf(a) : $"<{a}>",
                    NameB = set ? set.NameOf(b) : $"<{b}>",
                    Length = length,
                    Implausible = set && set.IsImplausible(a, b)
                });
            }

            for (int i = 0; i < pairs.Count; i++){
                PairLength p = pairs[i];
                p.Share = total > 0 ? (float)p.Length / total : 0f;
                pairs[i] = p;
            }

            pairs.Sort((x, y) => y.Length != x.Length ? y.Length.CompareTo(x.Length) : (x.A * Side + x.B).CompareTo(y.A * Side + y.B));
            return pairs;
        }
    }
}
