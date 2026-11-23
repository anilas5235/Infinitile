using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Engine.Scripts.WorldGen.Graph;

namespace Engine.Scripts.WorldGen.Biomes{
    /// <summary>The validator's verdict on a merged layout: its messages and the ε it used.</summary>
    public sealed class LayoutValidation{
        public readonly List<LayoutMessage> Messages = new();

        /// <summary>The neighbour bound per parameter (T, F, C, E, W).</summary>
        public float[] Epsilon;

        /// <summary>The parameter range the check covers per parameter (the graph's output bounds).</summary>
        public float[] RangeMin, RangeMax;

        /// <summary>Cells of the validator's grid (the box edges inside the parameter range).</summary>
        public long Cells;

        public bool HasErrors => Messages.Any(m => m.Severity == LayoutSeverity.Error);

        public IEnumerable<LayoutMessage> Errors => Messages.Where(m => m.Severity == LayoutSeverity.Error);

        /// <summary>Every error on its own line, for exceptions and logs.</summary>
        public string ErrorText(){
            return string.Join("\n", Errors.Select(m => m.ToString()));
        }

        public override string ToString(){
            return string.Join("\n", Messages.Select(m => m.ToString()));
        }
    }

    /// <summary>
    ///     The layout validator of WorldGen v2 §6.6. Runs when the settings load (the scheduler refuses a world with
    ///     errors) and in the preview's Layout tab.
    ///     <list type="bullet">
    ///         <item>Two boxes of one source overlap: error. A later source's box over an earlier one: info with the share it takes.</item>
    ///         <item>More than 255 biomes, or a grid past the lookup's cell limit: error.</item>
    ///         <item>
    ///             Implausible neighbours: error. The parameter space inside the graph's output bounds is cut at every box
    ///             edge; each cell gets the biomes that can win in it (the box result, or in a gap every box that is
    ///             not beaten everywhere in the cell by another one, an exact test because the squared distance is a sum
    ///             over parameters). Two cells are neighbours when in every parameter their intervals lie at most ε apart
    ///             (corners count); a gap cell is also its own neighbour. Water cells add the biomes a dry column can take
    ///             (the dry-column rule's land lookup over the cell), checked against their neighbours and each other.
    ///         </item>
    ///         <item>A box that never wins (a later box covers it): warning. A biome without boxes: info.</item>
    ///     </list>
    /// </summary>
    public static class BiomeLayoutValidator{
        /// <summary>ε per parameter when no graph is at hand (tests, a layout alone).</summary>
        public const float DefaultEpsilon = 0.01f;

        /// <summary>The parameter range assumed when no graph is at hand.</summary>
        public const float DefaultRange = 2f;

        private const int P = BiomeBox.Parameters;

        /// <summary>ε = 2 · s per parameter from the graph's slope bounds (<see cref="GraphSlopes" />).</summary>
        public static float[] Epsilon(CompiledNoiseGraph graph){
            float[] slopes = GraphSlopes.Of(graph);
            float[] e = new float[P];
            for (int d = 0; d < P; d++) e[d] = 2f * GraphSlopes.OfOutput(graph, slopes, ParameterOutput(d));
            return e;
        }

        /// <summary>The graph's output bounds per parameter, widened by the bounds margin.</summary>
        public static void Range(CompiledNoiseGraph graph, out float[] min, out float[] max){
            min = new float[P];
            max = new float[P];
            for (int d = 0; d < P; d++){
                GraphOutputInfo o = graph.Output(ParameterOutput(d));
                min[d] = o != null ? o.Bounds.Min - GraphLimits.BoundsMargin : -DefaultRange;
                max[d] = o != null ? o.Bounds.Max + GraphLimits.BoundsMargin : DefaultRange;
            }
        }

        /// <summary>The graph output of parameter <paramref name="d" /> (T, F, C, E, W).</summary>
        public static string ParameterOutput(int d){
            return d switch{
                BiomeBox.Temperature => GraphOutputs.Temperature,
                BiomeBox.Humidity => GraphOutputs.Humidity,
                BiomeBox.Continentalness => GraphOutputs.Continentalness,
                BiomeBox.Erosion => GraphOutputs.Erosion,
                _ => GraphOutputs.Weirdness
            };
        }

