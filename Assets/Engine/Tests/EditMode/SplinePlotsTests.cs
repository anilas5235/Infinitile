using System.Collections.Generic;
using Engine.Editor.WorldGen;
using Engine.Scripts.WorldGen.Height;
using Engine.Scripts.WorldGen.Settings;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Engine.Tests.EditMode{
    /// <summary>1D plots of every spline level evaluate the flattened subtree of each node.</summary>
    public class SplinePlotsTests{
        private TerrainSplineSet _set;

        [SetUp]
        public void SetUp(){
            _set = ScriptableObject.CreateInstance<TerrainSplineSet>();
            _set.ResetToPlaceholders();
        }

        [TearDown]
        public void TearDown(){
            Object.DestroyImmediate(_set);
        }

        [Test]
        public void FlattenOrder_CoversEveryNode_WithContiguousSubtrees(){
            List<FlatSpline> order = new();
            _set.Flatten(Allocator.Temp, out NativeArray<SplineNode> nodes, out NativeArray<SplinePoint> points,
                out SplineRoot offset, out SplineRoot amp, out SplineRoot jaggedness, order);
            try{
                Assert.AreEqual(nodes.Length, order.Count);
                Assert.AreEqual((offset.NodeStart, offset.NodeCount), (order[0].Subtree.NodeStart, order[0].Subtree.NodeCount));
                Assert.AreEqual("Offset", order[0].Tree);
                Assert.AreEqual(0, order[0].Depth);
                Assert.AreEqual("Amp", order[amp.NodeStart].Tree);
                Assert.AreEqual(amp.NodeCount, order[amp.NodeStart].Subtree.NodeCount);
                Assert.AreEqual("Jaggedness", order[jaggedness.NodeStart].Tree);
                for (int i = 0; i < order.Count; i++){
                    Assert.AreEqual(i, order[i].Subtree.NodeStart);
                    Assert.AreEqual(_set.Splines[order[i].Source].Axis, nodes[i].Axis);
                    // Every child lies inside its parent's subtree.
                    SplineNode node = nodes[i];
                    for (int k = 0; k < node.PointCount; k++){
                        int child = points[node.PointStart + k].Child;
                        if (child < 0) continue;
                        Assert.Greater(child, i);
                        Assert.Less(child, i + order[i].Subtree.NodeCount);
                        Assert.AreEqual(order[i].Depth + 1, order[child].Depth);
                    }
                }
            }
            finally{
                nodes.Dispose();
                points.Dispose();
            }
        }

        [Test]
        public void Plots_HitConstantKnots_AndChildrenAgreeWithTheirParents(){
            float3 probe = new(0.3f, 0.4f, 0.2f);
            List<SplinePlots.Plot> plots = SplinePlots.Compute(_set, probe, 65, out string error);
            Assert.IsNull(error);
            Assert.Greater(plots.Count, 3);

            // Offset at C = 0.3 is the constant knot 110 of the placeholders.
            SplinePlots.Plot offset = plots[0];
            Assert.AreEqual(SplineAxis.Continental, offset.Axis);
            Assert.AreEqual(0.3f, offset.Probe.x, 1e-6f);
            Assert.AreEqual(110f, offset.Probe.y, 1e-3f);
            Assert.AreEqual(70f, offset.Curve[0], 1e-3f); // end value at C = 0

            // A child point of a parent carries the child's value at the probe (children use other axes here).
            int checkedChildren = 0;
            foreach (SplinePlots.Plot parent in plots)
                for (int k = 0; k < parent.Points.Length; k++){
                    if (!parent.PointIsChild[k]) continue;
                    SplinePlots.Plot child = plots.Find(p => p.Node.Depth == parent.Node.Depth + 1 &&
                                                             p.Node.Subtree.NodeStart > parent.Node.Subtree.NodeStart &&
                                                             p.Node.Source == _set.Splines[parent.Node.Source].Points[k].Child);
                    Assert.IsNotNull(child);
                    Assert.AreNotEqual(parent.Axis, child.Axis);
                    Assert.AreEqual(child.Probe.y, parent.Points[k].y, 1e-4f, $"{parent.Name} point {k}");
                    checkedChildren++;
                }

            Assert.Greater(checkedChildren, 0);
            foreach (SplinePlots.Plot plot in plots){
                Assert.LessOrEqual(plot.ValueRange.x, plot.ValueRange.y);
                Assert.AreEqual(plot.Axis == SplineAxis.PeaksValleys ? -1f : 0f, plot.AxisRange.x);
            }
        }

        [Test]
        public void InvalidSet_ReportsError(){
            _set.OffsetRoot = -1;
            List<SplinePlots.Plot> plots = SplinePlots.Compute(_set, float3.zero, 16, out string error);
            Assert.IsEmpty(plots);
            StringAssert.Contains("OffsetRoot", error);
        }
    }
}
