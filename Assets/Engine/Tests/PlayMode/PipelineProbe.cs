using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Engine.Scripts.Behaviour;
using Engine.Scripts.Diagnostics;
using Engine.Scripts.Jobs.Chunk;
using Engine.Scripts.Jobs.ColliderBake;
using Engine.Scripts.Jobs.ColliderMeshing;
using Engine.Scripts.Jobs.Core;
using Engine.Scripts.Jobs.Light;
using Engine.Scripts.Render;
using Engine.Scripts.Utils;
using Engine.Scripts.Utils.Extensions;
using Engine.Scripts.World;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEngine;

namespace Engine.Tests.PlayMode{
    /// <summary>
    ///     Samples the chunk pipeline once per frame while a world builds: queue lengths, what every stage's job
    ///     is doing (idle, running, finished but not collected yet), the mesh batches that were dispatched, the
    ///     renderer's pending GPU builds and the main-thread time of the scheduler. <see cref="Report" /> turns the
    ///     samples into the lines the pipeline tests log and assert on.
    /// </summary>
    internal sealed class PipelineProbe : IDisposable{
        internal const int Data = PipelineStages.Data;
        internal const int Light = PipelineStages.Light;
        internal const int Mesh = PipelineStages.Mesh;
        internal const int Bake = PipelineStages.Bake;
        private const int StageCount = PipelineStages.Count;
        private static readonly string[] StageNames = PipelineStages.Names;

        private readonly VoxelWorld _world;
        private readonly PipelineStages _stages;
        private readonly Dictionary<int3, ChunkPartition> _meshMap;
        private readonly HashSet<int3> _colliderSet;
        private readonly ProfilerRecorder[] _recorders = new ProfilerRecorder[StageCount];

        // Main-thread work of the pipeline, per frame it ran in
        private static readonly string[] CostNames ={
            "data schedule", "data complete", "light schedule", "light complete", "mesh schedule", "mesh complete",
            "bake schedule", "bake complete", "gpu build dispatch", "gpu build copy", "render buffer rebuild"
        };

        private readonly ProfilerRecorder[] _costRecorders;
        private readonly double[] _costSumMs;
        private readonly float[] _costMaxMs;
        private readonly int[] _costFrames;
        private readonly Dictionary<int3, int> _dispatchCounts = new();
        private readonly List<float> _frameMs = new(1 << 16);
        private readonly List<float> _schedulerMs = new(1 << 16);
        private readonly List<int> _framesCut = new(1 << 16); // the budget's count of cut frames after every sample
        private readonly List<float> _effectiveBudgetMs = new(1 << 16); // the budget of every frame, after the cap
        private readonly VoxelEngineScheduler _scheduler;
        private readonly int _framesCutAtStart;

        // The scheduler's largest item of work per frame (FrameBudget.TimeItems) and the stage whose turn it was
        private readonly List<float> _itemMs = new(1 << 16);
        private readonly List<int> _itemStage = new(1 << 16);
        private readonly bool _timedItemsBefore;

        private readonly int[] _framesIdle = new int[StageCount];
        private readonly int[] _framesRunning = new int[StageCount];
        private readonly int[] _framesFinished = new int[StageCount];
        private readonly int[] _framesSleeping = new int[StageCount];
        private readonly CMeshBuildScheduler _meshJobs;
        private readonly int _meshBatchSize;
        private int _framesRendererBehind;
        private float _startTime;
        private readonly FrameAttribution _attribution;

        // The dequeue loop of the mesh dispatch: the handler's counters after every sample, and one entry per call
        private readonly MeshJobStateHandler _meshHandler;
        private readonly ProfilerRecorder _dequeueRecorder;
        private readonly MeshJobStateHandler.DequeueCounts _dequeueAtStart;
        private readonly List<MeshJobStateHandler.DequeueCounts> _dequeueTotals = new(1 << 16);
        private readonly List<DequeueCall> _dequeueCalls = new(1 << 14);
        private DequeueCall _pendingCall; // counted in the sample before; its marker time is read in this one
        private bool _hasPendingCall;
        private int _markerWithoutCall; // samples whose marker read time while no call was counted for that frame

