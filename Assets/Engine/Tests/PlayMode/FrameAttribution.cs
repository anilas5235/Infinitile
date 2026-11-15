using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Engine.Scripts.Utils;
using Unity.Profiling;
using Unity.Profiling.LowLevel.Unsafe;

namespace Engine.Tests.PlayMode{
    /// <summary>
    ///     Where the main thread spends a frame: one <see cref="ProfilerRecorder" /> per row, sampled once per
    ///     frame next to the frame time, so a table can say for every part how long it took (average, 95th
    ///     percentile, maximum) and which share of the frames over the goal it explains. The rows are the engine's
    ///     own markers ("Voxel.*") and markers of Unity's player loop; a marker a Unity version does not have is
    ///     printed as "not available". Rows are indented under the part they usually run in, so a row's time is
    ///     also inside the rows above it and the columns must not be added up.
    /// </summary>
    internal sealed class FrameAttribution : IDisposable{
        private const string Head = "[FrameAttribution] ";

        /// <summary>A row below this many milliseconds did not run in the frame.</summary>
        private const float ActiveMs = 0.01f;

        /// <summary>The marker that spans a whole frame in the Editor, and the one that does in a build.</summary>
        private const string ReferenceEditor = "Application.Tick", ReferencePlayer = "PlayerLoop";

        /// <summary>Longest frames of a window that are printed with everything that ran in them.</summary>
        private const int WorstFrames = 8;

        private sealed class Row{
            internal int Depth;
            internal string Label;
            internal string Name; // a Unity marker or counter, found by name
            internal ProfilerMarker Marker; // one of the engine's markers
            internal bool HasMarker;
            internal string[] Sum; // labels of the rows a computed row adds up
            internal bool Delayed; // a counter that arrives some frames late: no per-frame comparison
        }

        private static Row Unity(int depth, string name, string label = null){
            return new Row{ Depth = depth, Label = label ?? name, Name = name };
        }

        private static Row Counter(int depth, string name){
            return new Row{ Depth = depth, Label = name + " (some frames late)", Name = name, Delayed = true };
        }

        private static Row Voxel(int depth, string label, ProfilerMarker marker){
            return new Row{ Depth = depth, Label = label, Marker = marker, HasMarker = true };
        }

        private static Row Total(int depth, string label, params string[] sum){
            return new Row{ Depth = depth, Label = label, Sum = sum };
        }

