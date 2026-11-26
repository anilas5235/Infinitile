using System.Collections.Generic;
using System.Linq;
using System.Text;
using Engine.Editor.WorldGen;
using Engine.Scripts.Jobs.Chunk;
using Engine.Scripts.WorldGen.Density;
using Engine.Scripts.WorldGen.Graph;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using static Engine.Tests.EditMode.NoiseGraphTestKit;

namespace Engine.Tests.EditMode{
    /// <summary>
    ///     The density terrain of WorldGen v2 (§5, phase V2): the corner-column ranges and uniform cells against the
    ///     interpolation of the full lattice (W2-DEN-03), the top slide, chunk borders, the heightmap, a voxel hash
    ///     over the benchmark fixture's chunks (W2-ID-05) and the work counters per chunk class (W2-TST-05).
    /// </summary>
    public class DensityTerrainTests{
        private const int Height = DensityLayout.Height;
        private const int Size = DensityLayout.ChunkSize;

        private CompiledNoiseGraph _fixture;
        private GeneratorConfig _config;

        [OneTimeSetUp]
        public void SetUp(){
            GraphCompileResult r = DensityFixture.Compile();
            Assert.IsTrue(r.Succeeded, string.Join("\n", r.Messages));
            _fixture = r.Graph;
            _config = TestFixtures.CreateGeneratorConfig(Unity.Collections.Allocator.Persistent);
            _config.WorldGen.Seed = DensityFixture.Seed;
            _config.WorldGen.DensityStoneVoxel = TestFixtures.Stone;
            DensityFixture.UseShippedSea(ref _config.WorldGen);
        }

        [OneTimeTearDown]
        public void TearDown(){
            _fixture?.Dispose();
            TestFixtures.DisposeGeneratorConfig(ref _config);
        }

        /// <summary>Two chunks per class: the reference evaluation runs every cell.</summary>
        private static IEnumerable<int2> SomeFixtureChunks(){
            return DensityFixture.Classes.SelectMany(c => DensityFixture.Chunks(c).Take(2));
        }

        private static IEnumerable<int2> AllFixtureChunks(){
            return DensityFixture.AllChunks().Select(e => e.Chunk);
        }

        /// <summary>
        ///     A terrain graph of flat ground at about <paramref name="surface" /> with a factor of about
        ///     <paramref name="factor" />, in the shipped graph's density form (§5.1): near y 255 its top slide turns the
        ///     density negative over solid rock; its generation top is y 255 (AD-45).
        /// </summary>
        internal static GraphBuilder FlatGround(double surface, double factor, double cellHeight, double slideStart = 235, double slideEnd = 255){
            return new GraphBuilder()
                .Noise("ground", NoiseKind.Simplex2, 2, 160f)
                .Noise("detail", NoiseKind.Simplex3, 3, 64f, 24f, amplitude: 0.5f)
                .Node("surface", Op("add", ("a", surface), ("b", Op("mul", ("a", Noise("fbm", "ground")), ("b", 2.0)))))
                .Node("factor", Op("add", ("a", factor), ("b", Op("mul", ("a", Noise("fbm", "ground")), ("b", factor * 0.25)))))
                .Node("noise", Noise("fbm", "detail"))
                .Node("shape", Op("mul", ("a", Op("mul", ("a", "factor"), ("b", Op("sub", ("a", "surface"), ("b", "y"))))), ("b", 0.015625)))
                .Node("weight", Op("smoothstep", ("in", Op("sub", ("a", "y"), ("b", "surface"))), ("from", -32.0), ("to", -16.0)))
                .Node("raw", Op("add", ("a", "shape"), ("b", Op("mul", ("a", "weight"), ("b", "noise")))))
                .Node("slid", Op("lerp", ("t", Op("smoothstep", ("in", "y"), ("from", slideStart), ("to", slideEnd))), ("a", "raw"), ("b", -1.0)))
                .Node("density", Op("interpolated", ("in", "slid"), ("cellHeight", cellHeight)))
                .Output(GraphOutputs.PreliminarySurface, "surface")
                .Output(GraphOutputs.Factor, "factor")
                .Output(GraphOutputs.TerrainNoise, new Dictionary<string, object>{ ["node"] = "noise", ["boundOnly"] = true })
                .Output(GraphOutputs.TopSlideStart, new Dictionary<string, object>{ ["node"] = slideStart, ["boundOnly"] = true })
                .Output(GraphOutputs.TopSlideEnd, new Dictionary<string, object>{ ["node"] = slideEnd, ["boundOnly"] = true })
                .Output(GraphOutputs.TerrainDensity, "density");
        }

