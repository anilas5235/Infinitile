using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Engine.Editor.WorldGen;
using Engine.Scripts.Jobs.Chunk;
using Engine.Scripts.VoxelConfig.Data.Generation;
using Engine.Scripts.WorldGen.Biomes;
using Engine.Scripts.WorldGen.Density;
using Engine.Scripts.WorldGen.Graph;
using Engine.Scripts.WorldGen.Settings;
using Engine.Scripts.WorldGen.Surface;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEditor;
using Debug = UnityEngine.Debug;

namespace Engine.Tests.EditMode{
    /// <summary>
    ///     The world report (§13.4) and the sweep (§6.7) of WorldGen v2 on the five fixed seeds (DEC-10), with the shipped
    ///     graph and layout. Both print and write to <c>Temp/world-report/</c>; neither sets limits (the targets show
    ///     pass or fail, the owner picks on the sweep). Also the check that the report's column sampler gives the
    ///     chunk's biome, wet flag and top voxel on lattice columns.
    /// </summary>
    public class WorldReportTests{
        private static string Dir{
            get{
                string dir = Path.Combine(Directory.GetCurrentDirectory(), "Temp", "world-report");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        private static BiomeSet Set => AssetDatabase.LoadAssetAtPath<BiomeSet>(BiomeLayoutTests.BiomeSetPath);

        [Test]
        [Category(TestTiers.Report)]
        [Explicit(TestTiers.ReportReason)]
        [Timeout(3600000)] // minutes: past NUnit's default of 180 s
        public void WorldReport_FiveSeeds(){
            GraphCompileResult compile = ShippedNoiseGraph.Compile();
            Assert.IsTrue(compile.Succeeded, string.Join("\n", compile.Messages));
            using CompiledNoiseGraph graph = compile.Graph;
            BiomeLayout layout = BiomeLayout.FromBiomeSet(Set);
            using BiomeLayoutDataScope data = new(layout);
            StringBuilder all = new();
            Stopwatch watch = Stopwatch.StartNew();
            foreach (uint seed in BiomeWorldReport.Seeds){
                WorldReportSeed r = BiomeWorldReport.Run(graph, layout, data.Data, seed);
                string text = BiomeWorldReport.Text(r, "shipped");
                Debug.Log(text);
                all.AppendLine(text);
            }

            all.AppendLine(System.FormattableString.Invariant($"({watch.Elapsed.TotalSeconds:0.0} s)"));
            File.WriteAllText(Path.Combine(Dir, "report.txt"), all.ToString());
        }

        /// <summary>
        ///     The snow measures (§8, phase V4) on the five seeds: the snow line at the median Mountain T, the snow share
        ///     of Mountain ground above y 200 per T band, snow per biome, hard snow edges, sea ice, tour spots, and the
        ///     T_snow calibration over the pooled Mountain T (DEC-175). Writes <c>snow.txt</c>.
        /// </summary>
        [Test]
        [Category(TestTiers.Report)]
        [Explicit(TestTiers.ReportReason)]
        [Timeout(3600000)]
        public void SnowReport_FiveSeeds(){
            GraphCompileResult compile = ShippedNoiseGraph.Compile();
            Assert.IsTrue(compile.Succeeded, string.Join("\n", compile.Messages));
            using CompiledNoiseGraph graph = compile.Graph;
            BiomeLayout layout = BiomeLayout.FromBiomeSet(Set);
            using BiomeLayoutDataScope data = new(layout);
            SurfaceRuleSet set = SurfaceRuleSet.Shipped();
            using SurfaceRuleCompileResult rules = SurfaceRuleCompiler.CompileShipped(layout, data.Data.Defs.ToArray(), graph, null, Allocator.Persistent);
            Assert.IsTrue(rules.Succeeded, rules.ErrorText());
            StringBuilder all = new();
            List<float> mountainT = new();
            Stopwatch watch = Stopwatch.StartNew();
            foreach (uint seed in BiomeWorldReport.Seeds){
                SnowReportSeed r = SnowReport.Run(graph, layout, data.Data, rules.Rules, set.Climate, seed);
                mountainT.AddRange(r.MountainT);
                string text = SnowReport.Text(r, rules.Rules);
                Debug.Log(text);
                all.AppendLine(text);
            }

            float median = SnowReport.Median(mountainT);
            float calibrated = SnowReport.CalibratedSnowTemperature(median, rules.Rules);
            all.AppendLine(System.FormattableString.Invariant(
                $"calibration (DEC-175): median Mountain T over the five seeds {median:0.0000} ({mountainT.Count} samples) → T_snow = T_med − 86 L = {calibrated:0.0000} (rule file: {rules.Rules.SnowTemperature:0.0000}); ") +
                System.FormattableString.Invariant($"snow line at that median with the file's T_snow: y {rules.Rules.ColdThreshold(median, 0f, rules.Rules.SnowTemperature)}"));
            all.AppendLine(System.FormattableString.Invariant($"({watch.Elapsed.TotalSeconds:0.0} s)"));
            Debug.Log(all.ToString());
            File.WriteAllText(Path.Combine(Dir, "snow.txt"), all.ToString());
        }

        /// <summary>
        ///     The river measures (§9.2, phase V5) on the five seeds: spacing along lines, the width of the valleys slice and
        ///     of the water across the river (p10, median, p90, p99), the shares of the slice and of the river columns, dry and
        ///     open-water crossings, tour spots. Writes <c>rivers.txt</c>.
        /// </summary>
        [Test]
        [Category(TestTiers.Report)]
        [Explicit(TestTiers.ReportReason)]
        [Timeout(3600000)]
        public void RiverReport_FiveSeeds(){
            GraphCompileResult compile = ShippedNoiseGraph.Compile();
            Assert.IsTrue(compile.Succeeded, string.Join("\n", compile.Messages));
            using CompiledNoiseGraph graph = compile.Graph;
            BiomeLayout layout = BiomeLayout.FromBiomeSet(Set);
            using BiomeLayoutDataScope data = new(layout);
            StringBuilder all = new();
            List<RiverReportSeed> seeds = new();
            Stopwatch watch = Stopwatch.StartNew();
            foreach (uint seed in BiomeWorldReport.Seeds){
                RiverReportSeed r = RiverReport.Run(graph, data.Data, seed);
                seeds.Add(r);
                string text = RiverReport.Text(r);
                Debug.Log(text);
                all.AppendLine(text);
            }

            all.AppendLine(RiverReport.Pooled(seeds));
            all.AppendLine(System.FormattableString.Invariant($"({watch.Elapsed.TotalSeconds:0.0} s)"));
            Debug.Log(all.ToString());
            File.WriteAllText(Path.Combine(Dir, "rivers.txt"), all.ToString());
        }

        [Test]
        [Category(TestTiers.Report)]
        [Explicit(TestTiers.ReportReason)]
        [Timeout(3600000)] // minutes: past NUnit's default of 180 s
        public void Sweep_FiveSeeds(){
            List<SweepRow> rows = new();
            Stopwatch watch = Stopwatch.StartNew();
            foreach (SweepCandidate c in BiomeSweep.Candidates()) rows.Add(BiomeSweep.Run(c, Set));
            string table = BiomeSweep.Table(rows) + System.FormattableString.Invariant($"\n({watch.Elapsed.TotalSeconds:0.0} s)");
            Debug.Log(table);
            File.WriteAllText(Path.Combine(Dir, "sweep.txt"), table);
            Assert.IsTrue(rows.All(r => r.Error == null), string.Join("\n", rows.Where(r => r.Error != null).Select(r => r.Candidate.Name + ": " + r.Error)));
        }

        /// <summary>
        ///     A few candidates of the sweep by their letter (first character of the name), for a quick look at
        ///     combinations; writes <c>sweep-finalists.txt</c>.
        /// </summary>
        [Test]
        [Category(TestTiers.Report)]
        [Explicit(TestTiers.ReportReason)]
        [Timeout(3600000)]
        public void Sweep_Finalists([Values("AS")] string letters){
            List<SweepRow> rows = BiomeSweep.Candidates().Where(c => letters.IndexOf(c.Name[0]) >= 0).Select(c => BiomeSweep.Run(c, Set)).ToList();
            string table = BiomeSweep.Table(rows);
            Debug.Log(table);
            File.WriteAllText(Path.Combine(Dir, "sweep-finalists.txt"), table);
            Assert.IsTrue(rows.All(r => r.Error == null), string.Join("\n", rows.Where(r => r.Error != null).Select(r => r.Candidate.Name + ": " + r.Error)));
        }

        /// <summary>The sampler on lattice columns of probed chunks: biome, wet flag and top voxel as the chunk stores them.</summary>
        [Test]
        public void ColumnSampler_EqualsTheChunkOnLatticeColumns(){
            GraphCompileResult compile = ShippedNoiseGraph.Compile();
            Assert.IsTrue(compile.Succeeded, string.Join("\n", compile.Messages));
            using CompiledNoiseGraph graph = compile.Graph;
            GeneratorConfigScope scope = new(BiomeLayoutTests.Shipped());
            try{
                int checkedColumns = 0, wet = 0;
                foreach (int2 chunk in DensityFixture.Classes.SelectMany(c => DensityFixture.Chunks(c).Take(2))){
                    int2 origin = chunk * 32;
                    using DensityProbeResult probe = DensityProbe.Run(graph, scope.Config.WorldGen, origin,
                        DensityChunk.PassTerrain, false);
                    using DensityProbeResult biomes = DensityProbe.Run(graph, scope.Config.WorldGen, origin,
                        DensityChunk.PassBiomes, false);
                    List<float2> points = new();
                    List<int2> local = new();
                    for (int x = 0; x < 32; x += 4)
                    for (int z = 0; z < 32; z += 4){
                        points.Add(new float2(origin.x + x, origin.y + z));
                        local.Add(new int2(x, z));
                    }

                    using BiomeColumns c = BiomeColumnSampler.Sample(graph, scope.Config.WorldGen.Layout, scope.Config.WorldGen.Seed, points.ToArray(), true);
                    for (int i = 0; i < points.Count; i++){
                        int2 l = local[i];
                        Assert.AreEqual(biomes.PaddedHardAt(l.x, l.y), c.Biome[i], $"biome at {points[i]}");
                        Assert.AreEqual(biomes.PaddedWetAt(l.x, l.y) ? 1 : 0, c.Wet[i], $"wet flag at {points[i]}");
                        int top = probe.Heightmap[l.x * 32 + l.y];
                        Assert.AreEqual(top, c.Top[i], $"top voxel at {points[i]}");
                        checkedColumns++;
                        wet += c.Wet[i];
                    }
                }

                Assert.Greater(wet, 0);
                Assert.Less(wet, checkedColumns);
            }
            finally{
                scope.Dispose();
            }
        }

        /// <summary>
        ///     The locator on the shipped world: the found column has the biome, no ring point nearer than it has
        ///     the biome, and the peak is at least as high as every column on a coarse grid around the start.
        /// </summary>
        [Test]
        public void Locator_FindsTheNearestBiomeAndTheHighestPeak(){
            GraphCompileResult compile = ShippedNoiseGraph.Compile();
            Assert.IsTrue(compile.Succeeded, string.Join("\n", compile.Messages));
            using CompiledNoiseGraph graph = compile.Graph;
            BiomeLayout layout = BiomeLayout.FromBiomeSet(Set);
            using BiomeLayoutDataScope data = new(layout);
            const uint seed = 1337;
            int2 from = new(100, -50);
            foreach (string name in new[]{ "Desert", "Plains" }){
                byte biome = (byte)layout.Biomes.FindIndex(b => b.Name == name);
                Assert.IsTrue(BiomeLocator.FindBiome(graph, data.Data, seed, from, biome, out int2 at, out int top, out _), name);
                using BiomeColumns c = BiomeColumnSampler.Sample(graph, data.Data, seed, new[]{ (float2)at }, true);
                Assert.AreEqual(biome, c.Biome[0], $"{name} at {at}");
                Assert.AreEqual(c.Top[0], top);
                float distance = math.distance(at, from);
                List<float2> nearer = new();
                for (int z = -(int)distance; z <= distance; z += 32)
                for (int x = -(int)distance; x <= distance; x += 32)
                    if (x * x + z * z < (distance - 24) * (distance - 24))
                        nearer.Add(from + new float2(x, z));
                using BiomeColumns n = BiomeColumnSampler.Sample(graph, data.Data, seed, nearer.ToArray());
                for (int i = 0; i < nearer.Count; i++) Assert.AreNotEqual(biome, n.Biome[i], $"{name} at {nearer[i]} is nearer than {at}");
            }

            Assert.IsTrue(BiomeLocator.FindPeak(graph, data.Data, seed, from, out int2 peak, out int peakTop));
            Assert.LessOrEqual(math.distance(peak, from), BiomeLocator.PeakRadius + 2 * BiomeLocator.Step);
            List<float2> grid = new();
            for (int z = -1024; z <= 1024; z += 64)
            for (int x = -1024; x <= 1024; x += 64)
                grid.Add(from + new float2(x, z));
            using BiomeColumns g = BiomeColumnSampler.Sample(graph, data.Data, seed, grid.ToArray(), true);
            for (int i = 0; i < grid.Count; i++) Assert.LessOrEqual(g.Top[i], peakTop, $"column {grid[i]} is above the peak {peak}");
        }

        private sealed class BiomeLayoutDataScope : System.IDisposable{
            public BiomeLayoutData Data;

            public BiomeLayoutDataScope(BiomeLayout layout){
                Data = BiomeLayoutData.Create(layout, null, Allocator.Persistent);
            }

            public void Dispose(){
                Data.Dispose();
            }
        }

        private sealed class GeneratorConfigScope : System.IDisposable{
            public GeneratorConfig Config;

            public GeneratorConfigScope(BiomeLayout layout){
                Config = TestFixtures.CreateGeneratorConfig(Allocator.Persistent);
                Config.WorldGen.Seed = DensityFixture.Seed;
                Config.WorldGen.DensityStoneVoxel = TestFixtures.Stone;
                Config.WorldGen.Layout.Dispose();
                Config.WorldGen.Layout = BiomeLayoutData.Create(layout, null, Allocator.Persistent);
            }

            public void Dispose(){
                TestFixtures.DisposeGeneratorConfig(ref Config);
            }
        }
    }
}
