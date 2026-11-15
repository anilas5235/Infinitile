using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Engine.Scripts.Behaviour;
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
        internal const int Data = 0;
        internal const int Light = 1;
        internal const int Mesh = 2;
        internal const int Bake = 3;
        private const int StageCount = 4;
        private static readonly string[] StageNames = { "data", "light", "mesh", "bake" };

        private readonly VoxelWorld _world;
        private readonly Func<bool>[] _sleeping = new Func<bool>[StageCount];
        private readonly Func<bool>[] _jobReady = new Func<bool>[StageCount];
        private readonly Func<bool>[] _jobComplete = new Func<bool>[StageCount];
        private readonly Dictionary<int3, ChunkPartition> _meshMap;
        private readonly HashSet<int3> _colliderSet;
        private readonly HashSet<int3> _pendingBuilds;
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
        private readonly VoxelEngineScheduler _scheduler;
        private readonly int _framesCutAtStart;

        private readonly int[] _framesIdle = new int[StageCount];
        private readonly int[] _framesRunning = new int[StageCount];
        private readonly int[] _framesFinished = new int[StageCount];
        private readonly int[] _framesSleeping = new int[StageCount];
        private readonly float[] _doneSeconds = { -1f, -1f, -1f, -1f };
        private readonly int[] _doneFrames = new int[StageCount];
        private readonly Func<bool> _rendererBehind;
        private readonly CMeshBuildScheduler _meshJobs;
        private readonly int _meshBatchSize;
        private int _framesRendererBehind;
        private float _gpuDoneSeconds = -1f;
        private int _gpuDoneFrame;
        private float _startTime;
        private readonly FrameAttribution _attribution;

        /// <param name="world">The world to sample.</param>
        /// <param name="attribute">
        ///     Also records where the main thread spends every frame, for <see cref="Attribution" />.
        /// </param>
        internal PipelineProbe(VoxelWorld world, bool attribute = false){
            _world = world;
            if (attribute) _attribution = new FrameAttribution();
            VoxelEngineScheduler scheduler = PlayModeFixtures.GetPrivateField<VoxelEngineScheduler>(world, "_scheduler");
            _scheduler = scheduler;
            _framesCutAtStart = scheduler.Budget.FramesCut;
            DataJobStateHandler data = PlayModeFixtures.GetPrivateField<DataJobStateHandler>(scheduler, "_dataJobHandler");
            LightJobStateHandler light = PlayModeFixtures.GetPrivateField<LightJobStateHandler>(scheduler, "_lightJobHandler");
            MeshJobStateHandler mesh = PlayModeFixtures.GetPrivateField<MeshJobStateHandler>(scheduler, "_meshJobHandler");
            ColliderJobStateHandler bake =
                PlayModeFixtures.GetPrivateField<ColliderJobStateHandler>(scheduler, "_colliderJobHandler");
            ChunkScheduler chunkJobs = PlayModeFixtures.GetPrivateField<ChunkScheduler>(scheduler, "_chunkScheduler");
            LightScheduler lightJobs = PlayModeFixtures.GetPrivateField<LightScheduler>(scheduler, "_lightScheduler");
            CMeshBuildScheduler meshJobs =
                PlayModeFixtures.GetPrivateField<CMeshBuildScheduler>(scheduler, "_cMeshBuildScheduler");
            ColliderBakeScheduler bakeJobs =
                PlayModeFixtures.GetPrivateField<ColliderBakeScheduler>(scheduler, "_colliderBakeScheduler");

            _sleeping[Data] = () => data.Sleeping;
            _sleeping[Light] = () => light.Sleeping;
            _sleeping[Mesh] = () => mesh.Sleeping;
            _sleeping[Bake] = () => bake.Sleeping;
            _jobReady[Data] = () => chunkJobs.IsReady;
            _jobReady[Light] = () => lightJobs.IsReady;
            _jobReady[Mesh] = () => meshJobs.IsReady;
            _jobReady[Bake] = () => bakeJobs.IsReady;
            _jobComplete[Data] = () => chunkJobs.IsComplete;
            _jobComplete[Light] = () => lightJobs.IsComplete;
            _jobComplete[Mesh] = () => meshJobs.IsComplete;
            _jobComplete[Bake] = () => bakeJobs.IsComplete;
            _rendererBehind = () => meshJobs.RendererBehind;
            _meshJobs = meshJobs;
            _meshBatchSize = mesh.MeshBatchSize;

            ChunkPool pool = PlayModeFixtures.GetPrivateField<ChunkPool>(world, "_chunkPool");
            _meshMap = PlayModeFixtures.GetPrivateField<Dictionary<int3, ChunkPartition>>(pool, "_meshMap");
            _colliderSet = PlayModeFixtures.GetPrivateField<HashSet<int3>>(pool, "_colliderSet");
            _pendingBuilds =
                PlayModeFixtures.GetPrivateField<HashSet<int3>>(VoxelWorldRenderer.Instance, "_pendingBuildSet");

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

        /// <summary>A frame of 120 frames per second, the goal of the performance work (2026-11-14).</summary>
        internal const float GoalFrameMs = 1000f / 120f;

        /// <summary>A frame of 60 frames per second; a longer one is a stutter the player sees.</summary>
        internal const float StutterFrameMs = 1000f / 60f;

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
        internal float BuiltSeconds => _doneSeconds[Bake];

        internal int BuiltFrames => _doneFrames[Bake];

        /// <summary>True once every stage slept after its upstream stages and no GPU build is pending.</summary>
        internal bool Built => _doneSeconds[Bake] >= 0f && _gpuDoneSeconds >= 0f;

        public void Dispose(){
            if (_world != null) _world.PartitionBuildRequested -= OnMeshBatch;
            for (int i = 0; i < StageCount; i++) _recorders[i].Dispose();
            for (int i = 0; i < _costRecorders.Length; i++) _costRecorders[i].Dispose();
            _attribution?.Dispose();
        }

        /// <summary>
        ///     Where the main thread spent the frames <paramref name="from" /> (included) to <paramref name="to" />
        ///     (excluded), as the table of <see cref="FrameAttribution.Table" />; empty without attribution.
        /// </summary>
        internal string Attribution(string label, int from, int to){
            return _attribution != null ? _attribution.Table(label, _frameMs, from, to) : "";
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
                "main-thread budget {5:0.##} ms, cut a turn in {6} frames\n", label,
                Distribution(_frameMs, from, to), OverGoal(_frameMs, from, to), Distribution(_schedulerMs, from, to),
                OverGoal(_schedulerMs, from, to), _scheduler.Budget.Milliseconds, FramesCutBefore(to) - FramesCutBefore(from));
        }

        /// <summary>Frames in which the budget had stopped a stage's turn before sample <paramref name="sample" />.</summary>
        private int FramesCutBefore(int sample){
            sample = math.clamp(sample, 0, _framesCut.Count);
            return sample == 0 ? _framesCutAtStart : _framesCut[sample - 1];
        }

        /// <summary>
        ///     Forgets the completion times, so a second phase (the world settling after a flight) is timed from now.
        /// </summary>
        internal void RestartCompletion(){
            for (int i = 0; i < StageCount; i++) _doneSeconds[i] = -1f;
            _gpuDoneSeconds = -1f;
        }

        /// <summary>
        ///     Share of the partitions in the columns within <paramref name="radius" /> chunks of the focus that
        ///     have a mesh.
        /// </summary>
        internal float Coverage(int radius){
            int3 focus = _world.Focus.Pos;
            int meshed = 0, total = 0;
            for (int x = -radius; x <= radius; x++)
            for (int z = -radius; z <= radius; z++)
            for (int y = 0; y < VoxelConstants.PartitionsPerChunk; y++){
                total++;
                if (_meshMap.ContainsKey(new int3(focus.x + x, y, focus.z + z))) meshed++;
            }

            return meshed / (float)total;
        }

        /// <summary>Takes one sample; call once per frame.</summary>
        internal void Sample(){
            Frames++;
            WorldStats stats = _world.Stats;
            PeakQueue[Data] = math.max(PeakQueue[Data], stats.DataQueue);
            PeakQueue[Light] = math.max(PeakQueue[Light], stats.LightQueue);
            PeakQueue[Mesh] = math.max(PeakQueue[Mesh], stats.MeshQueue);
            PeakQueue[Bake] = math.max(PeakQueue[Bake], stats.BakeQueue);
            PeakPendingBuilds = math.max(PeakPendingBuilds, _pendingBuilds.Count);
            if (_rendererBehind()) _framesRendererBehind++;

            long schedulerNs = 0;
            bool upstreamDone = stats.LoadedChunks >= _world.Settings.Chunk.LoadDistance.SquareSize();
            for (int i = 0; i < StageCount; i++){
                schedulerNs += _recorders[i].LastValue;
                bool sleeping = _sleeping[i]();
                if (!_jobReady[i]()){
                    if (_jobComplete[i]()) _framesFinished[i]++;
                    else _framesRunning[i]++;
                }
                else if (sleeping){
                    _framesSleeping[i]++;
                }
                else{
                    _framesIdle[i]++;
                }

                upstreamDone &= sleeping && _jobReady[i]();
                if (upstreamDone && _doneSeconds[i] < 0f){
                    _doneSeconds[i] = Seconds;
                    _doneFrames[i] = Frames;
                }
            }

            if (_doneSeconds[Mesh] >= 0f && _gpuDoneSeconds < 0f && _pendingBuilds.Count == 0){
                _gpuDoneSeconds = Seconds;
                _gpuDoneFrame = Frames;
            }

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
            _attribution?.Sample();
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
                    _doneSeconds[i], _doneFrames[i]));
            }

            text.AppendLine(string.Format(inv, "{0}: renderer builds done after {1:0.0} s (frame {2}); the mesh stage " +
                                               "waited for the renderer in {3} frames",
                head, _gpuDoneSeconds, _gpuDoneFrame, _framesRendererBehind));
            text.Append(FrameTimes(label, 0, Frames));
            text.Append(head).Append(": main-thread ms per frame it ran in (avg / max / frames):");
            for (int i = 0; i < CostNames.Length; i++){
                text.Append(string.Format(inv, " {0} {1:0.000} / {2:0.000} / {3};", CostNames[i],
                    _costSumMs[i] / math.max(_costFrames[i], 1), _costMaxMs[i], _costFrames[i]));
            }

            text.AppendLine();
            return text.ToString();
        }

        private static string Distribution(List<float> values, int from, int to){
            from = math.clamp(from, 0, values.Count);
            to = math.clamp(to, from, values.Count);
            if (to == from) return "no samples";
            List<float> sorted = values.GetRange(from, to - from);
            sorted.Sort();
            double sum = 0;
            foreach (float value in sorted) sum += value;
            int last = sorted.Count - 1;
            return string.Format(CultureInfo.InvariantCulture,
                "avg {0:0.000}, p50 {1:0.000}, p95 {2:0.000}, p99 {3:0.000}, max {4:0.000}",
                sum / sorted.Count, sorted[sorted.Count / 2], sorted[(int)(last * 0.95f)], sorted[(int)(last * 0.99f)],
                sorted[last]);
        }

        /// <summary>How many of the samples are longer than a frame of 120 and of 60 frames per second.</summary>
        private static string OverGoal(List<float> values, int from, int to){
            from = math.clamp(from, 0, values.Count);
            to = math.clamp(to, from, values.Count);
            int overGoal = 0, overStutter = 0;
            for (int i = from; i < to; i++){
                if (values[i] > GoalFrameMs) overGoal++;
                if (values[i] > StutterFrameMs) overStutter++;
            }

            int count = math.max(to - from, 1);
            return string.Format(CultureInfo.InvariantCulture,
                "{0} of {1} over 8.33 ms ({2:0.00} %), {3} over 16.67 ms ({4:0.00} %)",
                overGoal, to - from, 100f * overGoal / count, overStutter, 100f * overStutter / count);
        }
    }
}
