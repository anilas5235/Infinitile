using System;
using System.Collections.Generic;
using System.Linq;
using Engine.Scripts.WorldGen.Graph;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using static Engine.Tests.EditMode.NoiseGraphTestKit;

namespace Engine.Tests.EditMode{
    /// <summary>
    ///     The noise graph's JSON form, the validator's rejections, the shipped graph (outputs, bounds, work counters,
    ///     golden hash) and the seeding of noises by name.
    /// </summary>
    public class NoiseGraphCompilerTests{
        #region JSON

        [Test]
        public void ShippedGraph_IsWrittenInCanonicalForm(){
            string text = ShippedNoiseGraph.LoadJson();
            Assert.IsNotNull(text, "the shipped graph asset is missing");
            text = text.Replace("\r\n", "\n");
            Assert.AreEqual(text.TrimEnd(), NoiseGraphJson.Write(NoiseGraphJson.Parse(text)).TrimEnd(),
                "the shipped graph must be stored as NoiseGraphJson.Write writes it (sorted keys)");
        }

        [Test]
        public void JsonRoundTrip_KeepsEveryField(){
            GraphBuilder g = new GraphBuilder()
                .Noise("flat", NoiseKind.Simplex2, 3, 250f, lacunarity: 2.5f, gain: 0.4f, amplitude: 0.75f)
                .Noise("volume", NoiseKind.Simplex3, 2, 90f, 45f)
                .Node("curve", Spline(Noise("fbm", "flat"), false, Point(-1, -0.5, 0.25), Point(0.5, "x"), Point(1, 1.0)))
                .Node("density", Op("interpolated", ("in", Op("add", ("a", Noise("ridged", "volume")), ("b", "curve"))), ("cellHeight", 8.0)))
                .Output("density", "density")
                .Output("curve", new Dictionary<string, object>{ ["node"] = "curve", ["perColumn"] = true })
                .Output("constant", 0.5);
            string json = NoiseGraphJson.Write(g.Graph);
            NoiseGraph read = NoiseGraphJson.Parse(json);
            Assert.AreEqual(json, NoiseGraphJson.Write(read));
            Assert.AreEqual(45f, read.Noises["volume"].WavelengthY);
            Assert.AreEqual(250f, read.Noises["flat"].WavelengthY, "a 2D noise's wavelengthY is its wavelength");
            Assert.AreEqual(NoiseGraph.CurrentFormatVersion, read.FormatVersion);
            StringAssert.Contains("\"formatVersion\": 1", json);
            GraphCompileResult r = Compile(read);
            r.Graph?.Dispose();
            Assert.IsTrue(r.Succeeded, string.Join("\n", r.Messages));
        }

        [Test]
        public void MalformedJson_IsAnErrorNamingTheFile(){
            GraphCompileResult r = GraphCompiler.Compile("{ \"formatVersion\": 1, \"noises\": [", "broken.json");
            Assert.IsFalse(r.Succeeded);
            Assert.IsTrue(r.Errors.Any(m => m.File == "broken.json"), string.Join("\n", r.Messages));
        }

        [Test]
        public void DeeplyNestedJson_IsAnErrorNotAStackOverflow(){
            const int depth = 100000;
            string json = "{ \"formatVersion\": 1, \"outputs\": { \"o\": " + string.Concat(Enumerable.Repeat("{ \"op\": \"abs\", \"in\": ", depth)) + "1"
                          + new string('}', depth) + " } }";
            GraphCompileResult r = GraphCompiler.Compile(json, "deep.json");
            Assert.IsFalse(r.Succeeded);
            Assert.IsTrue(r.Errors.Any(m => m.File == "deep.json" && m.Text.Contains($"more than {GraphLimits.MaxJsonNesting} deep")),
                string.Join("\n", r.Messages));
        }

        #endregion

        #region Validator

        private static void AssertError(GraphCompileResult r, string node, string fragment){
            r.Graph?.Dispose();
            Assert.IsFalse(r.Succeeded, "the graph compiled");
            bool found = r.Errors.Any(m => m.File == File && (node == null || m.Node == node) && m.Text.Contains(fragment));
            Assert.IsTrue(found, $"no error for node \"{node}\" containing \"{fragment}\"; got:\n" + string.Join("\n", r.Messages));
        }