        /// <summary>
        ///     The probe's terrain against the full lattice: every corner of the chunk computed and every voxel
        ///     interpolated (the region job's voxel output of terrain_density), terrain where it is above 0.
        /// </summary>
        private void AssertEqualsFullLattice(CompiledNoiseGraph graph, uint seed, int2 origin, string label){
            _config.WorldGen.Seed = seed;
            using DensityProbeResult probe = DensityProbe.Run(graph, _config.WorldGen, origin, DensityChunk.PassTerrain, true);
            using GraphRegionResult full = GraphEvaluator.EvaluateRegion(graph, seed, origin, new int2(Size, Size), true, false, true);
            int output = graph.Output(GraphOutputs.TerrainDensity).VoxelOutput;
            for (int x = 0; x < Size; x++)
            for (int z = 0; z < Size; z++)
            for (int y = 0; y < Height; y++){
                float d = full.Voxels[((output * Size + z) * Size + x) * Height + y];
                bool expected = d > 0f;
                if (probe.IsSolid(x, y, z) != expected)
                    Assert.Fail($"{label}: voxel ({origin.x + x}, {y}, {origin.y + z}) is {(expected ? "air" : "terrain")}, the full lattice gives density {d:R}.");
            }

            _config.WorldGen.Seed = DensityFixture.Seed;
        }

        #region Graph

        [Test]
        public void ShippedGraph_HasTerrain(){
            GraphCompileResult r = ShippedNoiseGraph.Compile();
            Assert.IsTrue(r.Succeeded, string.Join("\n", r.Messages));
            using CompiledNoiseGraph graph = r.Graph;
            Assert.IsTrue(DensityTerrainGraph.TryCreate(graph, out DensityTerrainGraph terrain, out string error), error);
            Assert.AreEqual(8, terrain.CellHeight, "DEC-159 starts with cells 4 × 8 × 4.");
            Assert.AreEqual(352f, terrain.SlideStart, "H2: the top slide 352 → 383 lies above the peaks (DEC-238).");
            Assert.That(terrain.NoiseBound, Is.GreaterThan(0f).And.LessThanOrEqualTo(1f));
            Assert.IsTrue(GraphEvaluator.HasGeneratedCode(graph), "The shipped graph should run as generated code; run Infinitile > Generate Noise Graph Code.");
        }

        [Test]
        public void TerrainOutputs_ComeTogether(){
            GraphBuilder g = FlatGround(100, 4, 8);
            g.Graph.Outputs.Remove(GraphOutputs.Factor);
            GraphCompileResult r = Compile(g.Graph);
            Assert.IsFalse(r.Succeeded);
            Assert.IsTrue(r.Errors.Any(m => m.Text.Contains("\"factor\" is missing")), string.Join("\n", r.Messages));
        }

        [Test]
        public void PreliminarySurfaceAbove255_IsRejected(){
            GraphCompileResult r = Compile(FlatGround(256, 4, 8).Graph);
            Assert.IsFalse(r.Succeeded);
            Assert.IsTrue(r.Errors.Any(m => m.Text.Contains(GraphOutputs.PreliminarySurface)), string.Join("\n", r.Messages));
        }

        [Test]
        public void NonPositiveFactor_IsRejected(){
            GraphCompileResult r = Compile(FlatGround(100, 0, 8).Graph);
            Assert.IsFalse(r.Succeeded);
            Assert.IsTrue(r.Errors.Any(m => m.Text.Contains("must stay above 0")), string.Join("\n", r.Messages));
        }

        #endregion

        #region Ranges and uniform cells (W2-DEN-03)

