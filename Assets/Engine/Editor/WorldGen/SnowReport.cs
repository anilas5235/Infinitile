using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Engine.Scripts.WorldGen.Biomes;
using Engine.Scripts.WorldGen.Density;
using Engine.Scripts.WorldGen.Graph;
using Engine.Scripts.WorldGen.Settings;
using Engine.Scripts.WorldGen.Surface;
using Unity.Collections;
using Unity.Mathematics;

namespace Engine.Editor.WorldGen{
    /// <summary>The snow, ice and tree line measures of one seed (WorldGen v2 §8, phase V4).</summary>
    public sealed class SnowReportSeed{
        public uint Seed;
        public string[] Names;

        /// <summary>T of every dry Mountain sample (the calibration pools them over the seeds, DEC-175).</summary>
        public float[] MountainT = Array.Empty<float>();

        public float MedianMountainT = float.NaN;

        /// <summary>The snow line (lowest snowy y) at the median Mountain T without wobble.</summary>
        public int SnowLineAtMedian = -1;

        /// <summary>Mountain ground at y 200 and above, in five T bands of equal count: the band's T range and snow share.</summary>
        public readonly List<(float From, float To, int Samples, double Snow)> MountainBands = new();

        /// <summary>Per biome: dry samples, their snow share, and (never-snow biomes) the share above their snow line.</summary>
        public long[] Dry;

        public double[] SnowShare, WouldSnowShare;

        /// <summary>Neighbour pairs where snow stops at a never-snow biome above its snow line, and all snow edges.</summary>
        public long HardEdges, SnowEdges;

        /// <summary>Share of the open water samples (top below the water level − 1) that freeze.</summary>
        public double IceShare;

        /// <summary>
        ///     River water (the river ice's weight, DEC-207): samples, the share that freezes, the share the sea rule alone
        ///     would freeze, and of the frozen ones the share with a snowy dry neighbour sample (W2-CLM-05's order).
        /// </summary>
        public long RiverWater;

        public double RiverIceShare, RiverSeaRuleShare, FrozenRiverSnowyBank;

        public readonly List<TourSpot> Tour = new();
    }

    /// <summary>
    ///     The snow measures of the world report (§13.4, phase V4) on a grid of columns with the column sampler: T, the
    ///     snow and tree line per column from the compiled rules' climate, and the hard biome as the surface biome (the
    ///     blend can move it a few blocks at borders). Snow lies on a dry top at or above the column's snow line in a
    ///     biome that may snow, as the snow rule puts it.
    /// </summary>
    public static class SnowReport{
        public const int Extent = 4096, Stride = 8;

