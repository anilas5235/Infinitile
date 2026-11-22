using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Engine.Scripts.Jobs.Chunk;
using Engine.Scripts.WorldGen.Density;
using Engine.Scripts.WorldGen.Graph;
using Engine.Scripts.WorldGen.Settings;
using NUnit.Framework;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;

namespace Engine.Tests.EditMode{
    /// <summary>
    ///     What the density terrain of WorldGen v2 looks like (§5, phase V2) over 4096² columns around the origin, seed
    ///     1337, with the shipped graph and the shipped water level: the top terrain height (percentiles, per 512²
    ///     region), the share under water, the connected areas of high ground above y 180 and the ground at the snow
    ///     line (210), sampled every 4 columns like the v1 terrain look report (so the numbers compare with DEC-108),
    ///     the overhangs (land columns with air under their top voxel above the water, and those air voxels) and the
    ///     floating pieces (terrain not connected to the ground: wholly inside one chunk and not reached from y 0 or the
    ///     chunk sides; pieces that cross a chunk side are not counted). Writes a height map with hillshade to
    ///     <c>Temp/tuning-captures/density-height.png</c>. Prints values and sets no limits.
    /// </summary>
    [Category(TestTiers.Report)]
    [Explicit(TestTiers.ReportReason)]
    public class DensityLookReportTests{
        private const string ShippedSettingsPath = "Assets/EngineSettings/WorldGenSettings.asset";
        private const int Chunks = 128; // 4096 columns
        private const int Columns = Chunks * 32;
        private const int Stride = 4;
        private const int Batch = 256;
        private const int Seed = 1337;

        /// <summary>The z rows (array index, 0 = z −2048) whose vertical slices the candidate dump keeps.</summary>
        /// <summary>The window's center in columns (the candidate report only; the shipped report stays at the origin).</summary>
        private static int2 _center;

        private static readonly int[] SliceRows = Enumerable.Range(0, 16).Select(i => i * 256 + 128).ToArray();

        [BurstCompile]
        private struct ChunkStatsJob : IJob{
            [ReadOnly] public NativeArray<byte> Solid;
            [ReadOnly] public NativeArray<short> Heightmap;
            public int WaterLevel;

            /// <summary>x: overhang columns, y: overhang air voxels, z: floating voxels, w: floating pieces.</summary>
            public NativeArray<int4> Stats;

            public void Execute(){
                const int n = DensityLayout.ChunkSize, h = DensityLayout.Height;
                int overhangColumns = 0, overhangVoxels = 0;
                for (int c = 0; c < n * n; c++){
                    int top = Heightmap[c];
                    int gap = 0;
                    for (int y = math.max(WaterLevel, 0); y < top; y++)
                        if (Solid[c * h + y] == 0) gap++;
                    if (gap > 0) overhangColumns++;
                    overhangVoxels += gap;
                }

                // Reach the terrain from y 0 and the chunk sides; the rest is floating.
                NativeArray<byte> reached = new(DensityLayout.Voxels, Allocator.Temp);
                NativeList<int> stack = new(4096, Allocator.Temp);
                for (int x = 0; x < n; x++)
                for (int z = 0; z < n; z++){
                    bool side = x == 0 || z == 0 || x == n - 1 || z == n - 1;
                    for (int y = 0; y < h; y++){
                        if (!side && y > 0) break;
                        int i = (x * n + z) * h + y;
                        if (Solid[i] != 0 && reached[i] == 0){
                            reached[i] = 1;
                            stack.Add(i);
                        }
                    }
                }

                Flood(ref stack, ref reached, 1);
                int floatingVoxels = 0, pieces = 0;
                for (int i = 0; i < reached.Length; i++){
                    if (Solid[i] == 0 || reached[i] != 0) continue;
                    pieces++;
                    reached[i] = 2;
                    stack.Add(i);
                    floatingVoxels += Flood(ref stack, ref reached, 2) + 1;
                }

                Stats[0] = new int4(overhangColumns, overhangVoxels, floatingVoxels, pieces);
                reached.Dispose();
                stack.Dispose();
            }