        private static Row[] BuildRows(){
            return new[]{
                Total(0, "pipeline on the main thread (sum)", "stage data", "stage light", "stage mesh",
                    "stage bake", "focus update", "gpu build complete", "gpu build dispatch", "render buffer rebuild"),
                Unity(0, "PlayerLoop"),
                Unity(1, "Update.ScriptRunBehaviourUpdate", "Update of all scripts"),
                Total(2, "scheduler stages (sum)", "stage data", "stage light", "stage mesh", "stage bake"),
                Voxel(3, "stage data", VoxelProfilerMarkers.SchedulerData),
                Voxel(3, "stage light", VoxelProfilerMarkers.SchedulerLight),
                Voxel(3, "stage mesh", VoxelProfilerMarkers.SchedulerMesh),
                Voxel(3, "stage bake", VoxelProfilerMarkers.SchedulerCollider),
                Voxel(3, "step collect (any stage)", VoxelProfilerMarkers.SchedulerCollect),
                Voxel(4, "data complete", VoxelProfilerMarkers.DataGenComplete),
                Voxel(5, "chunk unload (eviction)", VoxelProfilerMarkers.ChunkUnload),
                Voxel(6, "pool reclaim chunk", VoxelProfilerMarkers.PoolReclaimChunk),
                Voxel(7, "pool deactivate chunk object", VoxelProfilerMarkers.PoolDeactivateChunk),
                Voxel(7, "renderer release", VoxelProfilerMarkers.RenderRelease),
                Voxel(6, "wait for mesh job", VoxelProfilerMarkers.WaitMeshBuild),
                Voxel(6, "wait for light job + apply", VoxelProfilerMarkers.WaitLight),
                Voxel(6, "wait for bake job", VoxelProfilerMarkers.WaitColliderBake),
                Voxel(5, "gpu upload voxels / light", VoxelProfilerMarkers.GpuUploadChunk),
                Voxel(4, "light complete", VoxelProfilerMarkers.LightComplete),
                Voxel(4, "mesh complete", VoxelProfilerMarkers.MeshBuildComplete),
                Voxel(5, "mesh claim partitions", VoxelProfilerMarkers.MeshBuildClaim),
                Voxel(6, "pool claim partition", VoxelProfilerMarkers.PoolClaimPartition),
                Voxel(7, "pool activate chunk object", VoxelProfilerMarkers.PoolActivateChunk),
                Voxel(7, "partition clear (any caller)", VoxelProfilerMarkers.PartitionClear),
                Voxel(5, "mesh apply mesh data", VoxelProfilerMarkers.MeshBuildApply),
                Voxel(4, "bake complete", VoxelProfilerMarkers.ColliderBakeComplete),
                Voxel(5, "collider assign", VoxelProfilerMarkers.ColliderAssign),
                Voxel(3, "step scan (any stage)", VoxelProfilerMarkers.SchedulerScan),
                Voxel(3, "step dispatch (any stage)", VoxelProfilerMarkers.SchedulerDispatch),
                Voxel(4, "data schedule", VoxelProfilerMarkers.DataGenSchedule),
                Voxel(4, "light schedule", VoxelProfilerMarkers.LightSchedule),
                Voxel(4, "mesh schedule", VoxelProfilerMarkers.MeshBuildSchedule),
                Voxel(4, "bake schedule", VoxelProfilerMarkers.ColliderBakeSchedule),
                Voxel(2, "gpu build complete", VoxelProfilerMarkers.PointBuildComplete),
                Voxel(3, "gpu build copy", VoxelProfilerMarkers.PointBuildCopy),
                Voxel(2, "gpu build dispatch", VoxelProfilerMarkers.PointBuildDispatch),
                Unity(1, "Update.ScriptRunDelayedDynamicFrameRate", "coroutines (focus update, the test)"),
                Voxel(2, "focus update", VoxelProfilerMarkers.FocusUpdate),
                Voxel(3, "focus data queue", VoxelProfilerMarkers.FocusData),
                Voxel(3, "focus light queue", VoxelProfilerMarkers.FocusLight),
                Voxel(3, "focus mesh queue", VoxelProfilerMarkers.FocusMesh),
                Voxel(3, "focus bake queue", VoxelProfilerMarkers.FocusCollider),
                Voxel(3, "focus chunk store queue", VoxelProfilerMarkers.FocusChunkStore),
                Voxel(3, "focus pool queues", VoxelProfilerMarkers.FocusPool),
                Unity(1, "PreLateUpdate.ScriptRunBehaviourLateUpdate", "LateUpdate of all scripts"),
                Voxel(2, "render buffer rebuild", VoxelProfilerMarkers.RenderRebuildBuffers),
                Unity(1, "FixedUpdate.PhysicsFixedUpdate"),
                Unity(1, "PreUpdate.PhysicsUpdate"),
                Unity(1, "PreLateUpdate.PhysicsLateUpdate"),
                Unity(1, "EarlyUpdate.UpdateAsyncReadbackManager", "GPU readback manager"),
                Unity(1, "PreLateUpdate.UIElementsUpdatePanels", "HUD update"),
                Unity(1, "PostLateUpdate.UIElementsRepaintPanels", "HUD repaint"),
                Unity(1, "PostLateUpdate.UpdateAllRenderers"),
                Unity(1, "PostLateUpdate.PlayerUpdateCanvases"),
                Unity(1, "PostLateUpdate.ProfilerEndFrame", "profiler end of frame (measurement cost)"),
                // In the Editor the player loop ends with the repaint of the game view: render, then present
                Unity(1, "GUIView.RepaintAll.PlayerLoopController", "repaint of the views that follow the game"),
                Unity(2, "GameView.Repaint"),
                Unity(3, "GameView.Render", "game view render"),
                Unity(4, "UniversalRenderPipeline.RenderSingleCameraInternal: Main Camera", "URP render of the main camera"),
                Unity(3, "GameView.Paint", "game view paint"),
                Unity(3, "Gfx.PresentFrame", "present"),
                Unity(3, "GfxDeviceD3D12.WaitForLastPresentation", "wait for the last present (D3D12)"),
                Unity(3, "GfxDeviceD3D12.WaitForLastPresent", "wait for the last present 2 (D3D12)"),
                Unity(3, "GfxDeviceD3D12.InsertEditorWaitForPresentFence", "Editor present fence (D3D12)"),
                Unity(3, "GfxDeviceD3D12.WaitForGPU", "wait for the GPU (D3D12)"),
                Unity(1, "GUIView.RepaintAll.RepaintScene", "repaint of the scene view"),
                Unity(1, "WaitForTargetFPS", "wait for the frame cap"),
                Unity(0, "Application.Tick", "Editor tick (the whole frame in the Editor)"),
                Unity(1, "Application.UpdateScene", "Editor scene update (holds the player loop)"),
                Unity(1, "SceneHierarchyWindow.Repaint", "repaint of the hierarchy window"),
                Unity(1, "InspectorWindow.Repaint", "repaint of the inspector"),
                Unity(1, "EditorApplication.update", "Editor update callbacks"),
                Unity(1, "Application.Idle", "Editor idle wait"),
                // Wherever they run (player loop or Editor loop)
                Unity(0, "Camera.Render", "camera render (all cameras)"),
                Unity(0, "CullScriptable", "Unity culling (all cameras)"),
                Unity(0, "RenderLoop.Draw", "Unity draw of game objects (all cameras)"),
                Unity(0, "ScriptableRenderContext.Submit", "URP submit (all cameras)"),
                Voxel(0, "voxel draw (all cameras)", VoxelProfilerMarkers.RenderDraw),
                Voxel(1, "voxel culling (all cameras)", VoxelProfilerMarkers.RenderCulling),
                Unity(0, "Gfx.WaitForPresentOnGfxThread", "wait for the present (GPU / vsync)"),
                Unity(0, "Gfx.WaitForRenderThread", "wait for the render thread"),
                Unity(0, "Semaphore.WaitForSignal", "main thread blocked on a signal"),
                Unity(0, "JobHandle.Complete", "main thread waits for a job (any caller)"),
                Unity(0, "GameObject.Activate", "game object activation (any caller)"),
                Unity(0, "GameObject.Deactivate", "game object deactivation (any caller)"),
                Unity(0, "Physics.SyncTransforms", "physics transform sync (any caller)"),
                Unity(0, "Physics.HandleColliderHierarchyChanges", "physics collider hierarchy changes"),
                Unity(0, "GC.Collect"),
                Counter(0, "CPU Main Thread Frame Time"),
                Counter(0, "CPU Render Thread Frame Time"),
                Counter(0, "GPU Frame Time")
            };
        }

