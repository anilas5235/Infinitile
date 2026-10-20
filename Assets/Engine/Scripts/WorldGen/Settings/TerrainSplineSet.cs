using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Engine.Scripts.WorldGen.Height;
using Unity.Collections;
using UnityEngine;

namespace Engine.Scripts.WorldGen.Settings{
    /// <summary>A serialised control point: constant <see cref="Value" /> or a <see cref="Child" /> spline index.</summary>
    [Serializable]
    public class SplinePointData{
        [Tooltip("Position on the parent spline's axis: C and E in [0,1], PV in [-1,1].")]
        public float X;

        [Tooltip("Constant value; ignored when Child is set.")]
        public float Value;

        [Tooltip("Index into Splines of the child spline that supplies the value, or -1 for the constant.")]
        public int Child = -1;
    }

    /// <summary>One serialised spline of the flat list; children reference other entries by index.</summary>
    [Serializable]
    public class SplineData{
        [Tooltip("Display name; the JSON importer derives it from the path (e.g. Offset[0.5][0]).")]
        public string Name;

        public SplineAxis Axis;
        public SplineInterpolation Interpolation = SplineInterpolation.MonotoneCubic;
        public List<SplinePointData> Points = new();
    }

    /// <summary>One node of the flattened trees (<see cref="TerrainSplineSet.Flatten" />), for the TRN-06 plots.</summary>
    public struct FlatSpline{
        /// <summary>Root tree: Offset, Amp or Jaggedness.</summary>
        public string Tree;

        /// <summary>Index of the source entry in <see cref="TerrainSplineSet.Splines" />.</summary>
        public int Source;

        /// <summary>Nesting level, 0 for the root.</summary>
        public int Depth;

        /// <summary>The node and its children: evaluating it gives this spline level with its sub-splines.</summary>
        public SplineRoot Subtree;
    }

    /// <summary>
    ///     Terrain-first spline set (TRN-05): Offset, Amp and J as nested splines over (C, E, PV). The asset stores
    ///     the trees as a flat list (Unity cannot serialise recursive classes); authoring happens through the nested
    ///     JSON form (<see cref="ToJson" /> / <see cref="FromJson" />, buttons on the inspector) or the §19.1
    ///     placeholders (<see cref="ResetToPlaceholders" />). <see cref="Flatten" /> builds the Burst arrays.
    ///     <para>
    ///         JSON form: each spline is an object <c>{"Axis": "C"|"E"|"PV", "Interpolation": "MonotoneCubic"|"Linear",
    ///         "Points": [{"X": x, "Value": number | spline}, ...]}</c>; a root may also be a plain number
    ///         (constant). Top-level keys: Offset, Amp, Jaggedness, Notes.
    ///     </para>
    /// </summary>
    [CreateAssetMenu(fileName = "TerrainSplineSet", menuName = "Voxel/TerrainSplineSet", order = 2)]
    public class TerrainSplineSet : ScriptableObject{
        /// <summary>Nesting bound of <see cref="Flatten" />; deeper trees are reported as a cycle or runaway nesting.</summary>
        public const int MaxDepth = 8;

