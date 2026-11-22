using Engine.Scripts.WorldGen.Core;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;

namespace Engine.Scripts.WorldGen.Graph{
    /// <summary>A noise entry's source data: the definition's numbers, the octave count it uses and its name salt.</summary>
    internal struct NoiseSource{
        public int Octaves;
        public float Wavelength, WavelengthY, Lacunarity, Gain, Amplitude;
        public uint Salt;
    }

    /// <summary>
    ///     Compile-time float work of a node list, in Burst (the compiler does no float math in managed code):
    ///     octave tables, prepared node constants (1 / span of smoothstep and y_gradient), spline segment spans and
    ///     derived tangents, and the value of every node at the origin, which is the folded value of every
    ///     constant node.
    /// </summary>
    [BurstCompile(FloatMode = FloatMode.Strict, CompileSynchronously = true)]
    internal unsafe struct GraphPrepareJob : IJob{
        public NativeArray<GraphNode> Nodes;
        public NativeArray<SplinePoint> Points;
        [ReadOnly] public NativeArray<SplineInfo> Splines;
        [ReadOnly] public NativeArray<NoiseSource> Sources;
        [ReadOnly] public NativeArray<NoiseEntry> Entries;
        public NativeArray<NoiseOctave> Octaves;

        /// <summary>1 for nodes that depend on no input and no noise (their value is the same everywhere).</summary>
        [ReadOnly] public NativeArray<byte> Constant;

        /// <summary>Value of every node at (0, 0, 0).</summary>
        [WriteOnly] public NativeArray<float> Values;

        public void Execute(){
            for (int e = 0; e < Entries.Length; e++) FillOctaves(Entries[e], Sources[e]);

            GraphNode* nodes = (GraphNode*)Nodes.GetUnsafePtr();
            SplinePoint* points = (SplinePoint*)Points.GetUnsafePtr();
            SplineInfo* splines = (SplineInfo*)Splines.GetUnsafeReadOnlyPtr();
            NoiseEntry* entries = (NoiseEntry*)Entries.GetUnsafeReadOnlyPtr();
            NoiseOctave* octaves = (NoiseOctave*)Octaves.GetUnsafePtr();
            float4* values = (float4*)UnsafeUtility.Malloc(sizeof(float4) * math.max(1, Nodes.Length), 16, Allocator.Temp);
            float4* splineValues = stackalloc float4[GraphKernels.SplineScratch];

            for (int i = 0; i < Nodes.Length; i++){
                ref GraphNode n = ref nodes[i];
                switch (n.Op){
                    case GraphOp.Smoothstep:
                        n.P1 = 1f / (n.P1 - n.P0);
                        break;
                    case GraphOp.ClampedMap:
                        n.P1 = 1f / (n.P1 - n.P0);
                        n.P3 = n.P3 - n.P2;
                        break;
                    case GraphOp.Spline:
                        PrepareSpline(splines[n.Aux], points, values);
                        break;
                }

                values[i] = GraphKernels.EvaluateNode(i, nodes, values, splines, points, entries, octaves, float4.zero, float4.zero,
                    float4.zero, 0u, splineValues);
                Values[i] = values[i].x;
            }

            UnsafeUtility.Free(values, Allocator.Temp);
        }

        private void FillOctaves(NoiseEntry entry, NoiseSource source){
            float sum = 0f;
            float a = 1f;
            for (int o = 0; o < entry.OctaveCount; o++){
                sum += a;
                a *= source.Gain;
            }

            float scale = source.Amplitude / sum;
            float f = 1f / source.Wavelength;
            float fy = 1f / source.WavelengthY;
            a = 1f;
            for (int o = 0; o < entry.OctaveCount; o++){
                Octaves[entry.OctaveStart + o] = new NoiseOctave{
                    FrequencyXZ = f,
                    FrequencyY = fy,
                    Amplitude = a * scale,
                    Salt = HashSalts.FbmOctave(source.Salt, o)
                };
                f *= source.Lacunarity;
                fy *= source.Lacunarity;
                a *= source.Gain;
            }
        }

