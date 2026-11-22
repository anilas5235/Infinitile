using System;
using System.Collections.Generic;
using System.Linq;
using Engine.Scripts.WorldGen.Graph;
using NUnit.Framework;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using static Engine.Tests.EditMode.NoiseGraphTestKit;

namespace Engine.Tests.EditMode{
    /// <summary>
    ///     The compiled programs against the direct, uncompiled evaluation: bit for bit on hand-written and random
    ///     graphs, sound bounds on every sampled value, the marker interpolation against a trilinear reference and
    ///     equal values on the shared lattice and corners of neighbouring chunks.
    /// </summary>
    public class NoiseGraphEquivalenceTests{
        /// <summary>A small terrain-and-caves graph: 2D climate and surface, a terrain marker (8) and two cave markers (4).</summary>
        internal static GraphBuilder TerrainCaves(){
            return new GraphBuilder()
                .Noise("continents", NoiseKind.Simplex2, 4, 900f)
                .Noise("warp", NoiseKind.Simplex2, 2, 400f)
                .Noise("erosion", NoiseKind.Simplex2, 3, 500f)
                .Noise("terrain", NoiseKind.Simplex3, 3, 120f, 60f)
                .Noise("cheese", NoiseKind.Simplex3, 2, 70f, 35f)
                .Noise("tunnel", NoiseKind.Simplex3, 1, 50f, 25f)
                .Node("c", Op("shift", ("in", Noise("fbm", "continents")), ("x", Op("mul", ("a", Noise("fbm", "warp")), ("b", 120.0)))))
                .Node("e", Noise("fbm", "erosion"))
                .Node("offset", Spline("c", false, Point(-1, -0.6, 0.2), Point(-0.2, -0.1, 0.8),
                    Point(0.3, Spline("e", false, Point(-1, 0.6, -0.1), Point(0.2, 0.25), Point(1, 0.05)), 0.4), Point(1, 0.9, 0)))
                .Node("factor", Spline("e", true, Point(-1, 6.0), Point(0, 3.0), Point(1, 1.5)))
                .Node("surface", Op("add", ("a", 64.0), ("b", Op("mul", ("a", "offset"), ("b", 96.0)))))
                .Node("terrain_in", Op("add", ("a", Op("mul", ("a", "factor"), ("b", Op("mul", ("a", Op("sub", ("a", "surface"), ("b", "y"))), ("b", 1.0 / 128.0))))),
                    ("b", Op("mul", ("a", Noise("fbm", "terrain")), ("b", 0.5)))))
                .Node("terrain_marker", Op("interpolated", ("in", "terrain_in"), ("cellHeight", 8.0)))
                .Node("cheese_in", Op("add", ("a", Noise("fbm", "cheese")),
                    ("b", Op("y_gradient", ("fromY", 0.0), ("toY", 64.0), ("fromValue", -0.3), ("toValue", 0.2)))))
                .Node("cheese_marker", Op("interpolated", ("in", "cheese_in"), ("cellHeight", 4.0)))
                .Node("tunnel_in", Op("sub", ("a", Op("abs", ("in", Noise("simplex3", "tunnel")))), ("b", 0.08)))
                .Node("tunnel_marker", Op("interpolated", ("in", "tunnel_in"), ("cellHeight", 4.0)))
                .Output("continentalness", "c")
                .Output("surface", new Dictionary<string, object>{ ["node"] = "surface", ["perColumn"] = true })
                .Output("terrain_density", Op("soft_clamp", ("in", "terrain_marker")))
                .Output("cave_density", Op("min", ("a", "cheese_marker"),
                    ("b", Op("max", ("a", "tunnel_marker"), ("b", Op("mul", ("a", Op("sub", ("a", "y"), ("b", "surface"))), ("b", -0.05)))))));
        }

        #region Compiled against direct