        /// <summary>The §19.1 placeholder splines in the JSON form (heights in blocks, water level 124).</summary>
        public const string PlaceholderJson = @"{
  ""Notes"": ""Placeholder Terrain-first splines (WorldGenSpec §19.1). Only to get a plausible starting world; replace them."",
  ""Offset"": {""Axis"": ""C"", ""Points"": [
    {""X"": 0.0, ""Value"": 70},
    {""X"": 0.3, ""Value"": 110},
    {""X"": 0.4, ""Value"": 122},
    {""X"": 0.5, ""Value"": {""Axis"": ""E"", ""Points"": [
      {""X"": 0.0, ""Value"": {""Axis"": ""PV"", ""Points"": [{""X"": -1, ""Value"": 128}, {""X"": 0, ""Value"": 136}, {""X"": 1, ""Value"": 150}]}},
      {""X"": 0.6, ""Value"": 132},
      {""X"": 1.0, ""Value"": 128}]}},
    {""X"": 0.75, ""Value"": {""Axis"": ""E"", ""Points"": [
      {""X"": 0.0, ""Value"": {""Axis"": ""PV"", ""Points"": [{""X"": -1, ""Value"": 135}, {""X"": 0, ""Value"": 160}, {""X"": 1, ""Value"": 195}]}},
      {""X"": 0.5, ""Value"": {""Axis"": ""PV"", ""Points"": [{""X"": -1, ""Value"": 132}, {""X"": 0, ""Value"": 145}, {""X"": 1, ""Value"": 165}]}},
      {""X"": 1.0, ""Value"": 138}]}},
    {""X"": 1.0, ""Value"": {""Axis"": ""E"", ""Points"": [{""X"": 0.0, ""Value"": 175}, {""X"": 1.0, ""Value"": 145}]}}]},
  ""Amp"": {""Axis"": ""C"", ""Points"": [
    {""X"": 0, ""Value"": 4},
    {""X"": 0.4, ""Value"": 6},
    {""X"": 0.75, ""Value"": {""Axis"": ""E"", ""Points"": [{""X"": 0, ""Value"": 22}, {""X"": 1, ""Value"": 8}]}},
    {""X"": 1, ""Value"": 26}]},
  ""Jaggedness"": {""Axis"": ""C"", ""Points"": [
    {""X"": 0, ""Value"": 0},
    {""X"": 0.6, ""Value"": 0},
    {""X"": 0.8, ""Value"": {""Axis"": ""E"", ""Points"": [
      {""X"": 0, ""Value"": {""Axis"": ""PV"", ""Points"": [{""X"": 0, ""Value"": 0}, {""X"": 1, ""Value"": 40}]}},
      {""X"": 0.5, ""Value"": 0}]}},
    {""X"": 1, ""Value"": 50}]}
}";

        [TextArea] public string Notes;

        [Header("Roots (indices into Splines, -1 = none)")]
        public int OffsetRoot = -1;

        public int AmpRoot = -1;

        [Tooltip("J: own extension of the Minecraft offset/factor splines (TRN-04).")]
        public int JaggednessRoot = -1;

        [Tooltip("Flat spline list; edit through JSON import/export or the placeholders button.")]
        public List<SplineData> Splines = new();

        /// <summary>True when all three roots reference a spline.</summary>
        public bool HasAllRoots => OffsetRoot >= 0 && AmpRoot >= 0 && JaggednessRoot >= 0;

        private void OnValidate(){
            foreach (string problem in Validate()) Debug.LogWarning($"{name}: {problem}", this);
        }

        // --- Validation --------------------------------------------------------------------------------------------