        /// <summary>
        ///     Segment spans and, when no point gives a derivative and every value is constant, Fritsch–Carlson
        ///     tangents from the constant values (as NestedSpline's MonotoneCubic); otherwise a missing derivative is 0.
        /// </summary>
        private void PrepareSpline(SplineInfo s, SplinePoint* all, float4* values){
            SplinePoint* p = all + s.PointStart;
            int n = s.PointCount;
            for (int k = 0; k < n; k++){
                if (k < n - 1){
                    p[k].Span = p[k + 1].Location - p[k].Location;
                    p[k].InverseSpan = 1f / p[k].Span;
                }
                else{
                    p[k].Span = 0f;
                    p[k].InverseSpan = 0f;
                }
            }

            if (s.Mode != SplineMode.Cubic || n < 2) return;
            for (int k = 0; k < n; k++){
                if (p[k].HasDerivative != 0) return;
                if (p[k].Value >= 0 && Constant[p[k].Value] == 0) return;
            }

            float* y = stackalloc float[GraphLimits.MaxSplinePoints];
            float* d = stackalloc float[GraphLimits.MaxSplinePoints];
            float* m = stackalloc float[GraphLimits.MaxSplinePoints];
            for (int k = 0; k < n; k++) y[k] = p[k].Value >= 0 ? values[p[k].Value].x : p[k].Constant;
            for (int k = 0; k < n - 1; k++) d[k] = (y[k + 1] - y[k]) / p[k].Span;
            m[0] = d[0];
            for (int k = 1; k < n - 1; k++) m[k] = d[k - 1] * d[k] > 0f ? 0.5f * (d[k - 1] + d[k]) : 0f;
            m[n - 1] = d[n - 2];
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

            for (int k = 0; k < n; k++) p[k].Derivative = m[k];
        }
    }

    /// <summary>
    ///     Direct, uncompiled evaluation of a node list at a list of positions: every node in order, markers as the
    ///     identity, noise through the scalar SeededSimplex. The reference the compiled programs are tested against.
    /// </summary>
    [BurstCompile(FloatMode = FloatMode.Strict, CompileSynchronously = true)]
    internal unsafe struct GraphDirectJob : IJob{
        [ReadOnly] public NativeArray<GraphNode> Nodes;
        [ReadOnly] public NativeArray<SplineInfo> Splines;
        [ReadOnly] public NativeArray<SplinePoint> Points;
        [ReadOnly] public NativeArray<NoiseEntry> Entries;
        [ReadOnly] public NativeArray<NoiseOctave> Octaves;
        [ReadOnly] public NativeArray<float3> Positions;

        /// <summary>Nodes whose values are written, per position.</summary>
        [ReadOnly] public NativeArray<int> Select;

        public uint Seed;

        /// <summary>Positions.Length × Select.Length values, position-major.</summary>
        [WriteOnly] public NativeArray<float> Values;

        public void Execute(){
            GraphNode* nodes = (GraphNode*)Nodes.GetUnsafeReadOnlyPtr();
            SplineInfo* splines = (SplineInfo*)Splines.GetUnsafeReadOnlyPtr();
            SplinePoint* points = (SplinePoint*)Points.GetUnsafeReadOnlyPtr();
            NoiseEntry* entries = (NoiseEntry*)Entries.GetUnsafeReadOnlyPtr();
            NoiseOctave* octaves = (NoiseOctave*)Octaves.GetUnsafeReadOnlyPtr();
            float4* values = (float4*)UnsafeUtility.Malloc(sizeof(float4) * math.max(1, Nodes.Length), 16, Allocator.Temp);
            float4* splineValues = stackalloc float4[GraphKernels.SplineScratch];
            int count = Positions.Length;
            int select = Select.Length;

            for (int p = 0; p < count; p += 4){
                float3 p0 = Positions[p];
                float3 p1 = Positions[math.min(p + 1, count - 1)];
                float3 p2 = Positions[math.min(p + 2, count - 1)];
                float3 p3 = Positions[math.min(p + 3, count - 1)];
                float4 x = new(p0.x, p1.x, p2.x, p3.x);
                float4 y = new(p0.y, p1.y, p2.y, p3.y);
                float4 z = new(p0.z, p1.z, p2.z, p3.z);
                for (int i = 0; i < Nodes.Length; i++)
                    values[i] = GraphKernels.EvaluateNode(i, nodes, values, splines, points, entries, octaves, x, y, z, Seed, splineValues);

                for (int l = 0; l < 4 && p + l < count; l++)
                for (int s = 0; s < select; s++)
                    Values[(p + l) * select + s] = values[Select[s]][l];
            }

            UnsafeUtility.Free(values, Allocator.Temp);
        }
    }

