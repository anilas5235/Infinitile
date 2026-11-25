using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Engine.Scripts.WorldGen.Biomes;
using Engine.Scripts.WorldGen.Graph;
using Engine.Scripts.WorldGen.Settings;
using Unity.Collections;
using Unity.Mathematics;

namespace Engine.Editor.WorldGen{
    /// <summary>Map sizes of a <see cref="BiomeWorldReport" /> run; the defaults are §13.4's.</summary>
    public sealed class WorldReportOptions{
        /// <summary>Shares and patches: 8192² blocks at stride 8.</summary>
        public int Extent = 8192, Stride = 8;

        /// <summary>Heights and edges: 4096² at stride 4 (0 skips the map).</summary>
        public int HeightExtent = 4096, HeightStride = 4;

        /// <summary>8 km windows every 2 km on 32768² at stride 32 (0 skips them).</summary>
        public int WindowExtent = 32768, WindowStride = 32;

        public const int WindowSize = 8000, WindowStep = 2000;

        /// <summary>The world's water level: the wetness test runs one block below it, as in the chunks (AD-40).</summary>
        public int WaterLevel = WorldGenSettings.DefaultWaterLevel;

        /// <summary>The sweep's cheaper maps: heights at stride 8.</summary>
        public static WorldReportOptions Sweep => new(){ HeightStride = 8 };
    }

    /// <summary>A place for the tour list: a column and why it is listed.</summary>
    public struct TourSpot{
        public string What;
        public int X, Z, Y;

        public override string ToString(){
            return $"{What}: teleport {X} {Y} {Z}";
        }
    }

    /// <summary>One target of §13.5 on one seed.</summary>
    public sealed class TargetResult{
        public string Name, Bound, Value;
        public bool Pass;

        /// <summary>The worst place, when the target has one.</summary>
        public TourSpot? Worst;
    }

    /// <summary>The world report's numbers for one seed.</summary>
    public sealed class WorldReportSeed{
        public uint Seed;
        public string[] Names;
        public bool[] IsWater;

        /// <summary>Per biome: share of all columns, share of the dry columns, share of its columns under water.</summary>
        public double[] AreaShare, LandShare, WetShare;

        /// <summary>Water (air one block below the water level), the ocean bands (a water biome before the dry-column rule), the dry-column rule's columns.</summary>
        public double WaterShare, OceanBandShare, SubstituteShare;

        public long OceanOnDry;

        /// <summary>Area-weighted median of the land patches, and the land in patches smaller than 64 × 64.</summary>
        public double MedianPatchKm2, SmallPatchLandShare;

        /// <summary>Per biome: the share of the 8 km windows it appears in (NaN without windows).</summary>
        public double[] WindowCoverage;

        public int Windows;

        /// <summary>Per biome: top-voxel percentiles (p50, p95) on the height map, or NaN.</summary>
        public double[] HeightP50, HeightP95;

        public double ImplausibleEdgeShare, Badlands180Share, Other204Share;
        public long ImplausibleEdges, DifferentEdges;
        public int MountainRingedByBadlands;

        public readonly List<TourSpot> Tour = new();
        public readonly List<TargetResult> Targets = new();
        public TourSpot? ImplausibleEdgeSpot, RingSpot;

        public int Id(string name){
            return Array.IndexOf(Names, name);
        }
    }

    /// <summary>
    ///     The world report of WorldGen v2 (§13.4): from <see cref="BiomeColumnSampler" /> on the 4-block lattice, so the
    ///     biome, wet flag and top voxel of every sample equal the chunk's. Maps per seed: 8192² at stride 8 (shares,
    ///     wet shares, patches), 4096² at stride 4 (heights and edges between biomes), 32768² at stride 32 (8 km windows
    ///     every 2 km); then the targets of §13.5 and the tour list (§13.6). The sweep (§6.7) runs the same with cheaper
    ///     height maps.
    /// </summary>
    public static class BiomeWorldReport{
        /// <summary>The five fixed seeds of the world report and the sweep (DEC-10).</summary>
        public static readonly uint[] Seeds ={ 1337u, 1u, 2u, 3u, 4u };

        /// <summary>A biome is common when it holds at least this share of the land on average.</summary>
        public const double CommonLandShare = 0.03;

