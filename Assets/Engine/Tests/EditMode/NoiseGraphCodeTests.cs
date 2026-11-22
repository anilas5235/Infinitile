using System;
using System.Collections.Generic;
using System.Linq;
using Engine.Editor.WorldGen;
using Engine.Scripts.WorldGen.Graph;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using static Engine.Tests.EditMode.NoiseGraphTestKit;

namespace Engine.Tests.EditMode{
    /// <summary>
    ///     Generated graph code: the committed files are what the generator makes, the code gives the interpreter's
    ///     values bit for bit (every operation, 2D and 3D, both cell heights) and any other graph runs in the
    ///     interpreter.
    /// </summary>
    public class NoiseGraphCodeTests{
        private const string TestCodePath = "Assets/Engine/Tests/EditMode/NoiseGraphTestCode.cs";

        private static readonly int2[] Origins ={ new(0, 0), new(-736, -32), new(12800, 9600) };
        private static readonly uint[] Seeds ={ 1u, 0xDEADBEEFu };

        /// <summary>
        ///     Every operation a program can hold: noises 2D and 3D (simplex, fBm, ridged, shifted on each axis, by a
        ///     node and by a constant), the constant forms of the arithmetic, the unary kernels, clamp, smoothstep,
        ///     range choice, lerp, y gradient, cubic and linear splines with node values, markers of both cell heights,
        ///     a per-column output and a noise in the voxel program.
        /// </summary>
        internal static GraphBuilder AllOperations(){
            return new GraphBuilder()
                .Noise("base", NoiseKind.Simplex2, 1, 300f)
                .Noise("detail", NoiseKind.Simplex2, 3, 180f)
                .Noise("ridges", NoiseKind.Simplex2, 2, 240f)
                .Noise("volume", NoiseKind.Simplex3, 2, 60f, 30f)
                .Noise("folds", NoiseKind.Simplex3, 2, 80f, 20f)
                .Noise("grain", NoiseKind.Simplex3, 1, 24f)
                .Node("warped", Op("shift", ("in", Noise("fbm", "detail")), ("x", Op("mul", ("a", Noise("simplex2", "base")), ("b", 40.0))), ("z", 17.5)))
                .Node("ridge", Noise("ridged", "ridges"))
                .Node("k1", Op("add", ("a", "warped"), ("b", 0.25)))
                .Node("k2", Op("sub", ("a", "k1"), ("b", 0.5)))
                .Node("k3", Op("sub", ("a", 0.75), ("b", "k2")))
                .Node("k4", Op("mul", ("a", "k3"), ("b", 3.0)))
                .Node("k5", Op("min", ("a", "k4"), ("b", 0.9)))
                .Node("k6", Op("max", ("a", "k5"), ("b", -0.9)))
                .Node("u1", Op("abs", ("in", "warped")))
                .Node("u2", Op("neg", ("in", "u1")))
                .Node("u3", Op("square", ("in", "k6")))
                .Node("u4", Op("cube", ("in", "warped")))
                .Node("u5", Op("half_negative", ("in", "warped")))
                .Node("u6", Op("quarter_negative", ("in", "warped")))
                .Node("u7", Op("soft_clamp", ("in", Op("mul", ("a", "warped"), ("b", 2.5)))))
                .Node("clamped", Op("clamp", ("in", "ridge"), ("min", 0.2), ("max", 0.8)))
                .Node("smooth", Op("smoothstep", ("in", "warped"), ("from", -0.3), ("to", 0.6)))
                .Node("choice", Op("range_choice", ("in", "warped"), ("inRange", "u3"), ("outRange", "u4"), ("min", -0.2), ("max", 0.3)))
                .Node("mix", Op("lerp", ("t", "smooth"), ("a", 0.25), ("b", "ridge")))
                .Node("curve", Spline("warped", false, Point(-1, -0.5, 0.3), Point(0, "u7"), Point(0.5, 0.2, -1), Point(1, 0.8)))
                .Node("steps", Spline("ridge", true, Point(0, 0.0), Point(0.5, "clamped"), Point(1, 1.0)))
                .Node("flat_sum", Op("add", ("a", Op("add", ("a", Op("add", ("a", "k6"), ("b", "u2"))), ("b", Op("add", ("a", "u5"), ("b", "u6"))))),
                    ("b", Op("add", ("a", Op("add", ("a", "choice"), ("b", "mix"))), ("b", Op("add", ("a", "curve"), ("b", "steps")))))))
                .Node("surface", Op("add", ("a", 64.0), ("b", Op("mul", ("a", "curve"), ("b", 40.0)))))
                .Node("density_in", Op("add", ("a", Op("mul", ("a", Op("sub", ("a", "surface"), ("b", "y"))), ("b", 1.0 / 64.0))),
                    ("b", Op("mul", ("a", Op("shift", ("in", Noise("fbm", "volume")), ("y", Op("mul", ("a", "warped"), ("b", 12.0))))), ("b", 0.5)))))
                .Node("density", Op("interpolated", ("in", "density_in"), ("cellHeight", 8.0)))
                .Node("cave_in", Op("add", ("a", Noise("ridged", "folds")),
                    ("b", Op("y_gradient", ("fromY", 0.0), ("toY", 96.0), ("fromValue", -0.4), ("toValue", 0.3)))))
                .Node("cave", Op("interpolated", ("in", "cave_in"), ("cellHeight", 4.0)))
                .Output("flat", "flat_sum")
                .Output("u1", "u1")
                .Output("surface", new Dictionary<string, object>{ ["node"] = "surface", ["perColumn"] = true })
                .Output("density", Op("soft_clamp", ("in", "density")))
                .Output("cave", Op("min", ("a", "cave"), ("b", Op("max", ("a", "density"), ("b", Op("mul", ("a", Op("sub", ("a", "y"), ("b", "surface"))), ("b", -0.05)))))))
                .Output("grain", Op("add", ("a", "density"), ("b", Op("mul", ("a", Noise("simplex3", "grain")), ("b", 0.1)))));
        }

