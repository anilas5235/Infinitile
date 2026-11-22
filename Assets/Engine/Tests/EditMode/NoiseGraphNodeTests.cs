using System;
using System.Collections.Generic;
using System.Linq;
using Engine.Scripts.WorldGen.Core;
using Engine.Scripts.WorldGen.Graph;
using Engine.Scripts.WorldGen.Noise;
using NUnit.Framework;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using static Engine.Tests.EditMode.NoiseGraphTestKit;

namespace Engine.Tests.EditMode{
    /// <summary>
    ///     Every node of the noise graph against a scalar reference written from its definition, and the lane simplex
    ///     against the scalar simplex bit for bit.
    /// </summary>
    public class NoiseGraphNodeTests{
        private static readonly double[] SplineLinearLocations ={ -2, 0, 2 };

        #region 2D nodes

        /// <summary>Points (x, z) over [−300, 300]², not on the lattice.</summary>
        private static float2[] Points2D(){
            List<float2> points = new();
            for (int j = 0; j < 20; j++)
            for (int i = 0; i < 20; i++)
                points.Add(new float2(-300f + 31.25f * i + 0.37f, -300f + 30.5f * j - 0.81f));
            return points.ToArray();
        }

        private static GraphBuilder Graph2D(){
            GraphBuilder g = new GraphBuilder()
                .Noise("single", NoiseKind.Simplex2, 1, 90f, amplitude: 1.5f)
                .Noise("octaves", NoiseKind.Simplex2, 3, 150f, lacunarity: 2.1f, gain: 0.45f, amplitude: 1.3f)
                .Noise("ridges", NoiseKind.Simplex2, 2, 120f)
                .Node("a", Op("mul", ("a", "x"), ("b", 0.01)))
                .Node("b", Op("mul", ("a", "z"), ("b", 0.007)))
                .Node("t", Op("mul", ("a", Op("add", ("a", "z"), ("b", 300.0))), ("b", 1.0 / 600.0)));

            g.Output("const", Op("const", ("value", 2.5)))
                .Output("x", "x")
                .Output("z", "z")
                .Output("add", Op("add", ("a", "a"), ("b", "b")))
                .Output("sub", Op("sub", ("a", "a"), ("b", "b")))
                .Output("mul", Op("mul", ("a", "a"), ("b", "b")))
                .Output("min", Op("min", ("a", "a"), ("b", "b")))
                .Output("max", Op("max", ("a", "a"), ("b", "b")))
                .Output("abs", Op("abs", ("in", "a")))
                .Output("neg", Op("neg", ("in", "a")))
                .Output("square", Op("square", ("in", "a")))
                .Output("cube", Op("cube", ("in", "a")))
                .Output("half_negative", Op("half_negative", ("in", "a")))
                .Output("quarter_negative", Op("quarter_negative", ("in", "a")))
                .Output("soft_clamp", Op("soft_clamp", ("in", "a")))
                .Output("clamp", Op("clamp", ("in", "a"), ("min", -1.0), ("max", 0.5)))
                .Output("lerp", Op("lerp", ("t", "t"), ("a", "a"), ("b", "b")))
                .Output("smoothstep", Op("smoothstep", ("in", "a"), ("from", -1.0), ("to", 2.0)))
                .Output("smoothstep_falling", Op("smoothstep", ("in", "a"), ("from", 2.0), ("to", -1.0)))
                .Output("range_choice", Op("range_choice", ("in", "a"), ("min", -0.5), ("max", 1.0), ("inRange", "b"),
                    ("outRange", Op("neg", ("in", "b")))))
                .Output("spline_linear", Spline("a", true, Point(-2, -1.0), Point(0, "b"), Point(2, 3.0)))
                .Output("spline_cubic", Spline("a", false, Point(-2, 1.0, 0.5), Point(0, "b", -1), Point(1.5, 2.0, 0)))
                .Output("simplex2", Noise("simplex2", "single"))
                .Output("fbm", Noise("fbm", "octaves"))
                .Output("ridged", Noise("ridged", "ridges"))
                .Output("shift", Op("shift", ("in", Noise("fbm", "octaves")), ("x", Op("mul", ("a", "z"), ("b", 0.5))),
                    ("z", Op("add", ("a", "x"), ("b", 7.0)))));
            return g;
        }

        private static double Hermite(double c, double[] l, double[] v, double[] d){
            if (c < l[0]) return v[0];
            for (int k = 0; k < l.Length - 1; k++){
                if (c >= l[k + 1]) continue;
                double span = l[k + 1] - l[k];
                double t = (c - l[k]) / span;
                double dv = v[k + 1] - v[k];
                double g = d[k] * span - dv, h = dv - d[k + 1] * span;
                return v[k] + t * dv + t * (1 - t) * (g + t * (h - g));
            }

            return v[^1];
        }