        public static WorldReportSeed Run(CompiledNoiseGraph graph, BiomeLayout layout, in BiomeLayoutData data, uint seed, WorldReportOptions o = null){
            o ??= new WorldReportOptions();
            int nb = layout.Biomes.Count;
            WorldReportSeed r = new(){
                Seed = seed,
                Names = layout.Biomes.Select(b => b.Name).ToArray(),
                IsWater = layout.Biomes.Select(b => b.IsWater).ToArray(),
                AreaShare = new double[nb],
                LandShare = new double[nb],
                WetShare = new double[nb],
                WindowCoverage = Enumerable.Repeat(double.NaN, nb).ToArray(),
                HeightP50 = Enumerable.Repeat(double.NaN, nb).ToArray(),
                HeightP95 = Enumerable.Repeat(double.NaN, nb).ToArray()
            };

            ShareMap(graph, layout, data, seed, o, r);
            if (o.HeightExtent > 0) HeightMap(graph, layout, data, seed, o, r);
            if (o.WindowExtent > 0) WindowMap(graph, data, seed, o, r);
            Evaluate(r, o);
            return r;
        }

        private static NativeArray<float2> Grid(int extent, int stride, out int side){
            side = extent / stride;
            NativeArray<float2> points = new(side * side, Allocator.TempJob);
            int origin = -extent / 2;
            for (int z = 0; z < side; z++)
            for (int x = 0; x < side; x++)
                points[z * side + x] = new float2(origin + x * stride, origin + z * stride);
            return points;
        }

        private static void ShareMap(CompiledNoiseGraph graph, BiomeLayout layout, in BiomeLayoutData data, uint seed, WorldReportOptions o, WorldReportSeed r){
            int nb = r.Names.Length;
            using NativeArray<float2> points = Grid(o.Extent, o.Stride, out int side);
            using BiomeColumns c = BiomeColumnSampler.Sample(graph, data, seed, points, waterLevel: o.WaterLevel);
            int n = side * side;
            long[] count = new long[nb], wet = new long[nb];
            long water = 0, dry = 0, band = 0, substituted = 0;
            byte[] map = new byte[n];
            for (int i = 0; i < n; i++){
                byte b = c.Biome[i];
                map[i] = b;
                bool isWet = c.Wet[i] != 0;
                if (isWet) water++;
                else dry++;
                if (c.Substituted[i] != 0) substituted++;
                if (b >= nb) continue;
                count[b]++;
                if (isWet) wet[b]++;
                if (r.IsWater[b] || c.Substituted[i] != 0) band++;
                if (r.IsWater[b] && !isWet) r.OceanOnDry++;
            }

            long[] land = new long[nb];
            for (int i = 0; i < n; i++)
                if (c.Wet[i] == 0 && map[i] < nb)
                    land[map[i]]++;
            for (int b = 0; b < nb; b++){
                r.AreaShare[b] = (double)count[b] / n;
                r.LandShare[b] = dry > 0 ? (double)land[b] / dry : 0;
                r.WetShare[b] = count[b] > 0 ? (double)wet[b] / count[b] : double.NaN;
            }

            r.WaterShare = (double)water / n;
            r.OceanBandShare = (double)band / n;
            r.SubstituteShare = (double)substituted / n;

            // Patches: 4-connected samples of one biome.
            List<Patch> patches = Patches(map, side);
            double km2 = (double)o.Stride * o.Stride / 1e6;
            int small = (int)math.ceil(64.0 * 64.0 / (o.Stride * o.Stride));
            List<Patch> landPatches = patches.Where(p => p.Biome < nb && !r.IsWater[p.Biome]).OrderBy(p => p.Size).ToList();
            long landSamples = landPatches.Sum(p => (long)p.Size);
            long half = landSamples / 2, run = 0;
            foreach (Patch p in landPatches){
                run += p.Size;
                if (run < half) continue;
                r.MedianPatchKm2 = p.Size * km2;
                break;
            }

            r.SmallPatchLandShare = landSamples > 0 ? (double)landPatches.Where(p => p.Size < small).Sum(p => (long)p.Size) / landSamples : 0;

            int origin = -o.Extent / 2;
            for (int b = 0; b < nb; b++){
                List<Patch> of = patches.Where(p => p.Biome == b).ToList();
                if (of.Count == 0) continue;
                Patch largest = of.OrderByDescending(p => p.Size).First();
                // The smallest patch that is not cut by the map edge, else the smallest.
                Patch smallest = of.Where(p => !p.TouchesEdge).OrderBy(p => p.Size).FirstOrDefault() ?? of.OrderBy(p => p.Size).First();
                r.Tour.Add(Spot(FormattableString.Invariant($"{r.Names[b]}: largest patch {largest.Size * km2:0.##} km²"), largest, origin, o.Stride, c.Surface));
                r.Tour.Add(Spot(FormattableString.Invariant($"{r.Names[b]}: smallest patch {smallest.Size * km2 * 1e6:0} m²"), smallest, origin, o.Stride, c.Surface));
            }
        }