        /// <summary>One run of the dequeue loop: what it took and dropped, and the marker's time for it.</summary>
        private struct DequeueCall{
            internal int Sample;
            internal MeshJobStateHandler.DequeueCounts Counts;
            internal float Ms;
        }

        /// <param name="world">The world to sample.</param>
        /// <param name="attribute">
        ///     Also records where the main thread spends every frame, for <see cref="Attribution" />.
        /// </param>
        internal PipelineProbe(VoxelWorld world, bool attribute = false){
            _world = world;
            if (attribute) _attribution = new FrameAttribution();
            VoxelEngineScheduler scheduler = world.Scheduler;
            _scheduler = scheduler;
            _framesCutAtStart = scheduler.Budget.FramesCut;
            _timedItemsBefore = scheduler.Budget.TimeItems;
            scheduler.Budget.TimeItems = true;
            _stages = new PipelineStages(world);
            MeshJobStateHandler mesh = scheduler.MeshHandler;
            _meshJobs = scheduler.MeshJobs;
            _meshBatchSize = mesh.MeshBatchSize;
            _meshHandler = mesh;
            _dequeueAtStart = mesh.Dequeued;
            // Requeues are only counted for an attributed run: the tracking allocates
            if (attribute) mesh.TrackRequeues = true;
            _dequeueRecorder = ProfilerRecorder.StartNew(VoxelProfilerMarkers.MeshBuildDequeue);

            ChunkPool pool = PlayModeFixtures.GetPrivateField<ChunkPool>(world, "_chunkPool");
            _meshMap = PlayModeFixtures.GetPrivateField<Dictionary<int3, ChunkPartition>>(pool, "_meshMap");
            _colliderSet = PlayModeFixtures.GetPrivateField<HashSet<int3>>(pool, "_colliderSet");

            _recorders[Data] = ProfilerRecorder.StartNew(VoxelProfilerMarkers.SchedulerData);
            _recorders[Light] = ProfilerRecorder.StartNew(VoxelProfilerMarkers.SchedulerLight);
            _recorders[Mesh] = ProfilerRecorder.StartNew(VoxelProfilerMarkers.SchedulerMesh);
            _recorders[Bake] = ProfilerRecorder.StartNew(VoxelProfilerMarkers.SchedulerCollider);

            ProfilerMarker[] costs ={
                VoxelProfilerMarkers.DataGenSchedule, VoxelProfilerMarkers.DataGenComplete,
                VoxelProfilerMarkers.LightSchedule, VoxelProfilerMarkers.LightComplete,
                VoxelProfilerMarkers.MeshBuildSchedule, VoxelProfilerMarkers.MeshBuildComplete,
                VoxelProfilerMarkers.ColliderBakeSchedule, VoxelProfilerMarkers.ColliderBakeComplete,
                VoxelProfilerMarkers.PointBuildDispatch, VoxelProfilerMarkers.PointBuildCopy,
                VoxelProfilerMarkers.RenderRebuildBuffers
            };
            _costRecorders = new ProfilerRecorder[costs.Length];
            _costSumMs = new double[costs.Length];
            _costMaxMs = new float[costs.Length];
            _costFrames = new int[costs.Length];
            for (int i = 0; i < costs.Length; i++) _costRecorders[i] = ProfilerRecorder.StartNew(costs[i]);

            world.PartitionBuildRequested += OnMeshBatch;
            _startTime = Time.realtimeSinceStartup;
        }

        /// <summary>A frame of 120 frames per second, the goal of the performance work.</summary>
        internal const float GoalFrameMs = FrameTimeStats.GoalFrameMs;

        /// <summary>A frame of 60 frames per second; a longer one is a stutter the player sees.</summary>
        internal const float StutterFrameMs = FrameTimeStats.StutterFrameMs;

        /// <summary>Frames sampled so far.</summary>
        internal int Frames{ get; private set; }

        /// <summary>Seconds since the probe was created.</summary>
        internal float Seconds => Time.realtimeSinceStartup - _startTime;

        /// <summary>Chunks in the chunk store.</summary>
        internal int LoadedChunks => _world.Stats.LoadedChunks;