        private readonly Row[] _rows = BuildRows();
        private readonly ProfilerRecorder[] _recorders;
        private readonly List<float>[] _ms;
        private readonly int[][] _sumOf;
        private readonly int _reference;
        private int _samples;

        internal FrameAttribution(int capacity = 1 << 16){
            Dictionary<string, ProfilerRecorderHandle> handles = AvailableHandles();
            _recorders = new ProfilerRecorder[_rows.Length];
            _ms = new List<float>[_rows.Length];
            _sumOf = new int[_rows.Length][];
            _reference = -1;
            for (int i = 0; i < _rows.Length; i++){
                Row row = _rows[i];
                if (row.Sum != null){
                    _sumOf[i] = new int[row.Sum.Length];
                    for (int k = 0; k < row.Sum.Length; k++) _sumOf[i][k] = IndexOf(row.Sum[k]);
                    _ms[i] = new List<float>(capacity);
                    continue;
                }

                if (row.HasMarker) _recorders[i] = ProfilerRecorder.StartNew(row.Marker);
                else if (handles.TryGetValue(row.Name, out ProfilerRecorderHandle handle))
                    // A recorder made from a handle does not run until it is started. Markers are read on this
                    // (the main) thread only: several of them also run on job and render threads
                    _recorders[i] = new ProfilerRecorder(handle, 1,
                        row.Delayed
                            ? ProfilerRecorderOptions.Default | ProfilerRecorderOptions.StartImmediately
                            : ProfilerRecorderOptions.Default | ProfilerRecorderOptions.StartImmediately |
                              ProfilerRecorderOptions.CollectOnlyOnCurrentThread);
                if (!_recorders[i].Valid) continue;
                _ms[i] = new List<float>(capacity);
                // The row that spans a whole frame: the Editor's tick, or the player loop in a build
                if (row.Name == ReferenceEditor || (row.Name == ReferencePlayer && _reference < 0)) _reference = i;
            }
        }