        /// <summary>Every structural problem of the asset; empty when <see cref="Flatten" /> will succeed.</summary>
        public List<string> Validate(){
            List<string> problems = new();
            Splines ??= new List<SplineData>();
            CheckRoot(nameof(OffsetRoot), OffsetRoot, problems);
            CheckRoot(nameof(AmpRoot), AmpRoot, problems);
            CheckRoot(nameof(JaggednessRoot), JaggednessRoot, problems);

            for (int i = 0; i < Splines.Count; i++){
                SplineData s = Splines[i];
                string label = $"spline {i} ({s?.Name})";
                if (s == null){
                    problems.Add($"{label} is null.");
                    continue;
                }

                s.Points ??= new List<SplinePointData>();
                if (s.Points.Count == 0) problems.Add($"{label} has no control points.");
                if (s.Points.Count > NestedSpline.MaxPoints)
                    problems.Add($"{label} has {s.Points.Count} control points; at most {NestedSpline.MaxPoints} are supported.");
                float lo = s.Axis == SplineAxis.PeaksValleys ? -1f : 0f;
                for (int k = 0; k < s.Points.Count; k++){
                    SplinePointData p = s.Points[k];
                    if (p == null){
                        problems.Add($"{label} point {k} is null.");
                        continue;
                    }

                    if (float.IsNaN(p.X) || float.IsInfinity(p.X)) problems.Add($"{label} point {k}: X is not finite.");
                    else if (p.X < lo || p.X > 1f) problems.Add($"{label} point {k}: X = {p.X} lies outside the {AxisName(s.Axis)} range [{lo}, 1].");
                    if (k > 0 && s.Points[k - 1] != null && !(p.X > s.Points[k - 1].X))
                        problems.Add($"{label} point {k}: X must increase strictly (got {s.Points[k - 1].X} then {p.X}).");
                    if (p.Child >= 0 && (p.Child >= Splines.Count || p.Child == i))
                        problems.Add($"{label} point {k}: child {p.Child} is not a valid other spline.");
                    if (p.Child < 0 && (float.IsNaN(p.Value) || float.IsInfinity(p.Value)))
                        problems.Add($"{label} point {k}: value is not finite.");
                }
            }

            if (problems.Count > 0) return problems;

            foreach ((string rootName, int root) in Roots()){
                int nodes = CountNodes(root, 0, problems, rootName);
                if (nodes > NestedSpline.MaxNodesPerRoot)
                    problems.Add($"{rootName} tree has {nodes} splines; at most {NestedSpline.MaxNodesPerRoot} are supported.");
            }

            return problems;
        }

        private void CheckRoot(string field, int root, List<string> problems){
            if (root < 0) problems.Add($"{field} is not set (Terrain-first needs Offset, Amp and Jaggedness).");
            else if (root >= (Splines?.Count ?? 0)) problems.Add($"{field} = {root} is not a valid spline index.");
        }

        private int CountNodes(int index, int depth, List<string> problems, string rootName){
            if (depth > MaxDepth){
                problems.Add($"{rootName} tree nests deeper than {MaxDepth} levels (cycle?).");
                return 0;
            }

            int count = 1;
            foreach (SplinePointData p in Splines[index].Points)
                if (p.Child >= 0)
                    count += CountNodes(p.Child, depth + 1, problems, rootName);
            return count;
        }

        private IEnumerable<(string, int)> Roots(){
            yield return ("Offset", OffsetRoot);
            yield return ("Amp", AmpRoot);
            yield return ("Jaggedness", JaggednessRoot);
        }

        // --- Burst mirror (INT-03) -------------------------------------------------------------------------------

        /// <summary>
        ///     Flattens the three trees into pre-ordered node/point arrays (children after parents, as
        ///     <see cref="NestedSpline.Evaluate" /> expects). The caller owns the arrays. Throws when
        ///     <see cref="Validate" /> reports problems.
        /// </summary>
        /// <param name="order">
        ///     Optional: filled with one entry per flattened node, in node order (TRN-06 plots label and evaluate
        ///     every spline level through it).
        /// </param>
        public void Flatten(Allocator allocator, out NativeArray<SplineNode> nodes, out NativeArray<SplinePoint> points,
            out SplineRoot offset, out SplineRoot amp, out SplineRoot jaggedness, List<FlatSpline> order = null){
            List<string> problems = Validate();
            if (problems.Count > 0)
                throw new InvalidOperationException($"{name}: invalid TerrainSplineSet: {string.Join(" ", problems)}");

            List<SplineNode> nodeList = new();
            List<SplinePoint> pointList = new();
            order?.Clear();
            offset = AddTree("Offset", OffsetRoot, nodeList, pointList, order);
            amp = AddTree("Amp", AmpRoot, nodeList, pointList, order);
            jaggedness = AddTree("Jaggedness", JaggednessRoot, nodeList, pointList, order);

            nodes = new NativeArray<SplineNode>(nodeList.ToArray(), allocator);
            points = new NativeArray<SplinePoint>(pointList.ToArray(), allocator);
        }