        /// <summary>Partitions the pool holds active (with a mesh, or without a voxel).</summary>
        internal int ActivePartitions => _meshMap.Count;

        /// <summary>Active partitions that are collidable.</summary>
        internal int CollidablePartitions => _colliderSet.Count;

        /// <summary>Longest queue seen per stage.</summary>
        internal int[] PeakQueue{ get; } = new int[StageCount];

        /// <summary>Most partitions that waited for their GPU build in the renderer.</summary>
        internal int PeakPendingBuilds{ get; private set; }

        /// <summary>Mesh batches dispatched and the partitions in them.</summary>
        internal int MeshBatches{ get; private set; }

        internal int MeshPartitions{ get; private set; }

        /// <summary>Partitions that got a mesh from a mesh job since the world was created.</summary>
        internal int MeshedPartitions => _meshJobs.MeshedPartitions;

        /// <summary>Partitions without a voxel that became active without a mesh job and without a GPU build.</summary>
        internal int PartitionsWithoutMesh => _meshJobs.PartitionsWithoutMesh;

        /// <summary>Meshed partitions whose collider mesh came out empty, so they needed no bake.</summary>
        internal int PartitionsWithoutBake => _meshJobs.PartitionsWithoutBake;

        /// <summary>Partitions without a voxel that were handed to a mesh job all the same; must stay 0.</summary>
        internal int DispatchedWithoutVoxels{ get; private set; }

        /// <summary>Seconds until every stage slept with its upstream stages done; -1 while that did not happen.</summary>
        internal float BuiltSeconds => _stages.BuiltSeconds;

        internal int BuiltFrames => _stages.BuiltFrame;

        /// <summary>True once every stage slept after its upstream stages and no GPU build is pending.</summary>
        internal bool Built => _stages.Built;

        public void Dispose(){
            if (_world != null) _world.PartitionBuildRequested -= OnMeshBatch;
            for (int i = 0; i < StageCount; i++) _recorders[i].Dispose();
            for (int i = 0; i < _costRecorders.Length; i++) _costRecorders[i].Dispose();
            _dequeueRecorder.Dispose();
            if (_meshHandler != null) _meshHandler.TrackRequeues = false;
            if (_scheduler != null) _scheduler.Budget.TimeItems = _timedItemsBefore;
            _attribution?.Dispose();
        }

        /// <summary>
        ///     Where the main thread spent the frames <paramref name="from" /> (included) to <paramref name="to" />
        ///     (excluded), as the table of <see cref="FrameAttribution.Table" />; empty without attribution.
        /// </summary>
        internal string Attribution(string label, int from, int to){
            return _attribution != null ? _attribution.Table(label, _frameMs, from, to) : "";
        }

        /// <summary>
        ///     The attribution keyed on the scheduler's time per frame (see <see cref="FrameAttribution.TableByRow" />)
        ///     for the frames <paramref name="from" /> to <paramref name="to" />: a frame is over when the scheduler
        ///     took longer than its effective budget (the largest one of the window, after the frame cap). Empty
        ///     without attribution.
        /// </summary>
        internal string OverBudget(string label, int from, int to){
            if (_attribution == null) return "";
            from = math.clamp(from, 0, _effectiveBudgetMs.Count);
            to = math.clamp(to, from, _effectiveBudgetMs.Count);
            float limit = 0f;
            for (int i = from; i < to; i++) limit = math.max(limit, _effectiveBudgetMs[i]);
            return limit > 0f
                ? _attribution.TableByRow(label, FrameAttribution.SchedulerRow, limit, from, to)
                : $"[FrameAttribution] {label}: the budget has no limit\n";
        }

        private void OnMeshBatch(HashSet<int3> partitions){
            MeshBatches++;
            MeshPartitions += partitions.Count;
            foreach (int3 partition in partitions){
                _dispatchCounts.TryGetValue(partition, out int count);
                _dispatchCounts[partition] = count + 1;
                if (!_world.ChunkManager.PartitionHasVoxels(partition)) DispatchedWithoutVoxels++;
            }
        }

        /// <summary>Forgets the queue peaks, so a second phase reports its own.</summary>
        internal void ResetPeaks(){
            Array.Clear(PeakQueue, 0, PeakQueue.Length);
            PeakPendingBuilds = 0;
        }

