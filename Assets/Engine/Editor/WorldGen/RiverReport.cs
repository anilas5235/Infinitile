using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Engine.Scripts.WorldGen.Biomes;
using Engine.Scripts.WorldGen.Density;
using Engine.Scripts.WorldGen.Graph;
using Engine.Scripts.WorldGen.Surface;
using Unity.Collections;
using Unity.Mathematics;

namespace Engine.Editor.WorldGen{
    /// <summary>The river measures of one seed (§9.2, phase V5).</summary>
    public sealed class RiverReportSeed{
        public uint Seed;

        /// <summary>Along-line distances between neighbouring river crossings (blocks), as the offline 553 was measured.</summary>
        public readonly List<float> Spacing = new();

        /// <summary>Width across the river (blocks): of the valleys slice and of the water, both corrected to the river's normal.</summary>
        public readonly List<float> SliceWidth = new(), WetWidth = new();

        /// <summary>River crossings: all, dry (no water at the crossing), joined with open water (a wet run over 512 blocks or to the line's end).</summary>
        public int Crossings, DryCrossings, OpenWaterCrossings;

        /// <summary>W crossings off the river (river &lt; 0.75) on dry land above y 128: the dry gorges (DEC-141, DEC-197).</summary>
        public int GorgeCrossings;

        /// <summary>Depth of each dry gorge (blocks): the lower of the highest surfaces within 24 blocks on either side, minus the surface at the crossing.</summary>
        public readonly List<float> GorgeDepth = new();

        /// <summary>Line samples: all, in the valleys slice, river columns (river &gt; 0) under water, river columns, land (dry).</summary>
        public long Samples, SliceSamples, RiverWetSamples, RiverSamples, DrySamples;

        /// <summary>Plain-lowland samples in the valleys slice (C −0.11 … 0.03) and the wet ones among them.</summary>
        public long LowlandSliceSamples, LowlandSliceWet;

        /// <summary>Plain-lowland samples (C −0.11 … 0.03): their count over the river crossings in it is the mean spacing along the lines.</summary>
        public long LowlandSamples;

        public int LowlandCrossings;

        public readonly List<TourSpot> Tour = new();
    }

    /// <summary>
    ///     The river measures of WorldGen v2 (§9.2, phase V5) on straight lines at stride 1: 32 lines along x and 32 along z,
    ///     256 blocks apart, 8192 blocks long, around the origin. A river crossing is a sign change of W where the river
    ///     output (the river weight, as |W| ≈ 0 there) is at least 0.75. Spacing is the along-line distance between neighbouring
    ///     river crossings with no other W crossing and no ocean (C &lt; −0.19) between them. Widths are the runs of the valleys
    ///     slice and of wet columns through a crossing, times |∂W/∂s| / |∇W| (the gradient from a parallel line one block
    ///     over), so they are measured across the river.
    /// </summary>
    public static class RiverReport{
        public const int Lines = 32, Spacing = 256, Length = 8192;

        /// <summary>The valleys slice's half width in |W| (§9.2, narrowed from 0.05 by DEC-196).</summary>
        public const float Slice = 0.04f;

        /// <summary>Blocks on either side of a gorge crossing searched for its rim.</summary>
        public const int GorgeRim = 24;

        /// <summary>A crossing is a river where the river output there is at least this.</summary>
        public const float RiverMin = 0.75f;

        /// <summary>A wet run longer than this is open water (sea or a lake), not the river's own width.</summary>
        public const int OpenWater = 512;

        public static RiverReportSeed Run(CompiledNoiseGraph graph, in BiomeLayoutData data, uint seed){
            RiverReportSeed r = new(){ Seed = seed };
            int riverSlot = graph.Output(SurfaceRuleSet.RiverOutput).LatticeValue, wSlot = graph.Output("weirdness").LatticeValue,
                cSlot = graph.Output("continentalness").LatticeValue, sSlot = graph.Output("preliminary_surface").LatticeValue;
            float2[] points = new float2[2 * Length];
            float2[] main = new float2[Length];
            float[] w = new float[Length], w1 = new float[Length], river = new float[Length], c = new float[Length], surface = new float[Length];
            byte[] wet = new byte[Length];
            for (int axis = 0; axis < 2; axis++)
            for (int line = 0; line < Lines; line++){
                int across = -Length / 2 + Spacing / 2 + line * Spacing;
                float2 dir = axis == 0 ? new float2(1, 0) : new float2(0, 1), normal = axis == 0 ? new float2(0, 1) : new float2(1, 0);
                for (int i = 0; i < Length; i++){
                    float2 p = dir * (-Length / 2 + i) + normal * across;
                    main[i] = p;
                    points[i] = p;
                    points[Length + i] = p + normal;
                }

                float[] lattice = GraphEvaluator.SampleLattice(graph, seed, points, out _);
                int n = points.Length;
                for (int i = 0; i < Length; i++){
                    w[i] = lattice[wSlot * n + i];
                    w1[i] = lattice[wSlot * n + Length + i];
                    river[i] = lattice[riverSlot * n + i];
                    c[i] = lattice[cSlot * n + i];
                    surface[i] = lattice[sSlot * n + i];
                }

                using (BiomeColumns columns = BiomeColumnSampler.Sample(graph, data, seed, main))
                    for (int i = 0; i < Length; i++)
                        wet[i] = columns.Wet[i];
                Line(r, main, w, w1, river, c, surface, wet);
            }

            return r;
        }