        [Test]
        public void Validator_RejectsACycle(){
            GraphBuilder g = new GraphBuilder()
                .Node("a", Op("add", ("a", "b"), ("b", 1.0)))
                .Node("b", Op("mul", ("a", "c"), ("b", 2.0)))
                .Node("c", Op("abs", ("in", "a")))
                .Output("o", "a");
            GraphCompileResult r = Compile(g.Graph);
            AssertError(r, null, "Cycle");
            StringAssert.Contains("a → b → c → a", r.Errors.First(m => m.Text.Contains("Cycle")).Text);
        }

        [Test]
        public void Validator_RejectsUnknownNamesOpsAndFields(){
            GraphBuilder g = new GraphBuilder()
                .Noise("hills", NoiseKind.Simplex2)
                .Node("user", Op("add", ("a", "missing"), ("b", 1.0)))
                .Node("noise_by_name", Op("add", ("a", "hills"), ("b", 1.0)))
                .Node("strange", Op("sinus", ("in", "x")))
                .Node("extra", Op("abs", ("in", "x"), ("scale", 2.0)))
                .Node("unknown_noise", Noise("fbm", "nothing"))
                .Node("lacking", Op("clamp", ("in", "x"), ("min", 0.0)))
                .Output("o", "user");
            GraphCompileResult r = Compile(g.Graph);
            AssertError(r, "user", "unknown node \"missing\"");
            AssertError(r, "noise_by_name", "is a noise");
            AssertError(r, "strange", "unknown op \"sinus\"");
            AssertError(r, "extra", "unknown field \"scale\"");
            AssertError(r, "unknown_noise", "unknown noise \"nothing\"");
            AssertError(r, "lacking", "needs \"max\"");
        }

        [Test]
        public void Validator_RejectsMissingRequiredOutputs(){
            GraphBuilder g = new GraphBuilder().Output(GraphOutputs.Temperature, "x");
            GraphCompileResult r = GraphCompiler.Compile(g.Graph, File, GraphOutputs.Climate);
            foreach (string name in GraphOutputs.Climate.Where(n => n != GraphOutputs.Temperature))
                AssertError(r, null, $"required output \"{name}\"");
        }

        [Test]
        public void Validator_RejectsBadStructure(){
            GraphBuilder g = new GraphBuilder()
                .Noise("volume", NoiseKind.Simplex3)
                .Node("x", Op("const", ("value", 1.0)))
                .Node("inner", Op("interpolated", ("in", Noise("simplex3", "volume")), ("cellHeight", 8.0)))
                .Node("outer", Op("interpolated", ("in", Op("add", ("a", "inner"), ("b", "y"))), ("cellHeight", 4.0)))
                .Node("odd_cell", Op("interpolated", ("in", "y"), ("cellHeight", 6.0)))
                .Node("unordered", Spline("z", false, Point(0, 0.0), Point(0, 1.0)))
                .Node("flat_shift", Op("shift", ("in", Op("add", ("a", "z"), ("b", 1.0))), ("x", 1.0)))
                .Output("o", "outer")
                .Output("p", new Dictionary<string, object>{ ["node"] = "y", ["perColumn"] = true });
            GraphCompileResult r = Compile(g.Graph);
            AssertError(r, "x", "name of a coordinate input");
            AssertError(r, "outer", "marker inside another one");
            AssertError(r, "odd_cell", "must be 4 or 8");
            AssertError(r, "unordered", "increase strictly");
            AssertError(r, "flat_shift", "must be a simplex2");
        }

        [Test]
        public void Validator_RejectsAPerColumnOutputThatDependsOnY(){
            GraphBuilder g = new GraphBuilder().Output("p", new Dictionary<string, object>{ ["node"] = "y", ["perColumn"] = true });
            AssertError(Compile(g.Graph), null, "only 2D outputs");
        }