        /// <summary>Frames since the probe started in which the budget stopped a stage's turn.</summary>
        internal int FramesCut => _scheduler.Budget.FramesCut - _framesCutAtStart;

        /// <summary>The main-thread budget of the last frame, after the frame cap (0: no limit).</summary>
        internal float EffectiveBudgetMs => _scheduler.Budget.EffectiveMilliseconds;

        /// <summary>The scheduler's main-thread milliseconds of a frame that 95 % of all samples stay under.</summary>
        internal float SchedulerP95Ms{
            get{
                if (_schedulerMs.Count == 0) return 0f;
                List<float> sorted = new(_schedulerMs);
                sorted.Sort();
                return sorted[(int)((sorted.Count - 1) * 0.95f)];
            }
        }

        /// <summary>
        ///     Frame times and scheduler main-thread times of the samples <paramref name="from" /> (included) to
        ///     <paramref name="to" /> (excluded), as one log line. <see cref="Frames" /> is the index of the next
        ///     sample.
        /// </summary>
        internal string FrameTimes(string label, int from, int to){
            return string.Format(CultureInfo.InvariantCulture,
                "[PipelineProbe] {0}: frame ms {1}; {2}; scheduler main-thread ms {3}; {4}; " +
                "main-thread budget {5:0.##} ms ({6} after the frame cap), cut a turn in {7} frames\n", label,
                Distribution(_frameMs, from, to), OverGoal(_frameMs, from, to), Distribution(_schedulerMs, from, to),
                OverGoal(_schedulerMs, from, to), _scheduler.Budget.Milliseconds, EffectiveBudgets(from, to),
                FramesCutBefore(to) - FramesCutBefore(from));
        }

        /// <summary>The budgets of the frames <paramref name="from" /> to <paramref name="to" /> after the frame cap.</summary>
        private string EffectiveBudgets(int from, int to){
            from = math.clamp(from, 0, _effectiveBudgetMs.Count);
            to = math.clamp(to, from, _effectiveBudgetMs.Count);
            if (to == from) return "no samples";
            float low = float.MaxValue, high = 0f;
            for (int i = from; i < to; i++){
                low = math.min(low, _effectiveBudgetMs[i]);
                high = math.max(high, _effectiveBudgetMs[i]);
            }

            return string.Format(CultureInfo.InvariantCulture, low == high ? "{0:0.##} ms" : "{0:0.##} to {1:0.##} ms",
                low, high);
        }

        /// <summary>Frames in which the budget had stopped a stage's turn before sample <paramref name="sample" />.</summary>
        private int FramesCutBefore(int sample){
            sample = math.clamp(sample, 0, _framesCut.Count);
            return sample == 0 ? _framesCutAtStart : _framesCut[sample - 1];
        }

        /// <summary>The limit an item of work is judged against: the budget without a frame cap (2 ms shipped).</summary>
        internal const float ItemLimitMs = 2f;

        /// <summary>
        ///     The scheduler's largest item of work per frame in the samples <paramref name="from" /> (included) to
        ///     <paramref name="to" /> (excluded), as one log line: the distribution over the frames, the frames with
        ///     an item over <see cref="ItemLimitMs" />, and per stage the largest item and the frames over the limit.
        ///     An item is the span between two asks of the budget (see <see cref="FrameBudget.TimeItems" />), so it
        ///     holds the bookkeeping of the turn around it; the probe switches the timing on.
        /// </summary>
        internal string ItemTimes(string label, int from, int to){
            CultureInfo inv = CultureInfo.InvariantCulture;
            from = math.clamp(from, 0, _itemMs.Count);
            to = math.clamp(to, from, _itemMs.Count);
            float[] stageMax = new float[StageCount];
            int[] stageOver = new int[StageCount];
            int over = 0;
            for (int i = from; i < to; i++){
                float ms = _itemMs[i];
                if (ms > ItemLimitMs) over++;
                int stage = _itemStage[i];
                if (stage < 0 || stage >= StageCount) continue;
                stageMax[stage] = math.max(stageMax[stage], ms);
                if (ms > ItemLimitMs) stageOver[stage]++;
            }

            StringBuilder text = new();
            text.Append(string.Format(inv, "[PipelineProbe] {0}: largest scheduler item per frame ms {1}; {2} frames " +
                                           "with an item over {3:0.##} ms; per stage (max ms / frames over):", label,
                Distribution(_itemMs, from, to), over, ItemLimitMs));
            for (int i = 0; i < StageCount; i++)
                text.Append(string.Format(inv, " {0} {1:0.000} / {2};", StageNames[i], stageMax[i], stageOver[i]));
            text.AppendLine();
            return text.ToString();
        }