        /// <summary>
        ///     Checks one compiled graph against its direct evaluation: 2D outputs at arbitrary points, per-column outputs
        ///     at every column, 3D outputs at the lattice columns and the heights of the 8-block cell corners (where the
        ///     interpolation gives the corner values), and every value inside its bound.
        /// </summary>
        private static void CheckAgainstDirect(CompiledNoiseGraph graph, uint seed, int2 origin, int2 size, float2[] points, string label){
            List<GraphOutputInfo> flat = graph.Outputs.Where(o => !o.Is3D).ToList();
            List<GraphOutputInfo> volume = graph.Outputs.Where(o => o.Is3D).ToList();

            if (flat.Count > 0){
                float[] compiled = GraphEvaluator.SampleLattice(graph, seed, points, out _);
                float[] direct = graph.EvaluateDirect(points.Select(p => new float3(p.x, 0f, p.y)).ToList(),
                    flat.Select(o => o.ReferenceNode).ToList(), seed);
                for (int p = 0; p < points.Length; p++)
                for (int k = 0; k < flat.Count; k++){
                    float c = compiled[flat[k].LatticeValue * points.Length + p], d = direct[p * flat.Count + k];
                    if (!SameValue(c, d)) Assert.Fail($"{label}: 2D output {flat[k].Name} at {points[p]}: compiled {c:R}, direct {d:R}");
                    if (!flat[k].Bounds.Contains(c)) Assert.Fail($"{label}: 2D output {flat[k].Name} at {points[p]}: {c} outside {flat[k].Bounds}");
                }
            }

            using GraphRegionResult region = GraphEvaluator.EvaluateRegion(graph, seed, origin, size, true);

            List<GraphOutputInfo> columns = flat.Where(o => o.PerColumn).ToList();
            if (columns.Count > 0){
                List<float3> positions = new();
                for (int z = 0; z < size.y; z++)
                for (int x = 0; x < size.x; x++)
                    positions.Add(new float3(origin.x + x, 0f, origin.y + z));
                float[] direct = graph.EvaluateDirect(positions, columns.Select(o => o.ReferenceNode).ToList(), seed);
                for (int i = 0; i < positions.Count; i++)
                for (int k = 0; k < columns.Count; k++){
                    float c = region.Column(columns[k].ColumnOutput, i % size.x, i / size.x), d = direct[i * columns.Count + k];
                    if (!SameValue(c, d)) Assert.Fail($"{label}: column output {columns[k].Name} at {positions[i]}: compiled {c:R}, direct {d:R}");
                }
            }

            if (volume.Count == 0) return;
            List<float3> corners = new();
            for (int z = 0; z < size.y; z += 4)
            for (int x = 0; x < size.x; x += 4)
            for (int y = 0; y < GraphLimits.WorldHeight; y += GraphLimits.CellBlockHeight)
                corners.Add(new float3(origin.x + x, y, origin.y + z));
            float[] directVolume = graph.EvaluateDirect(corners, volume.Select(o => o.ReferenceNode).ToList(), seed);
            for (int i = 0; i < corners.Count; i++)
            for (int k = 0; k < volume.Count; k++){
                int3 p = (int3)corners[i];
                float c = region.Voxel(volume[k].VoxelOutput, p.x - origin.x, p.y, p.z - origin.y), d = directVolume[i * volume.Count + k];
                if (!SameValue(c, d)) Assert.Fail($"{label}: 3D output {volume[k].Name} at {corners[i]}: compiled {c:R}, direct {d:R}");
            }

            // Every voxel, interpolated or not, inside the output's bound.
            foreach (GraphOutputInfo o in volume)
                for (int z = 0; z < size.y; z++)
                for (int x = 0; x < size.x; x++)
                for (int y = 0; y < GraphLimits.WorldHeight; y++){
                    float v = region.Voxel(o.VoxelOutput, x, y, z);
                    if (!o.Bounds.Contains(v)) Assert.Fail($"{label}: 3D output {o.Name} at ({origin.x + x}, {y}, {origin.y + z}): {v} outside {o.Bounds}");
                }
        }

        private static float2[] RandomPoints(System.Random random, int count){
            float2[] points = new float2[count];
            for (int i = 0; i < count; i++){
                float range = i % 5 == 0 ? 4e6f : 6e3f;
                points[i] = new float2((float)(random.NextDouble() - 0.5) * range, (float)(random.NextDouble() - 0.5) * range);
            }

            return points;
        }

