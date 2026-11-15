#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using UnityEditor.Profiling;
using UnityEditorInternal;
using UnityEngine;

namespace Engine.Tests.PlayMode{
    /// <summary>
    ///     Records frames with the Editor's profiler and says where the main thread spent them: the sample tree
    ///     of the frames over the goal next to the tree of the other frames, the samples with the most time of
    ///     their own, and the longest frames one by one. <see cref="FrameAttribution" /> reads chosen markers
    ///     cheaply over a whole run; this reads every sample of a few seconds and costs frame time while it
    ///     records, so its numbers say what a frame is made of, not how long it is without the profiler.
    /// </summary>
    internal sealed class FrameHierarchy : IDisposable{
        private const string Head = "[FrameHierarchy] ";

        /// <summary>Samples under this many ms are not read (their time stays in the sample above them).</summary>
        private const float SmallestSampleMs = 0.002f;

        /// <summary>Longest frames that are printed one by one.</summary>
        private const int LongFrames = 4;

        /// <summary>
        ///     Samples a player build does not have: the Editor's own loop, the gizmo pass of the game view and
        ///     the profiler's counters. Their time is taken out of a frame for the estimate of a build.
        /// </summary>
        private static readonly string[] EditorOnly ={ "EditorLoop", "DrawGizmos", "Profiler.FlushCounters" };

        /// <summary>The sample that holds the pipeline's main-thread work of a frame.</summary>
        private const string Pipeline = "::VoxelWorld.Update()";

        /// <summary>Pipeline ms per frame the estimate holds the pipeline to; -1 leaves all of it in.</summary>
        private static readonly float[] Caps ={ -1f, 4f, 3f, 2f, 1f, 0f };