        /// <summary>The largest scheduler item of the samples <paramref name="from" /> to <paramref name="to" />.</summary>
        internal float MaxItemMs(int from, int to){
            from = math.clamp(from, 0, _itemMs.Count);
            to = math.clamp(to, from, _itemMs.Count);
            float max = 0f;
            for (int i = from; i < to; i++) max = math.max(max, _itemMs[i]);
            return max;
        }

        /// <summary>
        ///     Forgets the completion times, so a second phase (the world settling after a flight) is timed from now.
        /// </summary>
        internal void RestartCompletion(){
            _stages.RestartCompletion();
        }

        /// <summary>
        ///     Share of the partitions in the columns within <paramref name="radius" /> chunks of the focus that
        ///     have a mesh.
        /// </summary>
        internal float Coverage(int radius){
            return _stages.Coverage(radius);
        }

        /// <summary>Takes one sample; call once per frame.</summary>
        internal void Sample(){
            Frames++;
            WorldStats stats = _world.Stats;
            PeakQueue[Data] = math.max(PeakQueue[Data], stats.DataQueue);
            PeakQueue[Light] = math.max(PeakQueue[Light], stats.LightQueue);
            PeakQueue[Mesh] = math.max(PeakQueue[Mesh], stats.MeshQueue);
            PeakQueue[Bake] = math.max(PeakQueue[Bake], stats.BakeQueue);
            PeakPendingBuilds = math.max(PeakPendingBuilds, PipelineStages.PendingBuilds);
            if (_meshJobs.RendererBehind) _framesRendererBehind++;

            long schedulerNs = 0;
            for (int i = 0; i < StageCount; i++){
                schedulerNs += _recorders[i].LastValue;
                if (!_stages.JobReady[i]()){
                    if (_stages.JobComplete[i]()) _framesFinished[i]++;
                    else _framesRunning[i]++;
                }
                else if (_stages.Sleeping[i]()){
                    _framesSleeping[i]++;
                }
                else{
                    _framesIdle[i]++;
                }
            }

            _stages.SampleCompletion(Seconds, Frames);

            for (int i = 0; i < _costRecorders.Length; i++){
                float ms = _costRecorders[i].LastValue * 1e-6f;
                if (ms <= 0f) continue;
                _costSumMs[i] += ms;
                _costMaxMs[i] = math.max(_costMaxMs[i], ms);
                _costFrames[i]++;
            }

            _frameMs.Add(Time.unscaledDeltaTime * 1000f);
            _schedulerMs.Add(schedulerNs * 1e-6f);
            _framesCut.Add(_scheduler.Budget.FramesCut);
            _effectiveBudgetMs.Add(_scheduler.Budget.EffectiveMilliseconds);
            // The scheduler ticks in Update, before this sample: the item time and the stage are this frame's
            _itemMs.Add(_scheduler.Budget.LongestItemMs);
            _itemStage.Add(_scheduler.LastStage);
            SampleDequeue();
            _attribution?.Sample();
        }

