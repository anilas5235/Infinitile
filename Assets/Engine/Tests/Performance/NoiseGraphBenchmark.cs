using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using Engine.Editor.WorldGen;
using Engine.Scripts.WorldGen.Core;
using Engine.Scripts.WorldGen.Graph;
using Engine.Scripts.WorldGen.Noise;
using NUnit.Framework;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.PerformanceTesting;
using Debug = UnityEngine.Debug;

namespace Engine.Tests.Performance{
    /// <summary>
    ///     The noise graph interpreter against hand-written Burst code, every pair timed in A, B, B, A order on one
    ///     worker:
    ///     <list type="bullet">
    ///         <item>ns per simplex sample: the lane kernel against the scalar SeededSimplex call;</item>
    ///         <item>
    ///             ms per 32 × 32 chunk of a terrain-plus-caves graph (lattice, column, two corner programs and the
    ///             voxel program with three markers): the interpreter against straight-line code for the same graph,
    ///             one statement per instruction on the same lanes and buffers;
    ///         </item>
    ///         <item>
    ///             the same on an arithmetic-heavy graph whose only noise sits under a marker, so the voxel program
    ///             is 37 arithmetic instructions and the difference is the interpreter's dispatch.
    ///         </item>
    ///     </list>
    ///     The chunk tests also time the generated code of each graph against the hand-written code. All sides give
    ///     the same bits; the tests check that before they time anything. Each timed pair logs one summary line
    ///     starting with "Noise graph benchmark".
    /// </summary>
    [TestFixture]
    internal unsafe class NoiseGraphBenchmark{
        private const int WarmupCount = 3;
        private const int MeasurementCount = 20;
        private const uint Seed = 1337u;
        private const int SimplexSamples = 1 << 18;
        private const int Width = 32;
        private const int LatticeWidth = Width / 4 + 1;
        private const int LatticePoints = LatticeWidth * LatticeWidth;
        private const int Stride = GraphInterpolation.CornerStride;
        private const int Height = GraphLimits.WorldHeight;

        private static readonly int2[] Origins = { new(-4096, 2048), new(320, -640), new(12800, 9600), new(-736, -32) };

        #region Measurement

        /// <summary>
        ///     Times <paramref name="a" /> and <paramref name="b" /> in four blocks (A, B, B, A) of warm-up plus
        ///     measured runs; <paramref name="scale" /> turns one run's milliseconds into the reported unit.
        /// </summary>
        private static (double a, double b) Abba(string name, string nameA, string nameB, Action a, Action b, double scale, SampleUnit unit,
            string unitLabel){
            SampleGroup groupA = new($"{name} {nameA}", unit), groupB = new($"{name} {nameB}", unit);
            Action[] order = { a, b, b, a };
            List<double>[] blocks = new List<double>[4];
            for (int k = 0; k < 4; k++){
                blocks[k] = new List<double>();
                for (int i = 0; i < WarmupCount + MeasurementCount; i++){
                    Stopwatch watch = Stopwatch.StartNew();
                    order[k]();
                    watch.Stop();
                    if (i < WarmupCount) continue;
                    double value = watch.Elapsed.TotalMilliseconds * scale;
                    blocks[k].Add(value);
                    Measure.Custom(k == 0 || k == 3 ? groupA : groupB, value);
                }
            }

            double medianA = Median(blocks[0].Concat(blocks[3])), medianB = Median(blocks[1].Concat(blocks[2]));
            Debug.Log($"Noise graph benchmark {name}: {nameA} {medianA:0.000} {unitLabel} (blocks {Median(blocks[0]):0.000} / " +
                      $"{Median(blocks[3]):0.000}), {nameB} {medianB:0.000} {unitLabel} (blocks {Median(blocks[1]):0.000} / " +
                      $"{Median(blocks[2]):0.000}); {nameA} is {100.0 * (medianA / medianB - 1.0):+0.0;-0.0} % against {nameB}");
            return (medianA, medianB);
        }

        private static double Median(IEnumerable<double> values){
            double[] sorted = values.OrderBy(v => v).ToArray();
            int n = sorted.Length;
            return n % 2 == 1 ? sorted[n / 2] : 0.5 * (sorted[n / 2 - 1] + sorted[n / 2]);
        }

        /// <summary>Bit-equal, except that +0 and −0 count as equal.</summary>
        private static bool SameValue(float a, float b){
            return a == 0f && b == 0f || BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);
        }

        private static void AssertSame(string what, NativeArray<float> interpreter, int interpreterStart, NativeArray<float> hand, int handStart,
            int count){
            for (int i = 0; i < count; i++){
                float x = interpreter[interpreterStart + i], y = hand[handStart + i];
                if (!SameValue(x, y)) Assert.Fail($"{what} differs at {i}: interpreter {x:R}, hand-written {y:R}");
            }
        }

        /// <summary>Every buffer of two evaluations of one region, bit for bit.</summary>
        private static void AssertSameRegion(GraphRegionResult interpreted, GraphRegionResult generated){
            (string, NativeArray<float>, NativeArray<float>)[] buffers ={
                ("lattice", interpreted.Lattice, generated.Lattice), ("columns", interpreted.Columns, generated.Columns),
                ("corners", interpreted.Corners, generated.Corners), ("voxels", interpreted.Voxels, generated.Voxels)
            };
            foreach ((string what, NativeArray<float> x, NativeArray<float> y) in buffers)
                for (int i = 0; i < x.Length; i++)
                    if (BitConverter.SingleToInt32Bits(x[i]) != BitConverter.SingleToInt32Bits(y[i]))
                        Assert.Fail($"{what} differs at {i}: interpreter {x[i]:R}, generated {y[i]:R}");
        }