        /// <summary>
        ///     How many frames the profiler keeps (300 unless set): Unity has no public setter for it in this
        ///     version, so the Editor's own setting is reached by name; without it the report covers 300 frames.
        /// </summary>
        private static readonly PropertyInfo FrameCount = typeof(ProfilerDriver).Assembly
            .GetType("UnityEditor.Profiling.ProfilerUserSettings")
            ?.GetProperty("frameCount", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

        private readonly int _savedFrameCount;
        private readonly bool _wasEnabled;
        private readonly bool _wasProfilingEditor;
        private readonly List<List<int>> _childLists = new();

        /// <summary>Starts recording; the profiler keeps the last <paramref name="frames" /> frames (300 to 2000).</summary>
        public FrameHierarchy(int frames){
            _savedFrameCount = FrameCount?.GetValue(null) is int count ? count : 0;
            _wasEnabled = ProfilerDriver.enabled;
            _wasProfilingEditor = ProfilerDriver.profileEditor;
            FrameCount?.SetValue(null, Mathf.Clamp(frames, 300, 2000));
            ProfilerDriver.profileEditor = false;
            ProfilerDriver.ClearAllFrames();
            ProfilerDriver.enabled = true;
        }

        /// <summary>Stops recording; the recorded frames stay readable.</summary>
        public void Stop(){
            ProfilerDriver.enabled = false;
        }

        public void Dispose(){
            ProfilerDriver.enabled = _wasEnabled;
            ProfilerDriver.profileEditor = _wasProfilingEditor;
            if (_savedFrameCount > 0) FrameCount?.SetValue(null, _savedFrameCount);
        }

        /// <summary>
        ///     The report over the recorded frames. A frame counts as "over" when it took longer than
        ///     <paramref name="overMs" />; tree rows are printed when they average <paramref name="minMs" /> or
        ///     more in the frames over the goal or in the others.
        /// </summary>
        public string Report(string label, float overMs, float minMs){
            CultureInfo inv = CultureInfo.InvariantCulture;
            Node root = new("frame");
            Dictionary<string, double[]> selfByName = new();
            List<(float ms, int frame)> longest = new();
            List<float> frameMs = new(), editorMs = new(), pipelineMs = new();
            float[] parts = new float[2];
            int frames = 0, over = 0;
            double sumOver = 0, sumRest = 0;

            for (int frame = ProfilerDriver.firstFrameIndex; frame <= ProfilerDriver.lastFrameIndex; frame++){
                using HierarchyFrameDataView view = ProfilerDriver.GetHierarchyFrameDataView(frame, 0,
                    HierarchyFrameDataView.ViewModes.MergeSamplesWithTheSameName, HierarchyFrameDataView.columnDontSort,
                    false);
                if (view == null || !view.valid) continue;
                float ms = view.frameTimeMs;
                bool isOver = ms > overMs;
                frames++;
                if (isOver){
                    over++;
                    sumOver += ms;
                } else sumRest += ms;

                parts[0] = parts[1] = 0f;
                Add(view, view.GetRootItemID(), root, isOver ? 0 : 1, 0, selfByName, parts, false);
                longest.Add((ms, frame));
                frameMs.Add(ms);
                editorMs.Add(parts[0]);
                pipelineMs.Add(parts[1]);
            }

            if (frames == 0) return Head + label + ": the profiler recorded no frame\n";
            int rest = frames - over;
            StringBuilder text = new();
            text.AppendLine(string.Format(inv,
                "{0}{1}: {2} recorded frames (profiler on, so longer than in a measurement), {3} over {4:0.00} ms " +
                "({5:0.0} %) with {6:0.000} ms on average, {7} others with {8:0.000} ms",
                Head, label, frames, over, overMs, 100.0 * over / frames, over > 0 ? sumOver / over : 0, rest,
                rest > 0 ? sumRest / rest : 0));

            // What a time budget for the pipeline can reach, in the Editor and without the Editor's own time
            text.AppendLine(Head + label + ": frames over the goal when the pipeline's work of a frame (" + Pipeline +
                            ") is held to a number of ms and what is over it is left out. The work itself moves " +
                            "to later frames, so every row is the best case for that budget");
            Estimate("as recorded (Editor, profiler on)", frameMs, null, pipelineMs, overMs, text, inv);
            Estimate("without " + string.Join(", ", EditorOnly) + " (close to a player build)", frameMs, editorMs,
                pipelineMs, overMs, text, inv);

            // The tree: total and own time per frame, in the frames over the goal and in the others
            text.AppendLine(string.Format(inv, "{0}{1,-78}{2,9}{3,9}{4,9}{5,9}", Head, "sample tree, ms per frame",
                "tot over", "tot rest", "own over", "own rest"));
            Print(root, 0, Math.Max(over, 1), Math.Max(rest, 1), minMs, text, inv);

            // The samples with the most time of their own, wherever they sit in the tree
            List<KeyValuePair<string, double[]>> flat = new(selfByName);
            flat.Sort((a, b) => b.Value[0].CompareTo(a.Value[0]));
            text.AppendLine(string.Format(inv, "{0}{1,-78}{2,9}{3,9}", Head,
                "own time by sample name, ms per frame (largest in the frames over the goal first)", "own over",
                "own rest"));
            for (int i = 0; i < Math.Min(flat.Count, 45); i++)
                text.AppendLine(string.Format(inv, "{0}{1,-78}{2,9:0.000}{3,9:0.000}", Head, Cut(flat[i].Key, 76),
                    flat[i].Value[0] / Math.Max(over, 1), flat[i].Value[1] / Math.Max(rest, 1)));

            // The longest frames one by one
            longest.Sort((a, b) => b.ms.CompareTo(a.ms));
            for (int i = 0; i < Math.Min(LongFrames, longest.Count); i++){
                using HierarchyFrameDataView view = ProfilerDriver.GetHierarchyFrameDataView(longest[i].frame, 0,
                    HierarchyFrameDataView.ViewModes.MergeSamplesWithTheSameName, HierarchyFrameDataView.columnDontSort,
                    false);
                if (view == null || !view.valid) continue;
                text.AppendLine(string.Format(inv, "{0}{1}: long frame {2:0.00} ms, samples of 0.4 ms and more " +
                                                   "(total / own)", Head, label, longest[i].ms));
                PrintFrame(view, view.GetRootItemID(), 0, 0.4f, text, inv);
            }

            return text.ToString();
        }

        private static void Estimate(string what, List<float> frameMs, List<float> editorMs, List<float> pipelineMs,
            float overMs, StringBuilder text, CultureInfo inv){
            float[] ms = new float[frameMs.Count];
            double editor = 0;
            for (int i = 0; i < ms.Length; i++) editor += editorMs?[i] ?? 0f;
            text.AppendLine(string.Format(inv, "{0}  {1}: {2:0.000} ms per frame taken out", Head, what,
                editor / ms.Length));
            foreach (float cap in Caps){
                int over = 0, far = 0;
                double sum = 0;
                for (int i = 0; i < ms.Length; i++){
                    float pipeline = pipelineMs[i];
                    ms[i] = frameMs[i] - (editorMs?[i] ?? 0f) - (cap < 0f ? 0f : Math.Max(0f, pipeline - cap));
                    sum += ms[i];
                    if (ms[i] > overMs) over++;
                    if (ms[i] > 2f * overMs) far++;
                }

                Array.Sort(ms);
                text.AppendLine(string.Format(inv,
                    "{0}    pipeline {1,-18} frame ms avg {2,7:0.000}, p50 {3,7:0.000}, p95 {4,7:0.000}, p99 " +
                    "{5,7:0.000}, max {6,7:0.000}; {7,5} of {8} over {9:0.00} ms ({10,5:0.0} %), {11} over {12:0.00} ms",
                    Head, cap < 0f ? "as it is:" : cap == 0f ? "left out:" : string.Format(inv, "held to {0:0} ms:", cap),
                    sum / ms.Length, At(ms, 0.5f), At(ms, 0.95f), At(ms, 0.99f), ms[ms.Length - 1], over, ms.Length,
                    overMs, 100.0 * over / ms.Length, far, 2f * overMs));
            }
        }

        private static float At(float[] sorted, float share){
            return sorted[Math.Min(sorted.Length - 1, (int)(share * sorted.Length))];
        }

        private void Add(HierarchyFrameDataView view, int id, Node node, int bucket, int depth,
            Dictionary<string, double[]> selfByName, float[] parts, bool counted){
            while (_childLists.Count <= depth) _childLists.Add(new List<int>());
            List<int> children = _childLists[depth];
            view.GetItemChildren(id, children);
            for (int i = 0; i < children.Count; i++){
                int child = children[i];
                float total = view.GetItemColumnDataAsFloat(child, HierarchyFrameDataView.columnTotalTime);
                if (total < SmallestSampleMs) continue;
                float self = view.GetItemColumnDataAsFloat(child, HierarchyFrameDataView.columnSelfTime);
                string name = view.GetItemName(child);
                Node next = node.Child(name);
                next.Total[bucket] += total;
                next.Self[bucket] += self;
                if (!selfByName.TryGetValue(name, out double[] sums)) selfByName[name] = sums = new double[2];
                sums[bucket] += self;

                // Editor-only time and pipeline time of this frame; a sample inside a counted one is not counted again
                bool inside = counted;
                if (!counted){
                    if (Array.IndexOf(EditorOnly, name) >= 0){
                        parts[0] += total;
                        inside = true;
                    } else if (name.IndexOf(Pipeline, StringComparison.Ordinal) >= 0){
                        parts[1] += total;
                        inside = true;
                    }
                }

                if (view.HasItemChildren(child)) Add(view, child, next, bucket, depth + 1, selfByName, parts, inside);
            }
        }

        private static void Print(Node node, int depth, int over, int rest, float minMs, StringBuilder text,
            CultureInfo inv){
            if (node.Children == null) return;
            List<Node> children = new(node.Children.Values);
            children.Sort((a, b) => b.Total[0].CompareTo(a.Total[0]));
            foreach (Node child in children){
                double totalOver = child.Total[0] / over, totalRest = child.Total[1] / rest;
                if (totalOver < minMs && totalRest < minMs) continue;
                text.AppendLine(string.Format(inv, "{0}{1,-78}{2,9:0.000}{3,9:0.000}{4,9:0.000}{5,9:0.000}", Head,
                    Cut(new string(' ', depth * 2) + child.Name, 76), totalOver, totalRest, child.Self[0] / over,
                    child.Self[1] / rest));
                Print(child, depth + 1, over, rest, minMs, text, inv);
            }
        }

        private void PrintFrame(HierarchyFrameDataView view, int id, int depth, float minMs, StringBuilder text,
            CultureInfo inv){
            while (_childLists.Count <= depth) _childLists.Add(new List<int>());
            List<int> children = _childLists[depth];
            view.GetItemChildren(id, children);
            for (int i = 0; i < children.Count; i++){
                int child = children[i];
                float total = view.GetItemColumnDataAsFloat(child, HierarchyFrameDataView.columnTotalTime);
                if (total < minMs) continue;
                text.AppendLine(string.Format(inv, "{0}{1,-78}{2,9:0.000}{3,9:0.000}", Head,
                    Cut(new string(' ', depth * 2) + view.GetItemName(child), 76), total,
                    view.GetItemColumnDataAsFloat(child, HierarchyFrameDataView.columnSelfTime)));
                PrintFrame(view, child, depth + 1, minMs, text, inv);
            }
        }

        private static string Cut(string name, int length){
            return name.Length <= length ? name : name.Substring(0, length);
        }

        /// <summary>A sample of the tree, summed over the frames over the goal (0) and the others (1).</summary>
        private sealed class Node{
            public readonly string Name;
            public readonly double[] Total = new double[2];
            public readonly double[] Self = new double[2];
            public Dictionary<string, Node> Children;

            public Node(string name){
                Name = name;
            }

            public Node Child(string name){
                Children ??= new Dictionary<string, Node>();
                if (!Children.TryGetValue(name, out Node child)) Children[name] = child = new Node(name);
                return child;
            }
        }
    }