        [Test]
        public void FixtureChunks_EqualFullLattice([Values(1337u, 7u)] uint seed){
            foreach (int2 chunk in SomeFixtureChunks())
                AssertEqualsFullLattice(_fixture, seed, chunk * Size, $"fixture chunk {chunk}, seed {seed}");
        }

        /// <summary>
        ///     Flat ground in and below the top slide, with cells 8 and 4 high: a small factor makes Δdown large, a large
        ///     one small, so the slide's air pocket over solid rock lies below the ground's own range.
        /// </summary>
        [Test]
        public void FlatGround_EqualsFullLattice([Values(250.0, 244.0, 230.0, 130.0, 40.0)] double surface, [Values(0.75, 8.0)] double factor,
            [Values(8.0, 4.0)] double cellHeight){
            using CompiledNoiseGraph graph = CompileOrFail(FlatGround(surface, factor, cellHeight).Graph);
            foreach (int2 origin in new[]{ new int2(0, 0), new int2(-96, 320), new int2(4000, -2048) })
                AssertEqualsFullLattice(graph, Seed, origin, $"flat ground {surface} factor {factor} cells {cellHeight} at {origin}");
        }

        /// <summary>
        ///     AD-44: a cell-4 marker over the full 512 blocks has 129 corner layers, one more than the lanes. Low ground
        ///     with a tiny factor makes every corner column need all of them, so P4 and the region job run them in
        ///     slices; both must still equal each other.
        /// </summary>
        [Test]
        public void CellFourMarker_OverTheFullHeight_RunsInLaneSlices(){
            const int layers = Height / 4 + 1;
            Assert.Greater(layers, GraphLimits.MaxLanes, "the test needs more layers than lanes");
            using CompiledNoiseGraph graph = CompileOrFail(FlatGround(20, 0.001, 4, Height - 21, Height - 1).Graph);
            foreach (int2 origin in new[]{ new int2(0, 0), new int2(-96, 320) }){
                AssertEqualsFullLattice(graph, Seed, origin, $"low flat ground, factor 0.001, cells 4 at {origin}");
                using DensityProbeResult p = DensityProbe.Run(graph, _config.WorldGen, origin, DensityChunk.PassTerrain, true);
                Assert.Greater(p.Counters.CornerColumns, 0);
                Assert.AreEqual(p.Counters.CornerColumns * layers, p.Counters.Corners, "every corner column computes all 129 layers");
            }
        }

        [Test]
        public void ShippedCode_EqualsTheInterpreter(){
            GraphCompileResult r = ShippedNoiseGraph.Compile();
            Assert.IsTrue(r.Succeeded, string.Join("\n", r.Messages));
            using CompiledNoiseGraph graph = r.Graph;
            Assume.That(GraphEvaluator.HasGeneratedCode(graph), "The shipped code is out of date (ShippedGraph_HasTerrain).");
            foreach (int2 chunk in SomeFixtureChunks()){
                using DensityProbeResult interpreted = DensityProbe.Run(graph, _config.WorldGen, chunk * Size, DensityChunk.PassFill, true);
                using DensityProbeResult generated = DensityProbe.Run(graph, _config.WorldGen, chunk * Size);
                Assert.IsTrue(interpreted.Voxels.SequenceEqual(generated.Voxels), $"voxels of chunk {chunk}");
                Assert.AreEqual(interpreted.Counters.ToString(), generated.Counters.ToString(), $"work counters of chunk {chunk}");
            }
        }

        #endregion

        #region Borders, top, heightmap