        [Test]
        public void Validator_RejectsANewerFormatVersion(){
            GraphBuilder g = new GraphBuilder().Output("o", "x");
            g.Graph.FormatVersion = NoiseGraph.CurrentFormatVersion + 1;
            AssertError(Compile(g.Graph), null, "newer than this game reads");
        }

        [Test]
        public void Validator_RejectsGraphsOverTheLimits(){
            // Noises and octaves.
            GraphBuilder noises = new GraphBuilder().Output("o", "x");
            for (int i = 0; i <= GraphLimits.MaxNoises; i++) noises.Noise($"n{i:00}", NoiseKind.Simplex2);
            noises.Noise("too_many_octaves", NoiseKind.Simplex2, GraphLimits.MaxOctaves + 1);
            GraphCompileResult r = Compile(noises.Graph);
            AssertError(r, null, $"the limit is {GraphLimits.MaxNoises}");
            AssertError(r, null, "\"octaves\" must be 1 to");

            // Spline points and depth.
            Dictionary<string, object>[] points = Enumerable.Range(0, GraphLimits.MaxSplinePoints + 1).Select(k => Point(k, (double)k)).ToArray();
            GraphBuilder splines = new GraphBuilder().Node("long", Spline("x", true, points));
            splines.Node("s0", Spline("x", true, Point(0, 0.0), Point(1, 1.0)));
            for (int k = 1; k <= GraphLimits.MaxSplineDepth; k++) splines.Node($"s{k}", Spline("x", true, Point(0, $"s{k - 1}"), Point(1, 1.0)));
            splines.Output("o", $"s{GraphLimits.MaxSplineDepth}");
            r = Compile(splines.Graph);
            AssertError(r, "long", $"at most {GraphLimits.MaxSplinePoints}");
            AssertError(r, $"s{GraphLimits.MaxSplineDepth}", $"nested more than {GraphLimits.MaxSplineDepth} deep");

            // Instructions: a chain no folding or sharing can shorten.
            GraphBuilder chain = new GraphBuilder().Node("c0", Op("mul", ("a", "x"), ("b", 1.5)));
            for (int i = 1; i <= GraphLimits.MaxInstructions + 8; i++) chain.Node($"c{i}", Op("add", ("a", $"c{i - 1}"), ("b", "z")));
            chain.Output("o", $"c{GraphLimits.MaxInstructions + 8}");
            AssertError(Compile(chain.Graph), null, $"the limit is {GraphLimits.MaxInstructions}");

            // Registers: 70 values that all stay live until a second chain reads them again.
            const int values = 70;
            GraphBuilder live = new();
            for (int i = 0; i < values; i++) live.Node($"v{i}", Op("mul", ("a", "x"), ("b", 1.0 + i * 0.01)));
            live.Node("a0", Op("add", ("a", "v0"), ("b", "z"))).Node("b0", Op("max", ("a", "v0"), ("b", "z")));
            for (int i = 1; i < values; i++){
                live.Node($"a{i}", Op("add", ("a", $"a{i - 1}"), ("b", $"v{i}")));
                live.Node($"b{i}", Op("max", ("a", $"b{i - 1}"), ("b", $"v{i}")));
            }

            live.Output("o", Op("add", ("a", $"a{values - 1}"), ("b", $"b{values - 1}")));
            AssertError(Compile(live.Graph), null, $"registers; the limit is {GraphLimits.MaxRegisters}");
        }

        [Test]
        public void LongChainOfNamedNodes_IsAnErrorNotAStackOverflow(){
            // Far longer than a call stack is deep: resolving, folding and building the program never nest per node.
            const int length = 100000;
            GraphBuilder chain = new GraphBuilder().Node("c0", Op("mul", ("a", "x"), ("b", 1.5)));
            for (int i = 1; i < length; i++) chain.Node($"c{i}", Op("add", ("a", $"c{i - 1}"), ("b", "z")));
            chain.Output("o", $"c{length - 1}");
            AssertError(Compile(chain.Graph), null, $"the limit is {GraphLimits.MaxInstructions}");
        }