    /// <summary>
    ///     The Gizmos switch of the open Game views, off for the life of this object. With the switch on the
    ///     Editor rebuilds its gizmo list (GizmoManager.RecalculateGizmos, several ms with thousands of
    ///     partitions) in the frames after objects were switched on or off, and the chunk pool does that all
    ///     through a flight. A build has no such pass, so a measurement of the engine keeps it out.
    /// </summary>
    internal sealed class GameViewGizmos : IDisposable{
        private static readonly string[] Fields ={ "m_Gizmos", "m_ShowGizmos" };
        private readonly List<(UnityEngine.Object view, FieldInfo field)> _switchedOff = new();

        public GameViewGizmos(){
            Type viewType = typeof(UnityEditor.EditorWindow).Assembly.GetType("UnityEditor.PlayModeView");
            if (viewType == null) return;

            const BindingFlags declared = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            foreach (UnityEngine.Object view in Resources.FindObjectsOfTypeAll(viewType))
                for (Type type = view.GetType(); type != null && type != typeof(object); type = type.BaseType)
                    foreach (string name in Fields){
                        FieldInfo field = type.GetField(name, declared);
                        if (field == null || field.FieldType != typeof(bool) || !(bool)field.GetValue(view)) continue;

                        field.SetValue(view, false);
                        _switchedOff.Add((view, field));
                    }
        }

        /// <summary>Switches that were on and are off now (two per Game view: the button and what it drives).</summary>
        public int SwitchedOff => _switchedOff.Count;

        public void Dispose(){
            foreach ((UnityEngine.Object view, FieldInfo field) in _switchedOff)
                if (view)
                    field.SetValue(view, true);
            _switchedOff.Clear();
        }
    }
}
#endif