        /// <summary>A chunk shifted by half a chunk overlaps two others: the overlapping columns must be the same.</summary>
        [Test]
        public void NeighbouringChunks_AgreeAtBorders(){
            foreach (int2 chunk in SomeFixtureChunks()){
                int2 origin = chunk * Size;
                using DensityProbeResult a = DensityProbe.Run(_fixture, _config.WorldGen, origin);
                foreach (int2 shift in new[]{ new int2(16, 0), new int2(0, 16), new int2(16, 16), new int2(-4, 28) }){
                    using DensityProbeResult b = DensityProbe.Run(_fixture, _config.WorldGen, origin + shift);
                    for (int x = math.max(0, shift.x); x < math.min(Size, Size + shift.x); x++)
                    for (int z = math.max(0, shift.y); z < math.min(Size, Size + shift.y); z++){
                        int ia = (x * Size + z) * Height, ib = ((x - shift.x) * Size + z - shift.y) * Height;
                        for (int y = 0; y < Height; y++)
                            if (a.Voxels[ia + y] != b.Voxels[ib + y])
                                Assert.Fail($"chunk {chunk}: voxel ({origin.x + x}, {y}, {origin.y + z}) differs in the chunk shifted by {shift}.");
                        Assert.AreEqual(a.Heightmap[x * Size + z], b.Heightmap[(x - shift.x) * Size + z - shift.y],
                            $"chunk {chunk}: height of column ({origin.x + x}, {origin.y + z}) in the chunk shifted by {shift}");
                    }
                }
            }
        }

        [Test]
        public void NoTerrainAboveTheGenerationTop(){
            // The fixture's generation top is its top_slide_end (y 383 since H2, DEC-238); the chunk is 512 high since H1.
            Assert.IsTrue(DensityTerrainGraph.TryCreate(_fixture, out DensityTerrainGraph terrain, out string error), error);
            int top = (int)terrain.SlideEnd;
            Assert.AreEqual(383, top);
            foreach (int2 chunk in AllFixtureChunks()){
                using DensityProbeResult p = DensityProbe.Run(_fixture, _config.WorldGen, chunk * Size, DensityChunk.PassTerrain);
                for (int x = 0; x < Size; x++)
                for (int z = 0; z < Size; z++)
                for (int y = top; y < Height; y++)
                    if (p.IsSolid(x, y, z))
                        Assert.Fail($"terrain at y {y} in column ({chunk.x * Size + x}, {chunk.y * Size + z})");
            }

            // FlatGround's own top slide ends at y 255.
            using CompiledNoiseGraph high = CompileOrFail(FlatGround(253, 8, 8).Graph);
            using DensityProbeResult flat = DensityProbe.Run(high, _config.WorldGen, int2.zero, DensityChunk.PassTerrain);
            for (int i = 0; i < DensityLayout.Columns; i++)
                Assert.Less(flat.Heightmap[i], 255, "flat ground at y 253 reaches y 255");
        }

        [Test]
        public void Heightmap_IsTheTopTerrainVoxel(){
            foreach (int2 chunk in AllFixtureChunks()){
                using DensityProbeResult p = DensityProbe.Run(_fixture, _config.WorldGen, chunk * Size, DensityChunk.PassTerrain);
                for (int x = 0; x < Size; x++)
                for (int z = 0; z < Size; z++){
                    int top = Height - 1;
                    while (top >= 0 && !p.IsSolid(x, top, z)) top--;
                    Assert.AreEqual(top, p.Heightmap[x * Size + z], $"column ({chunk.x * Size + x}, {chunk.y * Size + z})");
                }
            }
        }

        [Test]
        public void PlaceholderFill_IsStoneWaterAir(){
            int waterLevel = _config.WorldGen.WaterLevel;
            foreach (int2 chunk in SomeFixtureChunks()){
                using DensityProbeResult p = DensityProbe.Run(_fixture, _config.WorldGen, chunk * Size);
                for (int x = 0; x < Size; x++)
                for (int z = 0; z < Size; z++)
                for (int y = 0; y < Height; y++){
                    ushort expected = p.IsSolid(x, y, z) ? TestFixtures.Stone : y < waterLevel ? TestFixtures.Water : TestFixtures.Air;
                    ushort actual = p.Voxels[(x * Size + z) * Height + y];
                    if (actual != expected) Assert.Fail($"voxel ({chunk.x * Size + x}, {y}, {chunk.y * Size + z}) is {actual}, expected {expected}");
                }
            }
        }

        #endregion

        #region Hash and work counters