        /// <summary>Every node of the uncompiled list at random positions lies inside its bound.</summary>
        private static void CheckReferenceBounds(CompiledNoiseGraph graph, uint seed, System.Random random, string label){
            List<float3> positions = new();
            for (int i = 0; i < 256; i++){
                float2 p = RandomPoints(random, 1)[0];
                positions.Add(new float3(p.x, random.Next(0, GraphLimits.WorldHeight), p.y));
            }

            int[] nodes = Enumerable.Range(0, graph.ReferenceNodeCount).ToArray();
            float[] values = graph.EvaluateDirect(positions, nodes, seed);
            for (int p = 0; p < positions.Count; p++)
            for (int n = 0; n < nodes.Length; n++){
                float v = values[p * nodes.Length + n];
                GraphBounds b = graph.ReferenceBound(n);
                if (!b.Contains(v)) Assert.Fail($"{label}: node {graph.ReferenceName(n)} at {positions[p]}: {v} outside {b}");
            }
        }

        [Test]
        public void TerrainCaves_CompiledEqualsDirect(){
            using CompiledNoiseGraph graph = CompileOrFail(TerrainCaves().Graph);
            System.Random random = new(3);
            CheckAgainstDirect(graph, Seed, new int2(-48, 96), new int2(32, 32), RandomPoints(random, 512), "terrain-caves");
            CheckReferenceBounds(graph, Seed, random, "terrain-caves");
            Assert.AreEqual(3, graph.Tables.MarkerCount);
            Assert.GreaterOrEqual(graph.Tables.CornerProgram4, 0);
            Assert.GreaterOrEqual(graph.Tables.CornerProgram8, 0);
            Assert.GreaterOrEqual(graph.Tables.ColumnProgram, 0);
        }

        /// <summary>
        ///     The voxel program's own lanes at every block, which the corner comparison of
        ///     <see cref="CheckAgainstDirect" /> cannot see: 3D outputs of the coordinates and 3D noise alone equal the
        ///     direct evaluation, and a 3D output over a 2D value equals its product with the interpolated column value.
        /// </summary>
        [Test]
        public void VoxelOutputs_EqualDirectAtEveryBlock(){
            GraphBuilder g = new GraphBuilder()
                .Noise("volume", NoiseKind.Simplex3, 2, 40f, 20f)
                .Noise("flat", NoiseKind.Simplex2, 2, 60f)
                .Output("coordinates", Op("mul", ("a", Op("add", ("a", "x"), ("b", "y"))), ("b", Op("sub", ("a", "z"), ("b", Op("mul", ("a", "y"), ("b", 3.0)))))))
                .Output("noise", Op("add", ("a", Noise("simplex3", "volume")), ("b", Op("mul", ("a", Noise("fbm", "volume")), ("b", "y")))))
                .Output("flat", Noise("fbm", "flat"))
                .Output("over_flat", Op("mul", ("a", "y"), ("b", Noise("fbm", "flat"))));
            using CompiledNoiseGraph graph = CompileOrFail(g.Graph);
            GraphOutputInfo coordinates = graph.Output("coordinates"), noise = graph.Output("noise"), flat = graph.Output("flat"), overFlat = graph.Output("over_flat");
            Assert.IsTrue(coordinates.Is3D && noise.Is3D && overFlat.Is3D && !flat.Is3D);
            Assert.AreEqual(0, graph.Tables.MarkerCount);

            int2 size = new(16, 16);
            foreach (int2 origin in new[]{ new int2(0, 0), new int2(-52, 28) }){
                using GraphRegionResult region = GraphEvaluator.EvaluateRegion(graph, Seed, origin, size, true, true);
                List<float3> positions = new();
                for (int z = 0; z < size.y; z++)
                for (int x = 0; x < size.x; x++)
                for (int y = 0; y < GraphLimits.WorldHeight; y++)
                    positions.Add(new float3(origin.x + x, y, origin.y + z));
                float[] direct = graph.EvaluateDirect(positions, new[]{ coordinates.ReferenceNode, noise.ReferenceNode }, Seed);

                int i = 0;
                for (int z = 0; z < size.y; z++)
                for (int x = 0; x < size.x; x++)
                for (int y = 0; y < GraphLimits.WorldHeight; y++, i++){
                    string at = $"({origin.x + x}, {y}, {origin.y + z})";
                    float c = region.Voxel(coordinates.VoxelOutput, x, y, z), n = region.Voxel(noise.VoxelOutput, x, y, z);
                    if (!SameValue(c, direct[2 * i])) Assert.Fail($"coordinates at {at}: compiled {c:R}, direct {direct[2 * i]:R}");
                    if (!SameValue(n, direct[2 * i + 1])) Assert.Fail($"noise at {at}: compiled {n:R}, direct {direct[2 * i + 1]:R}");
                    float expected = y * region.Column2D(flat.LatticeValue, x, z), o = region.Voxel(overFlat.VoxelOutput, x, y, z);
                    if (!SameValue(o, expected)) Assert.Fail($"over_flat at {at}: compiled {o:R}, y · interpolated column {expected:R}");
                }
            }
        }