        /// <summary>Validates against the graph's slopes and output bounds.</summary>
        public static LayoutValidation Validate(BiomeLayout layout, CompiledNoiseGraph graph){
            Range(graph, out float[] min, out float[] max);
            return Validate(layout, Epsilon(graph), min, max);
        }

        /// <summary>
        ///     Validates <paramref name="layout" /> with neighbour bound <paramref name="epsilon" /> per parameter over the
        ///     range <paramref name="rangeMin" /> … <paramref name="rangeMax" /> (defaults: <see cref="DefaultEpsilon" />,
        ///     ±<see cref="DefaultRange" />).
        /// </summary>
        public static LayoutValidation Validate(BiomeLayout layout, float[] epsilon = null, float[] rangeMin = null, float[] rangeMax = null){
            LayoutValidation v = new(){
                Epsilon = epsilon ?? Enumerable.Repeat(DefaultEpsilon, P).ToArray(),
                RangeMin = rangeMin ?? Enumerable.Repeat(-DefaultRange, P).ToArray(),
                RangeMax = rangeMax ?? Enumerable.Repeat(DefaultRange, P).ToArray()
            };
            v.Messages.AddRange(layout.MergeMessages);
            if (layout.Biomes.Count > BiomeLayout.MaxBiomes)
                Add(v, LayoutSeverity.Error, layout, -1, null, $"The layout has {layout.Biomes.Count} biomes; at most {BiomeLayout.MaxBiomes} fit the per-column ids.", "count");
            Overlaps(v, layout);
            for (int i = 0; i < layout.Biomes.Count; i++)
                if (layout.Boxes.All(b => b.Biome != i))
                    Add(v, LayoutSeverity.Info, layout, layout.Biomes[i].Source, layout.Biomes[i].File,
                        $"{layout.Biomes[i].Name} has no boxes and never generates.", "unused");
            if (!LookupFits(v, layout)) return v;
            Neighbours(v, layout);
            return v;
        }

        // --- Overlaps ---------------------------------------------------------------------------------------------------

        private static void Overlaps(LayoutValidation v, BiomeLayout layout){
            List<LayoutBox> boxes = layout.Boxes;
            for (int i = 0; i < boxes.Count; i++)
            for (int j = i + 1; j < boxes.Count; j++){
                LayoutBox a = boxes[i], b = boxes[j];
                if (!a.Overlaps(b)) continue;
                if (a.Source == b.Source){
                    string where = a.File == b.File ? a.File : $"{a.File} and {b.File}";
                    Add(v, LayoutSeverity.Error, layout, a.Source, where,
                        $"Boxes of one source overlap: {layout.NameOf(a.Biome)} ({a.Describe()}) and {layout.NameOf(b.Biome)} ({b.Describe()}).", "overlap");
                    continue;
                }

                LayoutBox earlier = a.Source < b.Source ? a : b, later = a.Source < b.Source ? b : a;
                double share = Share(v, earlier, later);
                Add(v, LayoutSeverity.Info, layout, later.Source, later.File,
                    $"{layout.NameOf(later.Biome)} ({later.Describe()}) of {SourceName(layout, later.Source)} takes " +
                    $"{share.ToString("0.#%", CultureInfo.InvariantCulture)} of {layout.NameOf(earlier.Biome)}'s box ({earlier.Describe()}) " +
                    $"of {SourceName(layout, earlier.Source)}.", "override");
            }
        }

        /// <summary>The volume share of <paramref name="earlier" /> (inside the parameter range) that <paramref name="later" /> covers.</summary>
        private static double Share(LayoutValidation v, LayoutBox earlier, LayoutBox later){
            double whole = 1, covered = 1;
            for (int d = 0; d < P; d++){
                double lo = Math.Max(earlier.Min[d], v.RangeMin[d]), hi = Math.Min(earlier.Max[d], v.RangeMax[d]);
                double clo = Math.Max(lo, later.Min[d]), chi = Math.Min(hi, later.Max[d]);
                whole *= Math.Max(0, hi - lo);
                covered *= Math.Max(0, chi - clo);
            }

            return whole > 0 ? covered / whole : 0;
        }