        /// <summary>A region job for <paramref name="graph" /> with its own buffers, run by the generated code.</summary>
        private static GraphRegionResult Generated<TPrograms>(CompiledNoiseGraph graph, out GeneratedRegionJob<TPrograms> job)
            where TPrograms : struct, IGraphPrograms{
            Assert.AreEqual(graph.CodeHash, default(TPrograms).CodeHash, $"{typeof(TPrograms).Name} is out of date; see GeneratedCode_IsUpToDate.");
            GraphRegionResult region = GraphEvaluator.Allocate(graph, Origins[0], new int2(Width, Width), true, false, out GraphRegionJob regionJob,
                Allocator.Persistent);
            regionJob.Seed = Seed;
            job = new GeneratedRegionJob<TPrograms>{ Region = regionJob };
            return region;
        }

        #endregion

        #region Graphs

        private static Dictionary<string, object> Op(string op, params (string key, object value)[] fields){
            Dictionary<string, object> o = new(){ ["op"] = op };
            foreach ((string key, object value) in fields) o[key] = value is int i ? (double)i : value;
            return o;
        }

        private static Dictionary<string, object> Noise(string op, string noise){
            return Op(op, ("noise", noise));
        }

        private static Dictionary<string, object> Point(double location, object value, double? derivative = null){
            Dictionary<string, object> p = new(){ ["location"] = location, ["value"] = value };
            if (derivative.HasValue) p["derivative"] = derivative.Value;
            return p;
        }

        private static Dictionary<string, object> Spline(object coordinate, bool linear, params Dictionary<string, object>[] points){
            Dictionary<string, object> o = Op("spline", ("coordinate", coordinate), ("points", points.Cast<object>().ToList()));
            if (linear) o["interpolation"] = "linear";
            return o;
        }

        private static void AddNoise(NoiseGraph g, string name, NoiseKind kind, int octaves, float wavelength, float wavelengthY = 0f){
            g.Noises[name] = new NoiseDefinition{
                Name = name, Kind = kind, Octaves = octaves, Wavelength = wavelength,
                WavelengthY = wavelengthY > 0f ? wavelengthY : wavelength, Lacunarity = 2f, Gain = 0.5f, Amplitude = 1f
            };
        }

        private static void AddNode(NoiseGraph g, string name, Dictionary<string, object> fields){
            g.Nodes[name] = new GraphNodeDefinition{ Name = name, Fields = fields };
        }

        /// <summary>
        ///     Terrain plus caves: a warped continentalness and erosion, a cubic offset spline with a nested erosion
        ///     spline, a linear factor spline and the surface height in 2D; a terrain density under an 8-block marker
        ///     and cheese and tunnel noises under 4-block markers; the surface per column; and a terrain and a cave
        ///     density per voxel.
        /// </summary>
        private static NoiseGraph TerrainCaves(){
            NoiseGraph g = new();
            AddNoise(g, "continents", NoiseKind.Simplex2, 4, 900f);
            AddNoise(g, "warp", NoiseKind.Simplex2, 2, 400f);
            AddNoise(g, "erosion", NoiseKind.Simplex2, 3, 500f);
            AddNoise(g, "terrain", NoiseKind.Simplex3, 3, 120f, 60f);
            AddNoise(g, "cheese", NoiseKind.Simplex3, 2, 70f, 35f);
            AddNoise(g, "tunnel", NoiseKind.Simplex3, 1, 50f, 25f);
            AddNode(g, "c", Op("shift", ("in", Noise("fbm", "continents")), ("x", Op("mul", ("a", Noise("fbm", "warp")), ("b", 120.0)))));
            AddNode(g, "e", Noise("fbm", "erosion"));
            AddNode(g, "offset", Spline("c", false, Point(-1, -0.6, 0.2), Point(-0.2, -0.1, 0.8),
                Point(0.3, Spline("e", false, Point(-1, 0.6, -0.1), Point(0.2, 0.25), Point(1, 0.05)), 0.4), Point(1, 0.9, 0)));
            AddNode(g, "factor", Spline("e", true, Point(-1, 6.0), Point(0, 3.0), Point(1, 1.5)));
            AddNode(g, "surface", Op("add", ("a", 64.0), ("b", Op("mul", ("a", "offset"), ("b", 96.0)))));
            AddNode(g, "terrain_in", Op("add", ("a", Op("mul", ("a", "factor"), ("b", Op("mul", ("a", Op("sub", ("a", "surface"), ("b", "y"))),
                ("b", 1.0 / 128.0))))), ("b", Op("mul", ("a", Noise("fbm", "terrain")), ("b", 0.5)))));
            AddNode(g, "terrain_marker", Op("interpolated", ("in", "terrain_in"), ("cellHeight", 8.0)));
            AddNode(g, "cheese_in", Op("add", ("a", Noise("fbm", "cheese")),
                ("b", Op("y_gradient", ("fromY", 0.0), ("toY", 64.0), ("fromValue", -0.3), ("toValue", 0.2)))));
            AddNode(g, "cheese_marker", Op("interpolated", ("in", "cheese_in"), ("cellHeight", 4.0)));
            AddNode(g, "tunnel_in", Op("sub", ("a", Op("abs", ("in", Noise("simplex3", "tunnel")))), ("b", 0.08)));
            AddNode(g, "tunnel_marker", Op("interpolated", ("in", "tunnel_in"), ("cellHeight", 4.0)));
            g.Outputs["continentalness"] = "c";
            g.Outputs["surface"] = new Dictionary<string, object>{ ["node"] = "surface", ["perColumn"] = true };
            g.Outputs["terrain_density"] = Op("soft_clamp", ("in", "terrain_marker"));
            g.Outputs["cave_density"] = Op("min", ("a", "cheese_marker"),
                ("b", Op("max", ("a", "tunnel_marker"), ("b", Op("mul", ("a", Op("sub", ("a", "y"), ("b", "surface"))), ("b", -0.05))))));
            return g;
        }