        private static double Linear(double c, double[] l, double[] v){
            if (c < l[0]) return v[0];
            for (int k = 0; k < l.Length - 1; k++)
                if (c < l[k + 1])
                    return v[k] + (c - l[k]) / (l[k + 1] - l[k]) * (v[k + 1] - v[k]);
            return v[^1];
        }

        /// <summary>
        ///     The octave sum of a noise as the design defines it: amplitudes divided by their sum. The simplex samples run
        ///     inside Burst, as in the interpreter: the 3D simplex jumps where a sample changes cell, and managed float math
        ///     can pick the other cell near a boundary.
        /// </summary>
        internal static double[] NoiseReference(NoiseDefinition n, bool ridged, int octaves, IReadOnlyList<float3> points){
            using NativeArray<float3> input = new(points.ToArray(), Allocator.TempJob);
            using NativeArray<double> values = new(points.Count, Allocator.TempJob);
            new NoiseReferenceJob{
                Points = input, Values = values, Seed = Seed, Salt = GraphResolver.NoiseSalt(n.Name), Is3D = n.Kind == NoiseKind.Simplex3,
                Ridged = ridged, Octaves = octaves, Wavelength = n.Wavelength, WavelengthY = n.WavelengthY, Lacunarity = n.Lacunarity,
                Gain = n.Gain, Amplitude = n.Amplitude
            }.Run();
            return values.ToArray();
        }

        internal static double NoiseReference(NoiseDefinition n, bool ridged, int octaves, float x, float y, float z){
            return NoiseReference(n, ridged, octaves, new[]{ new float3(x, y, z) })[0];
        }

        [BurstCompile(FloatMode = FloatMode.Strict, CompileSynchronously = true)]
        private struct NoiseReferenceJob : IJob{
            [ReadOnly] public NativeArray<float3> Points;
            public NativeArray<double> Values;
            public uint Seed, Salt;
            public bool Is3D, Ridged;
            public int Octaves;
            public float Wavelength, WavelengthY, Lacunarity, Gain, Amplitude;

            public void Execute(){
                double sum = 0, a = 1;
                for (int o = 0; o < Octaves; o++){
                    sum += a;
                    a *= Gain;
                }

                for (int i = 0; i < Points.Length; i++){
                    float3 p = Points[i];
                    double acc = 0;
                    float f = 1f / Wavelength, fy = 1f / WavelengthY;
                    a = 1;
                    for (int o = 0; o < Octaves; o++){
                        uint octaveSalt = HashSalts.FbmOctave(Salt, o);
                        double v = Is3D
                            ? SeededSimplex.Noise(new float3(p.x * f, p.y * fy, p.z * f), Seed, octaveSalt)
                            : SeededSimplex.Noise(new float2(p.x * f, p.z * f), Seed, octaveSalt);
                        acc += (Ridged ? 1 - math.abs(v) : v) * a / sum * Amplitude;
                        a *= Gain;
                        f *= Lacunarity;
                        fy *= Lacunarity;
                    }

                    Values[i] = acc;
                }
            }
        }

        private static double Expected2D(string output, NoiseGraph graph, float x, float z){
            double a = x * 0.01f, b = z * 0.007f, t = (z + 300f) * (1f / 600f);
            switch (output){
                case "const": return 2.5;
                case "x": return x;
                case "z": return z;
                case "add": return a + b;
                case "sub": return a - b;
                case "mul": return a * b;
                case "min": return Math.Min(a, b);
                case "max": return Math.Max(a, b);
                case "abs": return Math.Abs(a);
                case "neg": return -a;
                case "square": return a * a;
                case "cube": return a * a * a;
                case "half_negative": return a < 0 ? a / 2 : a;
                case "quarter_negative": return a < 0 ? a / 4 : a;
                case "soft_clamp":{
                    double v = Math.Clamp(a, -1, 1);
                    return 1.5 * v - 0.5 * v * v * v;
                }
                case "clamp": return Math.Clamp(a, -1, 0.5);
                case "lerp": return a + t * (b - a);
                case "smoothstep":{
                    double s = Math.Clamp((a + 1) / 3, 0, 1);
                    return s * s * (3 - 2 * s);
                }
                case "smoothstep_falling":{
                    double s = Math.Clamp((a - 2) / -3, 0, 1);
                    return s * s * (3 - 2 * s);
                }
                case "range_choice": return a >= -0.5 && a < 1 ? b : -b;
                case "spline_linear": return Linear(a, SplineLinearLocations, new[]{ -1, b, 3 });
                case "spline_cubic": return Hermite(a, new double[]{ -2, 0, 1.5 }, new[]{ 1, b, 2 }, new double[]{ 0.5, -1, 0 });
                case "simplex2": return NoiseReference(graph.Noises["single"], false, 1, x, 0f, z);
                case "fbm": return NoiseReference(graph.Noises["octaves"], false, 3, x, 0f, z);
                case "ridged": return NoiseReference(graph.Noises["ridges"], true, 2, x, 0f, z);
                case "shift": return NoiseReference(graph.Noises["octaves"], false, 3, x + z * 0.5f, 0f, z + (x + 7f));
                default: throw new ArgumentException(output);
            }
        }