        private static void Line(RiverReportSeed r, float2[] p, float[] w, float[] w1, float[] river, float[] c, float[] surface, byte[] wet){
            int n = p.Length;
            for (int i = 0; i < n; i++){
                r.Samples++;
                bool inSlice = math.abs(w[i]) <= Slice;
                if (inSlice) r.SliceSamples++;
                if (wet[i] == 0) r.DrySamples++;
                if (river[i] > 0f){
                    r.RiverSamples++;
                    if (wet[i] != 0) r.RiverWetSamples++;
                }

                bool lowland = c[i] >= -0.11f && c[i] <= 0.03f;
                if (lowland) r.LowlandSamples++;
                if (inSlice && lowland){
                    r.LowlandSliceSamples++;
                    if (wet[i] != 0) r.LowlandSliceWet++;
                }
            }

            int lastRiver = -1;
            bool oceanSince = false;
            for (int i = 0; i + 1 < n; i++){
                if (c[i] < -0.19f) oceanSince = true;
                if (w[i] == 0f || math.sign(w[i]) == math.sign(w[i + 1])) continue;
                int k = math.abs(w[i]) <= math.abs(w[i + 1]) ? i : i + 1; // the sample nearer the zero line
                if (river[k] < RiverMin){
                    lastRiver = -1;
                    bool dryHere = wet[k] == 0 && (k == 0 || wet[k - 1] == 0) && (k + 1 >= n || wet[k + 1] == 0);
                    if (dryHere && surface[k] > 128f){
                        r.GorgeCrossings++;
                        float left = float.MinValue, right = float.MinValue;
                        for (int d = 1; d <= GorgeRim; d++){
                            if (k - d >= 0) left = math.max(left, surface[k - d]);
                            if (k + d < n) right = math.max(right, surface[k + d]);
                        }

                        r.GorgeDepth.Add(math.max(0f, math.min(left, right) - surface[k]));
                        if (r.Tour.Count(t => t.What.StartsWith("dry gorge", StringComparison.Ordinal)) < 3)
                            r.Tour.Add(Spot(Invariant($"dry gorge (river {river[k]:0.00}, surface {surface[k]:0})"), p[k], surface[k]));
                    }

                    continue;
                }

                r.Crossings++;
                if (c[k] >= -0.11f && c[k] <= 0.03f) r.LowlandCrossings++;
                float s = i + w[i] / (w[i] - w[i + 1]);
                if (lastRiver >= 0 && !oceanSince) r.Spacing.Add(s - lastRiver);
                lastRiver = (int)math.round(s);
                oceanSince = false;

                // |∂W/∂s| / |∇W|: the run along the line is longer than across the river by 1 / that factor.
                float ds = w[i + 1] - w[i], dn = w1[k] - w[k];
                float factor = math.abs(ds) / math.max(1e-12f, math.sqrt(ds * ds + dn * dn));

                int a = k, b = k;
                while (a > 0 && math.abs(w[a - 1]) <= Slice) a--;
                while (b + 1 < n && math.abs(w[b + 1]) <= Slice) b++;
                if (a > 0 && b + 1 < n) r.SliceWidth.Add((b - a + 1) * factor);

                int m = -1;
                for (int d = 0; d <= 2 && m < 0; d++){
                    if (k - d >= 0 && wet[k - d] != 0) m = k - d;
                    else if (k + d < n && wet[k + d] != 0) m = k + d;
                }

                if (m < 0){
                    r.DryCrossings++;
                    if (r.Tour.Count(t => t.What.StartsWith("dry river", StringComparison.Ordinal)) < 3)
                        r.Tour.Add(Spot("dry river crossing (gorge or end)", p[k], surface[k]));
                    continue;
                }

                a = m;
                b = m;
                while (a > 0 && wet[a - 1] != 0) a--;
                while (b + 1 < n && wet[b + 1] != 0) b++;
                int run = b - a + 1;
                if (a == 0 || b + 1 == n || run > OpenWater){
                    r.OpenWaterCrossings++;
                    if (r.Tour.Count(t => t.What.StartsWith("river joins", StringComparison.Ordinal)) < 2)
                        r.Tour.Add(Spot("river joins open water", p[k], surface[k]));
                    continue;
                }

                float width = run * factor;
                r.WetWidth.Add(width);
                if (width >= 60f && r.Tour.Count(t => t.What.StartsWith("wide river", StringComparison.Ordinal)) < 3)
                    r.Tour.Add(Spot(Invariant($"wide river ({width:0} blocks)"), p[k], surface[k]));
                else if (width >= 15f && width <= 25f && r.Tour.Count(t => t.What.StartsWith("typical river", StringComparison.Ordinal)) < 3)
                    r.Tour.Add(Spot(Invariant($"typical river ({width:0} blocks)"), p[k], surface[k]));
                else if (width <= 8f && r.Tour.Count(t => t.What.StartsWith("narrow river", StringComparison.Ordinal)) < 2)
                    r.Tour.Add(Spot(Invariant($"narrow river ({width:0} blocks)"), p[k], surface[k]));
            }
        }

