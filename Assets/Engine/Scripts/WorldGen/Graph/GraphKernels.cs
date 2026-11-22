using System.Runtime.CompilerServices;
using Engine.Scripts.WorldGen.Noise;
using Unity.Mathematics;

namespace Engine.Scripts.WorldGen.Graph{
    /// <summary>
    ///     The arithmetic of every operation, written once on four lanes and used by the direct evaluation, the
    ///     constant folding and the interpreter, so all three give the same bits. Only called from Burst jobs
    ///     (FloatMode.Strict): managed code never does graph float math.
    /// </summary>
    internal static unsafe class GraphKernels{
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float4 HalfNegative(float4 a){
            return math.select(a, a * 0.5f, a < 0f);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float4 QuarterNegative(float4 a){
            return math.select(a, a * 0.25f, a < 0f);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float4 SoftClamp(float4 a){
            float4 v = math.clamp(a, -1f, 1f);
            return 1.5f * v - 0.5f * (v * v * v);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float4 Lerp(float4 t, float4 a, float4 b){
            return a + t * (b - a);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float4 Smoothstep(float4 v, float from, float inverseSpan){
            float4 t = math.saturate((v - from) * inverseSpan);
            return t * t * (3f - 2f * t);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float4 RangeChoice(float4 v, float4 inRange, float4 outRange, float min, float max){
            return math.select(outRange, inRange, (v >= min) & (v < max));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float4 ClampedMap(float4 v, float from, float inverseSpan, float fromValue, float valueSpan){
            return fromValue + math.saturate((v - from) * inverseSpan) * valueSpan;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float4 Cube(float4 a){
            return a * a * a;
        }

        /// <summary>
        ///     Every operation except inputs, noise, spline and marker. K variants read the constant from P0; the
        ///     node form of an operation (both operands given) and its K form give the same bits.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float4 Apply(GraphOp op, float4 a, float4 b, float4 c, float p0, float p1, float p2, float p3){
            switch (op){
                case GraphOp.Const: return p0;
                case GraphOp.Add: return a + b;
                case GraphOp.Sub: return a - b;
                case GraphOp.Mul: return a * b;
                case GraphOp.Min: return math.min(a, b);
                case GraphOp.Max: return math.max(a, b);
                case GraphOp.AddK: return a + p0;
                case GraphOp.SubK: return a - p0;
                case GraphOp.KSub: return p0 - a;
                case GraphOp.MulK: return a * p0;
                case GraphOp.MinK: return math.min(a, p0);
                case GraphOp.MaxK: return math.max(a, p0);
                case GraphOp.Abs: return math.abs(a);
                case GraphOp.Neg: return -a;
                case GraphOp.Square: return a * a;
                case GraphOp.Cube: return Cube(a);
                case GraphOp.HalfNegative: return HalfNegative(a);
                case GraphOp.QuarterNegative: return QuarterNegative(a);
                case GraphOp.SoftClamp: return SoftClamp(a);
                case GraphOp.Clamp: return math.clamp(a, p0, p1);
                case GraphOp.Lerp: return Lerp(a, b, c);
                case GraphOp.Smoothstep: return Smoothstep(a, p0, p1);
                case GraphOp.RangeChoice: return RangeChoice(a, b, c, p0, p1);
                case GraphOp.ClampedMap: return ClampedMap(a, p0, p1, p2, p3);
                case GraphOp.Marker: return a;
                default: return float4.zero;
            }
        }

        /// <summary>
        ///     A spline at coordinate <paramref name="c" />: the first value below the first location, the last value
        ///     at or above the last one, and in between the cubic segment with fixed derivatives
        ///     v = lerp(v0, v1, t) + t (1 − t) lerp(g, h, t) with g = d0·span − Δ and h = Δ − d1·span (linear: the
        ///     plain lerp). <paramref name="values" /> holds each point's value on the four lanes.
        /// </summary>
        public static float4 Spline(float4 c, SplinePoint* points, float4* values, int count, bool linear){
            float4 result = values[0];
            for (int k = 0; k < count - 1; k++){
                bool4 at = c >= points[k].Location;
                if (!math.any(at)) return result;
                SplinePoint p0 = points[k];
                float4 v0 = values[k];
                float4 dv = values[k + 1] - v0;
                float4 t = (c - p0.Location) * p0.InverseSpan;
                float4 segment = v0 + t * dv;
                if (!linear){
                    float4 g = p0.Derivative * p0.Span - dv;
                    float4 h = dv - points[k + 1].Derivative * p0.Span;
                    segment += t * (1f - t) * (g + t * (h - g));
                }

                result = math.select(result, segment, at);
            }

            return math.select(result, values[count - 1], c >= points[count - 1].Location);
        }

        /// <summary>
        ///     A noise entry on four positions through v1's scalar SeededSimplex: the reference the lane kernel must
        ///     match bit for bit. Octaves accumulate as acc + n · amp (ridged: acc + (1 − |n|) · amp), from 0.
        /// </summary>
        public static float4 NoiseReference(in NoiseEntry entry, NoiseOctave* octaves, float4 x, float4 y, float4 z, uint seed){
            float4 acc = float4.zero;
            for (int o = 0; o < entry.OctaveCount; o++){
                NoiseOctave oct = octaves[entry.OctaveStart + o];
                float4 px = x * oct.FrequencyXZ;
                float4 pz = z * oct.FrequencyXZ;
                float4 n;
                if (entry.Dimensions == 2){
                    n = new float4(
                        SeededSimplex.Noise(new float2(px.x, pz.x), seed, oct.Salt),
                        SeededSimplex.Noise(new float2(px.y, pz.y), seed, oct.Salt),
                        SeededSimplex.Noise(new float2(px.z, pz.z), seed, oct.Salt),
                        SeededSimplex.Noise(new float2(px.w, pz.w), seed, oct.Salt));
                }
                else{
                    float4 py = y * oct.FrequencyY;
                    n = new float4(
                        SeededSimplex.Noise(new float3(px.x, py.x, pz.x), seed, oct.Salt),
                        SeededSimplex.Noise(new float3(px.y, py.y, pz.y), seed, oct.Salt),
                        SeededSimplex.Noise(new float3(px.z, py.z, pz.z), seed, oct.Salt),
                        SeededSimplex.Noise(new float3(px.w, py.w, pz.w), seed, oct.Salt));
                }

                acc = entry.Mode == NoiseMode.Ridged ? acc + (1f - math.abs(n)) * oct.Amplitude : acc + n * oct.Amplitude;
            }

            return acc;
        }

        /// <summary>
        ///     Node <paramref name="i" /> of a node list on four positions, from the values of the nodes before it
        ///     (direct evaluation: markers are the identity, noise goes through the scalar reference).
        /// </summary>
        public static float4 EvaluateNode(int i, GraphNode* nodes, float4* values, SplineInfo* splines, SplinePoint* points,
            NoiseEntry* entries, NoiseOctave* octaves, float4 x, float4 y, float4 z, uint seed, float4* splineValues){
            GraphNode n = nodes[i];
            switch (n.Op){
                case GraphOp.X: return x;
                case GraphOp.Y: return y;
                case GraphOp.Z: return z;
                case GraphOp.Noise:{
                    float4 px = n.A >= 0 ? x + values[n.A] : x;
                    float4 py = n.B >= 0 ? y + values[n.B] : y;
                    float4 pz = n.C >= 0 ? z + values[n.C] : z;
                    return NoiseReference(entries[n.Aux], octaves, px, py, pz, seed);
                }
                case GraphOp.Spline:{
                    SplineInfo s = splines[n.Aux];
                    for (int k = 0; k < s.PointCount; k++){
                        SplinePoint p = points[s.PointStart + k];
                        splineValues[k] = p.Value >= 0 ? values[p.Value] : p.Constant;
                    }

                    return Spline(values[n.A], points + s.PointStart, splineValues, s.PointCount, s.Linear != 0);
                }
                default:
                    return Apply(n.Op, n.A >= 0 ? values[n.A] : float4.zero, n.B >= 0 ? values[n.B] : float4.zero,
                        n.C >= 0 ? values[n.C] : float4.zero, n.P0, n.P1, n.P2, n.P3);
            }
        }
    }
}