        private static TourSpot Spot(string what, Patch p, int origin, int stride, NativeArray<float> surface){
            return new TourSpot{
                What = what,
                X = origin + p.X * stride,
                Z = origin + p.Z * stride,
                Y = (int)math.max(surface[p.Index], 124f) + 3
            };
        }

        private static void HeightMap(CompiledNoiseGraph graph, BiomeLayout layout, in BiomeLayoutData data, uint seed, WorldReportOptions o, WorldReportSeed r){
            int nb = r.Names.Length;
            using NativeArray<float2> points = Grid(o.HeightExtent, o.HeightStride, out int side);
            using BiomeColumns c = BiomeColumnSampler.Sample(graph, data, seed, points, true, waterLevel: o.WaterLevel);
            int n = side * side;
            int origin = -o.HeightExtent / 2;
            List<short>[] heights = new List<short>[nb];
            for (int b = 0; b < nb; b++) heights[b] = new List<short>();
            int badlands = r.Id("Badlands"), mountain = r.Id("Mountain");
            long above180 = 0, badlands180 = 0, above204 = 0, other204 = 0;
            int peak = 0;
            for (int i = 0; i < n; i++){
                int b = c.Biome[i];
                short top = c.Top[i];
                if (b < nb && c.Wet[i] == 0) heights[b].Add(top);
                if (top >= 180){
                    above180++;
                    if (b == badlands) badlands180++;
                }

                if (top >= 204){
                    above204++;
                    if (b != badlands && b != mountain) other204++;
                }

                if (top > c.Top[peak]) peak = i;
            }

            for (int b = 0; b < nb; b++){
                if (heights[b].Count == 0) continue;
                heights[b].Sort();
                r.HeightP50[b] = heights[b][heights[b].Count / 2];
                r.HeightP95[b] = heights[b][Math.Min(heights[b].Count - 1, (int)(heights[b].Count * 0.95))];
            }

            r.Badlands180Share = above180 > 0 ? (double)badlands180 / above180 : 0;
            r.Other204Share = above204 > 0 ? (double)other204 / above204 : 0;
            r.Tour.Add(new TourSpot{ What = $"highest peak y {c.Top[peak]}", X = origin + peak % side * o.HeightStride, Z = origin + peak / side * o.HeightStride, Y = c.Top[peak] + 3 });

            // Edges between neighbouring samples of different biomes, and those between implausible pairs.
            for (int z = 0; z < side; z++)
            for (int x = 0; x < side; x++){
                int i = z * side + x;
                for (int k = 0; k < 2; k++){
                    int j = k == 0 ? (x + 1 < side ? i + 1 : -1) : (z + 1 < side ? i + side : -1);
                    if (j < 0 || c.Biome[i] == c.Biome[j]) continue;
                    r.DifferentEdges++;
                    if (!layout.IsImplausible(c.Biome[i], c.Biome[j])) continue;
                    if (r.ImplausibleEdges++ == 0)
                        r.ImplausibleEdgeSpot = new TourSpot{
                            What = $"implausible edge {r.Names[c.Biome[i]]} | {r.Names[c.Biome[j]]}", X = origin + x * o.HeightStride, Z = origin + z * o.HeightStride,
                            Y = c.Top[i] + 3
                        };
                }
            }

            r.ImplausibleEdgeShare = r.DifferentEdges > 0 ? (double)r.ImplausibleEdges / r.DifferentEdges : 0;

            // Mountain patches whose outer ring is more than half Badlands.
            if (mountain >= 0 && badlands >= 0){
                byte[] map = new byte[n];
                for (int i = 0; i < n; i++) map[i] = c.Biome[i];
                foreach (Patch p in Patches(map, side).Where(p => p.Biome == mountain)){
                    int ring = 0, bad = 0;
                    foreach (int i in p.Ring){
                        ring++;
                        if (map[i] == badlands) bad++;
                    }

                    if (ring == 0 || bad * 2 <= ring) continue;
                    if (r.MountainRingedByBadlands++ == 0)
                        r.RingSpot = new TourSpot{ What = "Mountain patch ringed by Badlands", X = origin + p.X * o.HeightStride, Z = origin + p.Z * o.HeightStride, Y = c.Top[p.Index] + 3 };
                }
            }
        }

