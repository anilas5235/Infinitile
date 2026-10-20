using System.Collections.Generic;
using Engine.Scripts.WorldGen.Height;
using Engine.Scripts.WorldGen.Settings;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Engine.Editor.WorldGen{
    /// <summary>
    ///     TRN-06: 1D plots of every spline level of a <see cref="TerrainSplineSet" />. Each flattened node is
    ///     evaluated with its sub-splines (<see cref="FlatSpline.Subtree" />) along its own axis while the other two
    ///     axes stay at the probe (C, E, PV); the probe value and the control points are marked. The evaluation runs
    ///     in a Burst job with strict float mode, like the sampler (GEN-16).
    /// </summary>
    public static class SplinePlots{
        /// <summary>One spline level.</summary>
        public class Plot{
            public FlatSpline Node;
            public string Name;
            public SplineAxis Axis;

            /// <summary>Axis range: [0,1] for C and E, [-1,1] for PV.</summary>
            public float2 AxisRange;

            /// <summary>Values at evenly spaced positions over <see cref="AxisRange" />.</summary>
            public float[] Curve;

            /// <summary>Control points (X, value at the probe); child points carry their child's value.</summary>
            public float2[] Points;

            /// <summary>Which control points take their value from a child spline.</summary>
            public bool[] PointIsChild;

            /// <summary>Probe position on this spline's axis and the value there.</summary>
            public float2 Probe;

            /// <summary>Min and max of the curve and the points.</summary>
            public float2 ValueRange;
        }

        /// <summary>
        ///     Plots every spline level. Returns an empty list and an error when the set does not flatten
        ///     (<see cref="TerrainSplineSet.Validate" />).
        /// </summary>
        public static List<Plot> Compute(TerrainSplineSet set, float3 probe, int samples, out string error){
            List<Plot> plots = new();
            error = null;
            if (!set){
                error = "No TerrainSplineSet assigned.";
                return plots;
            }

            List<string> problems = set.Validate();
            if (problems.Count > 0){
                error = string.Join(" ", problems);
                return plots;
            }

            samples = math.max(2, samples);
            List<FlatSpline> order = new();
            set.Flatten(Allocator.TempJob, out NativeArray<SplineNode> nodes, out NativeArray<SplinePoint> points,
                out _, out _, out _, order);
            NativeArray<SplineRoot> subtrees = new(order.Count, Allocator.TempJob);
            for (int i = 0; i < order.Count; i++) subtrees[i] = order[i].Subtree;
            NativeArray<float> curve = new(order.Count * samples, Allocator.TempJob);
            NativeArray<float> pointValues = new(order.Count * NestedSpline.MaxPoints, Allocator.TempJob);
            NativeArray<float> probeValues = new(order.Count, Allocator.TempJob);
            try{
                new PlotJob{
                    Nodes = nodes, Points = points, Subtrees = subtrees, Samples = samples, Probe = probe,
                    Curve = curve, PointValues = pointValues, ProbeValues = probeValues
                }.Schedule(order.Count, 1).Complete();

                for (int i = 0; i < order.Count; i++){
                    SplineNode node = nodes[order[i].Subtree.NodeStart];
                    float2 range = AxisRange(node.Axis);
                    Plot plot = new(){
                        Node = order[i],
                        Name = set.Splines[order[i].Source].Name,
                        Axis = node.Axis,
                        AxisRange = range,
                        Curve = new float[samples],
                        Points = new float2[node.PointCount],
                        PointIsChild = new bool[node.PointCount],
                        Probe = new float2(Coordinate(node.Axis, probe), probeValues[i])
                    };
                    if (string.IsNullOrEmpty(plot.Name)) plot.Name = $"{order[i].Tree} #{order[i].Source}";
                    float2 values = new(float.MaxValue, float.MinValue);
                    for (int s = 0; s < samples; s++){
                        float v = curve[i * samples + s];
                        plot.Curve[s] = v;
                        values = new float2(math.min(values.x, v), math.max(values.y, v));
                    }

                    for (int k = 0; k < node.PointCount; k++){
                        SplinePoint p = points[node.PointStart + k];
                        float v = pointValues[i * NestedSpline.MaxPoints + k];
                        plot.Points[k] = new float2(p.X, v);
                        plot.PointIsChild[k] = p.Child >= 0;
                        values = new float2(math.min(values.x, v), math.max(values.y, v));
                    }

                    plot.ValueRange = values;
                    plots.Add(plot);
                }
            }
            finally{
                nodes.Dispose();
                points.Dispose();
                subtrees.Dispose();
                curve.Dispose();
                pointValues.Dispose();
                probeValues.Dispose();
            }

            return plots;
        }

        /// <summary>Axis range of a spline axis (TRN-01).</summary>
        public static float2 AxisRange(SplineAxis axis){
            return axis == SplineAxis.PeaksValleys ? new float2(-1f, 1f) : new float2(0f, 1f);
        }

        /// <summary>Position of <paramref name="probe" /> (C, E, PV) on <paramref name="axis" />.</summary>
        public static float Coordinate(SplineAxis axis, float3 probe){
            return axis switch{
                SplineAxis.Continental => probe.x,
                SplineAxis.Erosion => probe.y,
                _ => probe.z
            };
        }

        private static float3 WithCoordinate(SplineAxis axis, float3 probe, float x){
            return axis switch{
                SplineAxis.Continental => new float3(x, probe.y, probe.z),
                SplineAxis.Erosion => new float3(probe.x, x, probe.z),
                _ => new float3(probe.x, probe.y, x)
            };
        }

        /// <summary>One flattened node per index: its curve, the values at its control points and at the probe.</summary>
        [BurstCompile(CompileSynchronously = true, FloatMode = FloatMode.Strict)]
        private struct PlotJob : IJobParallelFor{
            [ReadOnly] public NativeArray<SplineNode> Nodes;
            [ReadOnly] public NativeArray<SplinePoint> Points;
            [ReadOnly] public NativeArray<SplineRoot> Subtrees;
            public int Samples;
            public float3 Probe;

            [NativeDisableParallelForRestriction] [WriteOnly]
            public NativeArray<float> Curve;

            [NativeDisableParallelForRestriction] [WriteOnly]
            public NativeArray<float> PointValues;

            [WriteOnly] public NativeArray<float> ProbeValues;

            public void Execute(int index){
                SplineRoot root = Subtrees[index];
                SplineNode node = Nodes[root.NodeStart];
                float2 range = AxisRange(node.Axis);
                for (int s = 0; s < Samples; s++){
                    float x = math.lerp(range.x, range.y, s / (float)(Samples - 1));
                    Curve[index * Samples + s] = At(root, WithCoordinate(node.Axis, Probe, x));
                }

                int count = math.min(node.PointCount, NestedSpline.MaxPoints);
                for (int k = 0; k < count; k++)
                    PointValues[index * NestedSpline.MaxPoints + k] = At(root, WithCoordinate(node.Axis, Probe, Points[node.PointStart + k].X));
                ProbeValues[index] = At(root, Probe);
            }

            private float At(in SplineRoot root, float3 cep){
                return NestedSpline.Evaluate(root, cep.x, cep.y, cep.z, Nodes, Points);
            }
        }
    }
}