            private int Flood(ref NativeList<int> stack, ref NativeArray<byte> reached, byte mark){
                const int n = DensityLayout.ChunkSize, h = DensityLayout.Height;
                int count = 0;
                while (stack.Length > 0){
                    int i = stack[^1];
                    stack.RemoveAt(stack.Length - 1);
                    int y = i % h, xz = i / h, z = xz % n, x = xz / n;
                    count += Try(x > 0, i - n * h, ref stack, ref reached, mark);
                    count += Try(x < n - 1, i + n * h, ref stack, ref reached, mark);
                    count += Try(z > 0, i - h, ref stack, ref reached, mark);
                    count += Try(z < n - 1, i + h, ref stack, ref reached, mark);
                    count += Try(y > 0, i - 1, ref stack, ref reached, mark);
                    count += Try(y < h - 1, i + 1, ref stack, ref reached, mark);
                }

                return count;
            }

            private int Try(bool inside, int j, ref NativeList<int> stack, ref NativeArray<byte> reached, byte mark){
                if (!inside || Solid[j] == 0 || reached[j] != 0) return 0;
                reached[j] = mark;
                stack.Add(j);
                return 1;
            }
        }

        [Test]
        public void DensityLook_Report(){
            GraphCompileResult compile = ShippedNoiseGraph.Compile();
            Assert.IsTrue(compile.Succeeded, string.Join("\n", compile.Messages));
            using CompiledNoiseGraph graph = compile.Graph;
            Assert.IsTrue(GraphEvaluator.HasGeneratedCode(graph), "Regenerate the shipped graph code (Infinitile > Generate Noise Graph Code).");
            Report<ShippedNoiseGraphCode>(graph, "shipped");
        }

        /// <summary>
        ///     The report for every graph file in <c>Temp/density-candidates/*.json</c> (in the interpreter, so a tuning
        ///     round needs no recompile); the height map goes to <c>density-height-&lt;name&gt;.png</c>. Without the folder
        ///     the test reports nothing.
        /// </summary>
        [Test]
        [Timeout(3600000)]
        public void DensityLook_ReportCandidates(){
            string dir = Path.Combine(Directory.GetCurrentDirectory(), "Temp", "density-candidates");
            if (!Directory.Exists(dir)) Assert.Ignore($"No candidate folder at {dir}");
            // An optional center.txt ("x z", rounded down to whole chunks) moves the window off the origin.
            string center = Path.Combine(dir, "center.txt");
            _center = int2.zero;
            if (File.Exists(center)){
                string[] xz = File.ReadAllText(center).Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                _center = new int2(int.Parse(xz[0], CultureInfo.InvariantCulture), int.Parse(xz[1], CultureInfo.InvariantCulture)) / 32 * 32;
            }

            try{
                DumpClimate();
                foreach (string file in Directory.GetFiles(dir, "*.json").OrderBy(f => f, StringComparer.Ordinal)){
                    GraphCompileResult compile = GraphCompiler.Compile(File.ReadAllText(file), Path.GetFileName(file));
                    Assert.IsTrue(compile.Succeeded, file + ": " + string.Join("; ", compile.Messages));
                    using CompiledNoiseGraph graph = compile.Graph;
                    Report<InterpretedPrograms>(graph, Path.GetFileNameWithoutExtension(file));
                }
            }
            finally{
                _center = int2.zero;
            }
        }

