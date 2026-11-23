using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Engine.Scripts.WorldGen.Settings;
using Engine.Scripts.WorldGen.Biomes;
using Engine.Scripts.WorldGen.Graph;
using Unity.Collections;

namespace Engine.Editor.WorldGen{
    /// <summary>One candidate of the sweep: a change to the shipped graph and/or the shipped layout.</summary>
    public sealed class SweepCandidate{
        public string Name;
        public Action<NoiseGraph> Graph;
        public Action<BiomeLayout> Layout;
    }

    /// <summary>A candidate's results over the five seeds.</summary>
    public sealed class SweepRow{
        public SweepCandidate Candidate;
        public List<WorldReportSeed> Seeds = new();
        public string Error;

        public double Mean(Func<WorldReportSeed, double> f){
            return Seeds.Count == 0 ? double.NaN : Seeds.Average(f);
        }

        public double Min(Func<WorldReportSeed, double> f){
            return Seeds.Count == 0 ? double.NaN : Seeds.Min(f);
        }
    }

    /// <summary>
    ///     The sweep of §6.7 (DEC-136): candidate layouts, wavelength sets and C curves, each changed one factor at a
    ///     time from the shipped graph and layout, run through the world report on the five fixed seeds. It prints the
    ///     table the owner picks from; nothing is picked automatically (W2-TOOL-04).
    /// </summary>
    public static class BiomeSweep{
        /// <summary>The T/F band edges of the starting layout (§6.4).</summary>
        private const float Inner = 0.087f, Outer = 0.28f;

        /// <summary>
        ///     The massif footprint's edges and the Swamp edge in the shipped layout (DEC-161, moved to C 0.09 by DEC-166:
        ///     the terrain's ranges climb from there), the footprint of the V3 start layout (§6.4), and the edge of the
        ///     Mountain ridge band |W| &lt; 0.2 (DEC-163, DEC-166: Mountain only on the ridges of the ranges, the highland
        ///     valleys between them go to the lowland biomes).
        /// </summary>
        private const float MassifC = 0.09f, MassifE = -0.05f, SwampE = 0.4f, StartMassifC = 0.18f, StartMassifE = -0.38f,
            RidgeW = 0.2f;

        /// <summary>
        ///     The candidates, each one change from the shipped graph and layout. The V3 sweep that DEC-161 picked from
        ///     ran on the start layout (massif C ≥ 0.18, E ≤ −0.38, T/F wavelengths 4706 / 3765); row S moves the massif
        ///     edges and the wavelengths back there but keeps the Mountain ridge band and its companion boxes.
        /// </summary>
        public static List<SweepCandidate> Candidates(){
            return new List<SweepCandidate>{
                new(){ Name = "A shipped" },
                new(){ Name = "B T/F wavelength ×0.7", Graph = g => Wavelength(g, 0.7f) },
                new(){ Name = "C T/F wavelength ×1.4", Graph = g => Wavelength(g, 1.4f) },
                new(){ Name = "D T/F wavelength ×0.5 (V3 start ×0.7)", Graph = g => Wavelength(g, 0.5f) },
                new(){ Name = "E C curve knots +0.03 (more sea)", Graph = g => CurveShift(g, 0.03) },
                new(){ Name = "F C curve knots −0.03 (less sea)", Graph = g => CurveShift(g, -0.03) },
                new(){ Name = "G massif E < −0.15 (core only)", Layout = l => MoveEdge(l, 3, MassifE, -0.15f) },
                new(){ Name = "H massif E < −0.25", Layout = l => MoveEdge(l, 3, MassifE, -0.25f) },
                new(){ Name = "I T/F edges ±0.06 / ±0.24", Layout = l => BandEdges(l, 0.06f, 0.24f) },
                new(){ Name = "J T/F edges ±0.12 / ±0.33", Layout = l => BandEdges(l, 0.12f, 0.33f) },
                new(){ Name = "K Swamp from E ≥ 0.30", Layout = l => MoveEdge(l, 3, SwampE, 0.30f) },
                new(){ Name = "L Mountain band |W| < 0.15", Layout = l => RidgeBand(l, 0.15f) },
                new(){ Name = "M Mountain band |W| < 0.25", Layout = l => RidgeBand(l, 0.25f) },
                new(){ Name = "S start massif edges and wavelengths (ridge band kept)", Graph = g => Wavelength(g, 1f / 1.4f), Layout = l => {
                    MoveEdge(l, 2, MassifC, StartMassifC);
                    MoveEdge(l, 3, MassifE, StartMassifE);
                } }
            };
        }