        private static bool LookupFits(LayoutValidation v, BiomeLayout layout){
            long cells = 1;
            for (int d = 0; d < P; d++){
                int edges = layout.Boxes.SelectMany(b => new[]{ b.Min[d], b.Max[d] }).Where(x => !float.IsInfinity(x)).Distinct().Count();
                cells *= edges + 1;
                if (cells <= BiomeLookup.MaxCells) continue;
                string files = string.Join(", ", layout.Boxes.Select(b => b.File).Distinct());
                Add(v, LayoutSeverity.Error, layout, -1, files,
                    $"The biome lookup would need more than {BiomeLookup.MaxCells} cells; merge or align box edges ({files}).", "lookup");
                return false;
            }

            return true;
        }

        // --- Neighbours -------------------------------------------------------------------------------------------------

        private sealed class Grid{
            public float[][] Lines; // per parameter: the cut values from the range minimum to its maximum
            public int[] Counts, Strides;
            public long Cells;

            public float Lo(int d, int k){
                return Lines[d][k];
            }

            public float Hi(int d, int k){
                return Lines[d][k + 1];
            }
        }

        private static Grid MakeGrid(LayoutValidation v, BiomeLayout layout){
            Grid g = new(){ Lines = new float[P][], Counts = new int[P], Strides = new int[P] };
            long cells = 1;
            for (int d = 0; d < P; d++){
                float lo = v.RangeMin[d], hi = v.RangeMax[d];
                SortedSet<float> set = new(){ lo, hi };
                foreach (LayoutBox b in layout.Boxes){
                    if (b.Min[d] > lo && b.Min[d] < hi) set.Add(b.Min[d]);
                    if (b.Max[d] > lo && b.Max[d] < hi) set.Add(b.Max[d]);
                }

                g.Lines[d] = set.ToArray();
                g.Counts[d] = g.Lines[d].Length - 1;
                g.Strides[d] = (int)cells;
                cells *= g.Counts[d];
            }

            g.Cells = cells;
            return g;
        }

        private static void Neighbours(LayoutValidation v, BiomeLayout layout){
            Grid g = MakeGrid(v, layout);
            v.Cells = g.Cells;
            int cells = (int)g.Cells;
            BiomeMask water = default;
            foreach (LayoutBiome b in layout.Biomes)
                if (b.IsWater)
                    water.Add(b.Id);
            List<LayoutBox> all = layout.Boxes, land = layout.Boxes.Where(b => b.Biome < layout.Biomes.Count && !layout.Biomes[b.Biome].IsWater).ToList();
            BiomeMask[] main = new BiomeMask[cells], sub = new BiomeMask[cells];
            bool[] gap = new bool[cells];
            HashSet<int> winners = new();
            int[] k = new int[P];
            float[] lo = new float[P], hi = new float[P];
            for (int c = 0; c < cells; c++){
                Cell(g, c, k, lo, hi);
                main[c] = Biomes(all, lo, hi, out gap[c], winners);
                if (!Intersects(main[c], water)) continue;
                sub[c] = Biomes(land, lo, hi, out _, null);
            }

            foreach (LayoutBox b in all)
                if (!winners.Contains(b.Index))
                    Add(v, LayoutSeverity.Warning, layout, b.Source, b.File,
                        $"{layout.NameOf(b.Biome)}'s box ({b.Describe()}) never wins: " +
                        (Covered(v, b) ? "later boxes cover it." : "it lies outside the parameter range of the graph."), "unused");

            if (layout.ImplausiblePairs.Count == 0) return;
            HashSet<string> reported = new();
            int[] from = new int[P], to = new int[P], j = new int[P];
            for (int c = 0; c < cells; c++){
                Cell(g, c, k, lo, hi);
                for (int d = 0; d < P; d++){
                    int a = k[d], b = k[d];
                    while (a > 0 && lo[d] - g.Hi(d, a - 1) <= v.Epsilon[d]) a--;
                    while (b < g.Counts[d] - 1 && g.Lo(d, b + 1) - hi[d] <= v.Epsilon[d]) b++;
                    from[d] = a;
                    to[d] = b;
                    j[d] = a;
                }

                while (true){
                    int n = 0;
                    for (int d = 0; d < P; d++) n += j[d] * g.Strides[d];
                    if (n >= c) Pair(v, layout, g, c, n, main, sub, gap, k, j, reported);
                    int dd = 0;
                    while (dd < P && ++j[dd] > to[dd]){
                        j[dd] = from[dd];
                        dd++;
                    }

                    if (dd == P) break;
                }
            }
        }