    /// <summary>
    ///     Interval rules: bounds of every node from the bounds of its operands, in Burst. Every bound except a
    ///     constant's is widened by <see cref="GraphLimits.BoundsMargin" /> · max(1, |bound|), so a value one ulp
    ///     off never falls outside.
    /// </summary>
    [BurstCompile(FloatMode = FloatMode.Strict, CompileSynchronously = true)]
    internal unsafe struct GraphBoundsJob : IJob{
        [ReadOnly] public NativeArray<GraphNode> Nodes;
        [ReadOnly] public NativeArray<SplineInfo> Splines;
        [ReadOnly] public NativeArray<SplinePoint> Points;
        [ReadOnly] public NativeArray<NoiseEntry> Entries;
        [ReadOnly] public NativeArray<NoiseOctave> Octaves;
        public NativeArray<GraphBounds> Bounds;

        public void Execute(){
            for (int i = 0; i < Nodes.Length; i++){
                GraphNode n = Nodes[i];
                GraphBounds b = n.Op switch{
                    GraphOp.Const => new GraphBounds(n.P0, n.P0),
                    _ => Widen(Rule(n))
                };
                Bounds[i] = b;
            }
        }

        private GraphBounds Get(int node){
            return Bounds[node];
        }

        private GraphBounds Rule(GraphNode n){
            const float coordinate = GraphLimits.CoordinateLimit;
            switch (n.Op){
                case GraphOp.X:
                case GraphOp.Z:
                    return new GraphBounds(-coordinate, coordinate);
                case GraphOp.Y:
                    return new GraphBounds(0f, GraphLimits.WorldHeight);
                case GraphOp.Noise:{
                    NoiseEntry e = Entries[n.Aux];
                    float lo = 0f, hi = 0f;
                    for (int o = 0; o < e.OctaveCount; o++){
                        float amp = Octaves[e.OctaveStart + o].Amplitude;
                        if (e.Mode == NoiseMode.Ridged){
                            lo += math.min(0f, amp);
                            hi += math.max(0f, amp);
                        }
                        else{
                            lo -= math.abs(amp);
                            hi += math.abs(amp);
                        }
                    }

                    return new GraphBounds(lo, hi);
                }
                case GraphOp.Add:{
                    GraphBounds a = Get(n.A), b = Get(n.B);
                    return new GraphBounds(a.Min + b.Min, a.Max + b.Max);
                }
                case GraphOp.Sub:{
                    GraphBounds a = Get(n.A), b = Get(n.B);
                    return new GraphBounds(a.Min - b.Max, a.Max - b.Min);
                }
                case GraphOp.Mul:{
                    GraphBounds a = Get(n.A), b = Get(n.B);
                    float p0 = a.Min * b.Min, p1 = a.Min * b.Max, p2 = a.Max * b.Min, p3 = a.Max * b.Max;
                    return new GraphBounds(math.min(math.min(p0, p1), math.min(p2, p3)), math.max(math.max(p0, p1), math.max(p2, p3)));
                }
                case GraphOp.Min:{
                    GraphBounds a = Get(n.A), b = Get(n.B);
                    return new GraphBounds(math.min(a.Min, b.Min), math.min(a.Max, b.Max));
                }
                case GraphOp.Max:{
                    GraphBounds a = Get(n.A), b = Get(n.B);
                    return new GraphBounds(math.max(a.Min, b.Min), math.max(a.Max, b.Max));
                }
                case GraphOp.Abs:{
                    GraphBounds a = Get(n.A);
                    if (a.Min >= 0f) return a;
                    if (a.Max <= 0f) return new GraphBounds(-a.Max, -a.Min);
                    return new GraphBounds(0f, math.max(-a.Min, a.Max));
                }
                case GraphOp.Neg:{
                    GraphBounds a = Get(n.A);
                    return new GraphBounds(-a.Max, -a.Min);
                }
                case GraphOp.Square:{
                    GraphBounds a = Get(n.A);
                    float lo2 = a.Min * a.Min, hi2 = a.Max * a.Max;
                    if (a.Min >= 0f) return new GraphBounds(lo2, hi2);
                    if (a.Max <= 0f) return new GraphBounds(hi2, lo2);
                    return new GraphBounds(0f, math.max(lo2, hi2));
                }
                case GraphOp.Cube:{
                    GraphBounds a = Get(n.A);
                    return new GraphBounds(GraphKernels.Cube(a.Min).x, GraphKernels.Cube(a.Max).x);
                }
                case GraphOp.HalfNegative:
                case GraphOp.QuarterNegative:
                case GraphOp.SoftClamp:
                case GraphOp.Clamp:
                case GraphOp.Smoothstep:
                case GraphOp.ClampedMap:{
                    // Monotone in A (smoothstep and y_gradient may be decreasing): the ends map to the ends.
                    GraphBounds a = Get(n.A);
                    float lo = GraphKernels.Apply(n.Op, a.Min, 0f, 0f, n.P0, n.P1, n.P2, n.P3).x;
                    float hi = GraphKernels.Apply(n.Op, a.Max, 0f, 0f, n.P0, n.P1, n.P2, n.P3).x;
                    return new GraphBounds(math.min(lo, hi), math.max(lo, hi));
                }
                case GraphOp.Lerp:{
                    // b + t (c − b) is linear in each of t, b and c: the extremes lie on the corners of the box.
                    GraphBounds t = Get(n.A), b = Get(n.B), c = Get(n.C);
                    float lo = float.PositiveInfinity, hi = float.NegativeInfinity;
                    for (int corner = 0; corner < 8; corner++){
                        float tv = (corner & 1) == 0 ? t.Min : t.Max;
                        float bv = (corner & 2) == 0 ? b.Min : b.Max;
                        float cv = (corner & 4) == 0 ? c.Min : c.Max;
                        float v = GraphKernels.Lerp(tv, bv, cv).x;
                        lo = math.min(lo, v);
                        hi = math.max(hi, v);
                    }

                    return new GraphBounds(lo, hi);
                }
                case GraphOp.RangeChoice:{
                    GraphBounds a = Get(n.A), inRange = Get(n.B), outRange = Get(n.C);
                    if (a.Min >= n.P0 && a.Max < n.P1) return inRange;
                    if (a.Max < n.P0 || a.Min >= n.P1) return outRange;
                    return new GraphBounds(math.min(inRange.Min, outRange.Min), math.max(inRange.Max, outRange.Max));
                }
                case GraphOp.Spline:
                    return SplineRule(n);
                case GraphOp.Marker:
                    return Get(n.A);
                default:
                    return new GraphBounds(float.NegativeInfinity, float.PositiveInfinity);
            }
        }

