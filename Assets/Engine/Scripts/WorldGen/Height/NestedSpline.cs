using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;

namespace Engine.Scripts.WorldGen.Height{
    /// <summary>Axis a spline is defined over (TRN-01): C and E over [0,1], PV over [-1,1].</summary>
    public enum SplineAxis : byte{
        Continental = 0,
        Erosion = 1,
        PeaksValleys = 2
    }

    /// <summary>Per-spline interpolation (TRN-02).</summary>
    public enum SplineInterpolation : byte{
        /// <summary>Monotone cubic Hermite with Fritsch–Carlson tangents (default).</summary>
        MonotoneCubic = 0,

        /// <summary>Piecewise linear.</summary>
        Linear = 1
    }

    /// <summary>
    ///     One spline of the flattened tree: its axis, interpolation and the range of its control points in the
    ///     shared point array. Children of a node always have a higher node index than the node (pre-order), which
    ///     is what <see cref="NestedSpline.Evaluate" /> relies on.
    /// </summary>
    public struct SplineNode{
        public SplineAxis Axis;
        public SplineInterpolation Interpolation;

        /// <summary>First control point in <c>WorldGenConfig.SplinePoints</c>.</summary>
        public int PointStart;

        /// <summary>Number of control points (1 = constant).</summary>
        public int PointCount;
    }

    /// <summary>A control point: position on the parent's axis and either a constant value or a child spline.</summary>
    public struct SplinePoint{
        public float X;

        /// <summary>Constant value; ignored when <see cref="Child" /> is set.</summary>
        public float Value;

        /// <summary>Absolute index of the child spline in <c>WorldGenConfig.SplineNodes</c>, or -1 for a constant.</summary>
        public int Child;
    }

    /// <summary>A root spline (Offset, Amp or J): the pre-ordered node range of its tree.</summary>
    public struct SplineRoot{
        public int NodeStart;

        /// <summary>0 when the root is absent; the evaluation then returns 0.</summary>
        public int NodeCount;
    }

    /// <summary>
    ///     Nested spline evaluation (TRN-01, TRN-02): a spline over one axis whose control-point values are constants
    ///     or further splines over other axes, in the style of the Minecraft 1.18+ terrain shaper (offset/factor
    ///     splines documented on the Minecraft Wiki, "World generation"), reduced to the 2D case C → E → PV.
    ///     Interpolation is monotone cubic Hermite after Fritsch &amp; Carlson (1980), as described on Wikipedia,
    ///     "Monotone cubic interpolation", or piecewise linear per spline. A spline returns its end value outside the
    ///     first/last control point. Every spline is C0, so the height field is continuous by construction.
    ///     The jaggedness spline J is an own extension (TRN-04); Minecraft has no 2D equivalent of it.
    /// </summary>
    [BurstCompile]
    public static class NestedSpline{
        /// <summary>Maximum control points per spline (the per-column tangent scratch is a fixed list).</summary>
        public const int MaxPoints = 16;

        /// <summary>Maximum splines in one root tree (the per-column value scratch is a fixed list).</summary>
        public const int MaxNodesPerRoot = 127;

        /// <summary>
        ///     Evaluates <paramref name="root" /> at (C, E, PV). Nodes are evaluated from the last to the first of
        ///     the root's range, so every child value is known when its parent reads it (children follow parents).
        /// </summary>
        public static float Evaluate(in SplineRoot root, float c, float e, float pv,
            in NativeArray<SplineNode> nodes, in NativeArray<SplinePoint> points){
            if (root.NodeCount <= 0) return 0f;

            FixedList512Bytes<float> values = new();
            values.Length = root.NodeCount;
            for (int n = root.NodeCount - 1; n >= 0; n--){
                SplineNode node = nodes[root.NodeStart + n];
                float x = node.Axis switch{
                    SplineAxis.Continental => c,
                    SplineAxis.Erosion => e,
                    _ => pv
                };
                values[n] = EvaluateNode(node, x, points, ref values, root.NodeStart);
            }

            return values[0];
        }

        private static float EvaluateNode(in SplineNode node, float x, in NativeArray<SplinePoint> points,
            ref FixedList512Bytes<float> values, int nodeStart){
            int n = math.min(node.PointCount, MaxPoints);
            if (n <= 0) return 0f;

            // Control-point values: constants or the already evaluated child splines.
            FixedList128Bytes<float> y = new();
            for (int k = 0; k < n; k++){
                SplinePoint p = points[node.PointStart + k];
                y.Add(p.Child >= 0 ? values[p.Child - nodeStart] : p.Value);
            }

            if (n == 1) return y[0];

            // TRN-02: end-value clamping outside the control range.
            if (x <= points[node.PointStart].X) return y[0];
            if (x >= points[node.PointStart + n - 1].X) return y[n - 1];

            int k0 = 0;
            while (k0 < n - 2 && x >= points[node.PointStart + k0 + 1].X) k0++;
            float xk = points[node.PointStart + k0].X;
            float xk1 = points[node.PointStart + k0 + 1].X;
            float h = xk1 - xk;
            float t = (x - xk) / h;

            if (node.Interpolation == SplineInterpolation.Linear) return math.lerp(y[k0], y[k0 + 1], t);

            // Fritsch–Carlson tangents. The limiter runs over every interval because the limiting of interval k
            // may change m_{k+1}, which interval k+1 then reads; n is at most MaxPoints, so this is cheap.
            FixedList128Bytes<float> d = new();
            for (int k = 0; k < n - 1; k++){
                float dx = points[node.PointStart + k + 1].X - points[node.PointStart + k].X;
                d.Add((y[k + 1] - y[k]) / dx);
            }

            FixedList128Bytes<float> m = new();
            m.Add(d[0]);
            for (int k = 1; k < n - 1; k++) m.Add(d[k - 1] * d[k] > 0f ? 0.5f * (d[k - 1] + d[k]) : 0f);
            m.Add(d[n - 2]);

            for (int k = 0; k < n - 1; k++){
                if (d[k] == 0f){
                    m[k] = 0f;
                    m[k + 1] = 0f;
                    continue;
                }

                float alpha = m[k] / d[k];
                float beta = m[k + 1] / d[k];
                float r2 = alpha * alpha + beta * beta;
                if (r2 > 9f){
                    float tau = 3f / math.sqrt(r2);
                    m[k] = tau * alpha * d[k];
                    m[k + 1] = tau * beta * d[k];
                }
            }

            float t2 = t * t;
            float t3 = t2 * t;
            float h00 = 2f * t3 - 3f * t2 + 1f;
            float h10 = t3 - 2f * t2 + t;
            float h01 = -2f * t3 + 3f * t2;
            float h11 = t3 - t2;
            return h00 * y[k0] + h10 * h * m[k0] + h01 * y[k0 + 1] + h11 * h * m[k0 + 1];
        }
    }
}
