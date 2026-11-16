using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Engine.Scripts.Render;
using Engine.Scripts.Settings;
using Engine.Scripts.VoxelConfig;
using Engine.Scripts.World;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Engine.Scripts.Diagnostics{
    /// <summary>
    ///     The goal run of a player build: started with the command-line flag <c>-goalrun</c>, it runs the rig of
    ///     <see cref="GoalRunRig" /> in the shipped scene once per draw distance, writes the frame times of every
    ///     phase to a text file and quits. Without the flag nothing of it is created.
    ///     <para>
    ///         Optional arguments: <c>-goalrun-out &lt;file&gt;</c> (default: a new file in <c>GoalRunResults</c>
    ///         next to the executable), <c>-goalrun-draw 6,12</c> (the draw distances, in this order),
    ///         <c>-goalrun-label &lt;text&gt;</c> (a name for the run in the file and the file name),
    ///         <c>-goalrun-timeout &lt;minutes&gt;</c> (the run gives up, writes what it has and quits; default 30).
    ///         Window size and mode come from Unity's own flags (<c>-screen-width</c>, <c>-screen-height</c>,
    ///         <c>-screen-fullscreen</c>, <c>-window-mode</c>) or the last start of the player.
    ///     </para>
    ///     <para>
    ///         Every draw distance starts a fresh world (the scene is loaded again with the draw distance set on the
    ///         settings object in memory) and measures: the first <see cref="GoalRunRig.StableAfterSeconds" /> of the
    ///         build (not judged), standing until <see cref="GoalRunRig.StandSeconds" />, then each flight of
    ///         <see cref="GoalRunRig.GoalFlightSpeeds" /> and the stop after it until the world is built again. The
    ///         frame time is <see cref="Time.unscaledDeltaTime" /> (the wall clock between two frames), sampled
    ///         once per frame after Update, as the pipeline tests do. The frame cap is off (no vertical sync, no
    ///         target frame rate) and the player keeps running when its window loses the focus; the frames without
    ///         focus are counted per phase.
    ///     </para>
    /// </summary>
    internal sealed class GoalRunner : MonoBehaviour{
        internal const string Flag = "-goalrun";
        private const string OutFlag = "-goalrun-out";
        private const string DrawFlag = "-goalrun-draw";
        private const string LabelFlag = "-goalrun-label";
        private const string TimeoutFlag = "-goalrun-timeout";
        private const string ResultFolder = "GoalRunResults";
        private const string BuildInfoFile = "build-info.txt";
        private const int SampleCapacity = 1 << 17;

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private Options _options;
        private VoxelEngineSettings _settings; // held so the scene reloads keep the object with the draw distance
        private float _runStart;
        private bool _finished;
        private int _errorsLogged;
        private readonly StringBuilder _header = new();
        private readonly List<DrawRun> _runs = new();

        // The samples of the draw distance being measured: one entry per frame, the counters cumulative
        private readonly List<float> _frameMs = new(SampleCapacity);
        private readonly List<int> _framesCut = new(SampleCapacity);
        private readonly List<int> _gcCount = new(SampleCapacity);
        private readonly List<int> _unfocused = new(SampleCapacity);

        // The per-frame trace: what else happened in each sample (counters cumulative; see AppendTrace)
        private readonly List<float> _sampleSeconds = new(SampleCapacity);
        private readonly List<float> _focusX = new(SampleCapacity);
        private readonly List<int> _storeGrows = new(SampleCapacity);
        private readonly List<int> _storePages = new(SampleCapacity);
        private readonly List<int> _storeUsed = new(SampleCapacity);
        private readonly List<int> _buffers = new(SampleCapacity); // solid | transparent << 10 | foliage << 20
        private int _unfocusedCount;
        private int _framesCutAtStart, _gcAtStart;
        private float _drawStart;

        private int Frames => _frameMs.Count;
        private float Seconds => Time.realtimeSinceStartup - _drawStart;

        /// <summary>Before the first scene builds its world: a goal run ignores the player's saved choices.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void MarkAutomatedIfRequested(){
            if (IndexOf(Environment.GetCommandLineArgs(), Flag) >= 0) AutomatedRun.Active = true;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void StartIfRequested(){
            string[] args = Environment.GetCommandLineArgs();
            if (IndexOf(args, Flag) < 0) return;

            GameObject host = new("GoalRunner");
            DontDestroyOnLoad(host);
            host.AddComponent<GoalRunner>()._options = Options.Parse(args);
        }

        private static int IndexOf(string[] args, string flag){
            for (int i = 0; i < args.Length; i++)
                if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
                    return i;
            return -1;
        }

        /// <summary>The arguments of the run.</summary>
        private sealed class Options{
            internal int[] DrawDistances = { 6, 12 };
            internal string OutPath;
            internal string Label = "";
            internal float TimeoutMinutes = 30f;
            internal readonly List<string> Problems = new();

            internal static Options Parse(string[] args){
                Options options = new();
                options.OutPath = Value(args, OutFlag);
                string label = Value(args, LabelFlag);
                if (!string.IsNullOrWhiteSpace(label)) options.Label = label.Trim();

                string draws = Value(args, DrawFlag);
                if (!string.IsNullOrWhiteSpace(draws)){
                    List<int> list = new();
                    foreach (string part in draws.Split(',', ';'))
                        if (int.TryParse(part.Trim(), NumberStyles.Integer, Inv, out int draw) && draw > 0) list.Add(draw);
                        else options.Problems.Add($"ignored draw distance '{part}' of {DrawFlag}");
                    if (list.Count > 0) options.DrawDistances = list.ToArray();
                }

                string timeout = Value(args, TimeoutFlag);
                if (!string.IsNullOrWhiteSpace(timeout)){
                    if (float.TryParse(timeout, NumberStyles.Float, Inv, out float minutes) && minutes > 0f)
                        options.TimeoutMinutes = minutes;
                    else options.Problems.Add($"ignored {TimeoutFlag} '{timeout}'");
                }

                return options;
            }

            private static string Value(string[] args, string flag){
                int index = IndexOf(args, flag);
                return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
            }
        }

        /// <summary>One phase of a draw distance: a range of samples and what was read during it.</summary>
        private sealed class Phase{
            internal string Name;
            internal int From, To;
            internal bool Judged = true;
            internal bool Flight;
            internal float NearAvg = -1f, NearLow = -1f, WindowAvg = -1f, WindowEnd = -1f;

            /// <summary>Stop phases: seconds from the stop until the world was built again (-1: not within the timeout).</summary>
            internal float SettledSeconds = -2f;

            internal FrameTimeStats.Summary Times;
            internal int FramesCut, GcCollections, Unfocused;

            /// <summary>Interval store: grows, pages at the end, most pages in use during the phase.</summary>
            internal int StoreGrows, StorePagesEnd, StoreUsedMax;
        }

        /// <summary>Everything measured at one draw distance.</summary>
        private sealed class DrawRun{
            internal int Draw;
            internal string Sizes;
            internal string Window;
            internal float BuiltSeconds = -1f;
            internal int BuiltFrame = -1;
            internal string Problem;
            internal readonly List<Phase> Phases = new();

            /// <summary>The trace rows (frames over the goal, or with a store grow, a render buffer added or a GC).</summary>
            internal readonly StringBuilder Trace = new();

            /// <summary>The interval store's most pages in use at once, over the whole world.</summary>
            internal int StorePeak = -1;
        }

        private void Awake(){
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1;
            Application.runInBackground = true;
            Application.logMessageReceived += CountErrors;
            _runStart = Time.realtimeSinceStartup;
        }

        private void OnDestroy(){
            Application.logMessageReceived -= CountErrors;
        }

        private void CountErrors(string condition, string stackTrace, LogType type){
            if (type is LogType.Error or LogType.Exception or LogType.Assert) _errorsLogged++;
        }

        private void Update(){
            if (_finished || Time.realtimeSinceStartup - _runStart < _options.TimeoutMinutes * 60f) return;

            StopAllCoroutines();
            Finish($"stopped after the timeout of {_options.TimeoutMinutes:0.#} minutes; the phases listed are complete");
        }

        private IEnumerator Start(){
            WriteHeader();
            foreach (int draw in _options.DrawDistances){
                DrawRun run = new(){ Draw = draw };
                _runs.Add(run);
                yield return LoadFreshWorld(draw, run);
                if (run.Problem != null) continue;
                yield return Measure(run);
            }

            Finish(null);
        }

        /// <summary>
        ///     Loads the scene again with <paramref name="draw" /> as the draw distance. Waits for an idle scheduler
        ///     first: unloading the scene while a mesh job runs frees data the job reads.
        /// </summary>
        private IEnumerator LoadFreshWorld(int draw, DrawRun run){
            VoxelWorld world = VoxelWorld.Instance;
            if (world == null || world.Scheduler == null){
                run.Problem = "no voxel world in the scene";
                yield break;
            }

            _settings = world.Settings;
            PipelineStages stages = new(world);
            float deadline = Time.realtimeSinceStartup + 300f;
            while (!stages.Idle && Time.realtimeSinceStartup < deadline) yield return null;

            _settings.Chunk.DrawDistance = draw;
            AsyncOperation load = SceneManager.LoadSceneAsync(SceneManager.GetActiveScene().buildIndex, LoadSceneMode.Single);
            while (!load.isDone) yield return null;

            deadline = Time.realtimeSinceStartup + 60f;
            while (VoxelWorld.Instance == null || DataImporter.Instance == null || VoxelWorld.Instance.ChunkManager == null){
                if (Time.realtimeSinceStartup > deadline){
                    run.Problem = "the engine did not start within 60 s after the scene load";
                    yield break;
                }

                yield return null;
            }

            yield return null; // let Start() run on everything
            world = VoxelWorld.Instance;
            if (world.Settings.Chunk.DrawDistance != draw)
                run.Problem = $"the world started with draw distance {world.Settings.Chunk.DrawDistance}, not {draw}";
        }

        private IEnumerator Measure(DrawRun run){
            VoxelWorld world = VoxelWorld.Instance;
            int draw = world.Settings.Chunk.DrawDistance;
            VoxelWorldRenderer renderer = VoxelWorldRenderer.Instance;
            run.Sizes = string.Format(Inv, "mesh batch {0}, GPU build slots {1}, load distance {2}",
                world.Scheduler.MeshHandler.MeshBatchSize, renderer ? renderer.BuildSlots : -1,
                world.Settings.Chunk.LoadDistance);
            run.Window = WindowText();
            Transform focus = world.FocusTransform;
            GoalRunRig.FlightBody(focus);
            PipelineStages stages = new(world);
            _frameMs.Clear();
            _framesCut.Clear();
            _gcCount.Clear();
            _unfocused.Clear();
            _sampleSeconds.Clear();
            _focusX.Clear();
            _storeGrows.Clear();
            _storePages.Clear();
            _storeUsed.Clear();
            _buffers.Clear();
            _unfocusedCount = 0;
            _framesCutAtStart = world.Scheduler.Budget.FramesCut;
            _gcAtStart = GC.CollectionCount(0);
            _drawStart = Time.realtimeSinceStartup;

            // Standing where the flights start: the first build, and the frames after it
            int stableFrom = -1;
            while (Seconds < GoalRunRig.StandSeconds){
                yield return null;
                if (stableFrom < 0 && Seconds >= GoalRunRig.StableAfterSeconds) stableFrom = Frames;
                Sample(world, stages);
            }

            if (stableFrom < 0) stableFrom = Frames;
            int standEnd = Frames;
            if (stages.Built){
                run.BuiltSeconds = stages.BuiltSeconds;
                run.BuiltFrame = stages.BuiltFrame;
            }

            run.Phases.Add(new Phase{
                Name = $"build, first {GoalRunRig.StableAfterSeconds:0} s", From = 0, To = stableFrom, Judged = false
            });
            run.Phases.Add(new Phase{
                Name = $"stand {GoalRunRig.StableAfterSeconds:0}-{GoalRunRig.StandSeconds:0} s", From = stableFrom,
                To = standEnd
            });

            foreach (float speed in GoalRunRig.GoalFlightSpeeds) yield return FlyAndStop(run, world, stages, focus, draw, speed);

            foreach (Phase phase in run.Phases) Summarize(phase);
            AppendTrace(run);
            if (renderer) run.StorePeak = renderer.IntervalStorePeakPages;
        }

        /// <summary>One flight along +x at <paramref name="speed" />, and the stop until the world is built again.</summary>
        private IEnumerator FlyAndStop(DrawRun run, VoxelWorld world, PipelineStages stages, Transform focus, int draw,
            float speed){
            // The same frame as in the pipeline tests' goal run, where it writes the report of the phase before
            yield return null;
            Sample(world, stages);
            int flightBegin = Frames;
            Transform body = GoalRunRig.FlightBody(focus);
            Vector3 start = body.position;
            float flightStart = Time.realtimeSinceStartup;
            double nearSum = 0, windowSum = 0;
            float nearMin = 1f;
            int coverageSamples = 0;
            while (true){
                float elapsed = Time.realtimeSinceStartup - flightStart;
                if (elapsed >= GoalRunRig.FlightSeconds) break;
                body.position = GoalRunRig.FlightPosition(start, speed, elapsed);
                yield return null;
                Sample(world, stages);
                if (Frames % GoalRunRig.CoverageEveryFrames != 0) continue;
                float near = stages.Coverage(GoalRunRig.NearRadius);
                nearSum += near;
                nearMin = math.min(nearMin, near);
                windowSum += stages.Coverage(draw);
                coverageSamples++;
            }

            int flightEnd = Frames;
            run.Phases.Add(new Phase{
                Name = string.Format(Inv, "flight {0:0} u/s {1:0} s", speed, GoalRunRig.FlightSeconds),
                From = flightBegin, To = flightEnd, Flight = true,
                NearAvg = (float)(nearSum / math.max(coverageSamples, 1)), NearLow = nearMin,
                WindowAvg = (float)(windowSum / math.max(coverageSamples, 1)), WindowEnd = stages.Coverage(draw)
            });

            // The focus stops; how long until the world around it is complete?
            stages.RestartCompletion();
            float settleStart = Seconds;
            bool timedOut = false;
            while (!stages.Built){
                if (Seconds - settleStart > GoalRunRig.BuildTimeoutSeconds){
                    timedOut = true;
                    break;
                }

                yield return null;
                Sample(world, stages);
            }

            run.Phases.Add(new Phase{
                Name = string.Format(Inv, "stop after {0:0}", speed), From = flightEnd, To = Frames,
                SettledSeconds = timedOut ? -1f : Seconds - settleStart
            });
        }

        private void Sample(VoxelWorld world, PipelineStages stages){
            _frameMs.Add(Time.unscaledDeltaTime * 1000f);
            _framesCut.Add(world.Scheduler.Budget.FramesCut);
            _gcCount.Add(GC.CollectionCount(0));
            if (!Application.isFocused) _unfocusedCount++;
            _unfocused.Add(_unfocusedCount);
            _sampleSeconds.Add(Seconds);
            _focusX.Add(world.FocusTransform ? world.FocusTransform.position.x : 0f);
            VoxelWorldRenderer renderer = VoxelWorldRenderer.Instance;
            _storeGrows.Add(renderer ? renderer.IntervalStoreGrowCount : 0);
            _storePages.Add(renderer ? renderer.IntervalStorePages : 0);
            _storeUsed.Add(renderer ? renderer.IntervalStoreUsedPages : 0);
            _buffers.Add(renderer
                ? renderer.RenderBufferCount(0) | renderer.RenderBufferCount(1) << 10 | renderer.RenderBufferCount(2) << 20
                : 0);
            stages.SampleCompletion(Seconds, Frames);
        }

        private void Summarize(Phase phase){
            phase.Times = FrameTimeStats.Summarize(_frameMs, phase.From, phase.To);
            phase.FramesCut = Delta(_framesCut, _framesCutAtStart, phase.From, phase.To);
            phase.GcCollections = Delta(_gcCount, _gcAtStart, phase.From, phase.To);
            phase.Unfocused = Delta(_unfocused, 0, phase.From, phase.To);
            phase.StoreGrows = Delta(_storeGrows, 0, phase.From, phase.To);
            int from = math.clamp(phase.From, 0, _storeUsed.Count), to = math.clamp(phase.To, from, _storeUsed.Count);
            phase.StorePagesEnd = to > 0 ? _storePages[to - 1] : 0;
            for (int i = from; i < to; i++) phase.StoreUsedMax = math.max(phase.StoreUsedMax, _storeUsed[i]);
        }

        /// <summary>Most rows the trace of one draw distance writes; the rest are counted.</summary>
        private const int MaxTraceRows = 300;

        /// <summary>
        ///     The per-frame trace of a draw distance: one row per sample whose frame time is over
        ///     <see cref="FrameTimeStats.GoalFrameMs" />, or in which the interval store grew, a render buffer was added
        ///     or a gen-0 GC happened. A sample is taken after Update; work done in that frame's Update shows in the
        ///     frame time of the next sample, so each row shows both ("ms" and "next ms").
        /// </summary>
        private void AppendTrace(DrawRun run){
            StringBuilder text = run.Trace;
            int rows = 0, skipped = 0;
            for (int i = 0; i < Frames; i++){
                int grows = _storeGrows[i] - (i > 0 ? _storeGrows[i - 1] : 0);
                int gc = _gcCount[i] - (i > 0 ? _gcCount[i - 1] : _gcAtStart);
                int cut = _framesCut[i] - (i > 0 ? _framesCut[i - 1] : _framesCutAtStart);
                int b = _buffers[i], bPrev = i > 0 ? _buffers[i - 1] : 0;
                int addSolid = (b & 1023) - (bPrev & 1023);
                int addTransparent = (b >> 10 & 1023) - (bPrev >> 10 & 1023);
                int addFoliage = (b >> 20 & 1023) - (bPrev >> 20 & 1023);
                bool slow = _frameMs[i] > FrameTimeStats.GoalFrameMs;
                if (!slow && grows == 0 && gc == 0 && addSolid == 0 && addTransparent == 0 && addFoliage == 0) continue;
                if (rows >= MaxTraceRows){
                    skipped++;
                    continue;
                }

                Phase phase = PhaseOf(run, i);
                int from = phase?.From ?? 0;
                float next = i + 1 < Frames ? _frameMs[i + 1] : -1f;
                string mark = !slow ? "" : _frameMs[i] > FrameTimeStats.StutterFrameMs ? "  STUTTER" : "  slow";
                text.AppendLine(string.Format(Inv,
                    "  {0,-20} {1,6} {2,7:0.000} {3,9:0.0} | {4,7:0.00} {5,7:0.00} | {6,3} {7,5} {8,7} {9,7} | " +
                    "{10,3} {11,3} {12,3} | {13,3}{14}",
                    phase != null ? phase.Name : "?", i - from, _sampleSeconds[i] - _sampleSeconds[from], _focusX[i],
                    _frameMs[i], next, gc, grows, _storePages[i], _storeUsed[i], addSolid, addTransparent, addFoliage,
                    cut, mark));
                rows++;
            }

            if (skipped > 0) text.AppendLine(string.Format(Inv, "  ... {0} more row(s) not written", skipped));
        }

        private static Phase PhaseOf(DrawRun run, int sample){
            foreach (Phase p in run.Phases)
                if (sample >= p.From && sample < p.To)
                    return p;
            return null;
        }

        /// <summary>
        ///     How much a cumulative counter grew over the samples <paramref name="from" /> to <paramref name="to" />;
        ///     <paramref name="start" /> is its value before the first sample.
        /// </summary>
        private static int Delta(List<int> cumulative, int start, int from, int to){
            from = math.clamp(from, 0, cumulative.Count);
            to = math.clamp(to, from, cumulative.Count);
            if (to == from) return 0;
            int before = from > 0 ? cumulative[from - 1] : start;
            return cumulative[to - 1] - before;
        }

        private static string WindowText(){
            Resolution display = Screen.currentResolution;
            return string.Format(Inv, "{0} x {1}, {2}; display {3} x {4} at {5:0.##} Hz", Screen.width, Screen.height,
                Screen.fullScreenMode, display.width, display.height, display.refreshRateRatio.value);
        }

        private static string ExecutableFolder => Path.GetDirectoryName(Application.dataPath) ?? ".";

        private void WriteHeader(){
            string backend =
#if ENABLE_IL2CPP
                "IL2CPP";
#else
                "Mono";
#endif
            _header.AppendLine("Goal run of a player build");
            _header.AppendLine(string.Format(Inv, "started {0:yyyy-MM-dd HH:mm:ss} (local time){1}", DateTime.Now,
                _options.Label.Length > 0 ? $", label '{_options.Label}'" : ""));
            _header.AppendLine(string.Format(Inv, "player: Unity {0}, scripting backend {1}, development build {2}, " +
                                                 "Burst {3}, process {4} on {5}", Application.unityVersion, backend,
                Debug.isDebugBuild ? "yes" : "no", BurstState(), RuntimeInformation.ProcessArchitecture,
                RuntimeInformation.OSArchitecture));
            string buildInfo = Path.Combine(ExecutableFolder, BuildInfoFile);
            _header.AppendLine(File.Exists(buildInfo)
                ? "build: " + File.ReadAllText(buildInfo).Trim().Replace("\r", "").Replace("\n", "; ")
                : $"build: no {BuildInfoFile} next to the executable");
            _header.AppendLine(string.Format(Inv,
                "machine: {0}, {1} logical processors; {2} ({3}, {4} MB); {5} MB memory; {6}",
                SystemInfo.processorType, SystemInfo.processorCount, SystemInfo.graphicsDeviceName,
                SystemInfo.graphicsDeviceType, SystemInfo.graphicsMemorySize, SystemInfo.systemMemorySize,
                SystemInfo.operatingSystem));
            _header.AppendLine(string.Format(Inv, "window at the start: {0}; vSync {1}, target frame rate {2}, quality '{3}'",
                WindowText(), QualitySettings.vSyncCount, Application.targetFrameRate,
                QualitySettings.names.Length > 0 ? QualitySettings.names[QualitySettings.GetQualityLevel()] : "?"));
            _header.AppendLine(string.Format(Inv, "draw distances {0}; the frames from {1:0} s after the world start " +
                                                 "are judged: goal p95 at or under {2:0.00} ms (120 fps), and no frame " +
                                                 "over {3:0.00} ms (a stutter the player notices)",
                string.Join(", ", _options.DrawDistances), GoalRunRig.StableAfterSeconds, FrameTimeStats.GoalFrameMs,
                FrameTimeStats.StutterFrameMs));
            _header.AppendLine("frame ms = Time.unscaledDeltaTime of every frame (wall clock between two frames). " +
                               "near4 = active share of the partitions within 4 chunks of the focus, window = of the " +
                               "draw window (flights, sampled every 15 frames); built = seconds from the world start, " +
                               "settled = seconds from the stop, until every stage sleeps and no GPU build waits; cut = " +
                               "frames in which the main-thread budget stopped a stage's turn; GC = gen-0 collections; " +
                               "unfocused = frames without the window focus. The scheduler's main-thread time per frame " +
                               "is not measured: the engine reads it from profiler markers only.");
            foreach (string problem in _options.Problems) _header.AppendLine("argument: " + problem);
        }

        private static string BurstState(){
            return Unity.Burst.BurstCompiler.IsEnabled ? "on" : "off";
        }

        private void Finish(string stopped){
            if (_finished) return;
            _finished = true;
            foreach (DrawRun run in _runs)
            foreach (Phase phase in run.Phases)
                if (phase.Times.Count == 0 && phase.To > phase.From)
                    Summarize(phase);

            string report = Report(stopped);
            string path = WriteReport(report);
            Debug.Log($"[GoalRun] results written to {path}\n{report}");
            Application.Quit();
        }

        private string Report(string stopped){
            StringBuilder text = new();
            text.Append(_header);
            text.AppendLine(string.Format(Inv, "finished {0:yyyy-MM-dd HH:mm:ss} after {1:0} s{2}; errors logged during " +
                                               "the run {3}", DateTime.Now, Time.realtimeSinceStartup - _runStart,
                stopped != null ? " (" + stopped + ")" : "", _errorsLogged));
            text.AppendLine();
            foreach (DrawRun run in _runs) AppendTable(text, run);
            text.AppendLine("csv");
            text.AppendLine("label,draw,phase,judged,frames,avg_fps,avg_ms,p50_ms,p95_ms,p99_ms,max_ms,over_8_33,share_over_8_33," +
                            "over_16_67,share_over_16_67,near4_avg,near4_low,window_avg,window_end,built_or_settled_s," +
                            "frames_cut,gc0,unfocused");
            foreach (DrawRun run in _runs)
            foreach (Phase p in run.Phases){
                FrameTimeStats.Summary t = p.Times;
                float seconds = p.SettledSeconds > -2f ? p.SettledSeconds : p.From == 0 ? run.BuiltSeconds : -2f;
                text.AppendLine(string.Format(Inv,
                    "{0},{1},{2},{3},{4},{5:0.0},{6:0.000},{7:0.000},{8:0.000},{9:0.000},{10:0.000},{11},{12:0.0000}," +
                    "{13},{14:0.0000},{15:0.000},{16:0.000},{17:0.000},{18:0.000},{19},{20},{21},{22}",
                    Csv(_options.Label), run.Draw, Csv(p.Name), p.Judged ? 1 : 0, t.Count, t.Avg > 0 ? 1000 / t.Avg : 0,
                    t.Avg, t.P50, t.P95, t.P99, t.Max, t.OverGoal, t.OverGoalShare, t.OverStutter, t.OverStutterShare,
                    p.NearAvg, p.NearLow, p.WindowAvg, p.WindowEnd,
                    seconds > -2f ? seconds.ToString("0.00", Inv) : "", p.FramesCut, p.GcCollections, p.Unfocused));
            }

            return text.ToString();
        }

        private static string Csv(string value){
            return value.Contains(",") || value.Contains("\"") ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
        }

        private static void AppendTable(StringBuilder text, DrawRun run){
            text.AppendLine(string.Format(Inv, "draw {0}: {1}", run.Draw, run.Problem ?? run.Sizes));
            if (run.Problem != null){
                text.AppendLine();
                return;
            }

            text.AppendLine("window: " + run.Window);
            text.AppendLine(run.BuiltSeconds >= 0f
                ? string.Format(Inv, "world built after {0:0.0} s (frame {1})", run.BuiltSeconds, run.BuiltFrame)
                : string.Format(Inv, "world not built within the first {0:0} s", GoalRunRig.StandSeconds));
            text.AppendLine("phase                  frames    fps | p95 ms  p99 ms  max ms |   >8.33 ms         |" +
                            " >16.67 ms      | near4 avg/low | window avg/end | built/settled s |   cut   GC unfocused");
            foreach (Phase p in run.Phases){
                FrameTimeStats.Summary t = p.Times;
                string coverage = p.Flight
                    ? string.Format(Inv, "{0:0.000} / {1:0.000} | {2:0.000} / {3:0.000}", p.NearAvg, p.NearLow,
                        p.WindowAvg, p.WindowEnd)
                    : "              |               ";
                string seconds = p.SettledSeconds > -2f
                    ? p.SettledSeconds >= 0f ? string.Format(Inv, "settled {0,6:0.00}", p.SettledSeconds) : "not settled   "
                    : p.From == 0 && run.BuiltSeconds >= 0f
                        ? string.Format(Inv, "built   {0,6:0.00}", run.BuiltSeconds)
                        : "              ";
                text.AppendLine(string.Format(Inv,
                    "{0,-22} {1,6} {2,6:0} | {3,6:0.00} {4,7:0.00} {5,7:0.00} | {6,5} {7,6:0.00} %     | {8,4} {9,6:0.00} % | " +
                    "{10} | {11,15} | {12,5} {13,4} {14,9}",
                    p.Judged ? p.Name : p.Name + " *", t.Count, t.Avg > 0 ? 1000 / t.Avg : 0, t.P95, t.P99, t.Max,
                    t.OverGoal, 100f * t.OverGoalShare, t.OverStutter, 100f * t.OverStutterShare, coverage, seconds,
                    p.FramesCut, p.GcCollections, p.Unfocused));
            }

            text.AppendLine("* not judged (the first world build)");
            text.AppendLine("interval store per phase (grows, pages at the end, most pages in use):");
            foreach (Phase p in run.Phases)
                text.AppendLine(string.Format(Inv, "  {0,-22} grows {1}, pages {2}, used max {3}", p.Name, p.StoreGrows,
                    p.StorePagesEnd, p.StoreUsedMax));
            text.AppendLine(string.Format(Inv, "  peak pages in use over the world: {0}", run.StorePeak));
            text.AppendLine("trace: frames over 8.33 ms, and frames with a store grow, a render buffer added or a gen-0 GC");
            text.AppendLine("  (frame = index in the phase, s = seconds into the phase, x = focus x; the work of a frame's " +
                            "Update shows in the next frame's time, 'next ms'; +s/+t/+f = render buffers added to solid/" +
                            "transparent/foliage; cut = the budget stopped a stage's turn)");
            text.AppendLine("  phase                 frame       s         x |      ms next ms |  GC grows   pages    used |" +
                            "  +s  +t  +f | cut");
            text.Append(run.Trace);
            foreach (Phase p in run.Phases){
                if (!p.Judged || p.Times.Count == 0) continue;
                float margin = p.Times.P95 - FrameTimeStats.GoalFrameMs;
                text.AppendLine(string.Format(Inv, "verdict {0}: p95 {1:0.00} ms {2} by {3:0.00} ms; {4} frame(s) over 16.67 ms",
                    p.Name, p.Times.P95, margin <= 0f ? "met" : "missed", math.abs(margin), p.Times.OverStutter));
            }

            text.AppendLine();
        }

        /// <summary>Writes the report to the file of the arguments or the default one; falls back to the persistent data folder.</summary>
        private string WriteReport(string report){
            string name = string.Format(Inv, "goal-run{0}-{1:yyyyMMdd-HHmmss}.txt",
                _options.Label.Length > 0 ? "-" + SafeFileName(_options.Label) : "", DateTime.Now);
            string path = !string.IsNullOrWhiteSpace(_options.OutPath)
                ? Path.GetFullPath(_options.OutPath)
                : Path.Combine(ExecutableFolder, ResultFolder, name);
            try{
                Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
                File.WriteAllText(path, report);
                return path;
            }
            catch (Exception e){
                string fallback = Path.Combine(Application.persistentDataPath, name);
                Debug.LogWarning($"[GoalRun] could not write {path} ({e.Message}); writing {fallback}");
                File.WriteAllText(fallback, report);
                return fallback;
            }
        }

        private static string SafeFileName(string value){
            foreach (char c in Path.GetInvalidFileNameChars()) value = value.Replace(c, '_');
            return value.Replace(' ', '_');
        }
    }
}