        /// <summary>
        ///     Hull of the point values of every segment the coordinate's bound reaches. A cubic segment adds its
        ///     overshoot: the Hermite terms t (1 − t)² ≤ 4/27 and t² (1 − t) ≤ 4/27 times span · derivative.
        /// </summary>
        private GraphBounds SplineRule(GraphNode n){
            SplineInfo s = Splines[n.Aux];
            GraphBounds c = Get(n.A);
            int count = s.PointCount;
            float lo = float.PositiveInfinity, hi = float.NegativeInfinity;
            SplinePoint first = Points[s.PointStart];
            SplinePoint last = Points[s.PointStart + count - 1];
            if (count == 1 || c.Min < first.Location){
                GraphBounds v = ValueBounds(first);
                lo = math.min(lo, v.Min);
                hi = math.max(hi, v.Max);
            }

            if (c.Max >= last.Location){
                GraphBounds v = ValueBounds(last);
                lo = math.min(lo, v.Min);
                hi = math.max(hi, v.Max);
            }

            for (int k = 0; k < count - 1; k++){
                SplinePoint p0 = Points[s.PointStart + k];
                SplinePoint p1 = Points[s.PointStart + k + 1];
                if (c.Max < p0.Location || c.Min >= p1.Location) continue;
                GraphBounds v0 = ValueBounds(p0), v1 = ValueBounds(p1);
                float segLo = math.min(v0.Min, v1.Min), segHi = math.max(v0.Max, v1.Max);
                if (s.Mode == SplineMode.Cubic){
                    float o = 4f / 27f * p0.Span;
                    float a = o * p0.Derivative, b = -o * p1.Derivative;
                    segLo += math.min(0f, a) + math.min(0f, b);
                    segHi += math.max(0f, a) + math.max(0f, b);
                }

                lo = math.min(lo, segLo);
                hi = math.max(hi, segHi);
            }

            return new GraphBounds(lo, hi);
        }

        private GraphBounds ValueBounds(SplinePoint p){
            return p.Value >= 0 ? Get(p.Value) : new GraphBounds(p.Constant, p.Constant);
        }

        private static GraphBounds Widen(GraphBounds b){
            float m = GraphLimits.BoundsMargin * math.max(1f, math.max(math.abs(b.Min), math.abs(b.Max)));
            return new GraphBounds(b.Min - m, b.Max + m);
        }
    }
}
