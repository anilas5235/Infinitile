using System;
using System.Collections.Generic;
using System.Globalization;
using Engine.Scripts.WorldGen.Height;
using Engine.Scripts.WorldGen.Settings;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Engine.Tests.EditMode{
    /// <summary>Nested spline evaluation, JSON import/export and continuity.</summary>
    public class NestedSplineTests{
        private readonly List<Object> _created = new();
        private readonly List<IDisposable> _native = new();

        [TearDown]
        public void TearDown(){
            foreach (IDisposable d in _native) d.Dispose();
            _native.Clear();
            foreach (Object o in _created) Object.DestroyImmediate(o);
            _created.Clear();
        }

        /// <summary>A flattened spline set, evaluable per root.</summary>
        private sealed class Flat : IDisposable{
            public NativeArray<SplineNode> Nodes;
            public NativeArray<SplinePoint> Points;
            public SplineRoot Offset, Amp, Jaggedness;

            public float Eval(in SplineRoot root, float c, float e, float pv){
                return NestedSpline.Evaluate(root, c, e, pv, Nodes, Points);
            }

            public void Dispose(){
                Nodes.Dispose();
                Points.Dispose();
            }
        }

        private TerrainSplineSet Set(string json){
            TerrainSplineSet set = ScriptableObject.CreateInstance<TerrainSplineSet>();
            set.name = "Splines";
            _created.Add(set);
            set.FromJson(json);
            return set;
        }

        private Flat Flatten(TerrainSplineSet set){
            Flat flat = new();
            set.Flatten(Allocator.Persistent, out flat.Nodes, out flat.Points, out flat.Offset, out flat.Amp, out flat.Jaggedness);
            _native.Add(flat);
            return flat;
        }

        /// <summary>A set whose Offset is <paramref name="offset" /> and whose Amp and J are constants.</summary>
        private Flat Single(string offset){
            return Flatten(Set($"{{\"Offset\": {offset}, \"Amp\": 1, \"Jaggedness\": 0}}"));
        }

        private static string Spline(string axis, string interpolation, params (float x, string value)[] points){
            List<string> items = new();
            foreach ((float x, string value) in points)
                items.Add($"{{\"X\": {x.ToString(CultureInfo.InvariantCulture)}, \"Value\": {value}}}");
            string interp = interpolation == null ? "" : $"\"Interpolation\": \"{interpolation}\", ";
            return $"{{\"Axis\": \"{axis}\", {interp}\"Points\": [{string.Join(", ", items)}]}}";
        }

        // --- Evaluation ---------------------------------------------------------------------------------------------

        [Test]
        public void ConstantRoot_ReturnsTheConstantEverywhere(){
            Flat f = Single("42.5");
            Assert.AreEqual(42.5f, f.Eval(f.Offset, 0f, 0f, 0f));
            Assert.AreEqual(42.5f, f.Eval(f.Offset, 1f, 0.3f, -1f));
            Assert.AreEqual(1f, f.Eval(f.Amp, 0.5f, 0.5f, 0.5f));
            Assert.AreEqual(0f, f.Eval(f.Jaggedness, 0.5f, 0.5f, 0.5f));
        }

        [Test]
        public void MissingRoot_EvaluatesToZero(){
            SplineRoot none = default;
            Flat f = Single("1");
            Assert.AreEqual(0f, f.Eval(none, 0.5f, 0.5f, 0f));
        }

        [Test]
        public void Linear_InterpolatesExactlyBetweenControlPoints(){
            Flat f = Single(Spline("C", "Linear", (0f, "10"), (0.5f, "20"), (1f, "40")));
            Assert.AreEqual(10f, f.Eval(f.Offset, 0f, 0f, 0f));
            Assert.AreEqual(15f, f.Eval(f.Offset, 0.25f, 0f, 0f), 1e-5f);
            Assert.AreEqual(20f, f.Eval(f.Offset, 0.5f, 0f, 0f), 1e-5f);
            Assert.AreEqual(30f, f.Eval(f.Offset, 0.75f, 0f, 0f), 1e-5f);
            Assert.AreEqual(40f, f.Eval(f.Offset, 1f, 0f, 0f));
        }

        [Test]
        public void EndValues_AreHeldOutsideTheControlRange(){
            Flat f = Single(Spline("PV", null, (-0.5f, "3"), (0.5f, "7")));
            Assert.AreEqual(3f, f.Eval(f.Offset, 0f, 0f, -1f));
            Assert.AreEqual(3f, f.Eval(f.Offset, 0f, 0f, -0.5f));
            Assert.AreEqual(7f, f.Eval(f.Offset, 0f, 0f, 0.5f));
            Assert.AreEqual(7f, f.Eval(f.Offset, 0f, 0f, 1f));
            Assert.AreEqual(7f, f.Eval(f.Offset, 0f, 0f, 5f));
        }

        [Test]
        public void MonotoneCubic_PassesThroughControlPointsAndStaysMonotone(){
            // Fritsch–Carlson: monotone data gives a monotone interpolant, no overshoot.
            Flat f = Single(Spline("C", null, (0f, "70"), (0.3f, "110"), (0.4f, "122"), (0.5f, "136"), (1f, "140")));
            Assert.AreEqual(70f, f.Eval(f.Offset, 0f, 0f, 0f), 1e-5f);
            Assert.AreEqual(110f, f.Eval(f.Offset, 0.3f, 0f, 0f), 1e-4f);
            Assert.AreEqual(122f, f.Eval(f.Offset, 0.4f, 0f, 0f), 1e-4f);
            Assert.AreEqual(136f, f.Eval(f.Offset, 0.5f, 0f, 0f), 1e-4f);
            Assert.AreEqual(140f, f.Eval(f.Offset, 1f, 0f, 0f), 1e-5f);

            float previous = f.Eval(f.Offset, 0f, 0f, 0f);
            for (int i = 1; i <= 2000; i++){
                float v = f.Eval(f.Offset, i / 2000f, 0f, 0f);
                Assert.GreaterOrEqual(v, previous - 1e-5f, $"not monotone at C = {i / 2000f}");
                Assert.LessOrEqual(v, 140f + 1e-4f);
                previous = v;
            }
        }

        [Test]
        public void MonotoneCubic_IsSmootherThanLinearInsideAnInterval(){
            // Hermite with non-zero tangents at the interior point differs from the chord; linear equals it.
            Flat cubic = Single(Spline("C", null, (0f, "0"), (0.5f, "10"), (1f, "40")));
            Flat linear = Single(Spline("C", "Linear", (0f, "0"), (0.5f, "10"), (1f, "40")));
            Assert.AreEqual(5f, linear.Eval(linear.Offset, 0.25f, 0f, 0f), 1e-5f);
            Assert.AreNotEqual(5f, cubic.Eval(cubic.Offset, 0.25f, 0f, 0f));
            Assert.AreEqual(10f, cubic.Eval(cubic.Offset, 0.5f, 0f, 0f), 1e-4f);
        }

        [Test]
        public void Placeholders_EvaluateTheExpectedValuesAtTheControlPoints(){
            TerrainSplineSet set = ScriptableObject.CreateInstance<TerrainSplineSet>();
            _created.Add(set);
            set.ResetToPlaceholders();
            Flat f = Flatten(set);

            // Placeholder Offset: constants at C = 0, 0.3, 0.4; nested at 0.5 / 0.75 / 1.0.
            Assert.AreEqual(70f, f.Eval(f.Offset, 0f, 0.5f, 0f), 1e-4f);
            Assert.AreEqual(110f, f.Eval(f.Offset, 0.3f, 0.9f, -1f), 1e-4f);
            Assert.AreEqual(136f, f.Eval(f.Offset, 0.5f, 0f, 0f), 1e-4f);
            Assert.AreEqual(150f, f.Eval(f.Offset, 0.5f, 0f, 1f), 1e-4f);
            Assert.AreEqual(132f, f.Eval(f.Offset, 0.5f, 0.6f, 1f), 1e-4f);
            Assert.AreEqual(195f, f.Eval(f.Offset, 0.75f, 0f, 1f), 1e-4f);
            Assert.AreEqual(145f, f.Eval(f.Offset, 0.75f, 0.5f, 0f), 1e-4f);
            Assert.AreEqual(138f, f.Eval(f.Offset, 0.75f, 1f, 0.3f), 1e-4f);
            Assert.AreEqual(175f, f.Eval(f.Offset, 1f, 0f, 0f), 1e-4f);
            Assert.AreEqual(145f, f.Eval(f.Offset, 1f, 1f, 0f), 1e-4f);

            // Amp and J.
            Assert.AreEqual(4f, f.Eval(f.Amp, 0f, 0f, 0f), 1e-4f);
            Assert.AreEqual(22f, f.Eval(f.Amp, 0.75f, 0f, 0f), 1e-4f);
            Assert.AreEqual(8f, f.Eval(f.Amp, 0.75f, 1f, 0f), 1e-4f);
            Assert.AreEqual(26f, f.Eval(f.Amp, 1f, 0.5f, 0f), 1e-4f);
            Assert.AreEqual(0f, f.Eval(f.Jaggedness, 0.6f, 0f, 1f), 1e-4f);
            Assert.AreEqual(40f, f.Eval(f.Jaggedness, 0.8f, 0f, 1f), 1e-4f);
            Assert.AreEqual(0f, f.Eval(f.Jaggedness, 0.8f, 0f, -1f), 1e-4f, "PV below the first point holds the end value");
            Assert.AreEqual(0f, f.Eval(f.Jaggedness, 0.8f, 0.5f, 1f), 1e-4f);
            Assert.AreEqual(50f, f.Eval(f.Jaggedness, 1f, 0f, 0f), 1e-4f);
        }

        // --- JSON ---------------------------------------------------------------------------------------------------

        [Test]
        public void Json_RoundTripsAndKeepsTheEvaluation(){
            TerrainSplineSet a = ScriptableObject.CreateInstance<TerrainSplineSet>();
            a.name = "Splines"; // the export carries the asset name; the import leaves it to the asset
            _created.Add(a);
            a.ResetToPlaceholders();
            string json = a.ToJson();

            TerrainSplineSet b = Set(json);
            Assert.AreEqual(json, b.ToJson(), "export → import → export is stable");
            Assert.AreEqual(a.Splines.Count, b.Splines.Count);

            Flat fa = Flatten(a);
            Flat fb = Flatten(b);
            Unity.Mathematics.Random rng = new(7u);
            for (int i = 0; i < 500; i++){
                float c = rng.NextFloat(), e = rng.NextFloat(), pv = rng.NextFloat(-1f, 1f);
                Assert.AreEqual(fa.Eval(fa.Offset, c, e, pv), fb.Eval(fb.Offset, c, e, pv));
                Assert.AreEqual(fa.Eval(fa.Amp, c, e, pv), fb.Eval(fb.Amp, c, e, pv));
                Assert.AreEqual(fa.Eval(fa.Jaggedness, c, e, pv), fb.Eval(fb.Jaggedness, c, e, pv));
            }
        }

        [Test]
        public void Json_ImportSortsPointsAndHonoursInterpolationAndAxisNames(){
            TerrainSplineSet set = Set("{\"Offset\": {\"Axis\": \"Erosion\", \"Interpolation\": \"Linear\", \"Points\": [{\"X\": 1, \"Value\": 10}, {\"X\": 0, \"Value\": 0}]}, \"Amp\": 2, \"Jaggedness\": 0, \"Notes\": \"n\"}");
            Assert.AreEqual("n", set.Notes);
            SplineData offset = set.Splines[set.OffsetRoot];
            Assert.AreEqual(SplineAxis.Erosion, offset.Axis);
            Assert.AreEqual(SplineInterpolation.Linear, offset.Interpolation);
            Assert.AreEqual(0f, offset.Points[0].X);
            Assert.AreEqual(1f, offset.Points[1].X);
            Flat f = Flatten(set);
            Assert.AreEqual(5f, f.Eval(f.Offset, 0f, 0.5f, 0f), 1e-5f);
        }

        [Test]
        public void Json_RejectsMalformedInputAndLeavesTheAssetUntouched(){
            TerrainSplineSet set = ScriptableObject.CreateInstance<TerrainSplineSet>();
            _created.Add(set);
            set.ResetToPlaceholders();
            int count = set.Splines.Count;

            Assert.Throws<FormatException>(() => set.FromJson("{\"Offset\": [1, 2]"), "truncated");
            Assert.Throws<FormatException>(() => set.FromJson("{\"Offset\": {\"Axis\": \"Q\", \"Points\": [{\"X\": 0, \"Value\": 1}]}}"), "bad axis");
            Assert.Throws<FormatException>(() => set.FromJson("{\"Offset\": {\"Axis\": \"C\", \"Points\": []}}"), "no points");
            Assert.Throws<FormatException>(() => set.FromJson("{\"Offset\": {\"Axis\": \"C\", \"Points\": [{\"X\": 0}]}}"), "no value");
            Assert.AreEqual(count, set.Splines.Count, "a failed import changes nothing");
        }

        [Test]
        public void Validate_ReportsMissingRootsCyclesDuplicatesAndOversizedSplines(){
            TerrainSplineSet set = ScriptableObject.CreateInstance<TerrainSplineSet>();
            _created.Add(set);
            Assert.AreEqual(3, set.Validate().Count, "three missing roots");

            set.ResetToPlaceholders();
            Assert.IsEmpty(set.Validate());

            // Cycle: the Offset root's first point points back at the root.
            set.Splines[set.OffsetRoot].Points[0].Child = set.Splines[set.OffsetRoot].Points[3].Child;
            set.Splines[set.Splines[set.OffsetRoot].Points[3].Child].Points[1].Child = set.OffsetRoot;
            Assert.IsNotEmpty(set.Validate());
            Assert.Throws<InvalidOperationException>(() => set.Flatten(Allocator.Temp, out _, out _, out _, out _, out _));

            set.ResetToPlaceholders();
            set.Splines[set.AmpRoot].Points[1].X = set.Splines[set.AmpRoot].Points[0].X;
            StringAssert.Contains("increase strictly", string.Join(" ", set.Validate()));

            set.ResetToPlaceholders();
            for (int i = 0; i < NestedSpline.MaxPoints; i++)
                set.Splines[set.AmpRoot].Points.Add(new SplinePointData{ X = 2f + i, Value = 1f });
            StringAssert.Contains("at most", string.Join(" ", set.Validate()));
        }

        // --- Continuity ---------------------------------------------------------------------------------------------

        /// <summary>
        ///     Dense sampling of Offset, Amp and J along each axis: no jump between neighbouring samples may exceed
        ///     slope bound × step. The bound per axis is 3 × the steepest control polygon of any spline over that
        ///     axis (the Fritsch–Carlson interpolant's derivative is at most 3× the local secant) × 4 for the two
        ///     nesting levels above a PV spline, each of which is Lipschitz in its control values with constant
        ///     below 2 (h00 + h01 = 1 plus the tangent terms).
        /// </summary>
        [Test]
        public void Continuity_NoJumpExceedsTheSlopeBoundTimesTheStep(){
            TerrainSplineSet set = ScriptableObject.CreateInstance<TerrainSplineSet>();
            _created.Add(set);
            set.ResetToPlaceholders();
            Flat f = Flatten(set);

            const int steps = 1024;
            float[] fixedCoords ={ 0f, 0.1f, 0.33f, 0.5f, 0.61f, 0.75f, 0.9f, 1f };
            (string name, int root)[] roots ={ ("Offset", set.OffsetRoot), ("Amp", set.AmpRoot), ("Jaggedness", set.JaggednessRoot) };
            SplineRoot[] flatRoots ={ f.Offset, f.Amp, f.Jaggedness };

            for (int r = 0; r < roots.Length; r++)
            for (int axis = 0; axis < 3; axis++){
                float polygonSlope = MaxPolygonSlope(set, roots[r].root, (SplineAxis)axis);
                float bound = 3f * 4f * polygonSlope;
                float lo = axis == 2 ? -1f : 0f;
                float step = (1f - lo) / steps;
                float worst = 0f;
                foreach (float u in fixedCoords)
                foreach (float v in fixedCoords){
                    float previous = Eval(f, flatRoots[r], axis, lo, u, v);
                    for (int i = 1; i <= steps; i++){
                        float value = Eval(f, flatRoots[r], axis, lo + i * step, u, v);
                        worst = math.max(worst, math.abs(value - previous));
                        previous = value;
                    }
                }

                Assert.LessOrEqual(worst, bound * step + 1e-5f,
                    $"{roots[r].name} along {(SplineAxis)axis}: max jump {worst} > {bound} × {step}");
            }
        }

        private static float Eval(Flat f, in SplineRoot root, int axis, float a, float u, float v){
            // Axis takes the dense coordinate; u and v fill the other two (v for PV spans [0,1] of its range).
            float pvV = v * 2f - 1f;
            return axis switch{
                0 => f.Eval(root, a, u, pvV),
                1 => f.Eval(root, u, a, pvV),
                _ => f.Eval(root, u, v, a)
            };
        }

        /// <summary>Steepest control polygon over <paramref name="axis" /> in the tree below <paramref name="root" />, using child value envelopes.</summary>
        private static float MaxPolygonSlope(TerrainSplineSet set, int root, SplineAxis axis){
            float worst = 0f;
            Visit(set, root, axis, ref worst);
            return worst;
        }

        private static void Visit(TerrainSplineSet set, int index, SplineAxis axis, ref float worst){
            SplineData s = set.Splines[index];
            if (s.Axis == axis)
                for (int k = 1; k < s.Points.Count; k++){
                    (float lo0, float hi0) = Envelope(set, s.Points[k - 1]);
                    (float lo1, float hi1) = Envelope(set, s.Points[k]);
                    float dx = s.Points[k].X - s.Points[k - 1].X;
                    worst = math.max(worst, math.max(math.abs(hi1 - lo0), math.abs(hi0 - lo1)) / dx);
                }

            foreach (SplinePointData p in s.Points)
                if (p.Child >= 0)
                    Visit(set, p.Child, axis, ref worst);
        }

        private static (float, float) Envelope(TerrainSplineSet set, SplinePointData p){
            if (p.Child < 0) return (p.Value, p.Value);
            float lo = float.MaxValue, hi = float.MinValue;
            foreach (SplinePointData q in set.Splines[p.Child].Points){
                (float l, float h) = Envelope(set, q);
                lo = math.min(lo, l);
                hi = math.max(hi, h);
            }

            return (lo, hi);
        }
    }
}