        /// <summary>
        ///     C, E, PV and W of the shipped graph every <see cref="Stride" /> columns
        ///     (floats, row-major from z −2048) into <c>Temp/density-dump/climate.bin</c>, for the candidate pictures.
        /// </summary>
        private static void DumpClimate(){
            GraphCompileResult compile = ShippedNoiseGraph.Compile();
            Assert.IsTrue(compile.Succeeded, string.Join("\n", compile.Messages));
            using CompiledNoiseGraph graph = compile.Graph;
            int side = Columns / Stride;
            float2[] p = new float2[side * side];
            for (int j = 0; j < side; j++)
            for (int i = 0; i < side; i++)
                p[j * side + i] = new float2(i * Stride - Columns / 2 + _center.x, j * Stride - Columns / 2 + _center.y);
            float[] values = GraphEvaluator.SampleLattice(graph, Seed, p, out _);
            string[] names ={ "continentalness", "erosion", "pv", "weirdness" };
            float[] outv = new float[names.Length * p.Length];
            for (int o = 0; o < names.Length; o++){
                GraphOutputInfo info = graph.Outputs.First(x => x.Name == names[o]);
                Array.Copy(values, info.LatticeValue * p.Length, outv, o * p.Length, p.Length);
            }

            byte[] b = new byte[outv.Length * 4];
            Buffer.BlockCopy(outv, 0, b, 0, b.Length);
            string d = Path.Combine(Directory.GetCurrentDirectory(), "Temp", "density-dump");
            Directory.CreateDirectory(d);
            File.WriteAllBytes(Path.Combine(d, "climate.bin"), b);
        }