        /// <summary>
        ///     The counters of the dequeue loop. The scheduler runs in Update, before this sample, so the counters
        ///     already hold this frame's loop; a recorder's last value is the frame before this one. The loop counted
        ///     in the sample before is therefore paired with the marker time read now.
        /// </summary>
        private void SampleDequeue(){
            float markerMs = _dequeueRecorder.LastValue * 1e-6f;
            if (_hasPendingCall){
                _pendingCall.Ms = markerMs;
                _dequeueCalls.Add(_pendingCall);
            }
            else if (markerMs > 0f){
                _markerWithoutCall++;
            }

            MeshJobStateHandler.DequeueCounts now = _meshHandler.Dequeued;
            MeshJobStateHandler.DequeueCounts before = _dequeueTotals.Count > 0 ? _dequeueTotals[^1] : _dequeueAtStart;
            _hasPendingCall = now.Calls > before.Calls;
            if (_hasPendingCall) _pendingCall = new DequeueCall{ Sample = _dequeueTotals.Count, Counts = Minus(now, before) };
            _dequeueTotals.Add(now);
        }

        private static MeshJobStateHandler.DequeueCounts Minus(MeshJobStateHandler.DequeueCounts a,
            MeshJobStateHandler.DequeueCounts b){
            return new MeshJobStateHandler.DequeueCounts{
                Calls = a.Calls - b.Calls, Cuts = a.Cuts - b.Cuts, QueueAtStart = a.QueueAtStart - b.QueueAtStart, Taken = a.Taken - b.Taken,
                KeptWithVoxels = a.KeptWithVoxels - b.KeptWithVoxels, KeptEmpty = a.KeptEmpty - b.KeptEmpty,
                DroppedNotRelevant = a.DroppedNotRelevant - b.DroppedNotRelevant,
                DroppedNotLit = a.DroppedNotLit - b.DroppedNotLit,
                DroppedNotNeeded = a.DroppedNotNeeded - b.DroppedNotNeeded,
                QueueEmptied = a.QueueEmptied - b.QueueEmptied,
                RequeuedNotRelevant = a.RequeuedNotRelevant - b.RequeuedNotRelevant,
                RequeuedNotLit = a.RequeuedNotLit - b.RequeuedNotLit,
                RequeuedNotNeeded = a.RequeuedNotNeeded - b.RequeuedNotNeeded
            };
        }