        public static SnowReportSeed Run(CompiledNoiseGraph graph, BiomeLayout layout, in BiomeLayoutData data, in SurfaceRules rules, SurfaceClimate climate, uint seed,
            int extent = Extent, int stride = Stride, int waterLevel = WorldGenSettings.DefaultWaterLevel){
            int nb = layout.Biomes.Count;
            SnowReportSeed r = new(){
                Seed = seed, Names = layout.Biomes.Select(b => b.Name).ToArray(), Dry = new long[nb], SnowShare = new double[nb], WouldSnowShare = new double[nb]
            };
            int side = extent / stride, n = side * side, origin = -extent / 2;
            float2[] grid = new float2[n];
            for (int z = 0; z < side; z++)
            for (int x = 0; x < side; x++)
                grid[z * side + x] = new float2(origin + x * stride, origin + z * stride);

            using NativeArray<float2> points = new(grid, Allocator.TempJob);
            using BiomeColumns c = BiomeColumnSampler.Sample(graph, data, seed, points, true, waterLevel: waterLevel);
            float[] lattice = GraphEvaluator.SampleLattice(graph, seed, grid, out _);
            float Value(int slot, int i){
                return slot >= 0 ? lattice[slot * n + i] : 0f;
            }

            int Slot(string output){
                GraphOutputInfo o = output != null ? graph.Output(output) : null;
                return o != null ? o.LatticeValue : -1;
            }

            int tSlot = rules.TemperatureSlot, snowSlot = Slot(climate.SnowWobble), treeSlot = Slot(climate.TreeLineWobble), iceSlot = Slot(climate.IceWobble);
            int surfaceY = DensityLayout.WetnessY(waterLevel);
            int mountain = Array.IndexOf(r.Names, "Mountain");

            bool[] snow = new bool[n], dry = new bool[n], never = new bool[n], above = new bool[n];
            int[] snowLine = new int[n], treeLine = new int[n];
            long[] snowy = new long[nb], would = new long[nb];
            long water = 0, ice = 0, riverIce = 0, riverSeaRule = 0;
            float riverMin = rules.RiverIce >= 0 ? rules.RiverIceMin : 0.3f;
            List<int> frozenRivers = new();
            List<float> mountainT = new();
            List<(float T, bool Snow)> high = new();
            for (int i = 0; i < n; i++){
                int b = c.Biome[i];
                short top = c.Top[i];
                float t = Value(tSlot, i);
                snowLine[i] = rules.ColdThreshold(t, Value(snowSlot, i), rules.SnowTemperature);
                treeLine[i] = rules.TreeLine(t, Value(treeSlot, i));
                dry[i] = top >= surfaceY;
                never[i] = b < nb && data.Defs[b].neverSnows != 0;
                above[i] = dry[i] && top >= snowLine[i];
                snow[i] = above[i] && !never[i];
                if (!dry[i]){
                    water++;
                    bool river = c.River[i] >= riverMin && riverMin > 0f;
                    bool seaCold = rules.ColdAt(surfaceY, t, Value(iceSlot, i), rules.IceTemperature);
                    bool riverCold = rules.RiverIce >= 0 && river && rules.ColdAt(surfaceY, t, 0f, rules.RiverIceTemperature);
                    if (river){
                        r.RiverWater++;
                        if (!never[i] && seaCold) riverSeaRule++;
                    }

                    if (!never[i] && (seaCold || riverCold)){
                        ice++;
                        if (river){
                            riverIce++;
                            frozenRivers.Add(i);
                            if (r.Tour.All(s => !s.What.StartsWith("frozen river", StringComparison.Ordinal)))
                                r.Tour.Add(new TourSpot{ What = $"frozen river ({r.Names[b]}, T {F(t)})", X = (int)grid[i].x, Z = (int)grid[i].y, Y = surfaceY + 12 });
                        }
                        else if (r.Tour.All(s => !s.What.StartsWith("sea ice", StringComparison.Ordinal)))
                            r.Tour.Add(new TourSpot{ What = $"sea ice ({r.Names[b]}, T {F(t)})", X = (int)grid[i].x, Z = (int)grid[i].y, Y = surfaceY + 12 });
                    }

                    continue;
                }

                if (b >= nb) continue;
                r.Dry[b]++;
                if (snow[i]) snowy[b]++;
                if (above[i] && never[i]) would[b]++;
                if (b == mountain){
                    mountainT.Add(t);
                    if (top >= 200) high.Add((t, snow[i]));
                }
            }

            for (int b = 0; b < nb; b++){
                r.SnowShare[b] = r.Dry[b] > 0 ? (double)snowy[b] / r.Dry[b] : double.NaN;
                r.WouldSnowShare[b] = r.Dry[b] > 0 ? (double)would[b] / r.Dry[b] : double.NaN;
            }

            r.IceShare = water > 0 ? (double)ice / water : 0;
            r.RiverIceShare = r.RiverWater > 0 ? (double)riverIce / r.RiverWater : 0;
            r.RiverSeaRuleShare = r.RiverWater > 0 ? (double)riverSeaRule / r.RiverWater : 0;
            int withBank = 0, snowyBank = 0;
            foreach (int i in frozenRivers){
                int x = i % side, z = i / side;
                bool any = false, bankSnow = false;
                for (int k = 0; k < 4; k++){
                    int nx = x + (k == 0 ? 1 : k == 1 ? -1 : 0), nz = z + (k == 2 ? 1 : k == 3 ? -1 : 0);
                    if (nx < 0 || nz < 0 || nx >= side || nz >= side) continue;
                    int j = nz * side + nx;
                    if (!dry[j]) continue;
                    any = true;
                    // The snow line or the bank rule's temperature test (DEC-229). The neighbour has nearly the river sample's T, so
                    // this share mostly counts the banks outside the never-snow biomes; the rule's band, near_water and steep tests
                    // are not applied here. The game's banks are checked by SurfaceRuleTests.FrozenRiverBanks_CarryASnowLayer.
                    bankSnow |= snow[j] || !never[j] && rules.RiverIce >= 0 && rules.ColdAt(surfaceY, Value(tSlot, j), 0f, rules.RiverIceTemperature);
                }

                if (!any) continue;
                withBank++;
                if (bankSnow) snowyBank++;
            }

            r.FrozenRiverSnowyBank = withBank > 0 ? (double)snowyBank / withBank : double.NaN;
            r.MountainT = mountainT.ToArray();
            if (mountainT.Count > 0){
                mountainT.Sort();
                r.MedianMountainT = mountainT[mountainT.Count / 2];
                r.SnowLineAtMedian = rules.ColdThreshold(r.MedianMountainT, 0f, rules.SnowTemperature);
            }

            high.Sort((a, b) => a.T.CompareTo(b.T));
            for (int k = 0; k < 5 && high.Count >= 5; k++){
                int from = high.Count * k / 5, to = high.Count * (k + 1) / 5;
                int count = to - from, snowCount = 0;
                for (int i = from; i < to; i++)
                    if (high[i].Snow)
                        snowCount++;
                r.MountainBands.Add((high[from].T, high[to - 1].T, count, count > 0 ? (double)snowCount / count : double.NaN));
            }

            // Edges: snow next to no snow. Hard where the snowless side is a never-snow biome above its snow line.
            int hardSpots = 0;
            for (int z = 0; z < side; z++)
            for (int x = 0; x < side; x++){
                int i = z * side + x;
                for (int k = 0; k < 2; k++){
                    int j = k == 0 ? (x + 1 < side ? i + 1 : -1) : (z + 1 < side ? i + side : -1);
                    if (j < 0 || snow[i] == snow[j]) continue;
                    r.SnowEdges++;
                    int bare = snow[i] ? j : i;
                    if (!(above[bare] && never[bare])) continue;
                    r.HardEdges++;
                    if (hardSpots++ < 2)
                        r.Tour.Add(new TourSpot{
                            What = $"hard snow edge {r.Names[c.Biome[i]]} | {r.Names[c.Biome[j]]}", X = (int)grid[i].x, Z = (int)grid[i].y, Y = math.max(c.Top[i], c.Top[j]) + 6
                        });
                }
            }

            // Tour: the snow line at the median Mountain T, an alpine band below it, the highest snowy Mountain ground.
            if (mountain >= 0 && !float.IsNaN(r.MedianMountainT)){
                int best = -1, alpine = -1, peak = -1;
                float bestScore = float.MaxValue;
                for (int i = 0; i < n; i++){
                    if (c.Biome[i] != mountain || !dry[i]) continue;
                    float t = Value(tSlot, i);
                    float score = math.abs(t - r.MedianMountainT) * 100f + math.abs(c.Top[i] - snowLine[i]);
                    if (math.abs(c.Top[i] - snowLine[i]) <= 2 && score < bestScore){
                        bestScore = score;
                        best = i;
                    }

                    if (alpine < 0 && c.Top[i] >= treeLine[i] && c.Top[i] < snowLine[i] - 4) alpine = i;
                    if (snow[i] && (peak < 0 || c.Top[i] > c.Top[peak])) peak = i;
                }

                if (best >= 0)
                    r.Tour.Add(new TourSpot{ What = $"snow line at the median Mountain T (y {snowLine[best]})", X = (int)grid[best].x, Z = (int)grid[best].y, Y = c.Top[best] + 8 });
                if (alpine >= 0)
                    r.Tour.Add(new TourSpot{ What = $"alpine band (tree line {treeLine[alpine]}, snow line {snowLine[alpine]})", X = (int)grid[alpine].x, Z = (int)grid[alpine].y, Y = c.Top[alpine] + 8 });
                if (peak >= 0)
                    r.Tour.Add(new TourSpot{ What = "highest snowy Mountain ground", X = (int)grid[peak].x, Z = (int)grid[peak].y, Y = c.Top[peak] + 8 });
            }

            return r;
        }