        private static void Report<TPrograms>(CompiledNoiseGraph graph, string name) where TPrograms : struct, IGraphPrograms{
            WorldGenSettings settings = AssetDatabase.LoadAssetAtPath<WorldGenSettings>(ShippedSettingsPath);
            WorldGenConfig config = settings.ToConfig(Seed, null, Allocator.Persistent);
            int water = config.WaterLevel;
            short[] height = new short[Columns * Columns];
            bool dump = name != "shipped";
            byte[] gaps = dump ? new byte[Columns * Columns] : null;
            const int wh = DensityLayout.Height;
            byte[] slices = dump ? new byte[SliceRows.Length * Columns * wh] : null;
            long overhangColumns = 0, overhangVoxels = 0, floatingVoxels = 0, floatingPieces = 0, land = 0;
            DateTime start = DateTime.Now;
            try{
                List<int2> all = new();
                for (int cz = 0; cz < Chunks; cz++)
                for (int cx = 0; cx < Chunks; cx++)
                    all.Add(new int2(cx, cz));
                for (int b = 0; b < all.Count; b += Batch){
                    int count = Math.Min(Batch, all.Count - b);
                    DensityProbeResult[] results = new DensityProbeResult[count];
                    NativeArray<DensityWorkCounters>[] counters = new NativeArray<DensityWorkCounters>[count];
                    NativeArray<int4>[] stats = new NativeArray<int4>[count];
                    NativeArray<JobHandle> handles = new(count, Allocator.Temp);
                    try{
                        for (int k = 0; k < count; k++){
                            int2 origin = (all[b + k] - Chunks / 2) * 32 + _center;
                            DensityProbeJob<TPrograms> job =
                                DensityProbe.Create<TPrograms>(graph, config, origin, DensityChunk.PassTerrain, out results[k]);
                            counters[k] = job.Counters;
                            stats[k] = new NativeArray<int4>(1, Allocator.TempJob);
                            JobHandle h = job.Schedule();
                            handles[k] = new ChunkStatsJob{ Solid = results[k].Solid, Heightmap = results[k].Heightmap, WaterLevel = water, Stats = stats[k] }
                                .Schedule(h);
                        }

                        JobHandle.CompleteAll(handles);
                        for (int k = 0; k < count; k++){
                            int2 c = all[b + k];
                            NativeArray<short> map = results[k].Heightmap;
                            for (int x = 0; x < 32; x++)
                            for (int z = 0; z < 32; z++){
                                short top = map[x * 32 + z];
                                height[(c.y * 32 + z) * Columns + c.x * 32 + x] = top;
                                if (top >= water) land++;
                                if (!dump) continue;
                                int gap = 0, col = (x * 32 + z) * wh;
                                for (int y = math.max(water, 0); y < top; y++)
                                    if (results[k].Solid[col + y] == 0) gap++;
                                gaps[(c.y * 32 + z) * Columns + c.x * 32 + x] = (byte)math.min(gap, 255);
                                int row = Array.IndexOf(SliceRows, c.y * 32 + z);
                                if (row < 0) continue;
                                for (int y = 0; y < wh; y++) slices[(row * wh + y) * Columns + c.x * 32 + x] = results[k].Solid[col + y];
                            }

                            int4 s = stats[k][0];
                            overhangColumns += s.x;
                            overhangVoxels += s.y;
                            floatingVoxels += s.z;
                            floatingPieces += s.w;
                        }
                    }
                    finally{
                        for (int k = 0; k < count; k++){
                            results[k]?.Dispose();
                            if (counters[k].IsCreated) counters[k].Dispose();
                            if (stats[k].IsCreated) stats[k].Dispose();
                        }

                        handles.Dispose();
                    }
                }
            }
            finally{
                config.Dispose();
            }

            double seconds = (DateTime.Now - start).TotalSeconds;
            StringBuilder sb = new();
            sb.AppendLine($"Density look · {name} · seed {Seed} · {Columns}² columns around {_center} · water level {water} · {seconds:F1} s");
            sb.AppendLine("top height " + Percentiles(height.Select(v => (float)v)));
            sb.AppendLine($"under water {100.0 * (height.Length - land) / height.Length:F1} % of the columns · highest {height.Max()} · lowest {height.Min()}");

            // Sampled every Stride columns, like the v1 report (DEC-108 numbers).
            int side = Columns / Stride;
            float[] sampled = new float[side * side];
            for (int j = 0; j < side; j++)
            for (int i = 0; i < side; i++)
                sampled[j * side + i] = height[j * Stride * Columns + i * Stride];
            List<int> high = AreaSizes(sampled, side, 180f);
            sb.AppendLine("ground ≥ 180: " + Areas(high, sampled.Length) + " (DEC-108 target: 42 areas, largest 0.62 km²)");
            sb.AppendLine($"ground ≥ 210 (snow line): {100.0 * sampled.Count(v => v >= 210f) / sampled.Length:F1} % (DEC-108: 3.4 %) · " +
                          $"≥ 235 (top slide): {100.0 * sampled.Count(v => v >= 235f) / sampled.Length:F2} %");
            sb.AppendLine($"overhangs: {100.0 * overhangColumns / Math.Max(1, land):F2} % of the land columns have air under their top above the water, " +
                          $"{overhangVoxels} air voxels ({(double)overhangVoxels / Math.Max(1, overhangColumns):F1} per such column)");
            sb.AppendLine($"floating pieces (inside one chunk): {floatingPieces} pieces, {floatingVoxels} voxels, " +
                          $"{(double)floatingPieces / (Chunks * Chunks):F3} per chunk");

            sb.AppendLine("per 512² region (rows north to south = z high to low): median / max top height");
            const int region = 512, regions = Columns / region;
            for (int rz = regions - 1; rz >= 0; rz--){
                List<string> cells = new();
                for (int rx = 0; rx < regions; rx++){
                    List<short> values = new();
                    for (int z = rz * region; z < (rz + 1) * region; z += Stride)
                    for (int x = rx * region; x < (rx + 1) * region; x += Stride)
                        values.Add(height[z * Columns + x]);
                    values.Sort();
                    cells.Add($"{values[values.Count / 2],3}/{values[^1],3}");
                }

                sb.AppendLine($"  z {rz * region - Columns / 2,5}: " + string.Join("  ", cells));
            }

            Debug.Log(sb.ToString());
            if (dump){
                string d = Path.Combine(Directory.GetCurrentDirectory(), "Temp", "density-dump", name);
                Directory.CreateDirectory(d);
                byte[] hb = new byte[height.Length * 2];
                Buffer.BlockCopy(height, 0, hb, 0, hb.Length);
                File.WriteAllBytes(Path.Combine(d, "height.bin"), hb);
                File.WriteAllBytes(Path.Combine(d, "gaps.bin"), gaps);
                File.WriteAllBytes(Path.Combine(d, "slices.bin"), slices);
                File.WriteAllText(Path.Combine(d, "report.txt"), sb.ToString());
            }

            WriteHeightMap(sampled, side, water, name == "shipped" ? "density-height.png" : $"density-height-{name}.png");
        }