        [Test]
        public void Nodes2D_MatchTheirScalarDefinitions(){
            GraphBuilder builder = Graph2D();
            using CompiledNoiseGraph graph = CompileOrFail(builder.Graph);
            float2[] points = Points2D();
            float[] values = GraphEvaluator.SampleLattice(graph, Seed, points, out _);
            int failures = 0;
            foreach (GraphOutputInfo output in graph.Outputs){
                Assert.IsFalse(output.Is3D, output.Name);
                for (int p = 0; p < points.Length; p++){
                    float actual = values[output.LatticeValue * points.Length + p];
                    double expected = Expected2D(output.Name, builder.Graph, points[p].x, points[p].y);
                    if (Close(actual, expected)) continue;
                    if (failures++ < 20) TestContext.WriteLine($"{output.Name} at {points[p]}: {actual} vs {expected}");
                }
            }

            Assert.AreEqual(0, failures, "values off their reference (first 20 printed)");
        }

        [Test]
        public void SplineWithoutDerivatives_InterpolatesThePointsMonotonically(){
            // Constant values and no derivatives: tangents from the monotone (Fritsch–Carlson) rule, as v1's splines.
            double[] l ={ -1, -0.2, 0.1, 0.6, 1 };
            double[] v ={ -0.5, -0.1, 0.4, 0.45, 1.2 };
            Dictionary<string, object>[] points = new Dictionary<string, object>[l.Length];
            for (int k = 0; k < l.Length; k++) points[k] = Point(l[k], v[k]);
            GraphBuilder g = new GraphBuilder().Output("s", Spline(Op("mul", ("a", "x"), ("b", 0.001)), false, points));
            using CompiledNoiseGraph graph = CompileOrFail(g.Graph);

            float2[] samples = new float2[2401];
            for (int i = 0; i < samples.Length; i++) samples[i] = new float2(-1200 + i, 0);
            float[] values = GraphEvaluator.SampleLattice(graph, Seed, samples, out _);
            for (int i = 1; i < samples.Length; i++)
                Assert.GreaterOrEqual(values[i], values[i - 1] - 1e-6f, $"not monotone at x {samples[i].x}");
            for (int k = 0; k < l.Length; k++){
                int i = (int)Math.Round(l[k] * 1000) + 1200;
                Assert.AreEqual(v[k], values[i], 1e-5, $"point {k}");
            }

            Assert.AreEqual(v[0], values[0], 1e-6, "below the first point");
            Assert.AreEqual(v[^1], values[^1], 1e-6, "above the last point");
        }

        #endregion

        #region 3D nodes

        [Test]
        public void Nodes3D_MatchTheirScalarDefinitions(){
            GraphBuilder g = new GraphBuilder()
                .Noise("volume", NoiseKind.Simplex3, 1, 60f, 30f)
                .Noise("layers", NoiseKind.Simplex3, 3, 80f, 40f, gain: 0.55f, amplitude: 0.8f)
                .Noise("veins", NoiseKind.Simplex3, 2, 50f, 25f)
                .Output("y_gradient", Op("y_gradient", ("fromY", 10.0), ("toY", 200.0), ("fromValue", -1.0), ("toValue", 2.0)))
                .Output("simplex3", Noise("simplex3", "volume"))
                .Output("fbm3", Noise("fbm", "layers"))
                .Output("ridged3", Noise("ridged", "veins"))
                .Output("shift3", Op("shift", ("in", Noise("simplex3", "volume")), ("y", Op("mul", ("a", "x"), ("b", 0.25)))));
            using CompiledNoiseGraph graph = CompileOrFail(g.Graph);
            int2 origin = new(-32, 16), size = new(8, 8);
            using GraphRegionResult region = GraphEvaluator.EvaluateRegion(graph, Seed, origin, size, true);

            List<int3> samples = new();
            for (int z = 0; z < size.y; z++)
            for (int x = 0; x < size.x; x++)
            for (int y = 0; y < GraphLimits.WorldHeight; y += 3)
                samples.Add(new int3(x, y, z));
            float3 World(int3 s){
                return new float3(origin.x + s.x, s.y, origin.y + s.z);
            }

            float3[] plain = samples.Select(World).ToArray();
            float3[] shifted = plain.Select(p => new float3(p.x, p.y + p.x * 0.25f, p.z)).ToArray();
            Dictionary<string, double[]> noises = new(){
                ["simplex3"] = NoiseReference(g.Graph.Noises["volume"], false, 1, plain),
                ["fbm3"] = NoiseReference(g.Graph.Noises["layers"], false, 3, plain),
                ["ridged3"] = NoiseReference(g.Graph.Noises["veins"], true, 2, plain),
                ["shift3"] = NoiseReference(g.Graph.Noises["volume"], false, 1, shifted)
            };

            int failures = 0;
            foreach (GraphOutputInfo output in graph.Outputs){
                Assert.IsTrue(output.Is3D, output.Name);
                for (int i = 0; i < samples.Count; i++){
                    int3 s = samples[i];
                    double expected = output.Name == "y_gradient"
                        ? -1 + Math.Clamp((s.y - 10) / 190.0, 0, 1) * 3
                        : noises[output.Name][i];
                    float actual = region.Voxel(output.VoxelOutput, s.x, s.y, s.z);
                    if (Close(actual, expected)) continue;
                    if (failures++ < 20) TestContext.WriteLine($"{output.Name} at {World(s)}: {actual} vs {expected}");
                }
            }

            Assert.AreEqual(0, failures, "values off their reference (first 20 printed)");
        }