        [Test]
        public void RandomGraphs_CompiledEqualsDirect_AndBoundsAreSound(){
            const int graphs = 80;
            for (int g = 0; g < graphs; g++){
                System.Random random = new(1000 + g);
                NoiseGraph graph = RandomGraphGenerator.Generate(random);
                GraphCompileResult r = Compile(graph);
                if (!r.Succeeded) Assert.Fail($"random graph {g} failed to compile:\n" + string.Join("\n", r.Messages) + "\n" + NoiseGraphJson.Write(graph));
                using CompiledNoiseGraph compiled = r.Graph;
                uint seed = (uint)random.Next();
                int2 origin = new(random.Next(-4000, 4000) & ~3, random.Next(-4000, 4000) & ~3);
                try{
                    CheckAgainstDirect(compiled, seed, origin, new int2(8, 8), RandomPoints(random, 64), $"random graph {g}");
                    CheckReferenceBounds(compiled, seed, random, $"random graph {g}");
                }
                catch (AssertionException){
                    TestContext.WriteLine(NoiseGraphJson.Write(graph));
                    throw;
                }
            }
        }

        #endregion

        #region Marker interpolation

        [BurstCompile(FloatMode = FloatMode.Strict, CompileSynchronously = true)]
        private struct TrilinearReferenceJob : IJob{
            /// <summary>8 per voxel: at y0 (z0 x0, z0 x1, z1 x0, z1 x1), then the same at y1.</summary>
            [ReadOnly] public NativeArray<float> Corners;

            /// <summary>4 per voxel: z0 x0, z0 x1, z1 x0, z1 x1.</summary>
            [ReadOnly] public NativeArray<float> Flat;

            [ReadOnly] public NativeArray<float3> T;
            public NativeArray<float> Marker, Mix;

            public void Execute(){
                for (int i = 0; i < T.Length; i++){
                    float3 t = T[i];
                    int c = i * 8, f = i * 4;
                    float v00 = GraphInterpolation.Lerp(Corners[c], Corners[c + 1], t.x);
                    float v10 = GraphInterpolation.Lerp(Corners[c + 2], Corners[c + 3], t.x);
                    float v01 = GraphInterpolation.Lerp(Corners[c + 4], Corners[c + 5], t.x);
                    float v11 = GraphInterpolation.Lerp(Corners[c + 6], Corners[c + 7], t.x);
                    float m = GraphInterpolation.Lerp(GraphInterpolation.Lerp(v00, v10, t.z), GraphInterpolation.Lerp(v01, v11, t.z), t.y);
                    float flat = GraphInterpolation.Lerp(GraphInterpolation.Lerp(Flat[f], Flat[f + 1], t.x), GraphInterpolation.Lerp(Flat[f + 2], Flat[f + 3], t.x), t.z);
                    Marker[i] = m;
                    Mix[i] = m + flat;
                }
            }
        }