        [Test]
        public void Validator_RejectsNoiseSettingsThatOverflow(){
            GraphBuilder g = new GraphBuilder()
                .Noise("loud", NoiseKind.Simplex2, GraphLimits.MaxOctaves, 100f, 0f, 2f, 400f)
                .Noise("fast", NoiseKind.Simplex3, GraphLimits.MaxOctaves, 100f, 50f, 1000f)
                .Noise("calm", NoiseKind.Simplex2, GraphLimits.MaxOctaves, 100f, 0f, 2f, 0.5f)
                .Output("a", Noise("fbm", "loud"))
                .Output("b", Noise("ridged", "fast"))
                .Output("c", Noise("fbm", "calm"));
            GraphCompileResult r = Compile(g.Graph);
            AssertError(r, null, "Noise \"loud\": its octaves leave the float range");
            AssertError(r, null, "Noise \"fast\": its octaves leave the float range");
            Assert.IsFalse(r.Errors.Any(m => m.Text.Contains("\"calm\"")), string.Join("\n", r.Messages));
        }

        [Test]
        public void Validator_RejectsValuesThatCanOverflow(){
            GraphBuilder power = new GraphBuilder().Node("huge", Op("cube", ("in", Op("cube", ("in", "x"))))).Output("o", "huge");
            AssertError(Compile(power.Graph), null, "\"huge\": its values can leave the float range");

            GraphBuilder product = new GraphBuilder().Node("folded", Op("mul", ("a", 1e30), ("b", 1e30))).Output("o", Op("add", ("a", "folded"), ("b", "x")));
            AssertError(Compile(product.Graph), null, "\"folded\": its values can leave the float range");
        }

        [Test]
        public void Validator_ReportsEachCycleOnce(){
            GraphBuilder g = new GraphBuilder()
                .Node("a", Op("abs", ("in", "b")))
                .Node("b", Op("add", ("a", "a"), ("b", "a")))
                .Output("o", "a");
            GraphCompileResult r = Compile(g.Graph);
            AssertError(r, "a", "Cycle: a → b → a");
            Assert.AreEqual(1, r.Errors.Count(m => m.Text.Contains("Cycle")), string.Join("\n", r.Messages));
        }

        [Test]
        public void Validator_ReportsANameInANumberFieldAsABadField(){
            GraphBuilder g = new GraphBuilder()
                .Node("b", Op("smoothstep", ("in", "x"), ("from", "b"), ("to", 1.0)))
                .Node("c", Op("clamp", ("in", "x"), ("min", "c"), ("max", 1.0)))
                .Output("o", "b")
                .Output("p", "c");
            GraphCompileResult r = Compile(g.Graph);
            AssertError(r, "b", "\"from\" must be a number");
            AssertError(r, "c", "\"min\" must be a number");
            Assert.IsFalse(r.Errors.Any(m => m.Text.Contains("Cycle")), string.Join("\n", r.Messages));
        }

        [Test]
        public void Validator_RejectsADotInANodeName(){
            GraphBuilder g = new GraphBuilder().Node("terrain.base", Op("abs", ("in", "x"))).Output("o", "terrain.base");
            AssertError(Compile(g.Graph), "terrain.base", "cannot contain \".\"");
        }

        [Test]
        public void GraphValidator_ReportsTheSameMessagesWithoutKeepingAGraph(){
            GraphBuilder g = new GraphBuilder().Node("user", Op("add", ("a", "missing"), ("b", 1.0))).Output("o", "user");
            List<GraphMessage> messages = GraphValidator.Validate(g.Graph, File, Array.Empty<string>());
            Assert.IsTrue(messages.Any(m => m.Severity == GraphMessageSeverity.Error && m.Node == "user"), string.Join("\n", messages));
            Assert.IsEmpty(GraphValidator.Validate(ShippedNoiseGraph.LoadJson(), ShippedNoiseGraph.FileName));
        }

        #endregion

        #region Shipped graph