        /// <summary>The generated code of the test graphs, as the committed test file must hold it.</summary>
        private static string GenerateTestCode(){
            using CompiledNoiseGraph all = CompileOrFail(AllOperations().Graph);
            using CompiledNoiseGraph caves = CompileOrFail(NoiseGraphEquivalenceTests.TerrainCaves().Graph);
            return NoiseGraphCodeGenerator.Generate("Engine.Tests.EditMode", "the noise graph code tests", ("AllOperationsGraphCode", all),
                ("TerrainCavesGraphCode", caves));
        }

        private static void AssertUpToDate(string path, string expected, string how){
            if (!NoiseGraphCodeGenerator.IsUpToDate(path, expected, out string fresh))
                Assert.Fail($"{path} is out of date: {how} (the generated text is in {fresh}).");
        }

        private static void AssertSame(NativeArray<float> interpreted, NativeArray<float> generated, string what){
            Assert.AreEqual(interpreted.Length, generated.Length, what);
            for (int i = 0; i < interpreted.Length; i++)
                if (BitConverter.SingleToInt32Bits(interpreted[i]) != BitConverter.SingleToInt32Bits(generated[i]))
                    Assert.Fail($"{what}[{i}]: interpreter {interpreted[i]:R}, generated {generated[i]:R}");
        }

        private static void AssertSameRegion(GraphRegionResult interpreted, GraphRegionResult generated, string label){
            AssertSame(interpreted.Lattice, generated.Lattice, $"{label} lattice");
            AssertSame(interpreted.Columns, generated.Columns, $"{label} columns");
            AssertSame(interpreted.Columns2D, generated.Columns2D, $"{label} interpolated columns");
            AssertSame(interpreted.Corners, generated.Corners, $"{label} corners");
            AssertSame(interpreted.Voxels, generated.Voxels, $"{label} voxels");
            Assert.AreEqual(interpreted.Counters.ToString(), generated.Counters.ToString(), $"{label} work counters");
        }

