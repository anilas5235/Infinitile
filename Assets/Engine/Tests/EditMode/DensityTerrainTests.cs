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
            Assert.AreEqual(235f, terrain.SlideStart);
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
        public void NoTerrainAbove255(){
            // The generation top of the fixture and FlatGround is y 255 (top_slide_end); the chunk is 512 high since H1.
            const int top = 255;
            foreach (int2 chunk in AllFixtureChunks()){
                using DensityProbeResult p = DensityProbe.Run(_fixture, _config.WorldGen, chunk * Size, DensityChunk.PassTerrain);
                for (int x = 0; x < Size; x++)
                for (int z = 0; z < Size; z++)
                for (int y = top; y < Height; y++)
                    if (p.IsSolid(x, y, z))
                        Assert.Fail($"terrain at y {y} in column ({chunk.x * Size + x}, {chunk.y * Size + z})");
            }

            using CompiledNoiseGraph high = CompileOrFail(FlatGround(253, 8, 8).Graph);
            using DensityProbeResult flat = DensityProbe.Run(high, _config.WorldGen, int2.zero, DensityChunk.PassTerrain);
            for (int i = 0; i < DensityLayout.Columns; i++)
                Assert.Less(flat.Heightmap[i], top, "flat ground at y 253 reaches y 255");
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
            const ulong expected = 0xD7DEE36334953991UL;
            ulong actual = VoxelHash();
            Assert.AreEqual(expected, actual, $"voxel hash 0x{actual:X16}");
        }

        /// <summary>
        ///     W2-HGT-02: the voxels below y 256, hashed in the order of the 256-high chunks, equal master's (e67aa08)
        ///     hash from before H1, so the 512-high build changed nothing in the look. Valid until H2 moves the terrain.
        /// </summary>
        [Test]
        public void VoxelHash_BelowY256_EqualsThe256HighWorld(){
            const ulong expected = 0x72AF39D7F38C0191UL;
            ulong actual = VoxelHash(256);
            Assert.AreEqual(expected, actual, $"voxel hash below y 256: 0x{actual:X16}");

            // Above the shipped graph's generation top (y 255) every voxel is air.
            foreach (int2 chunk in AllFixtureChunks()){
                using DensityProbeResult p = DensityProbe.Run(_fixture, _config.WorldGen, chunk * Size);
                for (int column = 0; column < Size * Size; column++)
                for (int y = 256; y < Height; y++)
                    if (p.Voxels[column * Height + y] != 0)
                        Assert.Fail($"chunk {chunk}: voxel {p.Voxels[column * Height + y]} at y {y} in column {column}");
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
            [DensityChunkClass.Ocean] = "8 chunks: corner columns 648, corners 4448, blocks by range 29800 / by sign 2345 / mixed 623, interpolated 79744, solid 844817; graph: lattice 4232, columns 0, corners 4448, voxels 0 in 0 cells, noise 2D 97336 / 3D 17792, instructions 18720 (315544 lane ops), interpolated 79744",
            [DensityChunkClass.Coast] = "8 chunks: corner columns 648, corners 4888, blocks by range 29534 / by sign 2560 / mixed 674, interpolated 86272, solid 1049000; graph: lattice 4232, columns 0, corners 4888, voxels 0 in 0 cells, noise 2D 97336 / 3D 19552, instructions 18720 (320384 lane ops), interpolated 86272",
            [DensityChunkClass.Lowland] = "8 chunks: corner columns 648, corners 3883, blocks by range 30242 / by sign 1968 / mixed 558, interpolated 71424, solid 1144771; graph: lattice 4232, columns 0, corners 3883, voxels 0 in 0 cells, noise 2D 97336 / 3D 15532, instructions 18720 (309329 lane ops), interpolated 71424",
            [DensityChunkClass.Massif] = "8 chunks: corner columns 648, corners 8342, blocks by range 26898 / by sign 4869 / mixed 1001, interpolated 128128, solid 1575542; graph: lattice 4232, columns 0, corners 8342, voxels 0 in 0 cells, noise 2D 97336 / 3D 33368, instructions 18720 (358378 lane ops), interpolated 128128"
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