        /// <summary>
        ///     FNV-1a over the voxel ids of every fixture chunk, in class and chunk order, per column (x, then z) the
        ///     voxels y 0 … <paramref name="height" /> − 1: with the full height the chunk's own order, with 256 the order
        ///     of the 256-high chunks before H1.
        /// </summary>
        private ulong VoxelHash(int height = Height){
            ulong h = 14695981039346656037UL;
            foreach (int2 chunk in AllFixtureChunks()){
                using DensityProbeResult p = DensityProbe.Run(_fixture, _config.WorldGen, chunk * Size);
                for (int column = 0; column < Size * Size; column++)
                for (int y = 0; y < height; y++){
                    ushort v = p.Voxels[column * Height + y];
                    h = (h ^ (byte)v) * 1099511628211UL;
                    h = (h ^ (byte)(v >> 8)) * 1099511628211UL;
                }
            }

            return h;
        }

        /// <summary>
        ///     The voxels of the fixture chunks (W2-ID-05). The fixture graph never moves with tuning, so a new hash means
        ///     the generator's code changed the world; update it only for a change that is meant to do that
        ///     (re-recorded once for the 512-high chunks, H1).
        /// </summary>
        [Test]
        public void VoxelHash_OfTheFixtureChunks(){
            const ulong expected = 0x2C1E2D8B2D350AC2UL;
            ulong actual = VoxelHash();
            Assert.AreEqual(expected, actual, $"voxel hash 0x{actual:X16}");
        }

        /// <summary>
        ///     DEC-232: the chunk job writes the layers from the chunk's highest <see cref="SurfacePass.AirFrom" /> on as
        ///     air without reading them. Every voxel there is air, and the chunk data equals the one read in full.
        /// </summary>
        [Test]
        public void ChunkData_FromTheAirLayer_EqualsTheFullRead(){
            int water = math.clamp(_config.WorldGen.WaterLevel, 0, Height);
            foreach (int2 chunk in AllFixtureChunks()){
                using DensityProbeResult p = DensityProbe.Run(_fixture, _config.WorldGen, chunk * Size);
                int airFrom = 0;
                for (int column = 0; column < Size * Size; column++) airFrom = math.max(airFrom, math.min(Height, math.max(p.Heightmap[column] + 2, water)));
                Assert.Less(airFrom, Height, $"chunk {chunk}: the air layer is in effect");
                for (int column = 0; column < Size * Size; column++)
                for (int y = airFrom; y < Height; y++)
                    if (p.Voxels[column * Height + y] != 0)
                        Assert.Fail($"chunk {chunk}: voxel {p.Voxels[column * Height + y]} at y {y} in column {column}, air from {airFrom}");

                ChunkJob.WriteToChunkData(p.Voxels, out Engine.Scripts.Data.ChunkVoxelData full);
                ChunkJob.WriteToChunkData(p.Voxels, out Engine.Scripts.Data.ChunkVoxelData cut, airFrom);
                try{
                    Assert.AreEqual(full.Data.CompressedLength, cut.Data.CompressedLength, $"chunk {chunk}: runs");
                    for (int n = 0; n < full.Data.CompressedLength; n++){
                        Assert.AreEqual(full.Data.NodeAt(n).Value, cut.Data.NodeAt(n).Value, $"chunk {chunk}: run {n} id");
                        Assert.AreEqual(full.Data.NodeAt(n).Count, cut.Data.NodeAt(n).Count, $"chunk {chunk}: run {n} end");
                    }

                    Assert.AreEqual(full.OccupiedPartitions, cut.OccupiedPartitions, $"chunk {chunk}: occupied partitions");
                }
                finally{
                    full.Dispose();
                    cut.Dispose();
                }
            }
        }

        private DensityWorkCounters Count(DensityChunkClass c){
            DensityWorkCounters sum = default;
            foreach (int2 chunk in DensityFixture.Chunks(c)){
                using DensityProbeResult p = DensityProbe.Run(_fixture, _config.WorldGen, chunk * Size, DensityChunk.PassTerrain, true);
                sum.Add(p.Counters);
            }

            return sum;
        }

        /// <summary>
        ///     The work of P1 and P4's terrain per chunk class (W2-TST-05), summed over the class's chunks: a change in the
        ///     work shows up here without a timing run. Update a row only for a change that is meant to change the work.
        /// </summary>
        [Test]
        public void WorkCounters_PerChunkClass([ValueSource(nameof(ClassValues))] DensityChunkClass c){
            string expected = ExpectedCounters[c];
            string actual = Count(c).ToString();
            Assert.AreEqual(expected, actual, $"work counters of {c}");
        }