        /// <summary>One simplex3 noise under a 4-block marker and a chain of 37 arithmetic nodes per voxel.</summary>
        private static NoiseGraph ArithmeticChain(){
            NoiseGraph g = new();
            AddNoise(g, "detail", NoiseKind.Simplex3, 1, 40f, 20f);
            AddNode(g, "n", Op("interpolated", ("in", Noise("simplex3", "detail")), ("cellHeight", 4.0)));
            AddNode(g, "v1", Op("mul", ("a", Op("add", ("a", "x"), ("b", "y"))), ("b", 0.01)));
            AddNode(g, "v2", Op("mul", ("a", Op("sub", ("a", "z"), ("b", "y"))), ("b", 0.013)));
            AddNode(g, "v3", Op("mul", ("a", "y"), ("b", 0.02)));
            AddNode(g, "t1", Op("add", ("a", "n"), ("b", "v1")));
            AddNode(g, "t2", Op("mul", ("a", "t1"), ("b", "v2")));
            AddNode(g, "t3", Op("max", ("a", "t2"), ("b", "v3")));
            AddNode(g, "t4", Op("abs", ("in", "t3")));
            AddNode(g, "t5", Op("sub", ("a", "t4"), ("b", 0.3)));
            AddNode(g, "t6", Op("min", ("a", "t5"), ("b", "v1")));
            AddNode(g, "t7", Op("square", ("in", "t6")));
            AddNode(g, "t8", Op("soft_clamp", ("in", "t7")));
            AddNode(g, "t9", Op("lerp", ("t", "t8"), ("a", "v2"), ("b", "v3")));
            AddNode(g, "t10", Op("clamp", ("in", "t9"), ("min", -0.5), ("max", 0.5)));
            AddNode(g, "t11", Op("add", ("a", "t10"), ("b", "n")));
            AddNode(g, "t12", Op("half_negative", ("in", "t11")));
            AddNode(g, "t13", Op("mul", ("a", "t12"), ("b", 1.7)));
            AddNode(g, "t14", Op("smoothstep", ("in", "t13"), ("from", -1.0), ("to", 1.0)));
            AddNode(g, "t15", Op("sub", ("a", "t14"), ("b", "v3")));
            AddNode(g, "t16", Op("cube", ("in", "t15")));
            AddNode(g, "t17", Op("max", ("a", "t16"), ("b", "t1")));
            AddNode(g, "t18", Op("mul", ("a", "t17"), ("b", "t2")));
            AddNode(g, "t19", Op("quarter_negative", ("in", "t18")));
            AddNode(g, "t20", Op("add", ("a", "t19"), ("b", "t5")));
            AddNode(g, "t21", Op("min", ("a", "t20"), ("b", 0.8)));
            AddNode(g, "t22", Op("neg", ("in", "t21")));
            AddNode(g, "t23", Op("mul", ("a", "t22"), ("b", "n")));
            AddNode(g, "t24", Op("add", ("a", "t23"), ("b", "t13")));
            AddNode(g, "t25", Op("abs", ("in", "t24")));
            AddNode(g, "t26", Op("sub", ("a", "t25"), ("b", "t9")));
            AddNode(g, "t27", Op("range_choice", ("in", "t26"), ("min", -0.2), ("max", 0.4), ("inRange", "t20"), ("outRange", "t17")));
            AddNode(g, "t28", Op("mul", ("a", "t27"), ("b", 0.5)));
            AddNode(g, "t29", Op("add", ("a", "t28"), ("b", "t14")));
            AddNode(g, "t30", Op("max", ("a", "t29"), ("b", "t22")));
            AddNode(g, "t31", Op("soft_clamp", ("in", "t30")));
            g.Outputs["value"] = Op("add", ("a", "t31"), ("b", "t16"));
            return g;
        }

        [Test]
        public void GeneratedCode_IsUpToDate(){
            const string path = "Assets/Engine/Tests/Performance/NoiseGraphBenchmarkCode.cs";
            using CompiledNoiseGraph caves = CompileOrFail(TerrainCaves()), chain = CompileOrFail(ArithmeticChain());
            string expected = NoiseGraphCodeGenerator.Generate("Engine.Tests.Performance", "the noise graph benchmark", ("TerrainCavesBenchmarkCode", caves),
                ("ArithmeticChainBenchmarkCode", chain));
            if (!NoiseGraphCodeGenerator.IsUpToDate(path, expected, out string fresh))
                Assert.Fail($"{path} is out of date: replace it with the generated text in {fresh}.");
        }

        private static CompiledNoiseGraph CompileOrFail(NoiseGraph graph){
            GraphCompileResult r = GraphCompiler.Compile(graph, "benchmark", Array.Empty<string>());
            if (!r.Succeeded) Assert.Fail("Compile failed:\n" + string.Join("\n", r.Messages));
            return r.Graph;
        }

        #endregion

        #region Hand-written tables

        private struct NoiseSpec{
            public int Start, Count;
            public float Wavelength, WavelengthY, Lacunarity, Gain, Amplitude;
            public uint Salt;
        }

        /// <summary>
        ///     The octave table, spline spans and y-gradient constants of the hand-written code, computed the way the
        ///     compiler prepares them (in Burst, so the floats match).
        /// </summary>
        [BurstCompile(FloatMode = FloatMode.Strict, CompileSynchronously = true)]
        private struct TablesJob : IJob{
            [ReadOnly] public NativeArray<NoiseSpec> Noises;
            public NativeArray<NoiseOctave> Octaves;