        private static bool Covered(LayoutValidation v, LayoutBox b){
            for (int d = 0; d < P; d++)
                if (!(b.Min[d] < v.RangeMax[d] && b.Max[d] > v.RangeMin[d]))
                    return false;
            return true;
        }

        private static void Pair(LayoutValidation v, BiomeLayout layout, Grid g, int c, int n, BiomeMask[] main, BiomeMask[] sub, bool[] gap, int[] kc, int[] kn,
            HashSet<string> reported){
            if (c == n){
                // Inside one cell only a gap (several boxes can win) or a substitute set can hold two biomes.
                if (gap[c]) Check(v, layout, g, c, n, main[c], main[c], "gap", kc, reported);
                if (!sub[c].IsEmpty){
                    Check(v, layout, g, c, n, sub[c], sub[c], "substitute", kc, reported);
                    Check(v, layout, g, c, n, sub[c], main[c], "substitute", kc, reported);
                }

                return;
            }

            int touching = 0;
            for (int d = 0; d < P; d++)
                if (kc[d] == kn[d])
                    touching++;
            string kind = gap[c] || gap[n] ? "gap" : touching >= P - 1 ? "neighbour" : "corner";
            Check(v, layout, g, c, n, main[c], main[n], kind, kc, reported);
            if (!sub[c].IsEmpty){
                Check(v, layout, g, c, n, sub[c], main[n], "substitute", kc, reported);
                if (!sub[n].IsEmpty) Check(v, layout, g, c, n, sub[c], sub[n], "substitute", kc, reported);
            }

            if (!sub[n].IsEmpty) Check(v, layout, g, c, n, main[c], sub[n], "substitute", kc, reported);
        }

        private static void Check(LayoutValidation v, BiomeLayout layout, Grid g, int c, int n, BiomeMask a, BiomeMask b, string kind, int[] kc,
            HashSet<string> reported){
            foreach (UnityEngine.Vector2Int pair in layout.ImplausiblePairs){
                if (!(a.Contains(pair.x) && b.Contains(pair.y) || a.Contains(pair.y) && b.Contains(pair.x))) continue;
                string key = $"{pair.x}/{pair.y}/{kind}";
                if (!reported.Add(key)) continue;
                string what = kind switch{
                    "gap" => "meet in the nearest-box fill of a gap",
                    "substitute" => "meet through the dry-column rule (a dry column in a water box takes the nearest land box)",
                    "corner" => "touch at a corner",
                    _ => "are neighbours"
                };
                string files = string.Join(", ", new[]{ layout.Biomes[pair.x].File, layout.Biomes[pair.y].File }.Distinct());
                Add(v, LayoutSeverity.Error, layout, Math.Max(layout.Biomes[pair.x].Source, layout.Biomes[pair.y].Source), files,
                    $"Implausible pair {layout.NameOf(pair.x)}–{layout.NameOf(pair.y)} {what} near {Describe(g, c)}.", kind);
            }
        }