        [TestCase(8)]
        [TestCase(4)]
        public void MarkerValues_AreTheTrilinearInterpolationOfTheDirectCornerValues(int cellHeight){
            GraphBuilder g = new GraphBuilder()
                .Noise("volume", NoiseKind.Simplex3, 2, 40f, 20f)
                .Noise("flat", NoiseKind.Simplex2, 2, 60f)
                .Node("marker_in", Op("add", ("a", Noise("fbm", "volume")), ("b", Op("mul", ("a", "y"), ("b", 0.01)))))
                .Node("marker", Op("interpolated", ("in", "marker_in"), ("cellHeight", (double)cellHeight)))
                .Node("flat_value", Op("mul", ("a", Noise("fbm", "flat")), ("b", 3.0)))
                .Output("marker", "marker")
                .Output("mix", Op("add", ("a", "marker"), ("b", "flat_value")));
            using CompiledNoiseGraph graph = CompileOrFail(g.Graph);
            int markerIn = Enumerable.Range(0, graph.ReferenceNodeCount).First(i => graph.ReferenceName(i) == "marker_in");
            int flatValue = Enumerable.Range(0, graph.ReferenceNodeCount).First(i => graph.ReferenceName(i) == "flat_value");

            int2 origin = new(-16, 24), size = new(16, 8);
            using GraphRegionResult region = GraphEvaluator.EvaluateRegion(graph, Seed, origin, size, true);

            int voxels = size.x * size.y * GraphLimits.WorldHeight;
            float[] cornerInput = new float[voxels * 8], flatInput = new float[voxels * 4];
            float3[] tInput = new float3[voxels];

            // Direct values at every corner of the region's cells and at every lattice column.
            List<float3> cornerPositions = new();
            int lx = size.x / 4 + 1, lz = size.y / 4 + 1, ly = GraphLimits.WorldHeight / cellHeight + 1;
            for (int z = 0; z < lz; z++)
            for (int x = 0; x < lx; x++)
            for (int y = 0; y < ly; y++)
                cornerPositions.Add(new float3(origin.x + 4 * x, y * cellHeight, origin.y + 4 * z));
            float[] cornerValues = graph.EvaluateDirect(cornerPositions, new[]{ markerIn, flatValue }, Seed);
            float CornerValue(int x, int y, int z){
                return cornerValues[((z * lx + x) * ly + y) * 2];
            }

            float FlatValue(int x, int z){
                return cornerValues[(z * lx + x) * ly * 2 + 1];
            }

            int i = 0;
            for (int z = 0; z < size.y; z++)
            for (int x = 0; x < size.x; x++)
            for (int y = 0; y < GraphLimits.WorldHeight; y++, i++){
                int cx = x / 4, cz = z / 4, cy = y / cellHeight;
                for (int k = 0; k < 8; k++) cornerInput[i * 8 + k] = CornerValue(cx + (k & 1), cy + (k >> 2), cz + ((k >> 1) & 1));
                for (int k = 0; k < 4; k++) flatInput[i * 4 + k] = FlatValue(cx + (k & 1), cz + (k >> 1));
                tInput[i] = new float3((x & 3) * 0.25f, (y - cy * cellHeight) * (cellHeight == 4 ? 0.25f : 0.125f), (z & 3) * 0.25f);
            }

            using NativeArray<float> corners = new(cornerInput, Allocator.TempJob), flat = new(flatInput, Allocator.TempJob);
            using NativeArray<float3> t = new(tInput, Allocator.TempJob);
            using NativeArray<float> marker = new(voxels, Allocator.TempJob), mix = new(voxels, Allocator.TempJob);
            new TrilinearReferenceJob{ Corners = corners, Flat = flat, T = t, Marker = marker, Mix = mix }.Run();

            GraphOutputInfo markerOut = graph.Output("marker"), mixOut = graph.Output("mix");
            i = 0;
            for (int z = 0; z < size.y; z++)
            for (int x = 0; x < size.x; x++)
            for (int y = 0; y < GraphLimits.WorldHeight; y++, i++){
                float m = region.Voxel(markerOut.VoxelOutput, x, y, z), s = region.Voxel(mixOut.VoxelOutput, x, y, z);
                if (!SameValue(m, marker[i])) Assert.Fail($"marker at ({origin.x + x}, {y}, {origin.y + z}): {m:R} vs reference {marker[i]:R}");
                if (!SameValue(s, mix[i])) Assert.Fail($"mix at ({origin.x + x}, {y}, {origin.y + z}): {s:R} vs reference {mix[i]:R}");
            }

            Assert.AreEqual((long)voxels, region.Counters.Voxels);
            Assert.AreEqual((long)voxels, region.Counters.InterpolatedValues, "one marker per voxel");
            Assert.AreEqual((long)lx * lz * ly, region.Counters.Corners);
        }

        #endregion

        #region Neighbouring chunks