        #endregion

        #region Lane simplex

        [BurstCompile(FloatMode = FloatMode.Strict, CompileSynchronously = true)]
        private struct LaneSimplexJob : IJob{
            [ReadOnly] public NativeArray<float3> Points;
            public NativeArray<float> Lane2, Scalar2, Lane3, Scalar3;
            public uint Seed, Salt;

            public void Execute(){
                for (int i = 0; i < Points.Length; i += 4){
                    float3 p0 = Points[i], p1 = Points[i + 1], p2 = Points[i + 2], p3 = Points[i + 3];
                    float4 x = new(p0.x, p1.x, p2.x, p3.x), y = new(p0.y, p1.y, p2.y, p3.y), z = new(p0.z, p1.z, p2.z, p3.z);
                    float4 l2 = SimplexLanes.Noise2(x, z, Seed, Salt);
                    float4 l3 = SimplexLanes.Noise3(x, y, z, Seed, Salt);
                    for (int k = 0; k < 4; k++){
                        Lane2[i + k] = l2[k];
                        Lane3[i + k] = l3[k];
                        Scalar2[i + k] = SeededSimplex.Noise(new float2(x[k], z[k]), Seed, Salt);
                        Scalar3[i + k] = SeededSimplex.Noise(new float3(x[k], y[k], z[k]), Seed, Salt);
                    }
                }
            }
        }

        [Test]
        public void LaneSimplex_EqualsScalarSimplexBitForBit(){
            List<float3> points = new();
            System.Random random = new(42);
            for (int i = 0; i < 4000; i++)
                points.Add(new float3((float)(random.NextDouble() * 2000 - 1000), (float)(random.NextDouble() * 300), (float)(random.NextDouble() * 2000 - 1000)));
            for (int i = 0; i < 400; i++){
                // Integer and simplex-edge points (x = z, skew boundaries), large and negative coordinates.
                points.Add(new float3(i - 200, i % 7, i - 200));
                points.Add(new float3(i * 0.25f, 0.5f * i, -i * 0.25f));
                points.Add(new float3(1e6f + i * 13.7f, i, -1e6f - i * 9.1f));
                points.Add(new float3(-16777216f + i * 4096f, 255f - i % 256, 16777216f - i * 4096f));
            }

            points.AddRange(new[]{ float3.zero, new float3(-0f, -0f, -0f), new float3(0.5f), new float3(-0.5f) });
            while (points.Count % 4 != 0) points.Add(float3.zero);

            using NativeArray<float3> p = new(points.ToArray(), Allocator.TempJob);
            using NativeArray<float> lane2 = new(points.Count, Allocator.TempJob), scalar2 = new(points.Count, Allocator.TempJob);
            using NativeArray<float> lane3 = new(points.Count, Allocator.TempJob), scalar3 = new(points.Count, Allocator.TempJob);
            new LaneSimplexJob{
                Points = p, Lane2 = lane2, Scalar2 = scalar2, Lane3 = lane3, Scalar3 = scalar3, Seed = Seed, Salt = HashSalts.TemperatureNoise
            }.Run();

            for (int i = 0; i < points.Count; i++){
                if (BitConverter.SingleToInt32Bits(lane2[i]) != BitConverter.SingleToInt32Bits(scalar2[i]))
                    Assert.Fail($"simplex2 at {points[i]}: lane {lane2[i]:R} vs scalar {scalar2[i]:R}");
                if (BitConverter.SingleToInt32Bits(lane3[i]) != BitConverter.SingleToInt32Bits(scalar3[i]))
                    Assert.Fail($"simplex3 at {points[i]}: lane {lane3[i]:R} vs scalar {scalar3[i]:R}");
            }
        }

        #endregion
    }
}