        /// <summary>
        ///     The quantiles of the raw (warped) continentalness at the cumulative band shares of the C curve C1 (deep 6,
        ///     ocean 24, coast 8.9, near 16.9, mid 29.3, far 14.9 %), over five seeds and 300 k points each spread over
        ///     ±60 000 blocks: the raw knots of the C curve. Also the share of the shipped curve's output in the ocean
        ///     bands (C &lt; −0.19), which the knots set to 30 %.
        /// </summary>
        [Test]
        public void ContinentalnessQuantiles_Report(){
            string json = ShippedNoiseGraph.LoadJson();
            const string anchor = "\"continentalness\": \"continentalness_curve\",";
            Assert.IsTrue(json.Contains(anchor), "the C output moved");
            json = json.Replace(anchor, anchor + "\n    \"c_raw\": \"continentalness_warped\",");
            GraphCompileResult compile = GraphCompiler.Compile(json, "c_raw.json");
            Assert.IsTrue(compile.Succeeded, string.Join("\n", compile.Messages));
            using CompiledNoiseGraph graph = compile.Graph;
            GraphOutputInfo raw = graph.Outputs.First(o => o.Name == "c_raw"), curve = graph.Outputs.First(o => o.Name == "continentalness"),
                prelim = graph.Outputs.First(o => o.Name == GraphOutputs.PreliminarySurface);
            List<float> raws = new(), curved = new(), surface = new();
            System.Random random = new(11);
            const int points = 300_000;
            foreach (uint seed in new uint[]{ 1337, 1, 2, 3, 4 }){
                float2[] p = new float2[points];
                for (int i = 0; i < points; i++) p[i] = new float2((float)(random.NextDouble() * 120_000 - 60_000), (float)(random.NextDouble() * 120_000 - 60_000));
                float[] values = GraphEvaluator.SampleLattice(graph, seed, p, out _);
                for (int i = 0; i < points; i++){
                    raws.Add(values[raw.LatticeValue * points + i]);
                    curved.Add(values[curve.LatticeValue * points + i]);
                    surface.Add(values[prelim.LatticeValue * points + i]);
                }
            }

            raws.Sort();
            double[] cumulative ={ 0.06, 0.30, 0.389, 0.558, 0.851 };
            string knots = string.Join(", ", cumulative.Select(c => raws[(int)(c * raws.Count)].ToString("F4", CultureInfo.InvariantCulture)));
            Debug.Log($"raw C knots at {string.Join(" / ", cumulative)}: {knots} · raw min {raws[0]:F3} max {raws[^1]:F3} · " +
                      $"shipped curve: C < −0.19 on {100.0 * curved.Count(v => v < -0.19f) / curved.Count:F1} %, C < −0.11 on {100.0 * curved.Count(v => v < -0.11f) / curved.Count:F1} % · preliminary surface world-wide: < 124 on " +
                      $"{100.0 * surface.Count(v => v < 124f) / surface.Count:F1} %, ≥ 180 on {100.0 * surface.Count(v => v >= 180f) / surface.Count:F2} %, " +
                      $"≥ 210 on {100.0 * surface.Count(v => v >= 210f) / surface.Count:F2} %");
        }