        /// <summary>Scales the temperature and humidity wavelengths (the biome sizes on the T × F table).</summary>
        public static void Wavelength(NoiseGraph g, float factor){
            foreach (string name in new[]{ "temperature", "humidity" }){
                NoiseDefinition n = g.Noises[name];
                n.Wavelength *= factor;
                n.WavelengthY = n.Wavelength; // a 2D noise keeps both equal (the resolver checks it)
            }
        }

        /// <summary>Moves the interior knots of the C curve along the raw continentalness (+: more area in the ocean bands).</summary>
        public static void CurveShift(NoiseGraph g, double shift){
            GraphNodeDefinition node = g.Nodes["continentalness_curve"];
            List<object> points = (List<object>)node.Fields["points"];
            for (int i = 1; i < points.Count - 1; i++){
                Dictionary<string, object> p = (Dictionary<string, object>)points[i];
                p["location"] = Convert.ToDouble(p["location"], CultureInfo.InvariantCulture) + shift;
            }
        }

        /// <summary>Moves every box edge at <paramref name="from" /> on parameter <paramref name="d" /> to <paramref name="to" />.</summary>
        public static void MoveEdge(BiomeLayout l, int d, float from, float to){
            foreach (LayoutBox b in l.Boxes){
                if (Math.Abs(b.Min[d] - from) < 1e-4f) b.Min[d] = to;
                if (Math.Abs(b.Max[d] - from) < 1e-4f) b.Max[d] = to;
            }
        }

        /// <summary>Moves the edges ±<see cref="RidgeW" /> of the Mountain ridge band to ±<paramref name="w" />.</summary>
        public static void RidgeBand(BiomeLayout l, float w){
            MoveEdge(l, 4, RidgeW, w);
            MoveEdge(l, 4, -RidgeW, -w);
        }

        /// <summary>Moves the T and F band edges ±<see cref="Inner" /> and ±<see cref="Outer" />.</summary>
        public static void BandEdges(BiomeLayout l, float inner, float outer){
            for (int d = 0; d < 2; d++){
                MoveEdge(l, d, Inner, inner);
                MoveEdge(l, d, -Inner, -inner);
                MoveEdge(l, d, Outer, outer);
                MoveEdge(l, d, -Outer, -outer);
            }
        }

        /// <summary>Runs <paramref name="candidate" /> on the seeds; the validator's errors stop it.</summary>
        public static SweepRow Run(SweepCandidate candidate, BiomeSet set, IEnumerable<uint> seeds = null, WorldReportOptions options = null,
            Action<string> progress = null){
            SweepRow row = new(){ Candidate = candidate };
            NoiseGraph graph = NoiseGraphJson.Parse(ShippedNoiseGraph.LoadJson());
            candidate.Graph?.Invoke(graph);
            GraphCompileResult compile = GraphCompiler.Compile(graph, candidate.Name);
            if (!compile.Succeeded){
                row.Error = string.Join("; ", compile.Messages);
                return row;
            }

            using CompiledNoiseGraph compiled = compile.Graph;
            BiomeLayout layout = BiomeLayout.FromBiomeSet(set);
            candidate.Layout?.Invoke(layout);
            LayoutValidation validation = BiomeLayoutValidator.Validate(layout, compiled);
            if (validation.HasErrors){
                row.Error = "layout errors: " + validation.ErrorText();
                return row;
            }

            BiomeLayoutData data = BiomeLayoutData.Create(layout, null, Allocator.Persistent);
            try{
                foreach (uint seed in seeds ?? BiomeWorldReport.Seeds){
                    progress?.Invoke($"{candidate.Name} · seed {seed}");
                    row.Seeds.Add(BiomeWorldReport.Run(compiled, layout, data, seed, options ?? WorldReportOptions.Sweep));
                }
            }
            finally{
                data.Dispose();
            }

            return row;
        }