        /// <summary>
        ///     What the dequeue loop of the mesh dispatch did in the samples <paramref name="from" /> (included) to
        ///     <paramref name="to" /> (excluded): the entries it took, kept and dropped (by check), the dropped ones
        ///     a scan queued again (attributed runs only), the entries and the marker time per call and the time per
        ///     entry, and the slowest calls.
        /// </summary>
        internal string DequeueReport(string label, int from, int to){
            CultureInfo inv = CultureInfo.InvariantCulture;
            from = math.clamp(from, 0, _dequeueTotals.Count);
            to = math.clamp(to, from, _dequeueTotals.Count);
            MeshJobStateHandler.DequeueCounts start = from == 0 ? _dequeueAtStart : _dequeueTotals[from - 1];
            MeshJobStateHandler.DequeueCounts end = to == 0 ? _dequeueAtStart : _dequeueTotals[to - 1];
            MeshJobStateHandler.DequeueCounts t = Minus(end, start);
            string head = $"[PipelineProbe] {label}";
            StringBuilder text = new();
            long dropped = t.DroppedNotRelevant + t.DroppedNotLit + t.DroppedNotNeeded;
            long kept = t.KeptWithVoxels + t.KeptEmpty;
            double taken = math.max(t.Taken, 1);
            text.AppendLine(string.Format(inv,
                "{0}: dequeue loop: {1} calls ({19} cut by the budget), queue {2:0} entries at the start on average, " +
                "emptied it in {3}; took " +
                "{4} entries ({5:0.0} per call), kept {6} ({7:0.0} %: {8} with voxels, {9} empty), dropped {10} " +
                "({11:0.0} %): not relevant {12} ({13:0.0} %), neighbourhood not lit {14} ({15:0.0} %), no mesh " +
                "needed {16} ({17:0.0} %); {18:0.00} entries taken per kept one",
                head, t.Calls, t.QueueAtStart / (double)math.max(t.Calls, 1), t.QueueEmptied, t.Taken,
                t.Taken / (double)math.max(t.Calls, 1), kept, 100 * kept / taken, t.KeptWithVoxels, t.KeptEmpty,
                dropped, 100 * dropped / taken, t.DroppedNotRelevant, 100 * t.DroppedNotRelevant / taken,
                t.DroppedNotLit, 100 * t.DroppedNotLit / taken, t.DroppedNotNeeded, 100 * t.DroppedNotNeeded / taken,
                t.Taken / (double)math.max(kept, 1), t.Cuts));
            text.AppendLine(string.Format(inv,
                "{0}: dropped entries a scan queued again{1}: not relevant {2}, neighbourhood not lit {3}, no mesh " +
                "needed {4} ({5} of {6} dropped)",
                head, _meshHandler.TrackRequeues ? "" : " (not tracked in this run)", t.RequeuedNotRelevant,
                t.RequeuedNotLit, t.RequeuedNotNeeded, t.RequeuedNotRelevant + t.RequeuedNotLit + t.RequeuedNotNeeded,
                dropped));

            List<DequeueCall> calls = new();
            foreach (DequeueCall call in _dequeueCalls)
                if (call.Sample >= from && call.Sample < to) calls.Add(call);
            if (calls.Count == 0) return text.ToString();

            List<float> takenPerCall = new(calls.Count), keptPerCall = new(calls.Count), ms = new(calls.Count);
            double sumMs = 0, sumTaken = 0, slowMs = 0, slowTaken = 0, slowKept = 0, slowDropped = 0;
            int slowCalls = 0, zeroMs = 0;
            foreach (DequeueCall call in calls){
                MeshJobStateHandler.DequeueCounts c = call.Counts;
                takenPerCall.Add(c.Taken);
                keptPerCall.Add(c.KeptWithVoxels + c.KeptEmpty);
                ms.Add(call.Ms);
                sumMs += call.Ms;
                sumTaken += c.Taken;
                if (call.Ms <= 0f) zeroMs++;
                if (call.Ms < 1f) continue;
                slowCalls++;
                slowMs += call.Ms;
                slowTaken += c.Taken;
                slowKept += c.KeptWithVoxels + c.KeptEmpty;
                slowDropped += c.DroppedNotRelevant + c.DroppedNotLit + c.DroppedNotNeeded;
            }

            text.AppendLine(string.Format(inv,
                "{0}: dequeue per call: entries taken {1}; kept {2}; marker ms {3}; {4:0.00} us per entry taken " +
                "({5:0} entries in {6:0.0} ms); calls of 1 ms or more: {7}, {8:0.00} us per entry, {9:0.0} taken, " +
                "{10:0.0} kept and {11:0.0} dropped per call; check of the pairing: {12} calls read 0 ms, {13} " +
                "samples read marker time without a call",
                head, Distribution(takenPerCall, 0, takenPerCall.Count), Distribution(keptPerCall, 0, keptPerCall.Count),
                Distribution(ms, 0, ms.Count), 1000 * sumMs / math.max(sumTaken, 1), sumTaken, sumMs, slowCalls,
                1000 * slowMs / math.max(slowTaken, 1), slowTaken / math.max(slowCalls, 1),
                slowKept / math.max(slowCalls, 1), slowDropped / math.max(slowCalls, 1), zeroMs, _markerWithoutCall));

            calls.Sort((a, b) => b.Ms.CompareTo(a.Ms));
            text.Append(head).Append(": slowest dequeue calls (ms, queue at start, taken, kept with voxels / empty, " +
                                     "dropped not relevant / not lit / not needed):");
            for (int i = 0; i < math.min(10, calls.Count); i++){
                MeshJobStateHandler.DequeueCounts c = calls[i].Counts;
                text.Append(string.Format(inv, " {0:0.00} q{1} t{2} k{3}/{4} d{5}/{6}/{7};", calls[i].Ms,
                    c.QueueAtStart, c.Taken, c.KeptWithVoxels, c.KeptEmpty, c.DroppedNotRelevant, c.DroppedNotLit,
                    c.DroppedNotNeeded));
            }

            text.AppendLine();
            return text.ToString();
        }