        private SplineRoot AddTree(string tree, int root, List<SplineNode> nodes, List<SplinePoint> points, List<FlatSpline> order){
            int start = nodes.Count;
            AddNode(tree, root, 0, nodes, points, order);
            return new SplineRoot{ NodeStart = start, NodeCount = nodes.Count - start };
        }

        private int AddNode(string tree, int index, int depth, List<SplineNode> nodes, List<SplinePoint> points, List<FlatSpline> order){
            SplineData s = Splines[index];
            int nodeIndex = nodes.Count;
            order?.Add(new FlatSpline{ Tree = tree, Source = index, Depth = depth });
            int pointStart = points.Count;
            nodes.Add(new SplineNode{
                Axis = s.Axis,
                Interpolation = s.Interpolation,
                PointStart = pointStart,
                PointCount = s.Points.Count
            });
            foreach (SplinePointData p in s.Points) points.Add(new SplinePoint{ X = p.X, Value = p.Value, Child = -1 });
            for (int k = 0; k < s.Points.Count; k++){
                SplinePointData p = s.Points[k];
                if (p.Child < 0) continue;
                int child = AddNode(tree, p.Child, depth + 1, nodes, points, order);
                SplinePoint flat = points[pointStart + k];
                flat.Child = child;
                points[pointStart + k] = flat;
            }

            // A node's subtree is contiguous in pre-order, so it can be evaluated on its own (TRN-06).
            if (order != null){
                FlatSpline entry = order[nodeIndex];
                entry.Subtree = new SplineRoot{ NodeStart = nodeIndex, NodeCount = nodes.Count - nodeIndex };
                order[nodeIndex] = entry;
            }

            return nodeIndex;
        }

        // --- JSON (TRN-05) -----------------------------------------------------------------------------------------

        /// <summary>Nested tree of the whole set for <see cref="CanonicalJson" /> (also inlined into the settings manifest, INT-02).</summary>
        public Dictionary<string, object> ToTree(){
            return new Dictionary<string, object>{
                ["Name"] = name,
                ["Notes"] = Notes ?? "",
                ["Offset"] = SplineTree(OffsetRoot, 0),
                ["Amp"] = SplineTree(AmpRoot, 0),
                ["Jaggedness"] = SplineTree(JaggednessRoot, 0)
            };
        }

        private object SplineTree(int index, int depth){
            if (index < 0 || index >= Splines.Count || Splines[index] == null) return null;
            if (depth > MaxDepth) throw new InvalidOperationException($"{name}: spline nesting exceeds {MaxDepth} levels (cycle?).");
            SplineData s = Splines[index];
            List<object> points = new();
            foreach (SplinePointData p in s.Points ?? new List<SplinePointData>())
                points.Add(new Dictionary<string, object>{
                    ["X"] = p.X,
                    ["Value"] = p.Child >= 0 ? SplineTree(p.Child, depth + 1) : p.Value
                });
            return new Dictionary<string, object>{
                ["Axis"] = AxisName(s.Axis),
                ["Interpolation"] = s.Interpolation,
                ["Points"] = points
            };
        }

        /// <summary>Canonical nested JSON of the set.</summary>
        public string ToJson(){
            return CanonicalJson.Serialize(ToTree());
        }

        /// <summary>
        ///     Replaces the contents with the splines in <paramref name="json" /> (see the class summary for the form).
        ///     Throws <see cref="FormatException" /> on malformed input; the asset is untouched then.
        /// </summary>
        public void FromJson(string json){
            if (JsonReader.Parse(json) is not Dictionary<string, object> root)
                throw new FormatException("TerrainSplineSet JSON must be an object with Offset, Amp and Jaggedness.");

            List<SplineData> splines = new();
            int offset = ImportRoot(root, "Offset", splines);
            int amp = ImportRoot(root, "Amp", splines);
            int jaggedness = ImportRoot(root, "Jaggedness", splines);

            Splines = splines;
            OffsetRoot = offset;
            AmpRoot = amp;
            JaggednessRoot = jaggedness;
            if (root.TryGetValue("Notes", out object notes) && notes is string text) Notes = text;
        }