        private static void WindowMap(CompiledNoiseGraph graph, in BiomeLayoutData data, uint seed, WorldReportOptions o, WorldReportSeed r){
            int nb = r.Names.Length;
            using NativeArray<float2> points = Grid(o.WindowExtent, o.WindowStride, out int side);
            using BiomeColumns c = BiomeColumnSampler.Sample(graph, data, seed, points, waterLevel: o.WaterLevel);
            int size = WorldReportOptions.WindowSize / o.WindowStride, step = WorldReportOptions.WindowStep / o.WindowStride;
            int[] present = new int[nb];
            int windows = 0;
            bool[] seen = new bool[nb];
            for (int z0 = 0; z0 + size <= side; z0 += step)
            for (int x0 = 0; x0 + size <= side; x0 += step){
                Array.Clear(seen, 0, nb);
                for (int z = z0; z < z0 + size; z++)
                for (int x = x0; x < x0 + size; x++){
                    byte b = c.Biome[z * side + x];
                    if (b < nb) seen[b] = true;
                }

                windows++;
                for (int b = 0; b < nb; b++)
                    if (seen[b])
                        present[b]++;
            }

            r.Windows = windows;
            for (int b = 0; b < nb; b++) r.WindowCoverage[b] = windows > 0 ? (double)present[b] / windows : double.NaN;
        }

        private sealed class Patch{
            public int Biome, Size, Index, X, Z;
            public bool TouchesEdge;
            public List<int> Ring;
        }

        /// <summary>4-connected patches of one biome; each with a sample near its centroid and its outer ring.</summary>
        private static List<Patch> Patches(byte[] map, int side){
            int[] label = new int[map.Length];
            List<Patch> patches = new();
            Stack<int> stack = new();
            List<int> members = new();
            for (int s = 0; s < map.Length; s++){
                if (label[s] != 0) continue;
                byte b = map[s];
                int id = patches.Count + 1;
                label[s] = id;
                stack.Push(s);
                members.Clear();
                long sx = 0, sz = 0;
                bool edge = false;
                HashSet<int> ring = new();
                while (stack.Count > 0){
                    int i = stack.Pop();
                    members.Add(i);
                    int x = i % side, z = i / side;
                    sx += x;
                    sz += z;
                    if (x == 0 || z == 0 || x == side - 1 || z == side - 1) edge = true;
                    for (int k = 0; k < 4; k++){
                        int nx = x + (k == 0 ? 1 : k == 1 ? -1 : 0), nz = z + (k == 2 ? 1 : k == 3 ? -1 : 0);
                        if (nx < 0 || nz < 0 || nx >= side || nz >= side) continue;
                        int j = nz * side + nx;
                        if (map[j] != b){
                            ring.Add(j);
                            continue;
                        }

                        if (label[j] != 0) continue;
                        label[j] = id;
                        stack.Push(j);
                    }
                }

                float cx = (float)sx / members.Count, cz = (float)sz / members.Count;
                int best = members[0];
                float bestD = float.MaxValue;
                foreach (int i in members){
                    float dx = i % side - cx, dz = i / side - cz, d = dx * dx + dz * dz;
                    if (d < bestD){
                        bestD = d;
                        best = i;
                    }
                }

                patches.Add(new Patch{ Biome = b, Size = members.Count, Index = best, X = best % side, Z = best / side, TouchesEdge = edge, Ring = ring.ToList() });
            }

            return patches;
        }

        // --- Targets (§13.5) ----------------------------------------------------------------------------------------