        /// <summary>
        ///     The world-wide area share of a massif box C ≥ c0 and E &lt; e0 (curved C, erosion) for a grid of edges,
        ///     over five seeds and 200 k points each spread over ±60 000 blocks: sizes the Mountain zone (Mountain plus
        ///     Badlands is the whole box).
        /// </summary>
        [Test]
        public void MassifShare_Report(){
            GraphCompileResult compile = ShippedNoiseGraph.Compile();
            Assert.IsTrue(compile.Succeeded, string.Join("\n", compile.Messages));
            using CompiledNoiseGraph graph = compile.Graph;
            GraphOutputInfo cOut = graph.Outputs.First(o => o.Name == "continentalness"), eOut = graph.Outputs.First(o => o.Name == "erosion");
            List<float2> ce = new();
            System.Random random = new(12);
            const int points = 200_000;
            foreach (uint seed in new uint[]{ 1337, 1, 2, 3, 4 }){
                float2[] p = new float2[points];
                for (int i = 0; i < points; i++) p[i] = new float2((float)(random.NextDouble() * 120_000 - 60_000), (float)(random.NextDouble() * 120_000 - 60_000));
                float[] values = GraphEvaluator.SampleLattice(graph, seed, p, out _);
                for (int i = 0; i < points; i++) ce.Add(new float2(values[cOut.LatticeValue * points + i], values[eOut.LatticeValue * points + i]));
            }

            float[] cs ={ 0.03f, 0.08f, 0.12f, 0.15f, 0.18f, 0.2f, 0.22f, 0.25f, 0.28f, 0.3f, 0.35f };
            float[] es ={ -0.05f, -0.1f, -0.15f, -0.2f, -0.25f, -0.3f, -0.35f, -0.38f, -0.45f };
            StringBuilder sb = new();
            sb.AppendLine("massif box share of the area (%), rows C ≥ c0, columns E < e0");
            sb.AppendLine("c0 / e0 " + string.Join(" ", es.Select(e => e.ToString("F2", CultureInfo.InvariantCulture).PadLeft(6))));
            foreach (float c0 in cs)
                sb.AppendLine(c0.ToString("F2", CultureInfo.InvariantCulture).PadLeft(8) + " " +
                              string.Join(" ", es.Select(e0 => (100.0 * ce.Count(v => v.x >= c0 && v.y < e0) / ce.Count).ToString("F2", CultureInfo.InvariantCulture).PadLeft(6))));
            // The 4096² windows (centers every 1024 within ±16 384) of seed 1337 with the most area in the box C ≥ 0.2, E < −0.27.
            const int step = 64, reach = 16384 + 2048;
            int grid = 2 * reach / step;
            float2[] q = new float2[grid * grid];
            for (int j = 0; j < grid; j++)
            for (int i = 0; i < grid; i++)
                q[j * grid + i] = new float2(i * step - reach, j * step - reach);
            float[] qv = GraphEvaluator.SampleLattice(graph, 1337, q, out _);
            List<(float share, int x, int z)> windows = new();
            for (int cz = -16384; cz <= 16384; cz += 1024)
            for (int cx = -16384; cx <= 16384; cx += 1024){
                int inBox = 0, count = 0;
                for (int z = cz - 2048; z < cz + 2048; z += step)
                for (int x = cx - 2048; x < cx + 2048; x += step){
                    int k = (z + reach) / step * grid + (x + reach) / step;
                    count++;
                    if (qv[cOut.LatticeValue * q.Length + k] >= 0.2f && qv[eOut.LatticeValue * q.Length + k] < -0.27f) inBox++;
                }

                windows.Add((100f * inBox / count, cx, cz));
            }

            sb.AppendLine("seed 1337 windows with the most box area (C ≥ 0.2, E < −0.27): " +
                          string.Join(" · ", windows.OrderByDescending(w => w.share).Take(8).Select(w => FormattableString.Invariant($"({w.x}, {w.z}) {w.share:F1} %"))));
            Debug.Log(sb.ToString());
            string d = Path.Combine(Directory.GetCurrentDirectory(), "Temp", "density-dump");
            Directory.CreateDirectory(d);
            File.WriteAllText(Path.Combine(d, "massif-share.txt"), sb.ToString());
        }