        [Test]
        public void ShippedGraph_CompilesWithTheSixClimateOutputsInsideMinusOneToOne(){
            GraphCompileResult r = ShippedNoiseGraph.Compile();
            using CompiledNoiseGraph graph = r.Graph;
            Assert.IsTrue(r.Succeeded, string.Join("\n", r.Messages));
            Assert.IsEmpty(r.Messages, "the shipped graph compiles without warnings");
            foreach (string name in GraphOutputs.Climate){
                GraphOutputInfo o = graph.Output(name);
                Assert.IsNotNull(o, name);
                Assert.IsFalse(o.Is3D, name);
                Assert.GreaterOrEqual(o.Bounds.Min, -1.001f, name);
                Assert.LessOrEqual(o.Bounds.Max, 1.001f, name);
            }

            TestContext.WriteLine($"{graph.InstructionCount} instructions, {graph.NoiseCount} noises");
            foreach (GraphProgramStats p in graph.Programs) TestContext.WriteLine(p);
        }

        /// <summary>Columns of the golden hash: 48 × 48 points, 97 blocks apart, around the origin.</summary>
        private static float2[] GoldenColumns(){
            float2[] points = new float2[48 * 48];
            for (int j = 0; j < 48; j++)
            for (int i = 0; i < 48; i++)
                points[j * 48 + i] = new float2(-2328 + i * 97, -2328 + j * 97);
            return points;
        }

        private static uint HashValues(IEnumerable<float> values){
            uint h = 2166136261u;
            foreach (float v in values){
                uint bits = (uint)BitConverter.SingleToInt32Bits(v);
                for (int b = 0; b < 4; b++){
                    h ^= (bits >> (8 * b)) & 0xFF;
                    h *= 16777619u;
                }
            }

            return h;
        }

        /// <summary>
        ///     The shipped graph's 2D outputs over fixed columns for seed 1337, as one hash. A change to the shipped
        ///     graph, the compiler, the interpreter or the noise moves it; if the change is intended, update the
        ///     expected value in the same pull request.
        /// </summary>
        [Test]
        public void ShippedGraph_OutputsOnFixedColumnsMatchTheGoldenHash(){
            const uint expected = 0x52B61DCBu; // V4 rule outputs (beach_shape and the snow, ice, tree line and strata wobbles); V3 calmer border (DEC-171); V3 deeper valleys: massif rows d2 (DEC-168); V3 re-tune: ranges on the W zero line, Badlands K (DEC-163…165); V3: blend_border, T/F wavelengths ×1.4 (DEC-161) (V2: the terrain outputs, the massif re-tune and the soft knee)
            using CompiledNoiseGraph graph = ShippedNoiseGraph.Compile().Graph;
            float2[] columns = GoldenColumns();
            float[] lattice = GraphEvaluator.SampleLattice(graph, Seed, columns, out _);
            // Output by output in name order, so the hash does not depend on where the compiler keeps a value.
            List<float> values = new();
            foreach (GraphOutputInfo output in graph.Outputs.Where(o => !o.Is3D && !o.BoundOnly).OrderBy(o => o.Name, StringComparer.Ordinal))
                for (int p = 0; p < columns.Length; p++)
                    values.Add(lattice[output.LatticeValue * columns.Length + p]);
            uint hash = HashValues(values);
            Debug.Log($"Noise graph golden hash: 0x{hash:X8}");
            Assert.AreEqual(expected, hash, $"golden hash moved: 0x{hash:X8}");
        }

        [Test]
        public void ShippedGraph_WorkCountersCountEveryNoiseSample(){
            using CompiledNoiseGraph graph = ShippedNoiseGraph.Compile().Graph;
            float2[] points = GoldenColumns();
            GraphEvaluator.SampleLattice(graph, Seed, points, out GraphWorkCounters counters);
            // Octaves: continentalness 5 + warp 2 × 2 + erosion 4 + humidity 3 + temperature 3 + weirdness 3 + peak 3 (re-tune 2026-11-22) + blend border 1 (the detail noise went with DEC-171)
            // + the V4 rule outputs beach_shape, ice_wobble, snow_wobble, strata_shift and tree_line_wobble 1 each.
            const int samplesPerPoint = 31;
            Assert.AreEqual(points.Length, counters.LatticePoints);
            Assert.AreEqual((long)samplesPerPoint * points.Length, counters.NoiseSamples2D);
            Assert.AreEqual(0, counters.NoiseSamples3D);

            using GraphRegionResult region = GraphEvaluator.EvaluateRegion(graph, Seed, new int2(-64, 32), new int2(32, 32), true);
            Assert.AreEqual(9 * 9, region.Counters.LatticePoints, "a chunk's lattice is 9 × 9 points");
            // The per-column outputs (blend_border and the five V4 rule outputs) add their noise sample on each of the chunk's 32 × 32 columns.
            const int columnSamples = 6;
            Assert.AreEqual(samplesPerPoint * 81 + columnSamples * 32 * 32, region.Counters.NoiseSamples2D);
            Assert.Greater(region.Counters.NoiseSamples3D, 0, "the terrain density is 3D (N3)");
        }