        private static void Evaluate(WorldReportSeed r, WorldReportOptions o){
            int jungle = r.Id("Jungle"), plains = r.Id("Plains"), savanna = r.Id("Savanna"), badlands = r.Id("Badlands");
            void Add(string name, string bound, double value, string format, bool pass, TourSpot? worst = null){
                r.Targets.Add(new TargetResult{ Name = name, Bound = bound, Value = value.ToString(format, CultureInfo.InvariantCulture), Pass = pass, Worst = worst });
            }

            Add("Share of the area in the ocean bands", "30 % ± 2", r.OceanBandShare * 100, "0.0", math.abs(r.OceanBandShare - 0.30) <= 0.02);
            Add("Ocean on dry columns", "0", r.OceanOnDry, "0", r.OceanOnDry == 0);
            if (o.HeightExtent > 0){
                Add("Edges between implausible pairs (% of biome edges)", "0", r.ImplausibleEdgeShare * 100, "0.000", r.ImplausibleEdges == 0, r.ImplausibleEdgeSpot);
                Add("Badlands share of the ground at 180 and above (%)", "< 30", r.Badlands180Share * 100, "0.0", r.Badlands180Share < 0.30);
                Add("Ground at 204 and above that is neither Mountain nor Badlands (%)", "< 6", r.Other204Share * 100, "0.0", r.Other204Share < 0.06);
                Add("Mountain patches whose outer ring is more than half Badlands", "0", r.MountainRingedByBadlands, "0", r.MountainRingedByBadlands == 0, r.RingSpot);
                if (jungle >= 0){
                    Add("Jungle height at p95", "< 182", r.HeightP95[jungle], "0", !(r.HeightP95[jungle] >= 182));
                    Add("Jungle median height", "140–145", r.HeightP50[jungle], "0", r.HeightP50[jungle] >= 140 && r.HeightP50[jungle] <= 145);
                }
            }

            if (badlands >= 0) Add("Badlands share of the land (%)", "< 7", r.LandShare[badlands] * 100, "0.0", r.LandShare[badlands] < 0.07);
            if (jungle >= 0) Add("Jungle columns under water (%)", "< 25", r.WetShare[jungle] * 100, "0.0", !(r.WetShare[jungle] >= 0.25)); // DEC-199 (was 20)
            if (plains >= 0) Add("Plains columns under water (%)", "< 28", r.WetShare[plains] * 100, "0.0", !(r.WetShare[plains] >= 0.28));
            if (savanna >= 0) Add("Savanna columns under water (%)", "< 32", r.WetShare[savanna] * 100, "0.0", !(r.WetShare[savanna] >= 0.32));
            Add("Land in single-biome patches smaller than 64 × 64 (%)", "< 2", r.SmallPatchLandShare * 100, "0.00", r.SmallPatchLandShare < 0.02);
            Add("Median patch size (km²)", "≥ 1", r.MedianPatchKm2, "0.00", r.MedianPatchKm2 >= 1);
            if (r.Windows > 0){
                double worst = double.MaxValue;
                string worstName = "-";
                for (int b = 0; b < r.Names.Length; b++){
                    if (r.IsWater[b] || r.LandShare[b] < CommonLandShare) continue;
                    if (r.WindowCoverage[b] < worst){
                        worst = r.WindowCoverage[b];
                        worstName = r.Names[b];
                    }
                }

                if (worstName != "-") Add($"Common biomes in 8 km windows (lowest: {worstName}, %)", "≥ 90", worst * 100, "0", worst >= 0.90);
            }
        }

        // --- Text ---------------------------------------------------------------------------------------------------

        public static string Text(WorldReportSeed r, string title = null){
            StringBuilder sb = new();
            sb.AppendLine($"=== World report{(title != null ? " · " + title : "")} · seed {r.Seed} ===");
            sb.AppendLine($"water {P(r.WaterShare)} · ocean bands {P(r.OceanBandShare)} · dry-column rule {P(r.SubstituteShare, "0.00")} · median land patch {r.MedianPatchKm2.ToString("0.00", CultureInfo.InvariantCulture)} km² · " +
                          $"small patches {P(r.SmallPatchLandShare, "0.00")} of the land · windows {r.Windows}");
            sb.AppendLine("biome        area   land  wet   p50  p95  windows");
            for (int b = 0; b < r.Names.Length; b++)
                sb.AppendLine($"{r.Names[b],-11} {P(r.AreaShare[b]),6} {P(r.LandShare[b]),6} {P(r.WetShare[b], "0"),4} {H(r.HeightP50[b]),4} {H(r.HeightP95[b]),4}  {P(r.WindowCoverage[b], "0"),5}");
            sb.AppendLine("targets:");
            foreach (TargetResult t in r.Targets)
                sb.AppendLine($"  [{(t.Pass ? "pass" : "FAIL")}] {t.Name}: {t.Value} (target {t.Bound}){(t.Pass || t.Worst == null ? "" : " · worst: " + t.Worst)}");
            sb.AppendLine("  [ open ] Carved voxels connected (once caves are built) · snow, ice and the snow line: see snow.txt (SnowReport_FiveSeeds)");
            sb.AppendLine("tour list:");
            foreach (TourSpot s in r.Tour) sb.AppendLine("  " + s);
            foreach (TargetResult t in r.Targets.Where(t => !t.Pass && t.Worst != null)) sb.AppendLine("  worst place · " + t.Worst);
            return sb.ToString();
        }

        public static string P(double v, string format = "0.0"){
            return double.IsNaN(v) ? "-" : (v * 100).ToString(format, CultureInfo.InvariantCulture) + "%";
        }

        private static string H(double v){
            return double.IsNaN(v) ? "-" : v.ToString("0", CultureInfo.InvariantCulture);
        }
    }
}