        /// <summary>The samples as log lines, each starting with "[PipelineProbe] <paramref name="label" />".</summary>
        internal string Report(string label){
            CultureInfo inv = CultureInfo.InvariantCulture;
            StringBuilder text = new();
            string head = $"[PipelineProbe] {label}";
            float seconds = Seconds;
            text.AppendLine(string.Format(inv, "{0}: {1} frames in {2:0.0} s ({3:0} fps), {4} chunks loaded, " +
                                               "{5} partitions active, {6} collidable; mesh batch {7}, GPU build slots {8}",
                head, Frames, seconds, Frames / math.max(seconds, 1e-3f), _world.Stats.LoadedChunks, _meshMap.Count,
                _colliderSet.Count, _meshBatchSize, VoxelWorldRenderer.Instance.BuildSlots));
            text.AppendLine(string.Format(inv, "{0}: queue peaks data {1}, light {2}, mesh {3}, bake {4}, " +
                                               "renderer pending builds {5}",
                head, PeakQueue[Data], PeakQueue[Light], PeakQueue[Mesh], PeakQueue[Bake], PeakPendingBuilds));

            int repeated = 0;
            foreach (KeyValuePair<int3, int> pair in _dispatchCounts)
                if (pair.Value > 1) repeated += pair.Value - 1;
            int empty = 0;
            foreach (KeyValuePair<int3, ChunkPartition> pair in _meshMap)
                if (pair.Value.ColliderMesh.vertexCount == 0) empty++;
            text.AppendLine(string.Format(inv, "{0}: mesh batches {1} with {2} partitions ({3:0.00} per batch, " +
                                               "{4:0.00} frames per batch, {5:0.000} partitions per frame, " +
                                               "{6:0} per second), {7} repeated dispatches, " +
                                               "{8} of {9} active partitions have an empty collider mesh",
                head, MeshBatches, MeshPartitions, MeshPartitions / (float)math.max(MeshBatches, 1),
                Frames / (float)math.max(MeshBatches, 1), MeshPartitions / (float)math.max(Frames, 1),
                MeshPartitions / math.max(seconds, 1e-3f), repeated, empty, _meshMap.Count));

            int withoutMesh = PartitionsWithoutMesh;
            int meshed = MeshedPartitions;
            text.AppendLine(string.Format(inv, "{0}: {1} partitions hold no voxel and became active without a mesh " +
                                               "job or a GPU build ({2:0.0} % of {3}); {4} got a mesh job, {5} of " +
                                               "them needed no bake (empty collider mesh); {6} partitions without a " +
                                               "voxel were dispatched",
                head, withoutMesh, 100f * withoutMesh / math.max(withoutMesh + meshed, 1), withoutMesh + meshed, meshed,
                PartitionsWithoutBake, DispatchedWithoutVoxels));

            for (int i = 0; i < StageCount; i++){
                text.AppendLine(string.Format(inv, "{0}: {1} job frames: running {2}, finished but not collected {3}, " +
                                                   "idle with work {4}, sleeping {5}; done after {6:0.0} s (frame {7})",
                    head, StageNames[i], _framesRunning[i], _framesFinished[i], _framesIdle[i], _framesSleeping[i],
                    _stages.DoneSeconds(i), _stages.DoneFrame(i)));
            }

            text.AppendLine(string.Format(inv, "{0}: renderer builds done after {1:0.0} s (frame {2}); the mesh stage " +
                                               "waited for the renderer in {3} frames",
                head, _stages.GpuDoneSeconds, _stages.GpuDoneFrame, _framesRendererBehind));
            text.Append(FrameTimes(label, 0, Frames));
            text.Append(ItemTimes(label, 0, Frames));
            text.Append(head).Append(": main-thread ms per frame it ran in (avg / max / frames):");
            for (int i = 0; i < CostNames.Length; i++){
                text.Append(string.Format(inv, " {0} {1:0.000} / {2:0.000} / {3};", CostNames[i],
                    _costSumMs[i] / math.max(_costFrames[i], 1), _costMaxMs[i], _costFrames[i]));
            }

            text.AppendLine();
            return text.ToString();
        }

        private static string Distribution(List<float> values, int from, int to){
            return FrameTimeStats.Distribution(values, from, to);
        }

        /// <summary>How many of the samples are longer than a frame of 120 and of 60 frames per second.</summary>
        private static string OverGoal(List<float> values, int from, int to){
            return FrameTimeStats.OverGoal(values, from, to);
        }
    }
}