        /// <summary>The sweep table: means over the seeds (the window coverage: the lowest seed).</summary>
        public static string Table(IReadOnlyList<SweepRow> rows){
            CultureInfo culture = CultureInfo.CurrentCulture;
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            try{
                return TableText(rows);
            }
            finally{
                CultureInfo.CurrentCulture = culture;
            }
        }

        private static string TableText(IReadOnlyList<SweepRow> rows){
            StringBuilder sb = new();
            string[] names = rows.SelectMany(r => r.Seeds).Select(s => s.Names).FirstOrDefault() ?? Array.Empty<string>();
            sb.AppendLine("candidate                                   | sea band | water | Mount. | Badl. land | Badl. ≥180 | other ≥204 | med. patch km² | small | windows (lowest) | " +
                          "Jungle/Plains/Sav. wet | Jungle p50/p95 | implaus. edges | targets passed");
            foreach (SweepRow r in rows){
                if (r.Error != null){
                    sb.AppendLine($"{r.Candidate.Name,-43} | {r.Error}");
                    continue;
                }

                WorldReportSeed first = r.Seeds[0];
                int mountain = first.Id("Mountain"), badlands = first.Id("Badlands"), jungle = first.Id("Jungle"), plains = first.Id("Plains"), savanna = first.Id("Savanna");
                double lowestWindow = double.NaN;
                string lowestName = "-";
                foreach (WorldReportSeed s in r.Seeds)
                    for (int b = 0; b < s.Names.Length; b++){
                        if (s.IsWater[b] || r.Mean(x => x.LandShare[b]) < BiomeWorldReport.CommonLandShare || double.IsNaN(s.WindowCoverage[b])) continue;
                        if (double.IsNaN(lowestWindow) || s.WindowCoverage[b] < lowestWindow){
                            lowestWindow = s.WindowCoverage[b];
                            lowestName = s.Names[b];
                        }
                    }

                int passed = r.Seeds.Sum(s => s.Targets.Count(t => t.Pass)), total = r.Seeds.Sum(s => s.Targets.Count);
                sb.AppendLine($"{r.Candidate.Name,-43} | {P(r.Mean(s => s.OceanBandShare)),8} | {P(r.Mean(s => s.WaterShare)),5} | {P(r.Mean(s => s.AreaShare[mountain])),6} | " +
                              $"{P(r.Mean(s => s.LandShare[badlands])),10} | {P(r.Mean(s => s.Badlands180Share)),10} | {P(r.Mean(s => s.Other204Share)),10} | " +
                              $"{r.Mean(s => s.MedianPatchKm2),14:0.00} | {P(r.Mean(s => s.SmallPatchLandShare), "0.00"),5} | {P(lowestWindow, "0") + " " + lowestName,16} | " +
                              $"{P(r.Mean(s => s.WetShare[jungle]), "0")}/{P(r.Mean(s => s.WetShare[plains]), "0")}/{P(r.Mean(s => s.WetShare[savanna]), "0"),-12} | " +
                              $"{r.Mean(s => s.HeightP50[jungle]):0}/{r.Mean(s => s.HeightP95[jungle]):0}{"",-8} | {P(r.Mean(s => s.ImplausibleEdgeShare), "0.000"),14} | {passed}/{total}");
            }

            sb.AppendLine();
            sb.AppendLine("Biome shares of the area (mean over the seeds):");
            sb.AppendLine($"{"candidate",-43} | " + string.Join(" | ", names.Select(n => $"{n,8}")));
            foreach (SweepRow r in rows.Where(r => r.Error == null))
                sb.AppendLine($"{r.Candidate.Name,-43} | " + string.Join(" | ", names.Select((_, b) => $"{P(r.Mean(s => s.AreaShare[b])),8}")));
            return sb.ToString();
        }

        private static string P(double v, string format = "0.0"){
            return BiomeWorldReport.P(v, format);
        }
    }
}