        private static TourSpot Spot(string what, float2 p, float surface){
            return new TourSpot{ What = what, X = (int)p.x, Z = (int)p.y, Y = (int)math.max(DensityLayout.WetnessY + 1, surface) + 24 };
        }

        private static string Invariant(FormattableString s){
            return FormattableString.Invariant(s);
        }

        /// <summary>The <paramref name="q" /> quantile (0 … 1) of <paramref name="values" />, NaN when empty.</summary>
        public static double Quantile(IReadOnlyCollection<float> values, double q){
            if (values.Count == 0) return double.NaN;
            float[] sorted = values.OrderBy(v => v).ToArray();
            double x = q * (sorted.Length - 1);
            int lo = (int)Math.Floor(x), hi = Math.Min(lo + 1, sorted.Length - 1);
            return sorted[lo] + (sorted[hi] - sorted[lo]) * (x - lo);
        }

        private static string Percentiles(IReadOnlyCollection<float> v){
            return Invariant($"p10 {Quantile(v, 0.10):0} · median {Quantile(v, 0.5):0} · p90 {Quantile(v, 0.90):0} · p99 {Quantile(v, 0.99):0} (n {v.Count})");
        }

        public static string Text(RiverReportSeed r){
            StringBuilder sb = new();
            sb.AppendLine($"=== River report · seed {r.Seed} ===");
            sb.AppendLine(Body(r));
            sb.AppendLine("tour list:");
            foreach (TourSpot s in r.Tour) sb.AppendLine("  " + s);
            return sb.ToString();
        }

        /// <summary>The numbers of all seeds pooled.</summary>
        public static string Pooled(IReadOnlyList<RiverReportSeed> seeds){
            RiverReportSeed all = new();
            foreach (RiverReportSeed s in seeds){
                all.Spacing.AddRange(s.Spacing);
                all.SliceWidth.AddRange(s.SliceWidth);
                all.WetWidth.AddRange(s.WetWidth);
                all.GorgeDepth.AddRange(s.GorgeDepth);
                all.Crossings += s.Crossings;
                all.DryCrossings += s.DryCrossings;
                all.OpenWaterCrossings += s.OpenWaterCrossings;
                all.GorgeCrossings += s.GorgeCrossings;
                all.LowlandSamples += s.LowlandSamples;
                all.LowlandCrossings += s.LowlandCrossings;
                all.Samples += s.Samples;
                all.SliceSamples += s.SliceSamples;
                all.RiverWetSamples += s.RiverWetSamples;
                all.RiverSamples += s.RiverSamples;
                all.DrySamples += s.DrySamples;
                all.LowlandSliceSamples += s.LowlandSliceSamples;
                all.LowlandSliceWet += s.LowlandSliceWet;
            }

            return $"=== River report · {seeds.Count} seeds pooled ===\n" + Body(all);
        }

        private static string Body(RiverReportSeed r){
            double Share(long a, long b) => b > 0 ? (double)a / b : double.NaN;
            StringBuilder sb = new();
            sb.AppendLine(Invariant($"river crossings {r.Crossings}: dry {BiomeWorldReport.P(Share(r.DryCrossings, r.Crossings))}, joined with open water {BiomeWorldReport.P(Share(r.OpenWaterCrossings, r.Crossings))} · dry gorge crossings {r.GorgeCrossings}"));
            sb.AppendLine("spacing along the lines (offline 553): " + Invariant($"median {Quantile(r.Spacing, 0.5):0} · mean {(r.Spacing.Count > 0 ? r.Spacing.Average() : double.NaN):0}") +
                          Invariant($" (n {r.Spacing.Count}) · plain-lowland line length per river crossing {(r.LowlandCrossings > 0 ? (double)r.LowlandSamples / r.LowlandCrossings : double.NaN):0}"));
            sb.AppendLine("valleys slice width across (|W| ≤ 0.04): " + Percentiles(r.SliceWidth));
            sb.AppendLine("water width across (DEC-196): " + Percentiles(r.WetWidth));
            sb.AppendLine("dry gorge depth: " + Percentiles(r.GorgeDepth));
            sb.AppendLine($"valleys slice {BiomeWorldReport.P(Share(r.SliceSamples, r.Samples))} of the area · river columns {BiomeWorldReport.P(Share(r.RiverSamples, r.Samples), "0.00")}, " +
                          $"wet river columns {BiomeWorldReport.P(Share(r.RiverWetSamples, r.Samples), "0.00")} of the area ({BiomeWorldReport.P(Share(r.RiverWetSamples, r.RiverSamples))} of the river columns) · " +
                          $"plain-lowland slice wet {BiomeWorldReport.P(Share(r.LowlandSliceWet, r.LowlandSliceSamples))} · land {BiomeWorldReport.P(Share(r.DrySamples, r.Samples))}");
            return sb.ToString().TrimEnd();
        }
    }
}