        [TestCase(0, 0)]
        [TestCase(-32, -64)]
        public void NeighbouringChunks_ShareLatticeAndCornerValues(int x0, int z0){
            using CompiledNoiseGraph graph = CompileOrFail(TerrainCaves().Graph);
            int2 size = new(32, 32);
            using GraphRegionResult a = GraphEvaluator.EvaluateRegion(graph, Seed, new int2(x0, z0), size, true);
            using GraphRegionResult east = GraphEvaluator.EvaluateRegion(graph, Seed, new int2(x0 + 32, z0), size, true);
            using GraphRegionResult north = GraphEvaluator.EvaluateRegion(graph, Seed, new int2(x0, z0 + 32), size, true);
            GraphData tables = graph.Tables;

            for (int l = 0; l <= 8; l++){
                for (int v = 0; v < tables.LatticeValueCount; v++){
                    Assert.AreEqual(a.LatticeValue(v, 8, l), east.LatticeValue(v, 0, l), $"lattice value {v} on the east edge, row {l}");
                    Assert.AreEqual(a.LatticeValue(v, l, 8), north.LatticeValue(v, l, 0), $"lattice value {v} on the north edge, column {l}");
                }

                for (int m = 0; m < tables.MarkerCount; m++){
                    int lanes = GraphLimits.WorldHeight / tables.MarkerCellHeights[m] + 1;
                    for (int cy = 0; cy < lanes; cy++){
                        Assert.AreEqual(a.Corner(m, 8, l, cy), east.Corner(m, 0, l, cy), $"marker {m} corner on the east edge, row {l}, cy {cy}");
                        Assert.AreEqual(a.Corner(m, l, 8, cy), north.Corner(m, l, 0, cy), $"marker {m} corner on the north edge, column {l}, cy {cy}");
                    }
                }
            }
        }

        #endregion
    }

    /// <summary>
    ///     Random valid graphs for the fuzz test: every op, 2D and 3D noises, shifts, nested splines, markers of both
    ///     cell heights and per-column outputs. A rough magnitude estimate wraps a node in a clamp before values can
    ///     overflow, so every bound stays finite.
    /// </summary>
    internal static class RandomGraphGenerator{
        private sealed class Item{
            public object Reference;
            public bool Is3D, HasMarker;
            public int SplineDepth;
            public double Magnitude;
        }

        private static readonly string[] Unary ={ "abs", "neg", "square", "cube", "half_negative", "quarter_negative", "soft_clamp" };
        private static readonly string[] Binary ={ "add", "sub", "mul", "min", "max" };

        public static NoiseGraph Generate(System.Random random){
            GraphBuilder g = new();
            int noises2 = random.Next(1, 4), noises3 = random.Next(1, 4);
            List<string> flat = new(), volume = new();
            for (int i = 0; i < noises2; i++){
                flat.Add($"f{i}");
                g.Noise($"f{i}", NoiseKind.Simplex2, random.Next(1, 5), (float)(20 + random.NextDouble() * 500), 0f, (float)(1.5 + random.NextDouble()),
                    (float)(0.3 + random.NextDouble() * 0.4), (float)(0.25 + random.NextDouble() * 2));
            }

            for (int i = 0; i < noises3; i++){
                volume.Add($"v{i}");
                g.Noise($"v{i}", NoiseKind.Simplex3, random.Next(1, 4), (float)(16 + random.NextDouble() * 200), (float)(8 + random.NextDouble() * 100),
                    (float)(1.5 + random.NextDouble()), (float)(0.3 + random.NextDouble() * 0.4), (float)(0.25 + random.NextDouble() * 2));
            }

            List<Item> items = new(){
                new Item{ Reference = Op("mul", ("a", "x"), ("b", 0.01)), Magnitude = GraphLimits.CoordinateLimit * 0.01 },
                new Item{ Reference = Op("mul", ("a", "z"), ("b", 0.013)), Magnitude = GraphLimits.CoordinateLimit * 0.013 },
                new Item{ Reference = Op("mul", ("a", "y"), ("b", 0.02)), Magnitude = 256 * 0.02, Is3D = true }
            };

            int count = random.Next(8, 36);
            for (int n = 0; n < count; n++){
                Item item = Make(random, items, flat, volume);
                if (item.Magnitude > 1e7){
                    double limit = Math.Round(1 + random.NextDouble() * 50, 2);
                    item = new Item{
                        Reference = Op("clamp", ("in", item.Reference), ("min", -limit), ("max", limit)),
                        Is3D = item.Is3D, HasMarker = item.HasMarker, SplineDepth = item.SplineDepth, Magnitude = limit
                    };
                }

                string name = $"n{n:00}";
                g.Node(name, (Dictionary<string, object>)(item.Reference is Dictionary<string, object> d ? d : Op("add", ("a", item.Reference), ("b", 0.0))));
                items.Add(new Item{ Reference = name, Is3D = item.Is3D, HasMarker = item.HasMarker, SplineDepth = item.SplineDepth, Magnitude = item.Magnitude });
            }

            int outputs = random.Next(2, 6);
            for (int o = 0; o < outputs; o++){
                Item item = items[items.Count - 1 - random.Next(Math.Min(items.Count - 3, 8))];
                if (!item.Is3D && random.Next(3) == 0) g.Output($"o{o}", new Dictionary<string, object>{ ["node"] = item.Reference, ["perColumn"] = true });
                else g.Output($"o{o}", item.Reference);
            }

            return g.Graph;
        }