        /// <summary>Loads the §19.1 placeholder splines.</summary>
        public void ResetToPlaceholders(){
            FromJson(PlaceholderJson);
        }

        private static int ImportRoot(Dictionary<string, object> root, string key, List<SplineData> splines){
            return root.TryGetValue(key, out object value) && value != null ? ImportSpline(value, key, splines, 0) : -1;
        }

        private static int ImportSpline(object value, string path, List<SplineData> splines, int depth){
            if (depth > MaxDepth) throw new FormatException($"{path}: spline nesting exceeds {MaxDepth} levels.");
            SplineData s = new(){ Name = path };
            int index = splines.Count;
            splines.Add(s);

            if (value is double constant){
                s.Axis = SplineAxis.Continental;
                s.Points.Add(new SplinePointData{ X = 0f, Value = (float)constant });
                return index;
            }

            if (value is not Dictionary<string, object> obj)
                throw new FormatException($"{path}: a spline must be a number or an object with Axis and Points.");

            s.Axis = ParseAxis(obj.TryGetValue("Axis", out object axis) ? axis as string : null, path);
            s.Interpolation = ParseInterpolation(obj.TryGetValue("Interpolation", out object interp) ? interp as string : null, path);
            if (!obj.TryGetValue("Points", out object pointsValue) || pointsValue is not List<object> points || points.Count == 0)
                throw new FormatException($"{path}: Points must be a non-empty array.");

            List<(float x, object value)> parsed = new();
            foreach (object item in points){
                if (item is not Dictionary<string, object> point || !point.TryGetValue("X", out object xValue) || xValue is not double x)
                    throw new FormatException($"{path}: every point needs a numeric X.");
                if (!point.TryGetValue("Value", out object pointValue) || pointValue == null)
                    throw new FormatException($"{path}: point X = {x} has no Value.");
                parsed.Add(((float)x, pointValue));
            }

            // A stable sort by X keeps authoring order free; duplicates keep their order and are reported by Validate.
            // (List.Sort is unstable, OrderBy is stable.)
            foreach ((float x, object pointValue) in parsed.OrderBy(point => point.x)){
                SplinePointData p = new(){ X = x };
                if (pointValue is double v) p.Value = (float)v;
                else p.Child = ImportSpline(pointValue, $"{path}[{x.ToString(CultureInfo.InvariantCulture)}]", splines, depth + 1);
                s.Points.Add(p);
            }

            return index;
        }

        private static SplineAxis ParseAxis(string text, string path){
            switch (text){
                case "C":
                case nameof(SplineAxis.Continental): return SplineAxis.Continental;
                case "E":
                case nameof(SplineAxis.Erosion): return SplineAxis.Erosion;
                case "PV":
                case nameof(SplineAxis.PeaksValleys): return SplineAxis.PeaksValleys;
                default: throw new FormatException($"{path}: Axis must be C, E or PV (got '{text}').");
            }
        }

        private static SplineInterpolation ParseInterpolation(string text, string path){
            switch (text){
                case null:
                case nameof(SplineInterpolation.MonotoneCubic): return SplineInterpolation.MonotoneCubic;
                case nameof(SplineInterpolation.Linear): return SplineInterpolation.Linear;
                default: throw new FormatException($"{path}: Interpolation must be MonotoneCubic or Linear (got '{text}').");
            }
        }

        /// <summary>Short axis name as used in the JSON form.</summary>
        public static string AxisName(SplineAxis axis){
            return axis switch{
                SplineAxis.Continental => "C",
                SplineAxis.Erosion => "E",
                _ => "PV"
            };
        }
    }
}