        /// <summary>The snow line at the pooled median Mountain T: y 255, by the peaks (DEC-244; V4: 210).</summary>
        public const int CalibrationSnowLine = 255;

        /// <summary>T_snow that puts the snow line at <see cref="CalibrationSnowLine" /> at the median Mountain T (DEC-175, DEC-244): T_med − (255 − lapseFrom) · L = T_med − 128 L.</summary>
        public static float CalibratedSnowTemperature(float medianMountainT, in SurfaceRules rules, int snowLine = CalibrationSnowLine){
            return medianMountainT - (snowLine - rules.LapseFrom) * rules.Lapse;
        }

        public static float Median(IEnumerable<float> values){
            List<float> list = values.ToList();
            if (list.Count == 0) return float.NaN;
            list.Sort();
            return list[list.Count / 2];
        }

        public static string Text(SnowReportSeed r, in SurfaceRules rules){
            StringBuilder sb = new();
            sb.AppendLine($"=== Snow report · seed {r.Seed} ===");
            sb.AppendLine($"median Mountain T {F(r.MedianMountainT)} → snow line y {r.SnowLineAtMedian} (the calibration pools the five seeds: y {CalibrationSnowLine} at their joint median, DEC-244) · " +
                          $"T_snow {F(rules.SnowTemperature)}, L {F(rules.Lapse, "0.0000")} · sea ice {BiomeWorldReport.P(r.IceShare, "0.00")} of the open water");
            sb.AppendLine($"river water {r.RiverWater} samples: frozen {BiomeWorldReport.P(r.RiverIceShare, "0.00")} (the sea rule alone {BiomeWorldReport.P(r.RiverSeaRuleShare, "0.00")}); " +
                          $"frozen river samples with a dry neighbour that has snow or passes the bank rule's temperature test (an upper bound: no band, near-water or steep test): {BiomeWorldReport.P(r.FrozenRiverSnowyBank, "0.0")}");
            sb.AppendLine("Mountain ground at y 200 and above, snow share per T band (equal counts):");
            foreach ((float from, float to, int samples, double share) in r.MountainBands)
                sb.AppendLine($"  T {F(from),6} … {F(to),6}: {BiomeWorldReport.P(share),6} of {samples}");
            sb.AppendLine("biome        dry      snow   above its snow line but never snows");
            for (int b = 0; b < r.Names.Length; b++){
                if (r.Dry[b] == 0) continue;
                sb.AppendLine($"{r.Names[b],-11} {r.Dry[b],7} {BiomeWorldReport.P(r.SnowShare[b]),7}   {BiomeWorldReport.P(r.WouldSnowShare[b], "0.00")}");
            }

            sb.AppendLine($"hard snow edges (snow next to a never-snow biome above its snow line): {r.HardEdges} of {r.SnowEdges} snow edges " +
                          $"({BiomeWorldReport.P(r.SnowEdges > 0 ? (double)r.HardEdges / r.SnowEdges : 0, "0.00")})");
            sb.AppendLine("tour list:");
            foreach (TourSpot s in r.Tour) sb.AppendLine("  " + s);
            return sb.ToString();
        }

        private static string F(float v, string format = "0.000"){
            return float.IsNaN(v) ? "-" : v.ToString(format, CultureInfo.InvariantCulture);
        }
    }
}