        /// <summary>
        ///     The biomes that can win in the cell [<paramref name="lo" />, <paramref name="hi" />) among
        ///     <paramref name="boxes" />: the latest source's box that holds the cell (no gap), or in a gap every box no
        ///     other box beats everywhere in the cell.
        /// </summary>
        private static BiomeMask Biomes(List<LayoutBox> boxes, float[] lo, float[] hi, out bool gap, HashSet<int> winners){
            LayoutBox best = null;
            foreach (LayoutBox b in boxes){
                bool inside = true;
                for (int d = 0; d < P && inside; d++) inside = b.Min[d] <= lo[d] && hi[d] <= b.Max[d];
                if (!inside) continue;
                if (best == null || b.Source > best.Source || b.Source == best.Source && b.Index < best.Index) best = b;
            }

            BiomeMask mask = default;
            if (best != null){
                gap = false;
                winners?.Add(best.Index);
                mask.Add(best.Biome);
                return mask;
            }

            // Compare just inside the cell: a tie only on a cell edge (a box that touches the cell, nearest on that one
            // plane) is no area of the map, and the edge belongs to the neighbour cell, which carries its own set.
            gap = true;
            double[] inLo = new double[P], inHi = new double[P];
            for (int d = 0; d < P; d++){
                double delta = Math.Min(1e-4 * Math.Max(1.0, Math.Max(Math.Abs(lo[d]), Math.Abs(hi[d]))), ((double)hi[d] - lo[d]) * 0.25);
                inLo[d] = lo[d] + delta;
                inHi[d] = hi[d] - delta;
            }

            foreach (LayoutBox b in boxes){
                bool beaten = false;
                foreach (LayoutBox a in boxes){
                    if (a == b) continue;
                    double worst = 0;
                    for (int d = 0; d < P; d++) worst += Math.Max(Diff(a, b, d, inLo[d]), Diff(a, b, d, inHi[d]));
                    bool ranksFirst = a.Biome < b.Biome || a.Biome == b.Biome && a.Index < b.Index;
                    if (worst < -1e-12 || worst <= 1e-12 && ranksFirst){
                        beaten = true;
                        break;
                    }
                }

                if (beaten) continue;
                mask.Add(b.Biome);
                winners?.Add(b.Index);
            }

            return mask;
        }

        /// <summary>
        ///     gap_a(x)² − gap_b(x)² in parameter <paramref name="d" />. The grid cuts at every box edge, so on a cell
        ///     both gaps are 0 or linear in x and the largest difference lies at a cell edge.
        /// </summary>
        private static double Diff(LayoutBox a, LayoutBox b, int d, double x){
            double ga = Gap(a, d, x), gb = Gap(b, d, x);
            return ga * ga - gb * gb;
        }

        private static double Gap(LayoutBox b, int d, double x){
            if (x < b.Min[d]) return b.Min[d] - x;
            if (x > b.Max[d]) return x - b.Max[d];
            return 0;
        }

        private static bool Intersects(BiomeMask a, BiomeMask b){
            return (a.W0 & b.W0) != 0 || (a.W1 & b.W1) != 0 || (a.W2 & b.W2) != 0 || (a.W3 & b.W3) != 0;
        }

        private static void Cell(Grid g, int c, int[] k, float[] lo, float[] hi){
            for (int d = 0; d < P; d++){
                k[d] = c / g.Strides[d] % g.Counts[d];
                lo[d] = g.Lo(d, k[d]);
                hi[d] = g.Hi(d, k[d]);
            }
        }

        private static string Describe(Grid g, int c){
            StringBuilder s = new();
            for (int d = 0; d < P; d++){
                int k = c / g.Strides[d] % g.Counts[d];
                if (g.Counts[d] == 1) continue;
                if (s.Length > 0) s.Append(' ');
                s.Append(BiomeBox.ShortNames[d]).Append(' ')
                    .Append(g.Lo(d, k).ToString("0.###", CultureInfo.InvariantCulture)).Append('…')
                    .Append(g.Hi(d, k).ToString("0.###", CultureInfo.InvariantCulture));
            }

            return s.Length == 0 ? "everywhere" : s.ToString();
        }

        private static string SourceName(BiomeLayout layout, int source){
            return source >= 0 && source < layout.Sources.Count ? layout.Sources[source] : "?";
        }

        private static void Add(LayoutValidation v, LayoutSeverity severity, BiomeLayout layout, int source, string file, string text, string kind){
            v.Messages.Add(new LayoutMessage{
                Severity = severity, Source = source >= 0 ? SourceName(layout, source) : null, File = file, Text = text, Kind = kind
            });
        }
    }
}