        private static string Percentiles(IEnumerable<float> values){
            float[] v = values.OrderBy(x => x).ToArray();
            string P(double p) => v[(int)Math.Min(v.Length - 1, p * v.Length)].ToString("F0", CultureInfo.InvariantCulture);
            return $"p1 {P(.01)} · p10 {P(.1)} · p25 {P(.25)} · p50 {P(.5)} · p75 {P(.75)} · p90 {P(.9)} · p99 {P(.99)} · p99.9 {P(.999)}";
        }

        /// <summary>Sizes (in samples, largest first) of the 4-connected areas at or above <paramref name="level" />.</summary>
        private static List<int> AreaSizes(float[] h, int side, float level){
            bool[] seen = new bool[h.Length];
            List<int> sizes = new();
            Stack<int> stack = new();
            for (int i = 0; i < h.Length; i++){
                if (seen[i] || h[i] < level) continue;
                int size = 0;
                seen[i] = true;
                stack.Push(i);
                while (stack.Count > 0){
                    int k = stack.Pop();
                    size++;
                    int x = k % side, z = k / side;
                    foreach (int n in new[]{ x > 0 ? k - 1 : -1, x < side - 1 ? k + 1 : -1, z > 0 ? k - side : -1, z < side - 1 ? k + side : -1 }){
                        if (n < 0 || seen[n] || h[n] < level) continue;
                        seen[n] = true;
                        stack.Push(n);
                    }
                }

                sizes.Add(size);
            }

            sizes.Sort((a, b) => b.CompareTo(a));
            return sizes;
        }

        private static string Areas(List<int> sizes, int total){
            const double km2 = Stride * Stride / 1e6;
            long cells = sizes.Sum(s => (long)s);
            if (cells == 0) return "none";
            return $"{100.0 * cells / total:F1} % of all ({(cells * km2).ToString("F2", CultureInfo.InvariantCulture)} km²), {sizes.Count} areas, largest " +
                   string.Join(" / ", sizes.Take(5).Select(s => (s * km2).ToString("F2", CultureInfo.InvariantCulture))) + " km²";
        }

        private static void WriteHeightMap(float[] h, int side, int water, string file){
            Color32[] pixels = new Color32[h.Length];
            for (int z = 0; z < side; z++)
            for (int x = 0; x < side; x++){
                int i = z * side + x;
                float v = h[i];
                float dx = h[z * side + Math.Min(side - 1, x + 1)] - h[z * side + Math.Max(0, x - 1)];
                float dz = h[Math.Min(side - 1, z + 1) * side + x] - h[Math.Max(0, z - 1) * side + x];
                float shade = math.clamp(0.75f + (dz - dx) * 0.04f, 0.4f, 1.15f);
                Color c;
                if (v < water) c = Color.Lerp(new Color(0.05f, 0.15f, 0.45f), new Color(0.2f, 0.45f, 0.8f), math.saturate((v - 30f) / (water - 30f)));
                else if (v < 180f) c = Color.Lerp(new Color(0.35f, 0.6f, 0.3f), new Color(0.6f, 0.55f, 0.35f), math.saturate((v - water) / (180f - water))) * shade;
                else if (v < 210f) c = new Color(0.55f, 0.5f, 0.45f) * shade;
                else c = new Color(0.95f, 0.95f, 0.97f) * shade;
                c.a = 1f;
                pixels[i] = c;
            }

            Texture2D t = new(side, side, TextureFormat.RGBA32, false);
            t.SetPixels32(pixels);
            t.Apply(false);
            string dir = Path.Combine(Directory.GetCurrentDirectory(), "Temp", "tuning-captures");
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, file), t.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(t);
        }
    }
}