        private int IndexOf(string label){
            for (int i = 0; i < _rows.Length; i++)
                if (_rows[i].Label == label) return i;
            throw new ArgumentException($"no row '{label}'");
        }

        private static Dictionary<string, ProfilerRecorderHandle> AvailableHandles(){
            List<ProfilerRecorderHandle> all = new();
            ProfilerRecorderHandle.GetAvailable(all);
            Dictionary<string, ProfilerRecorderHandle> byName = new(all.Count);
            foreach (ProfilerRecorderHandle handle in all){
                string name = ProfilerRecorderHandle.GetDescription(handle).Name;
                if (!byName.ContainsKey(name)) byName.Add(name, handle);
            }

            return byName;
        }

        /// <summary>
        ///     Writes the name of every marker and counter this Unity version offers to <paramref name="path" />
        ///     and returns the rows of the table that have none.
        /// </summary>
        internal static string ListAvailable(string path){
            Dictionary<string, ProfilerRecorderHandle> handles = AvailableHandles();
            List<string> lines = new(handles.Count);
            foreach (KeyValuePair<string, ProfilerRecorderHandle> pair in handles){
                ProfilerRecorderDescription description = ProfilerRecorderHandle.GetDescription(pair.Value);
                lines.Add($"{pair.Key}\t{description.Category.Name}\t{description.UnitType}");
            }

            lines.Sort(StringComparer.Ordinal);
            File.WriteAllLines(path, lines);

            StringBuilder missing = new();
            foreach (Row row in BuildRows())
                if (row.Name != null && !handles.ContainsKey(row.Name)) missing.Append(row.Name).Append("; ");
            return $"{Head}{handles.Count} markers and counters written to {path}; rows without one: " +
                   (missing.Length > 0 ? missing.ToString() : "none");
        }

        public void Dispose(){
            for (int i = 0; i < _recorders.Length; i++)
                if (_recorders[i].Valid) _recorders[i].Dispose();
        }

        /// <summary>Takes one sample of every row; call once per frame, with the frame-time sample.</summary>
        internal void Sample(){
            for (int i = 0; i < _rows.Length; i++){
                if (_ms[i] == null || _sumOf[i] != null) continue;
                _ms[i].Add(_recorders[i].LastValue * 1e-6f);
            }

            for (int i = 0; i < _rows.Length; i++){
                if (_sumOf[i] == null) continue;
                float sum = 0f;
                foreach (int k in _sumOf[i])
                    if (_ms[k] != null) sum += _ms[k][_samples];
                _ms[i].Add(sum);
            }

            _samples++;
        }

        /// <summary>
        ///     Frames between a frame-time sample and the recorder sample that belongs to it: the one for which
        ///     the row that spans a whole frame is closest to the frame time over the whole run.
        /// </summary>
        private int Lag(List<float> frameMs, out double meanDifference){
            meanDifference = -1;
            if (_reference < 0) return 0;
            int best = 0;
            double bestSum = double.MaxValue;
            int count = Math.Min(frameMs.Count, _samples);
            for (int lag = -2; lag <= 2; lag++){
                double sum = 0;
                int used = 0;
                for (int i = 2; i < count - 2; i++){
                    sum += Math.Abs(frameMs[i] - _ms[_reference][i + lag]);
                    used++;
                }

                if (used == 0 || sum >= bestSum) continue;
                bestSum = sum;
                best = lag;
                meanDifference = sum / used;
            }

            return best;
        }