        private static void CheckAgainstInterpreter<TPrograms>(CompiledNoiseGraph graph, string label) where TPrograms : struct, IGraphPrograms{
            Assert.AreEqual(graph.CodeHash, default(TPrograms).CodeHash, $"{typeof(TPrograms).Name} is out of date");
            foreach (int2 origin in Origins)
            foreach (uint seed in Seeds){
                using GraphRegionResult interpreted = GraphEvaluator.EvaluateRegion(graph, seed, origin, new int2(32, 32), true, true, true);
                using GraphRegionResult generated = GraphEvaluator.EvaluateRegion<TPrograms>(graph, seed, origin, new int2(32, 32), true, true);
                AssertSameRegion(interpreted, generated, $"{label} at {origin}, seed {seed}");
            }
        }

        [Test]
        public void ShippedCode_IsUpToDate(){
            AssertUpToDate(NoiseGraphCodeGenerator.ShippedPath, NoiseGraphCodeGenerator.GenerateShipped(), "run Infinitile > Generate Noise Graph Code");
        }

        [Test]
        public void TestCode_IsUpToDate(){
            AssertUpToDate(TestCodePath, GenerateTestCode(), "replace it with the generated text");
        }

        [Test]
        public void ShippedCode_EqualsTheInterpreter_BitForBit(){
            GraphCompileResult result = ShippedNoiseGraph.Compile();
            Assert.IsTrue(result.Succeeded, string.Join("\n", result.Messages));
            using CompiledNoiseGraph graph = result.Graph;
            Assert.IsTrue(GraphEvaluator.HasGeneratedCode(graph), "The shipped graph should run as generated code.");
            foreach (int2 origin in Origins)
            foreach (uint seed in Seeds){
                using GraphRegionResult interpreted = GraphEvaluator.EvaluateRegion(graph, seed, origin, new int2(32, 32), true, true, true);
                using GraphRegionResult generated = GraphEvaluator.EvaluateRegion(graph, seed, origin, new int2(32, 32), true, true);
                AssertSameRegion(interpreted, generated, $"shipped graph at {origin}, seed {seed}");
            }
        }

        [Test]
        public void AllOperations_GeneratedCodeEqualsTheInterpreter_BitForBit(){
            using CompiledNoiseGraph graph = CompileOrFail(AllOperations().Graph);
            CollectionAssert.IsSupersetOf(graph.Programs.Select(p => p.Stage), new[]{ GraphStage.Lattice, GraphStage.Column, GraphStage.Corner, GraphStage.Voxel });
            CheckAgainstInterpreter<AllOperationsGraphCode>(graph, "all operations");
        }

        [Test]
        public void TerrainCaves_GeneratedCodeEqualsTheInterpreter_BitForBit(){
            using CompiledNoiseGraph graph = CompileOrFail(NoiseGraphEquivalenceTests.TerrainCaves().Graph);
            CheckAgainstInterpreter<TerrainCavesGraphCode>(graph, "terrain and caves");
        }

        [Test]
        public void ChangedGraph_RunsInTheInterpreter(){
            GraphCompileResult shipped = ShippedNoiseGraph.Compile();
            Assert.IsTrue(shipped.Succeeded, string.Join("\n", shipped.Messages));
            using CompiledNoiseGraph graph = shipped.Graph;

            NoiseGraph edited = NoiseGraphJson.Parse(ShippedNoiseGraph.LoadJson());
            NoiseDefinition noise = edited.Noises.Values.First();
            noise.Amplitude *= 0.5f;
            GraphCompileResult changed = GraphCompiler.Compile(edited, "edited.json");
            Assert.IsTrue(changed.Succeeded, string.Join("\n", changed.Messages));
            using CompiledNoiseGraph other = changed.Graph;

            Assert.AreNotEqual(graph.CodeHash, other.CodeHash);
            Assert.IsTrue(GraphEvaluator.HasGeneratedCode(graph));
            Assert.IsFalse(GraphEvaluator.HasGeneratedCode(other));
            Assert.Throws<ArgumentException>(() => GraphEvaluator.EvaluateRegion<ShippedNoiseGraphCode>(other, 1u, int2.zero, new int2(16, 16), false));
        }

        [Test]
        public void CodeHash_IsStableAcrossCompiles(){
            using CompiledNoiseGraph a = CompileOrFail(AllOperations().Graph), b = CompileOrFail(AllOperations().Graph);
            Assert.AreEqual(a.CodeHash, b.CodeHash);
            Assert.AreNotEqual(0UL, a.CodeHash);
        }
    }
}