            /// <summary>(first point, point count) of each spline in <see cref="Points" />.</summary>
            [ReadOnly] public NativeArray<int2> Splines;

            public NativeArray<SplinePoint> Points;

            /// <summary>In: fromY, toY, fromValue, toValue. Out: [4] inverse span, [5] value span.</summary>
            public NativeArray<float> Gradient;

            public void Execute(){
                for (int n = 0; n < Noises.Length; n++){
                    NoiseSpec s = Noises[n];
                    float sum = 0f, a = 1f;
                    for (int o = 0; o < s.Count; o++){
                        sum += a;
                        a *= s.Gain;
                    }

                    float scale = s.Amplitude / sum, f = 1f / s.Wavelength, fy = 1f / s.WavelengthY;
                    a = 1f;
                    for (int o = 0; o < s.Count; o++){
                        Octaves[s.Start + o] = new NoiseOctave{ FrequencyXZ = f, FrequencyY = fy, Amplitude = a * scale, Salt = HashSalts.FbmOctave(s.Salt, o) };
                        f *= s.Lacunarity;
                        fy *= s.Lacunarity;
                        a *= s.Gain;
                    }
                }

                for (int i = 0; i < Splines.Length; i++){
                    int2 range = Splines[i];
                    for (int k = range.x; k < range.x + range.y; k++){
                        SplinePoint p = Points[k];
                        if (k < range.x + range.y - 1){
                            p.Span = Points[k + 1].Location - p.Location;
                            p.InverseSpan = 1f / p.Span;
                        }
                        else{
                            p.Span = 0f;
                            p.InverseSpan = 0f;
                        }

                        Points[k] = p;
                    }
                }

                if (Gradient.Length >= 6){
                    Gradient[4] = 1f / (Gradient[1] - Gradient[0]);
                    Gradient[5] = Gradient[3] - Gradient[2];
                }
            }
        }

        private static NativeArray<NoiseOctave> OctaveTable(NoiseGraph graph, string[] noises){
            NativeArray<NoiseSpec> specs = new(noises.Length, Allocator.TempJob);
            int start = 0;
            for (int i = 0; i < noises.Length; i++){
                NoiseDefinition d = graph.Noises[noises[i]];
                specs[i] = new NoiseSpec{
                    Start = start, Count = d.Octaves, Wavelength = d.Wavelength, WavelengthY = d.WavelengthY, Lacunarity = d.Lacunarity,
                    Gain = d.Gain, Amplitude = d.Amplitude, Salt = GraphResolver.NoiseSalt(d.Name)
                };
                start += d.Octaves;
            }

            NativeArray<NoiseOctave> octaves = new(start, Allocator.Persistent);
            using NativeArray<int2> splines = new(0, Allocator.TempJob);
            using NativeArray<SplinePoint> points = new(0, Allocator.TempJob);
            using NativeArray<float> gradient = new(0, Allocator.TempJob);
            new TablesJob{ Noises = specs, Octaves = octaves, Splines = splines, Points = points, Gradient = gradient }.Run();
            specs.Dispose();
            return octaves;
        }

        private static SplinePoint P(double location, double value, double derivative = 0){
            return new SplinePoint{ Location = (float)location, Constant = (float)value, Derivative = (float)derivative, Value = -1 };
        }

        #endregion

