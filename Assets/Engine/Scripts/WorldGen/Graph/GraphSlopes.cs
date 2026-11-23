using System;
using Unity.Mathematics;

namespace Engine.Scripts.WorldGen.Graph{
    /// <summary>
    ///     Upper bounds on the slope per block across x and z of every node of a compiled graph (WorldGen v2 §6.6): the
    ///     layout validator's ε = 2 · s says how far a parameter can move between neighbouring columns, diagonal ones
    ///     included. Rules: a noise is Σ amplitude · <see cref="SimplexSlope" /> · frequency over its octaves, times
    ///     1 + the slopes of its warp offsets; a sum adds slopes, a product uses the operands' bounds, a curve
    ///     multiplies by its largest slope. Bilinear interpolation between lattice points keeps the bound. A node
    ///     whose slope has no finite bound (a range choice, a jump) gets +∞, and the validator then says so.
    /// </summary>
    public static unsafe class GraphSlopes{
        /// <summary>
        ///     The largest slope of <see cref="Noise.SeededSimplex" /> per unit of frequency (2D and 3D, across x and
        ///     z), with a margin. Measured once on random points; the test GraphSlopeTests keeps it.
        /// </summary>
        public const float SimplexSlope = 8f;

        /// <summary>Samples of a curve's coordinate range when its largest slope is measured.</summary>
        private const int CurveSamples = 4096;

        /// <summary>The slope bound of every node of the uncompiled node list.</summary>
        public static float[] Of(CompiledNoiseGraph graph){
            int count = graph.ReferenceNodeCount;
            float[] s = new float[count];
            for (int i = 0; i < count; i++) s[i] = Node(graph, i, s);
            return s;
        }

        /// <summary>The slope bound of the output <paramref name="name" />, +∞ when the graph has no such output.</summary>
        public static float OfOutput(CompiledNoiseGraph graph, float[] slopes, string name){
            GraphOutputInfo o = graph.Output(name);
            return o == null ? float.PositiveInfinity : slopes[o.ReferenceNode];
        }

        private static float Node(CompiledNoiseGraph graph, int i, float[] s){
            GraphNode n = graph.ReferenceNodes[i];
            float A() => n.A >= 0 ? s[n.A] : 0f;
            float B() => n.B >= 0 ? s[n.B] : 0f;
            float C() => n.C >= 0 ? s[n.C] : 0f;
            float Max(int node) => node >= 0 ? MaxAbs(graph.ReferenceBound(node)) : 0f;
            switch (n.Op){
                case GraphOp.Const: return 0f;
                case GraphOp.X:
                case GraphOp.Z: return 1f;
                case GraphOp.Y: return 0f;
                case GraphOp.Noise:{
                    NoiseEntry e = graph.Data.Entries[n.Aux];
                    float sum = 0f;
                    for (int o = 0; o < e.OctaveCount; o++){
                        NoiseOctave oct = graph.Data.Octaves[e.OctaveStart + o];
                        sum += math.abs(oct.Amplitude) * SimplexSlope * oct.FrequencyXZ;
                    }

                    // y offsets do not move with x or z unless their node does: only A (x) and C (z) count.
                    return sum * (1f + A() + C());
                }
                case GraphOp.Add:
                case GraphOp.Sub: return A() + B();
                case GraphOp.Mul: return Max(n.A) * B() + Max(n.B) * A();
                case GraphOp.Min:
                case GraphOp.Max: return math.max(A(), B());
                case GraphOp.MulK: return math.abs(n.P0) * A();
                case GraphOp.AddK:
                case GraphOp.SubK:
                case GraphOp.KSub:
                case GraphOp.MinK:
                case GraphOp.MaxK:
                case GraphOp.Abs:
                case GraphOp.Neg:
                case GraphOp.HalfNegative:
                case GraphOp.QuarterNegative:
                case GraphOp.Clamp:
                case GraphOp.Marker: return A();
                case GraphOp.Square: return 2f * Max(n.A) * A();
                case GraphOp.Cube: return 3f * Max(n.A) * Max(n.A) * A();
                case GraphOp.SoftClamp: return 1.5f * A();
                case GraphOp.Lerp: return A() * (Max(n.B) + Max(n.C)) + math.max(B(), C());
                case GraphOp.Smoothstep: return 1.5f * math.abs(n.P1) * A();
                case GraphOp.ClampedMap: return math.abs(n.P1 * n.P3) * A();
                case GraphOp.RangeChoice: return float.PositiveInfinity;
                case GraphOp.Spline: return Spline(graph, n, s);
                default: return float.PositiveInfinity;
            }
        }

        /// <summary>
        ///     A curve: its largest slope over the coordinate's bounds times the coordinate's slope, plus (for points whose
        ///     values are nodes) twice the largest slope of those values: the Hermite basis weights stay within 0…1 and
        ///     the tangents follow the values, so the factor 2 is a safe margin.
        /// </summary>
        private static float Spline(CompiledNoiseGraph graph, GraphNode n, float[] s){
            SplineInfo info = graph.ReferenceSplines[n.Aux];
            float valueSlope = 0f;
            bool constant = true;
            for (int k = 0; k < info.PointCount; k++){
                SplinePoint p = graph.ReferencePoints[info.PointStart + k];
                if (p.Value < 0) continue;
                constant = false;
                valueSlope = math.max(valueSlope, s[p.Value]);
            }

            GraphBounds c = graph.ReferenceBound(n.A);
            float curve = CurveSlope(graph, info, c, constant ? null : graph);
            return curve * s[n.A] + 2f * valueSlope;
        }

        /// <summary>
        ///     The largest |dv/dc| of the curve over <paramref name="c" /> by finite differences on
        ///     <see cref="CurveSamples" /> steps, with a 5 % margin. Points whose values are nodes take the middle of their
        ///     bounds (<paramref name="nested" /> set): the slope of the coordinate then is an estimate, not a bound.
        /// </summary>
        private static float CurveSlope(CompiledNoiseGraph graph, SplineInfo info, GraphBounds c, CompiledNoiseGraph nested){
            if (info.PointCount < 2) return 0f;
            float lo = c.Min, hi = c.Max;
            SplinePoint first = graph.ReferencePoints[info.PointStart], last = graph.ReferencePoints[info.PointStart + info.PointCount - 1];
            lo = math.max(lo, first.Location);
            hi = math.min(hi, last.Location);
            if (!(hi > lo)) return 0f;
            if (float.IsInfinity(lo) || float.IsInfinity(hi)) return float.PositiveInfinity;
            float4* values = stackalloc float4[GraphKernels.SplineScratch];
            SplinePoint* points = stackalloc SplinePoint[info.PointCount];
            for (int k = 0; k < info.PointCount; k++){
                SplinePoint p = graph.ReferencePoints[info.PointStart + k];
                points[k] = p;
                float v = p.Constant;
                if (p.Value >= 0 && nested != null){
                    GraphBounds b = graph.ReferenceBound(p.Value);
                    v = 0.5f * (b.Min + b.Max);
                }

                values[k] = v;
            }

            float step = (hi - lo) / CurveSamples, best = 0f;
            float previous = GraphKernels.Spline(lo, points, values, info.PointCount, info.Mode).x;
            for (int i = 1; i <= CurveSamples; i++){
                float v = GraphKernels.Spline(lo + i * step, points, values, info.PointCount, info.Mode).x;
                best = math.max(best, math.abs(v - previous) / step);
                previous = v;
            }

            return best * 1.05f;
        }

        private static float MaxAbs(GraphBounds b){
            return math.max(math.abs(b.Min), math.abs(b.Max));
        }
    }
}