        /// <summary>
        ///     The table for the frames <paramref name="from" /> (included) to <paramref name="to" /> (excluded) of
        ///     <paramref name="frameMs" />, which holds one frame time per <see cref="Sample" /> call. Columns:
        ///     milliseconds per frame over all frames (average, 95th percentile, maximum), the share of frames the
        ///     row ran in, its average in the frames over the goal and in the others, and the share of the frames
        ///     over the goal that would have met it without the row.
        /// </summary>
        internal string Table(string label, List<float> frameMs, int from, int to){
            CultureInfo inv = CultureInfo.InvariantCulture;
            int lag = Lag(frameMs, out double meanDifference);
            from = Math.Max(from, 2);
            to = Math.Min(Math.Min(to, frameMs.Count), _samples) - 2;
            StringBuilder text = new();
            if (to <= from) return $"{Head}{label}: no samples\n";

            int frames = to - from, over = 0, stutter = 0;
            double frameSum = 0;
            for (int i = from; i < to; i++){
                frameSum += frameMs[i];
                if (frameMs[i] > PipelineProbe.GoalFrameMs) over++;
                if (frameMs[i] > PipelineProbe.StutterFrameMs) stutter++;
            }

            text.AppendLine(string.Format(inv,
                "{0}{1}: {2} frames of {8:0.000} ms on average, {3} over 8.33 ms ({4:0.0} %), {5} over 16.67 ms; " +
                "recorder samples are taken {6} frame(s) from the frame time (mean |frame - {9}| {7:0.000} ms " +
                "over the whole run)",
                Head, label, frames, over, 100.0 * over / frames, stutter, lag, meanDifference, frameSum / frames,
                _reference >= 0 ? _rows[_reference].Label : "none"));
            text.AppendLine(string.Format(inv, "{0}{1,-52}{2,8}{3,8}{4,8}{5,9}{6,10}{7,10}{8,10}", Head,
                "ms per frame", "avg", "p95", "max", "ran in %", "avg over", "avg rest", "explains %"));

            List<float> sorted = new(frames);
            for (int r = 0; r < _rows.Length; r++){
                Row row = _rows[r];
                string name = new string(' ', 2 * row.Depth) + row.Label;
                if (_ms[r] == null){
                    text.AppendLine($"{Head}{name,-52} not available");
                    continue;
                }

                int shift = row.Delayed ? 0 : lag;
                sorted.Clear();
                double sum = 0, sumOver = 0, sumRest = 0;
                int active = 0, explained = 0;
                for (int i = from; i < to; i++){
                    float ms = _ms[r][i + shift];
                    sorted.Add(ms);
                    sum += ms;
                    if (ms > ActiveMs) active++;
                    if (frameMs[i] > PipelineProbe.GoalFrameMs){
                        sumOver += ms;
                        if (frameMs[i] - ms <= PipelineProbe.GoalFrameMs) explained++;
                    }
                    else{
                        sumRest += ms;
                    }
                }

                sorted.Sort();
                int last = sorted.Count - 1;
                string line = string.Format(inv, "{0}{1,-52}{2,8:0.000}{3,8:0.000}{4,8:0.000}{5,9:0.0}", Head, name,
                    sum / frames, sorted[(int)(last * 0.95f)], sorted[last], 100.0 * active / frames);
                if (!row.Delayed)
                    line += string.Format(inv, "{0,10:0.000}{1,10:0.000}{2,10:0.0}", sumOver / Math.Max(over, 1),
                        sumRest / Math.Max(frames - over, 1), 100.0 * explained / Math.Max(over, 1));
                text.AppendLine(line);
            }

            // The longest frames, each with every row that took part in it
            List<int> order = new(frames);
            for (int i = from; i < to; i++) order.Add(i);
            order.Sort((a, b) => frameMs[b].CompareTo(frameMs[a]));
            for (int k = 0; k < Math.Min(WorstFrames, order.Count); k++){
                int i = order[k];
                text.Append(string.Format(inv, "{0}{1}: long frame {2:0.00} ms (sample {3}):", Head, label, frameMs[i], i));
                for (int r = 0; r < _rows.Length; r++){
                    if (_ms[r] == null || _rows[r].Delayed) continue;
                    float ms = _ms[r][i + lag];
                    if (ms >= 0.3f) text.Append(string.Format(inv, " {0} {1:0.00};", _rows[r].Label, ms));
                }

                text.AppendLine();
            }

            return text.ToString();
        }
    }
}