        #region Hand-written code

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float4 Fbm2(float4 x, float4 z, NoiseOctave* o, int count, uint seed){
            float4 acc = float4.zero;
            for (int k = 0; k < count; k++)
                acc = acc + SimplexLanes.Noise2(x * o[k].FrequencyXZ, z * o[k].FrequencyXZ, seed, o[k].Salt) * o[k].Amplitude;
            return acc;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float4 Fbm3(float4 x, float4 y, float4 z, NoiseOctave* o, int count, uint seed){
            float4 acc = float4.zero;
            for (int k = 0; k < count; k++)
                acc = acc + SimplexLanes.Noise3(x * o[k].FrequencyXZ, y * o[k].FrequencyY, z * o[k].FrequencyXZ, seed, o[k].Salt) * o[k].Amplitude;
            return acc;
        }

        /// <summary>
        ///     Straight-line code for <see cref="TerrainCaves" /> on a 32 × 32 chunk, with the interpreter's lanes and
        ///     buffer layouts: lattice rows of 9, column rows of 32, corner columns of 33 and 65 lanes and 4 × 8 × 4
        ///     voxel cells. Octaves: continents 0–3, warp 4–5, erosion 6–8, terrain 9–11, cheese 12–13, tunnel 14.
        ///     Spline points: offset 0–3, its erosion spline 4–6, factor 7–9.
        /// </summary>
        [BurstCompile(FloatMode = FloatMode.Strict, CompileSynchronously = true)]
        private struct TerrainCavesHandJob : IJob{
            public uint Seed;
            public int2 Origin;
            [ReadOnly] public NativeArray<NoiseOctave> Octaves;
            [ReadOnly] public NativeArray<SplinePoint> Points;

            /// <summary>The y gradient's inverse span and value span.</summary>
            public float GradientInverseSpan, GradientValueSpan;

            /// <summary>[continentalness, factor, surface][lz][lx].</summary>
            public NativeArray<float> Lattice;

            /// <summary>Surface [z][x].</summary>
            public NativeArray<float> Columns;

            /// <summary>[terrain, cheese, tunnel][lz][lx][cy].</summary>
            public NativeArray<float> Corners;

            /// <summary>[terrain density, cave density][z][x][y].</summary>
            public NativeArray<float> Voxels;

            public void Execute(){
                NoiseOctave* o = (NoiseOctave*)Octaves.GetUnsafeReadOnlyPtr();
                SplinePoint* sp = (SplinePoint*)Points.GetUnsafeReadOnlyPtr();
                float* lattice = (float*)Lattice.GetUnsafePtr();
                float* columns = (float*)Columns.GetUnsafePtr();
                float* corners = (float*)Corners.GetUnsafePtr();
                float* voxels = (float*)Voxels.GetUnsafePtr();
                float4* values = stackalloc float4[4];
                int4 step = new(0, 1, 2, 3);

                // Lattice: continentalness, factor and surface.
                for (int lz = 0; lz < LatticeWidth; lz++)
                for (int x0 = 0; x0 < LatticeWidth; x0 += 4){
                    float4 x = Origin.x + 4 * math.min(x0 + step, LatticeWidth - 1);
                    float4 z = Origin.y + 4 * lz;
                    float4 c = Continentalness(x, z, o);
                    float4 e = Fbm2(x, z, o + 6, 3, Seed);
                    float4 surface = Surface(c, e, sp, values);
                    values[0] = sp[7].Constant;
                    values[1] = sp[8].Constant;
                    values[2] = sp[9].Constant;
                    float4 factor = GraphKernels.Spline(e, sp + 7, values, 3, true);
                    for (int k = 0; k < 4 && x0 + k < LatticeWidth; k++){
                        int at = lz * LatticeWidth + x0 + k;
                        lattice[at] = c[k];
                        lattice[LatticePoints + at] = factor[k];
                        lattice[2 * LatticePoints + at] = surface[k];
                    }
                }

                // Columns: the surface.
                for (int z = 0; z < Width; z++)
                for (int x0 = 0; x0 < Width; x0 += 4){
                    float4 x = Origin.x + x0 + step;
                    float4 zz = Origin.y + z;
                    float4 c = Continentalness(x, zz, o);
                    float4 e = Fbm2(x, zz, o + 6, 3, Seed);
                    *(float4*)(columns + z * Width + x0) = Surface(c, e, sp, values);
                }

                // Corners: terrain on 8-block cells, cheese and tunnel on 4-block cells.
                for (int lz = 0; lz < LatticeWidth; lz++)
                for (int lx = 0; lx < LatticeWidth; lx++){
                    int at = lz * LatticeWidth + lx;
                    float factor = lattice[LatticePoints + at], surface = lattice[2 * LatticePoints + at];
                    float4 x = Origin.x + 4 * lx, z = Origin.y + 4 * lz;
                    float* terrain = corners + at * Stride;
                    for (int q = 0; q < 9; q++){
                        float4 y = math.min(q * 4 + step, Height / 8) * 8;
                        float4 t = factor * ((surface - y) * 0.0078125f) + Fbm3(x, y, z, o + 9, 3, Seed) * 0.5f;
                        for (int k = 0; k < 4 && q * 4 + k <= Height / 8; k++) terrain[q * 4 + k] = t[k];
                    }

                    float* cheese = corners + (LatticePoints + at) * Stride;
                    float* tunnel = corners + (2 * LatticePoints + at) * Stride;
                    for (int q = 0; q < 17; q++){
                        float4 y = math.min(q * 4 + step, Height / 4) * 4;
                        float4 ch = Fbm3(x, y, z, o + 12, 2, Seed) + GraphKernels.ClampedMap(y, 0f, GradientInverseSpan, -0.3f, GradientValueSpan);
                        float4 tu = math.abs(Fbm3(x, y, z, o + 14, 1, Seed)) - 0.08f;
                        for (int k = 0; k < 4 && q * 4 + k <= Height / 4; k++){
                            cheese[q * 4 + k] = ch[k];
                            tunnel[q * 4 + k] = tu[k];
                        }
                    }
                }

                // Voxels, fused: each row of four voxels is interpolated (the same operations as
                // GraphInterpolation.Trilinear) and finished in registers, without staging the markers.
                float* surfaceColumns = stackalloc float[16];
                float4 tx = new(0f, 0.25f, 0.5f, 0.75f);
                for (int cz = 0; cz < Width / 4; cz++)
                for (int cx = 0; cx < Width / 4; cx++){
                    int at = cz * LatticeWidth + cx;
                    float* s = lattice + 2 * LatticePoints + at;
                    GraphInterpolation.Bilinear(s[0], s[1], s[LatticeWidth], s[LatticeWidth + 1], surfaceColumns);
                    float* t0 = corners + at * Stride;
                    float* c0 = corners + (LatticePoints + at) * Stride;
                    float* n0 = corners + (2 * LatticePoints + at) * Stride;
                    const int row = LatticeWidth * Stride;
                    for (int y = 0; y < Height; y++){
                        int t8 = y >> 3, t4 = y >> 2;
                        float ty8 = (y - t8 * 8) * 0.125f, ty4 = (y - t4 * 4) * 0.25f;
                        float4 ta = Lerp(t0[t8], t0[t8 + Stride], tx), tb = Lerp(t0[t8 + row], t0[t8 + row + Stride], tx);
                        float4 tc = Lerp(t0[t8 + 1], t0[t8 + 1 + Stride], tx), td = Lerp(t0[t8 + 1 + row], t0[t8 + 1 + row + Stride], tx);
                        float4 ca = Lerp(c0[t4], c0[t4 + Stride], tx), cb = Lerp(c0[t4 + row], c0[t4 + row + Stride], tx);
                        float4 cc = Lerp(c0[t4 + 1], c0[t4 + 1 + Stride], tx), cd = Lerp(c0[t4 + 1 + row], c0[t4 + 1 + row + Stride], tx);
                        float4 na = Lerp(n0[t4], n0[t4 + Stride], tx), nb = Lerp(n0[t4 + row], n0[t4 + row + Stride], tx);
                        float4 nc = Lerp(n0[t4 + 1], n0[t4 + 1 + Stride], tx), nd = Lerp(n0[t4 + 1 + row], n0[t4 + 1 + row + Stride], tx);
                        float fy = y;
                        for (int lz = 0; lz < 4; lz++){
                            float tz = lz * 0.25f;
                            float4 tm = Lerp(Lerp(ta, tb, tz), Lerp(tc, td, tz), ty8);
                            float4 cm = Lerp(Lerp(ca, cb, tz), Lerp(cc, cd, tz), ty4);
                            float4 tn = Lerp(Lerp(na, nb, tz), Lerp(nc, nd, tz), ty4);
                            float4 surface = *(float4*)(surfaceColumns + lz * 4);
                            float4 terrain = GraphKernels.SoftClamp(tm);
                            float4 cave = math.min(cm, math.max(tn, (fy - surface) * -0.05f));
                            int z = cz * 4 + lz;
                            for (int k = 0; k < 4; k++){
                                int index = (z * Width + cx * 4 + k) * Height + y;
                                voxels[index] = terrain[k];
                                voxels[Width * Width * Height + index] = cave[k];
                            }
                        }
                    }
                }
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private static float4 Lerp(float4 a, float4 b, float4 t){
                return GraphInterpolation.Lerp(a, b, t);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private float4 Continentalness(float4 x, float4 z, NoiseOctave* o){
                float4 shift = Fbm2(x, z, o + 4, 2, Seed) * 120f;
                return Fbm2(x + shift, z, o, 4, Seed);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private static float4 Surface(float4 c, float4 e, SplinePoint* sp, float4* values){
                values[0] = sp[4].Constant;
                values[1] = sp[5].Constant;
                values[2] = sp[6].Constant;
                float4 inner = GraphKernels.Spline(e, sp + 4, values, 3, false);
                values[0] = sp[0].Constant;
                values[1] = sp[1].Constant;
                values[2] = inner;
                values[3] = sp[3].Constant;
                float4 offset = GraphKernels.Spline(c, sp, values, 4, false);
                return 64f + offset * 96f;
            }
        }

        /// <summary>Straight-line code for <see cref="ArithmeticChain" /> on a 32 × 32 chunk.</summary>
        [BurstCompile(FloatMode = FloatMode.Strict, CompileSynchronously = true)]
        private struct ArithmeticHandJob : IJob{
            public uint Seed;
            public int2 Origin;
            [ReadOnly] public NativeArray<NoiseOctave> Octaves;

            /// <summary>[lz][lx][cy].</summary>
            public NativeArray<float> Corners;

            /// <summary>[z][x][y].</summary>
            public NativeArray<float> Voxels;

            public void Execute(){
                NoiseOctave* o = (NoiseOctave*)Octaves.GetUnsafeReadOnlyPtr();
                float* corners = (float*)Corners.GetUnsafePtr();
                float* voxels = (float*)Voxels.GetUnsafePtr();
                int4 step = new(0, 1, 2, 3);

                for (int lz = 0; lz < LatticeWidth; lz++)
                for (int lx = 0; lx < LatticeWidth; lx++){
                    float4 x = Origin.x + 4 * lx, z = Origin.y + 4 * lz;
                    float* column = corners + (lz * LatticeWidth + lx) * Stride;
                    for (int q = 0; q < 17; q++){
                        float4 y = math.min(q * 4 + step, Height / 4) * 4;
                        float4 n = Fbm3(x, y, z, o, 1, Seed);
                        for (int k = 0; k < 4 && q * 4 + k <= Height / 4; k++) column[q * 4 + k] = n[k];
                    }
                }

                float* m = stackalloc float[GraphLimits.MaxLanes];
                const int row = LatticeWidth * Stride;
                for (int cz = 0; cz < Width / 4; cz++)
                for (int cx = 0; cx < Width / 4; cx++){
                    float* c0 = corners + (cz * LatticeWidth + cx) * Stride;
                    float4 x = Origin.x + cx * 4 + step;
                    for (int cy = 0; cy < Height / GraphLimits.CellBlockHeight; cy++){
                        int baseY = cy * GraphLimits.CellBlockHeight;
                        GraphInterpolation.Trilinear(c0, c0 + Stride, c0 + row, c0 + row + Stride, 4, baseY, m);
                        for (int q = 0; q < GraphLimits.MaxLanes / 4; q++){
                            float4 n = ((float4*)m)[q];
                            float4 y = baseY + (q >> 2);
                            float4 z = Origin.y + cz * 4 + (q & 3);
                            float4 v1 = (x + y) * 0.01f;
                            float4 v2 = (z - y) * 0.013f;
                            float4 v3 = y * 0.02f;
                            float4 t1 = n + v1;
                            float4 t2 = t1 * v2;
                            float4 t3 = math.max(t2, v3);
                            float4 t4 = math.abs(t3);
                            float4 t5 = t4 - 0.3f;
                            float4 t6 = math.min(t5, v1);
                            float4 t7 = t6 * t6;
                            float4 t8 = GraphKernels.SoftClamp(t7);
                            float4 t9 = GraphKernels.Lerp(t8, v2, v3);
                            float4 t10 = math.clamp(t9, -0.5f, 0.5f);
                            float4 t11 = t10 + n;
                            float4 t12 = GraphKernels.HalfNegative(t11);
                            float4 t13 = t12 * 1.7f;
                            float4 t14 = GraphKernels.Smoothstep(t13, -1f, 0.5f);
                            float4 t15 = t14 - v3;
                            float4 t16 = GraphKernels.Cube(t15);
                            float4 t17 = math.max(t16, t1);
                            float4 t18 = t17 * t2;
                            float4 t19 = GraphKernels.QuarterNegative(t18);
                            float4 t20 = t19 + t5;
                            float4 t21 = math.min(t20, 0.8f);
                            float4 t22 = -t21;
                            float4 t23 = t22 * n;
                            float4 t24 = t23 + t13;
                            float4 t25 = math.abs(t24);
                            float4 t26 = t25 - t9;
                            float4 t27 = GraphKernels.RangeChoice(t26, t20, t17, -0.2f, 0.4f);
                            float4 t28 = t27 * 0.5f;
                            float4 t29 = t28 + t14;
                            float4 t30 = math.max(t29, t22);
                            float4 t31 = GraphKernels.SoftClamp(t30);
                            float4 value = t31 + t16;
                            int zz = cz * 4 + (q & 3), yy = baseY + (q >> 2);
                            for (int k = 0; k < 4; k++) voxels[(zz * Width + cx * 4 + k) * Height + yy] = value[k];
                        }
                    }
                }
            }
        }

        #endregion

        #region Simplex

        [BurstCompile(FloatMode = FloatMode.Strict, CompileSynchronously = true)]
        private struct SimplexJob : IJob{
            [ReadOnly] public NativeArray<float> X, Y, Z;
            public NativeArray<float> Result;
            public int Dimensions;

            /// <summary>1: the lane kernel; 0: the scalar call per sample.</summary>
            public int Lanes;

            public uint Salt;

            public void Execute(){
                float* x = (float*)X.GetUnsafeReadOnlyPtr(), y = (float*)Y.GetUnsafeReadOnlyPtr(), z = (float*)Z.GetUnsafeReadOnlyPtr();
                float* r = (float*)Result.GetUnsafePtr();
                int n = Result.Length;
                if (Lanes != 0){
                    if (Dimensions == 2) SimplexLanes.Noise2(x, z, r, n, Seed, Salt);
                    else SimplexLanes.Noise3(x, y, z, r, n, Seed, Salt);
                }
                else if (Dimensions == 2){
                    for (int i = 0; i < n; i++) r[i] = SeededSimplex.Noise(new float2(x[i], z[i]), Seed, Salt);
                }
                else{
                    for (int i = 0; i < n; i++) r[i] = SeededSimplex.Noise(new float3(x[i], y[i], z[i]), Seed, Salt);
                }
            }
        }

        [Test]
        [Performance]
        public void Simplex_LaneKernelAgainstScalar_NsPerSample([Values(2, 3)] int dimensions){
            float[] px = new float[SimplexSamples], py = new float[SimplexSamples], pz = new float[SimplexSamples];
            System.Random random = new(7);
            for (int i = 0; i < SimplexSamples; i++){
                px[i] = (float)(random.NextDouble() * 2000.0 - 1000.0);
                py[i] = (float)(random.NextDouble() * 20.0);
                pz[i] = (float)(random.NextDouble() * 2000.0 - 1000.0);
            }

            using NativeArray<float> x = new(px, Allocator.Persistent), y = new(py, Allocator.Persistent), z = new(pz, Allocator.Persistent),
                scalar = new(SimplexSamples, Allocator.Persistent), lanes = new(SimplexSamples, Allocator.Persistent);

            const uint salt = 0x9E3779B9u;
            SimplexJob scalarJob = new(){ X = x, Y = y, Z = z, Result = scalar, Dimensions = dimensions, Lanes = 0, Salt = salt };
            SimplexJob laneJob = scalarJob;
            laneJob.Lanes = 1;
            laneJob.Result = lanes;
            scalarJob.Run();
            laneJob.Run();
            for (int i = 0; i < SimplexSamples; i++)
                if (BitConverter.SingleToInt32Bits(scalar[i]) != BitConverter.SingleToInt32Bits(lanes[i]))
                    Assert.Fail($"sample {i}: scalar {scalar[i]:R}, lanes {lanes[i]:R}");

            Abba($"(a) simplex{dimensions}", "scalar", "lanes", () => scalarJob.Run(), () => laneJob.Run(), 1e6 / SimplexSamples,
                SampleUnit.Nanosecond, "ns/sample");
        }

        #endregion

        #region Chunks

        [Test]
        [Performance]
        public void TerrainCaves_InterpreterAgainstHandWritten_MsPerChunk(){
            NoiseGraph definition = TerrainCaves();
            using CompiledNoiseGraph graph = CompileOrFail(definition);
            Assert.AreEqual(3, graph.Tables.MarkerCount);
            using GraphRegionResult region = GraphEvaluator.Allocate(graph, Origins[0], new int2(Width, Width), true, false, out GraphRegionJob interpreter,
                Allocator.Persistent);
            interpreter.Seed = Seed;

            using NativeArray<NoiseOctave> octaves = OctaveTable(definition, new[]{ "continents", "warp", "erosion", "terrain", "cheese", "tunnel" });
            using NativeArray<SplinePoint> points = new(new[]{
                P(-1, -0.6, 0.2), P(-0.2, -0.1, 0.8), P(0.3, 0, 0.4), P(1, 0.9),
                P(-1, 0.6, -0.1), P(0.2, 0.25), P(1, 0.05),
                P(-1, 6), P(0, 3), P(1, 1.5)
            }, Allocator.Persistent);
            using NativeArray<int2> splines = new(new[]{ new int2(0, 4), new int2(4, 3), new int2(7, 3) }, Allocator.TempJob);
            using NativeArray<float> gradient = new(new[]{ 0f, 64f, (float)-0.3, (float)0.2, 0f, 0f }, Allocator.TempJob);
            using NativeArray<NoiseSpec> noSpecs = new(0, Allocator.TempJob);
            using NativeArray<NoiseOctave> noOctaves = new(0, Allocator.TempJob);
            new TablesJob{ Noises = noSpecs, Octaves = noOctaves, Splines = splines, Points = points, Gradient = gradient }.Run();

            using NativeArray<float> lattice = new(3 * LatticePoints, Allocator.Persistent), columns = new(Width * Width, Allocator.Persistent),
                corners = new(3 * LatticePoints * Stride, Allocator.Persistent), voxels = new(2 * Width * Width * Height, Allocator.Persistent);
            TerrainCavesHandJob hand = new(){
                Seed = Seed, Origin = Origins[0], Octaves = octaves, Points = points, GradientInverseSpan = gradient[4], GradientValueSpan = gradient[5],
                Lattice = lattice, Columns = columns, Corners = corners, Voxels = voxels
            };

            // Same bits first.
            interpreter.Run();
            hand.Run();
            GraphOutputInfo continentalness = graph.Output("continentalness"), surface = graph.Output("surface");
            GraphOutputInfo terrain = graph.Output("terrain_density"), cave = graph.Output("cave_density");
            AssertSame("continentalness", region.Lattice, continentalness.LatticeValue * LatticePoints, lattice, 0, LatticePoints);
            AssertSame("surface", region.Columns, surface.ColumnOutput * Width * Width, columns, 0, Width * Width);
            AssertSame("terrain density", region.Voxels, terrain.VoxelOutput * Width * Width * Height, voxels, 0, Width * Width * Height);
            AssertSame("cave density", region.Voxels, cave.VoxelOutput * Width * Width * Height, voxels, Width * Width * Height, Width * Width * Height);
            GraphWorkCounters counters = region.Counters;
            using GraphRegionResult generatedRegion = Generated(graph, out GeneratedRegionJob<TerrainCavesBenchmarkCode> generated);
            generated.Run();
            AssertSameRegion(region, generatedRegion);

            Abba("(b) terrain plus caves", "interpreter", "hand-written", () => {
                foreach (int2 origin in Origins){
                    interpreter.Origin = origin;
                    interpreter.Run();
                }
            }, () => {
                foreach (int2 origin in Origins){
                    hand.Origin = origin;
                    hand.Run();
                }
            }, 1.0 / Origins.Length, SampleUnit.Millisecond, "ms/chunk");
            Abba("(b) terrain plus caves, generated code", "generated", "hand-written", () => {
                foreach (int2 origin in Origins){
                    generated.Region.Origin = origin;
                    generated.Run();
                }
            }, () => {
                foreach (int2 origin in Origins){
                    hand.Origin = origin;
                    hand.Run();
                }
            }, 1.0 / Origins.Length, SampleUnit.Millisecond, "ms/chunk");
            Debug.Log($"Noise graph benchmark (b) work per chunk: {counters}; programs: {string.Join(" · ", graph.Programs.Select(p => p.ToString()))}");
        }

        [Test]
        [Performance]
        public void ArithmeticChain_InterpreterAgainstHandWritten_MsPerChunk(){
            NoiseGraph definition = ArithmeticChain();
            using CompiledNoiseGraph graph = CompileOrFail(definition);
            Assert.AreEqual(1, graph.Tables.MarkerCount);
            using GraphRegionResult region = GraphEvaluator.Allocate(graph, Origins[0], new int2(Width, Width), true, false, out GraphRegionJob interpreter,
                Allocator.Persistent);
            interpreter.Seed = Seed;

            using NativeArray<NoiseOctave> octaves = OctaveTable(definition, new[]{ "detail" });
            using NativeArray<float> corners = new(LatticePoints * Stride, Allocator.Persistent), voxels = new(Width * Width * Height, Allocator.Persistent);
            ArithmeticHandJob hand = new(){ Seed = Seed, Origin = Origins[0], Octaves = octaves, Corners = corners, Voxels = voxels };

            interpreter.Run();
            hand.Run();
            AssertSame("value", region.Voxels, graph.Output("value").VoxelOutput * Width * Width * Height, voxels, 0, Width * Width * Height);
            GraphWorkCounters counters = region.Counters;
            using GraphRegionResult generatedRegion = Generated(graph, out GeneratedRegionJob<ArithmeticChainBenchmarkCode> generated);
            generated.Run();
            AssertSameRegion(region, generatedRegion);

            Abba("(c) arithmetic chain", "interpreter", "hand-written", () => {
                foreach (int2 origin in Origins){
                    interpreter.Origin = origin;
                    interpreter.Run();
                }
            }, () => {
                foreach (int2 origin in Origins){
                    hand.Origin = origin;
                    hand.Run();
                }
            }, 1.0 / Origins.Length, SampleUnit.Millisecond, "ms/chunk");
            Abba("(c) arithmetic chain, generated code", "generated", "hand-written", () => {
                foreach (int2 origin in Origins){
                    generated.Region.Origin = origin;
                    generated.Run();
                }
            }, () => {
                foreach (int2 origin in Origins){
                    hand.Origin = origin;
                    hand.Run();
                }
            }, 1.0 / Origins.Length, SampleUnit.Millisecond, "ms/chunk");
            Debug.Log($"Noise graph benchmark (c) work per chunk: {counters}; programs: {string.Join(" · ", graph.Programs.Select(p => p.ToString()))}");
        }

        #endregion
    }
}