        private static Item Pick(System.Random random, List<Item> items, Func<Item, bool> filter = null){
            List<Item> pool = filter == null ? items : items.Where(filter).ToList();
            if (pool.Count == 0) pool = items;
            // Prefer recent nodes so the graph gets deep.
            int k = random.Next(3) == 0 ? random.Next(pool.Count) : pool.Count - 1 - random.Next(Math.Min(pool.Count, 4));
            return pool[k];
        }

        private static double Constant(System.Random random, double range = 3){
            return Math.Round((random.NextDouble() * 2 - 1) * range, 3);
        }

        private static Item Make(System.Random random, List<Item> items, List<string> flat, List<string> volume){
            switch (random.Next(12)){
                case 0:
                case 1:{
                    // Noise, sometimes shifted.
                    bool is3D = random.Next(2) == 0;
                    string noise = is3D ? volume[random.Next(volume.Count)] : flat[random.Next(flat.Count)];
                    string op = random.Next(4) switch{ 0 => is3D ? "simplex3" : "simplex2", 1 => "ridged", _ => "fbm" };
                    Dictionary<string, object> node = Noise(op, noise);
                    Item result = new(){ Reference = node, Is3D = is3D, Magnitude = 2.5 };
                    if (random.Next(3) != 0) return result;
                    Dictionary<string, object> shift = Op("shift", ("in", node));
                    foreach (string axis in is3D ? new[]{ "x", "y", "z" } : new[]{ "x", "z" }){
                        if (random.Next(2) == 0) continue;
                        Item offset = Pick(random, items, i => !i.HasMarker);
                        shift[axis] = offset.Reference;
                        result.Is3D |= offset.Is3D;
                        result.SplineDepth = Math.Max(result.SplineDepth, offset.SplineDepth);
                    }

                    result.Reference = shift;
                    return result;
                }
                case 2:
                case 3:{
                    Item a = Pick(random, items);
                    bool constant = random.Next(3) == 0;
                    Item b = constant ? new Item{ Reference = Constant(random), Magnitude = 3 } : Pick(random, items);
                    string op = Binary[random.Next(Binary.Length)];
                    if (random.Next(2) == 0) (a, b) = (b, a);
                    return Combine(new Item{
                        Reference = Op(op, ("a", a.Reference), ("b", b.Reference)),
                        Magnitude = op == "mul" ? a.Magnitude * b.Magnitude : op is "add" or "sub" ? a.Magnitude + b.Magnitude : Math.Max(a.Magnitude, b.Magnitude)
                    }, a, b);
                }
                case 4:{
                    Item a = Pick(random, items);
                    string op = Unary[random.Next(Unary.Length)];
                    double m = op switch{ "square" => a.Magnitude * a.Magnitude, "cube" => a.Magnitude * a.Magnitude * a.Magnitude, "soft_clamp" => 1, _ => a.Magnitude };
                    return Combine(new Item{ Reference = Op(op, ("in", a.Reference)), Magnitude = m }, a);
                }
                case 5:{
                    Item a = Pick(random, items);
                    double lo = Constant(random), hi = lo + Math.Round(random.NextDouble() * 3, 3);
                    return Combine(new Item{ Reference = Op("clamp", ("in", a.Reference), ("min", lo), ("max", hi)), Magnitude = Math.Max(Math.Abs(lo), Math.Abs(hi)) }, a);
                }
                case 6:{
                    Item t = Pick(random, items), a = Pick(random, items), b = Pick(random, items);
                    return Combine(new Item{
                        Reference = Op("lerp", ("t", t.Reference), ("a", a.Reference), ("b", b.Reference)),
                        Magnitude = a.Magnitude + t.Magnitude * (a.Magnitude + b.Magnitude) + 1
                    }, t, a, b);
                }
                case 7:{
                    Item a = Pick(random, items);
                    double from = Constant(random), to = from + (random.Next(2) == 0 ? 1 : -1) * (0.1 + Math.Round(random.NextDouble() * 2, 3));
                    return Combine(new Item{ Reference = Op("smoothstep", ("in", a.Reference), ("from", from), ("to", to)), Magnitude = 1 }, a);
                }
                case 8:{
                    Item a = Pick(random, items), b = Pick(random, items), c = Pick(random, items);
                    double lo = Constant(random), hi = lo + Math.Round(random.NextDouble() * 2, 3);
                    return Combine(new Item{
                        Reference = Op("range_choice", ("in", a.Reference), ("min", lo), ("max", hi), ("inRange", b.Reference), ("outRange", c.Reference)),
                        Magnitude = Math.Max(b.Magnitude, c.Magnitude)
                    }, a, b, c);
                }
                case 9:{
                    double fromY = random.Next(0, 200), toY = fromY + random.Next(1, 120) * (random.Next(2) == 0 ? 1 : -1);
                    double fromValue = Constant(random), toValue = Constant(random);
                    return new Item{
                        Reference = Op("y_gradient", ("fromY", fromY), ("toY", toY), ("fromValue", fromValue), ("toValue", toValue)),
                        Is3D = true, Magnitude = Math.Max(Math.Abs(fromValue), Math.Abs(toValue))
                    };
                }
                case 10:{
                    Item coordinate = Pick(random, items, i => i.SplineDepth < GraphLimits.MaxSplineDepth - 1);
                    int points = random.Next(1, 6);
                    bool linear = random.Next(3) == 0;
                    double location = Constant(random, 2);
                    List<Item> parts = new(){ coordinate };
                    List<Dictionary<string, object>> list = new();
                    double magnitude = 0;
                    for (int k = 0; k < points; k++){
                        object value;
                        if (random.Next(2) == 0){
                            Item v = Pick(random, items, i => i.SplineDepth < GraphLimits.MaxSplineDepth - 1 && !i.HasMarker);
                            parts.Add(v);
                            value = v.Reference;
                            magnitude = Math.Max(magnitude, v.Magnitude);
                        }
                        else{
                            double c = Constant(random);
                            value = c;
                            magnitude = Math.Max(magnitude, Math.Abs(c));
                        }

                        double? derivative = random.Next(2) == 0 ? Constant(random, 2) : null;
                        list.Add(Point(location, value, derivative));
                        location += 0.05 + Math.Round(random.NextDouble() * 1.5, 3);
                    }

                    Item result = Combine(new Item{ Reference = Spline(coordinate.Reference, linear, list.ToArray()), Magnitude = 3 * magnitude + 20 }, parts.ToArray());
                    result.SplineDepth++;
                    return result;
                }
                default:{
                    // Marker over a node without one (a 2D operand folds to the identity).
                    Item a = Pick(random, items, i => !i.HasMarker && i.Is3D);
                    if (a.HasMarker) a = items[2];
                    double cellHeight = random.Next(2) == 0 ? 4 : 8;
                    Item result = Combine(new Item{ Reference = Op("interpolated", ("in", a.Reference), ("cellHeight", cellHeight)), Magnitude = a.Magnitude }, a);
                    result.HasMarker = true;
                    return result;
                }
            }
        }

        private static Item Combine(Item result, params Item[] operands){
            foreach (Item o in operands){
                result.Is3D |= o.Is3D;
                result.HasMarker |= o.HasMarker;
                result.SplineDepth = Math.Max(result.SplineDepth, o.SplineDepth);
            }

            return result;
        }
    }
}