        private static readonly DensityChunkClass[] ClassValues = DensityFixture.Classes;

        private static readonly Dictionary<DensityChunkClass, string> ExpectedCounters = new(){
            [DensityChunkClass.Ocean] = "8 chunks: corner columns 648, corners 4527, blocks by range 29708 / by sign 2486 / mixed 574, interpolated 73472, solid 921239; graph: lattice 4232, columns 0, corners 4527, voxels 0 in 0 cells, noise 2D 177744 / 3D 18108, instructions 31968 (621117 lane ops), interpolated 73472",
            [DensityChunkClass.Coast] = "8 chunks: corner columns 648, corners 4063, blocks by range 30284 / by sign 1664 / mixed 820, interpolated 104960, solid 1062404; graph: lattice 4232, columns 0, corners 4063, voxels 0 in 0 cells, noise 2D 177744 / 3D 16252, instructions 31968 (616013 lane ops), interpolated 104960",
            [DensityChunkClass.Lowland] = "8 chunks: corner columns 648, corners 3886, blocks by range 30256 / by sign 1924 / mixed 588, interpolated 75264, solid 1143209; graph: lattice 4232, columns 0, corners 3886, voxels 0 in 0 cells, noise 2D 177744 / 3D 15544, instructions 31968 (614066 lane ops), interpolated 75264",
            [DensityChunkClass.Massif] = "8 chunks: corner columns 648, corners 4386, blocks by range 30034 / by sign 1934 / mixed 800, interpolated 102400, solid 1599044; graph: lattice 4232, columns 0, corners 4386, voxels 0 in 0 cells, noise 2D 177744 / 3D 17544, instructions 31968 (619566 lane ops), interpolated 102400",
            [DensityChunkClass.TallMassif] = "8 chunks: corner columns 648, corners 5352, blocks by range 29697 / by sign 1687 / mixed 1384, interpolated 177152, solid 2080493; graph: lattice 4232, columns 0, corners 5352, voxels 0 in 0 cells, noise 2D 177744 / 3D 21408, instructions 31968 (630192 lane ops), interpolated 177152",
            [DensityChunkClass.CoastalCliff] = "8 chunks: corner columns 648, corners 5106, blocks by range 29760 / by sign 1824 / mixed 1184, interpolated 151552, solid 1197256; graph: lattice 4232, columns 0, corners 5106, voxels 0 in 0 cells, noise 2D 177744 / 3D 20424, instructions 31968 (627486 lane ops), interpolated 151552"
        };

        #endregion

        #region Explicit

        /// <summary>Prints chunks per class for <see cref="DensityFixture" /> (the lists there are fixed; this made them).</summary>
        [Test]
        [Explicit(TestTiers.ReportReason)]
        [Category(TestTiers.Report)]
        public void FindFixtureChunks(){
            Dictionary<DensityChunkClass, List<int2>> found = DensityFixture.Find(_fixture, DensityFixture.Seed, _config.WorldGen.WaterLevel, 8, 512, 8);
            StringBuilder s = new();
            foreach ((DensityChunkClass c, List<int2> chunks) in found)
                s.Append($"{c}: {string.Join(", ", chunks.Select(p => $"new int2({p.x}, {p.y})"))}\n");
            Debug.Log(s.ToString());
        }

        /// <summary>Prints the work counters per class, in the form of <see cref="ExpectedCounters" />.</summary>
        [Test]
        [Explicit(TestTiers.ReportReason)]
        [Category(TestTiers.Report)]
        public void PrintWorkCountersAndHash(){
            StringBuilder s = new();
            foreach (DensityChunkClass c in DensityFixture.Classes) s.Append($"[DensityChunkClass.{c}] = \"{Count(c)}\",\n");
            s.Append($"voxel hash 0x{VoxelHash():X16}\n");
            Debug.Log(s.ToString());
        }

        #endregion
    }
}