        [Test]
        public void ShippedGraph_EverySampledValueLiesInsideItsBound(){
            using CompiledNoiseGraph graph = ShippedNoiseGraph.Compile().Graph;
            System.Random random = new(7);
            List<float3> positions = new();
            for (int i = 0; i < 4096; i++){
                float range = i % 4 == 0 ? 2e6f : 2e4f;
                positions.Add(new float3((float)(random.NextDouble() - 0.5) * range, 0f, (float)(random.NextDouble() - 0.5) * range));
            }

            int[] nodes = Enumerable.Range(0, graph.ReferenceNodeCount).ToArray();
            float[] values = graph.EvaluateDirect(positions, nodes, Seed);
            for (int p = 0; p < positions.Count; p++)
            for (int n = 0; n < nodes.Length; n++){
                float v = values[p * nodes.Length + n];
                GraphBounds b = graph.ReferenceBound(n);
                if (!b.Contains(v)) Assert.Fail($"node {graph.ReferenceName(n)} at {positions[p]}: {v} outside {b}");
            }
        }

        #endregion

        #region Seeds

        [Test]
        public void NoiseSeeds_DependOnTheNameOnly(){
            GraphBuilder a = new GraphBuilder()
                .Noise("alpha", NoiseKind.Simplex2, 2, 120f)
                .Noise("hills", NoiseKind.Simplex2, 3, 300f)
                .Output("o", Noise("fbm", "hills"));
            GraphBuilder b = new GraphBuilder()
                .Noise("hills", NoiseKind.Simplex2, 3, 300f)
                .Noise("aaa", NoiseKind.Simplex2, 1, 50f)
                .Noise("zeta", NoiseKind.Simplex3, 2, 80f)
                .Output("other", Noise("fbm", "aaa"))
                .Output("o", Noise("fbm", "hills"));
            GraphBuilder renamed = new GraphBuilder()
                .Noise("hills2", NoiseKind.Simplex2, 3, 300f)
                .Output("o", Noise("fbm", "hills2"));

            float2[] points = GoldenColumns();
            float[] Values(GraphBuilder g, uint seed){
                using CompiledNoiseGraph graph = CompileOrFail(g.Graph);
                float[] all = GraphEvaluator.SampleLattice(graph, seed, points, out _);
                return all.Skip(graph.Output("o").LatticeValue * points.Length).Take(points.Length).ToArray();
            }

            float[] va = Values(a, Seed), vb = Values(b, Seed), vr = Values(renamed, Seed), vs = Values(a, Seed + 1);
            for (int i = 0; i < points.Length; i++)
                Assert.IsTrue(SameValue(va[i], vb[i]), $"other noises moved \"hills\" at {points[i]}");
            Assert.Greater(Enumerable.Range(0, points.Length).Count(i => va[i] != vr[i]), points.Length * 9 / 10, "renaming must reseed");
            Assert.Greater(Enumerable.Range(0, points.Length).Count(i => va[i] != vs[i]), points.Length * 9 / 10, "the world seed must reseed");
            Assert.AreNotEqual(GraphResolver.NoiseSalt("hills"), GraphResolver.NoiseSalt("hills2"));
            Assert.AreEqual(GraphResolver.NoiseSalt("hills"), GraphResolver.NoiseSalt("hills"));
        }

        #endregion
    }
}
