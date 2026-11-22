using System.Collections.Generic;
using System.Linq;
using Engine.Scripts.WorldGen.Biomes;
using Engine.Scripts.WorldGen.Graph;
using Engine.Scripts.WorldGen.Noise;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;

namespace Engine.Tests.EditMode{
    /// <summary>
    ///     The slope bounds behind the layout validator's ε (WorldGen v2 §6.6): the seeded simplex's largest slope g
    ///     (<see cref="GraphSlopes.SimplexSlope" />, measured once and kept here), and the climate parameters of the
    ///     shipped graph, whose step between neighbouring columns (diagonal ones included) must stay within ε = 2 · s.
    /// </summary>
    public class GraphSlopeTests{
        [Test]
        public void SimplexSlope_BoundsTheMeasuredSlope_AndStaysTight(){
            System.Random r = new(11);
            const float h = 1e-3f;
            float max2 = 0f, max3 = 0f;
            for (int i = 0; i < 400000; i++){
                // Near the origin and divided by the step the floats really took, so rounding does not inflate the slope.
                float2 p = new((float)(r.NextDouble() * 64 - 32), (float)(r.NextDouble() * 64 - 32));
                double a = r.NextDouble() * 2 * math.PI;
                float2 d = new((float)System.Math.Cos(a), (float)System.Math.Sin(a));
                uint salt = (uint)r.Next();
                float2 p2 = p + d * h;
                float s2 = math.abs(SeededSimplex.Noise(p2, 7u, salt) - SeededSimplex.Noise(p, 7u, salt)) / math.length(p2 - p);
                float3 q = new(p.x, (float)(r.NextDouble() * 64), p.y);
                float3 q2 = q + new float3(d.x, 0f, d.y) * h;
                float s3 = math.abs(SeededSimplex.Noise(q2, 7u, salt) - SeededSimplex.Noise(q, 7u, salt)) / math.length(q2 - q);
                max2 = math.max(max2, s2);
                max3 = math.max(max3, s3);
            }

            Debug.Log($"[GraphSlopes] measured simplex slope per unit frequency: 2D {max2:0.000}, 3D across x/z {max3:0.000}; constant {GraphSlopes.SimplexSlope}");
            Assert.LessOrEqual(max2, GraphSlopes.SimplexSlope);
            Assert.LessOrEqual(max3, GraphSlopes.SimplexSlope);
            Assert.Greater(math.max(max2, max3), GraphSlopes.SimplexSlope * 0.75f, "the constant is not loose");
        }

        [Test]
        public void ShippedClimate_StepBetweenNeighbourColumns_StaysWithinEpsilon(){
            GraphCompileResult r = ShippedNoiseGraph.Compile();
            Assert.IsTrue(r.Succeeded, string.Join("\n", r.Messages));
            using CompiledNoiseGraph graph = r.Graph;
            float[] epsilon = BiomeLayoutValidator.Epsilon(graph);
            System.Random random = new(3);
            List<float2> points = new();
            for (int i = 0; i < 20000; i++){
                float2 p = new(random.Next(-200000, 200000), random.Next(-200000, 200000));
                points.Add(p);
                points.Add(p + new float2(1, 1));
            }

            float[] values = GraphEvaluator.SampleLattice(graph, 12345u, points.ToArray(), out _);
            float[] worst = new float[BiomeBox.Parameters];
            for (int d = 0; d < BiomeBox.Parameters; d++){
                int slot = graph.Output(BiomeLayoutValidator.ParameterOutput(d)).LatticeValue;
                for (int i = 0; i < points.Count; i += 2)
                    worst[d] = math.max(worst[d], math.abs(values[slot * points.Count + i + 1] - values[slot * points.Count + i]));
                Assert.IsTrue(float.IsFinite(epsilon[d]), $"ε of {BiomeBox.ParameterNames[d]} is finite");
                Assert.LessOrEqual(worst[d], epsilon[d], $"{BiomeBox.ParameterNames[d]}: largest diagonal step {worst[d]} against ε {epsilon[d]}");
            }

            Debug.Log("[GraphSlopes] climate ε (2·s) vs the largest measured diagonal step: " +
                      string.Join(", ", Enumerable.Range(0, BiomeBox.Parameters).Select(d => $"{BiomeBox.ShortNames[d]} {epsilon[d]:0.######} / {worst[d]:0.######}")));
        }
    }
}
