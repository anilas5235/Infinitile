using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using Engine.Scripts.Jobs.Core;
using Engine.Scripts.Settings;
using Engine.Scripts.World;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Engine.Tests.PlayMode{
    /// <summary>
    ///     Measurements of the pipeline that assert nothing but a time limit: the runs of
    ///     <see cref="PipelineThroughputTests" /> without a frame cap (the Editor's own frame rate), and the same
    ///     runs with other scheduler batch sizes. They print "[PipelineProbe] ..." lines and take several minutes,
    ///     so they only run on request (explicit); keep the Editor focused, an unfocused Editor reads slow.
    /// </summary>
    [Explicit("Measurement: prints pipeline timings only, run on request")]
    public class PipelineMeasurementTests{
        /// <summary>
        ///     Mesh batch size and GPU build slots of the variants compared with the shipped settings (16 and 8):
        ///     the values shipped until 2026-11-14, and twice the shipped ones.
        /// </summary>
        private static readonly (int meshBatch, int buildSlots)[] Variants ={ (8, 4), (32, 16) };

        /// <summary>
        ///     The small draw distance of <see cref="DrawDistances_AreCompared" />: the one the mesh batch of 8 and
        ///     the 4 GPU build slots were written for. The shipped one has 16 and 8.
        /// </summary>
        private const int SmallDrawDistance = 6;

        /// <summary>
        ///     Draw distance, mesh batch and GPU build slots of <see cref="FrameTimeGoal_OtherSizes_AreMeasured" />:
        ///     smaller sizes than the derived ones at the shipped draw distance (16 and 8) and at the small one
        ///     (8 and 4), and the small draw distance with the sizes of the shipped one.
        /// </summary>
        private static readonly (int draw, int meshBatch, int buildSlots)[] GoalVariants ={
            (12, 8, 4), (12, 12, 6), (6, 4, 2), (6, 16, 8)
        };

        private int _savedTargetFrameRate;
        private int _savedVSync;
#if UNITY_EDITOR
        private GameViewGizmos _gizmos;
#endif
        private SchedulerSettings _scheduler;
        private int _savedMeshBatch;
        private int _savedBuildSlots;
        private int _savedReference;
        private float _savedBudget;
        private float _savedCapShare;

        /// <summary>Takes the shared scheduler settings and remembers the shipped values for the tear-down.</summary>
        private void TakeSchedulerSettings(){
            _scheduler = VoxelWorld.Instance.Settings.Scheduler;
            _savedMeshBatch = _scheduler.meshingBatchSize;
            _savedBuildSlots = _scheduler.partitionBuildBatchSize;
            _savedReference = _scheduler.referenceDrawDistance;
            _savedBudget = _scheduler.mainThreadBudgetMs;
            _savedCapShare = _scheduler.cappedFrameSpareShare;
        }

        [SetUp]
        public void SaveFrameSettings(){
            _savedTargetFrameRate = Application.targetFrameRate;
            _savedVSync = QualitySettings.vSyncCount;
            _scheduler = null;
#if UNITY_EDITOR
            // With the Gizmos button of the game view on, every chunk object the pool switches on or off makes the
            // Editor rebuild its gizmo list in the next frame (6 to 10 ms): not part of the engine, so it is off
            // for the runs that measure and back on in the tear-down
            _gizmos = new GameViewGizmos();
            Debug.Log($"[PipelineProbe] Gizmos of the game view: {_gizmos.SwitchedOff} switch(es) were on and are " +
                      "off for this run");
#endif
        }

        [UnityTearDown]
        public IEnumerator TearDown(){
            Application.targetFrameRate = _savedTargetFrameRate;
            QualitySettings.vSyncCount = _savedVSync;
#if UNITY_EDITOR
            _gizmos?.Dispose();
            _gizmos = null;
#endif
            if (_scheduler == null) yield return PipelineThroughputTests.FreshSceneAfterFlight();
            yield return PipelineThroughputTests.WaitForBuiltWorld();
            if (_scheduler == null) yield break;

            // The settings asset is shared in memory: put the shipped values back and rebuild the world with them.
            _scheduler.meshingBatchSize = _savedMeshBatch;
            _scheduler.partitionBuildBatchSize = _savedBuildSlots;
            _scheduler.referenceDrawDistance = _savedReference;
            _scheduler.mainThreadBudgetMs = _savedBudget;
            _scheduler.cappedFrameSpareShare = _savedCapShare;
            yield return PlayModeFixtures.ReloadWorldWithDrawDistance(PlayModeFixtures.ShippedDrawDistance);
            yield return PipelineThroughputTests.WaitForBuiltWorld();
        }

        [UnityTest]
        [Order(1)]
        [Timeout(900000)]
        public IEnumerator FreshWorld_Uncapped_IsMeasured(){
            yield return PipelineThroughputTests.BuildFreshWorld(0, new PipelineThroughputTests.PipelineRun());
        }

        [UnityTest]
        [Order(2)]
        [Timeout(900000)]
        public IEnumerator Flight_Uncapped_IsMeasured(){
            yield return PipelineThroughputTests.FlyThroughBuildingWorld(0, new PipelineThroughputTests.PipelineRun());
        }

        [UnityTest]
        [Order(3)]
        [Timeout(2400000)]
        public IEnumerator SchedulerSettings_AreCompared(){
            // The same four runs with other mesh batches and GPU build slots; the world is rebuilt per run,
            // so the mesh scheduler and the renderer are created with the changed settings.
            yield return PlayModeFixtures.LoadGameScene(drawDistance: PlayModeFixtures.ShippedDrawDistance);
            TakeSchedulerSettings();
            _scheduler.referenceDrawDistance = 0; // the variants are used as given, not scaled with the draw distance

            foreach ((int meshBatch, int buildSlots) in Variants){
                _scheduler.meshingBatchSize = meshBatch;
                _scheduler.partitionBuildBatchSize = buildSlots;
                string variant = $"mesh batch {meshBatch}, build slots {buildSlots}: ";
                int capped = PipelineThroughputTests.CappedFrameRate;
                yield return PipelineThroughputTests.BuildFreshWorld(capped, new PipelineThroughputTests.PipelineRun(), variant);
                yield return PipelineThroughputTests.FlyThroughBuildingWorld(capped, new PipelineThroughputTests.PipelineRun(),
                    variant);
                yield return PipelineThroughputTests.BuildFreshWorld(0, new PipelineThroughputTests.PipelineRun(), variant);
                yield return PipelineThroughputTests.FlyThroughBuildingWorld(0, new PipelineThroughputTests.PipelineRun(),
                    variant);
            }
        }

        [UnityTest]
        [Order(4)]
        [Timeout(1200000)]
        public IEnumerator FrameTimeGoal_ShippedSizes_IsMeasured(){
            // The goal run (stand, then a sprint flight through the built world) without a frame cap at the small
            // and at the shipped draw distance, with the sizes the settings derive for each. Then the runs the
            // earlier numbers were taken with: fresh world and a flight through the building world.
            yield return PipelineThroughputTests.StandThenFly(0, "", SmallDrawDistance);
            yield return PipelineThroughputTests.StandThenFly(0);

            yield return PipelineThroughputTests.BuildFreshWorld(0, new PipelineThroughputTests.PipelineRun());
            yield return PipelineThroughputTests.FlyThroughBuildingWorld(0, new PipelineThroughputTests.PipelineRun());
            int capped = PipelineThroughputTests.CappedFrameRate;
            yield return PipelineThroughputTests.BuildFreshWorld(capped, new PipelineThroughputTests.PipelineRun(), "",
                SmallDrawDistance);
            yield return PipelineThroughputTests.FlyThroughBuildingWorld(capped, new PipelineThroughputTests.PipelineRun(),
                "", SmallDrawDistance);
        }

        [UnityTest]
        [Order(6)]
        [Timeout(600000)]
        public IEnumerator FrameTimeGoal_MainThread_IsAttributed(){
            // The goal run at the shipped draw distance with the table that says where the main thread spends
            // the frames of each phase (standing, the two flights, settling): what has to be bounded per frame.
            yield return PipelineThroughputTests.StandThenFly(0, "", 0, true);
        }

        [UnityTest]
        [Order(6)]
        [Timeout(600000)]
        public IEnumerator FrameTimeGoal_SmallDraw_IsAttributed(){
            // The same run and table at the small draw distance
            yield return PipelineThroughputTests.StandThenFly(0, "", SmallDrawDistance, true);
        }

        /// <summary>
        ///     Main-thread budgets of the comparison in milliseconds per frame (see
        ///     <see cref="SchedulerSettings.mainThreadBudgetMs" />); 0 is the scheduler without a limit.
        /// </summary>
        private static readonly float[] Budgets ={ 0f, 2f, 3f, 4f };

        /// <summary>The same budgets in the other order, so that no budget is always the last run of a session.</summary>
        private static readonly float[] BudgetsBackwards ={ 4f, 3f, 2f };

        [UnityTest]
        [Order(6)]
        [Timeout(900000)]
        public IEnumerator FrameBudget_ShippedDraw_IsCompared(){
            // The goal run (standing, then the two flights) once per main-thread budget at the shipped draw distance
            yield return CompareBudgets(PlayModeFixtures.ShippedDrawDistance, Budgets);
        }

        [UnityTest]
        [Order(6)]
        [Timeout(900000)]
        public IEnumerator FrameBudget_ShippedDrawBackwards_IsCompared(){
            yield return CompareBudgets(PlayModeFixtures.ShippedDrawDistance, BudgetsBackwards);
        }

        [UnityTest]
        [Order(6)]
        [Timeout(900000)]
        public IEnumerator FrameBudget_SmallDraw_IsCompared(){
            yield return CompareBudgets(SmallDrawDistance, Budgets);
        }

        [UnityTest]
        [Order(6)]
        [Timeout(900000)]
        public IEnumerator FrameBudget_SmallDrawBackwards_IsCompared(){
            yield return CompareBudgets(SmallDrawDistance, BudgetsBackwards);
        }

        [UnityTest]
        [Order(6)]
        [Timeout(900000)]
        public IEnumerator FrameBudget_OnAndOff_IsCompared(){
            // Without a limit, twice with the shipped budget, without a limit again: both are measured twice in
            // one session, and a drift of the session does not favour either
            float shipped = new SchedulerSettings().mainThreadBudgetMs;
            yield return CompareBudgets(PlayModeFixtures.ShippedDrawDistance, new[]{ 0f, shipped, shipped, 0f }, true);
        }

        /// <summary>
        ///     Main-thread budget and share of a capped frame's spare time (see
        ///     <see cref="SchedulerSettings.cappedFrameSpareShare" />) of <see cref="FrameBudget_CappedSpare_IsCompared" />, in
        ///     the order of the runs: without a limit, the shipped budget without and with the spare time (all of it
        ///     and half of it), then the same in reverse, so that every variant is measured twice in one session and
        ///     a drift of the session favours none.
        /// </summary>
        private static readonly (float budget, float share)[] CappedVariants ={
            (0f, 0f), (2f, 0f), (2f, 1f), (2f, 0.5f), (2f, 0.5f), (2f, 1f), (2f, 0f), (0f, 0f)
        };

        [UnityTest]
        [Order(6)]
        [Timeout(1800000)]
        public IEnumerator FrameBudget_CappedSpare_IsCompared(){
            // Under the 60 frames cap: a fresh world at the shipped draw distance until it is built, and a sprint
            // flight through a world that is still being built, once per variant, each in a world built again
            yield return PlayModeFixtures.LoadGameScene(drawDistance: PlayModeFixtures.ShippedDrawDistance);
            TakeSchedulerSettings();
            int capped = PipelineThroughputTests.CappedFrameRate;
            for (int i = 0; i < CappedVariants.Length; i++){
                (float budget, float share) = CappedVariants[i];
                _scheduler.mainThreadBudgetMs = budget;
                _scheduler.cappedFrameSpareShare = share;
                string variant = string.Format(CultureInfo.InvariantCulture, "budget {0:0.##} ms, spare share {1:0.##}, " +
                                                                             "part {2}: ", budget, share, i + 1);
                yield return PipelineThroughputTests.FreshSceneAfterFlight();
                yield return PipelineThroughputTests.BuildFreshWorld(capped, new PipelineThroughputTests.PipelineRun(),
                    variant);
                yield return PipelineThroughputTests.FlyThroughBuildingWorld(capped,
                    new PipelineThroughputTests.PipelineRun(), variant);
            }
        }

        /// <summary>Runs of <see cref="FrameBudget_CappedFlightOvershoot_IsAttributed" /> in one session.</summary>
        private const int OvershootRuns = 3;

        [UnityTest]
        [Order(9)]
        [Timeout(1800000)]
        public IEnumerator FrameBudget_CappedFlightOvershoot_IsAttributed(){
            // The capped flight through a world that is still being built, at the shipped draw distance and with
            // the shipped scheduler settings (the run of Flight_AtSixtyFrames_KeepsMeshQueueInsideDrawWindow), with
            // every row of the attribution recorded: the log says how many frames the scheduler took longer than
            // its effective budget, by how much, and which item or wait took the time in them. Several runs in one
            // session, each in a world built again, so the runs can be compared with each other.
            yield return PlayModeFixtures.LoadGameScene(drawDistance: PlayModeFixtures.ShippedDrawDistance);
            int capped = PipelineThroughputTests.CappedFrameRate;
            for (int i = 0; i < OvershootRuns; i++){
                yield return PipelineThroughputTests.FreshSceneAfterFlight();
                yield return PipelineThroughputTests.FlyThroughBuildingWorld(capped,
                    new PipelineThroughputTests.PipelineRun(), $"overshoot part {i + 1}: ",
                    PlayModeFixtures.ShippedDrawDistance, true);
            }
        }

        /// <summary>
        ///     Entries per budget item of the mesh dispatch's dequeue loop (see
        ///     <see cref="MeshJobStateHandler.DequeueSlice" />) in the order of the capped flights of
        ///     <see cref="MeshDequeueSlice_IsCompared" />: 0 (the loop in one piece, before 2026-11-15) and the shipped
        ///     slice, in the order A B B A A B.
        /// </summary>
        private static readonly int[] CappedDequeueSlices ={
            0, MeshJobStateHandler.DefaultDequeueSlice, MeshJobStateHandler.DefaultDequeueSlice, 0, 0,
            MeshJobStateHandler.DefaultDequeueSlice
        };

        /// <summary>The slices of the uncapped flights of <see cref="MeshDequeueSlice_IsCompared" />, A B B A.</summary>
        private static readonly int[] UncappedDequeueSlices ={
            0, MeshJobStateHandler.DefaultDequeueSlice, MeshJobStateHandler.DefaultDequeueSlice, 0
        };

        [UnityTest]
        [Order(10)]
        [Timeout(3600000)]
        public IEnumerator MeshDequeueSlice_IsCompared(){
            // The capped flight through a world that is still being built (with the attribution: frames over the
            // effective budget and the dequeue counts), then the uncapped flight, at the shipped draw distance,
            // with the dequeue loop in one piece and with the shipped slice. The slice is set on the fresh world's
            // mesh stage before the probe starts; the frames of the load before it use the shipped slice.
            yield return PlayModeFixtures.LoadGameScene(drawDistance: PlayModeFixtures.ShippedDrawDistance);
            int capped = PipelineThroughputTests.CappedFrameRate;
            for (int i = 0; i < CappedDequeueSlices.Length; i++){
                int slice = CappedDequeueSlices[i];
                yield return PipelineThroughputTests.FreshSceneAfterFlight();
                yield return PipelineThroughputTests.FlyThroughBuildingWorld(capped,
                    new PipelineThroughputTests.PipelineRun(), $"dequeue slice {slice}, capped part {i + 1}: ",
                    PlayModeFixtures.ShippedDrawDistance, true, world => SetDequeueSlice(world, slice));
            }

            for (int i = 0; i < UncappedDequeueSlices.Length; i++){
                int slice = UncappedDequeueSlices[i];
                yield return PipelineThroughputTests.FreshSceneAfterFlight();
                yield return PipelineThroughputTests.FlyThroughBuildingWorld(0,
                    new PipelineThroughputTests.PipelineRun(), $"dequeue slice {slice}, uncapped part {i + 1}: ",
                    PlayModeFixtures.ShippedDrawDistance, false, world => SetDequeueSlice(world, slice));
            }
        }

        private static void SetDequeueSlice(VoxelWorld world, int slice){
            VoxelEngineScheduler scheduler = PlayModeFixtures.GetPrivateField<VoxelEngineScheduler>(world, "_scheduler");
            PlayModeFixtures.GetPrivateField<MeshJobStateHandler>(scheduler, "_meshJobHandler").DequeueSlice = slice;
        }

        /// <summary>
        ///     One goal run per budget, each in a world that is built again with the budget (the scheduler reads
        ///     the setting when the world is built). <paramref name="numbered" /> adds the position in the order to
        ///     the log labels, for an order that holds a budget twice.
        /// </summary>
        private IEnumerator CompareBudgets(int drawDistance, float[] budgets, bool numbered = false){
            yield return PlayModeFixtures.LoadGameScene(drawDistance: drawDistance);
            TakeSchedulerSettings();
            for (int i = 0; i < budgets.Length; i++){
                _scheduler.mainThreadBudgetMs = budgets[i];
                string variant = string.Format(CultureInfo.InvariantCulture, "budget {0:0.##} ms{1}: ", budgets[i],
                    numbered ? $", part {i + 1}" : "");
                // The flights of the run before took the focus out of the player: start from the scene as it ships
                yield return PipelineThroughputTests.FreshSceneAfterFlight();
                yield return PipelineThroughputTests.StandThenFly(0, variant, drawDistance);
            }
        }

        [UnityTest]
        [Order(7)]
        [Timeout(120000)]
        public IEnumerator ProfilerMarkers_AreListed(){
            // Not a measurement: writes the markers and counters this Unity version offers to a file and logs the
            // rows of the attribution table that have none (Unity renames markers between versions). It also
            // prints the table of a few seconds of the starting world, to see that every row reads a value.
            yield return PlayModeFixtures.LoadGameScene();
            using FrameAttribution attribution = new(1024);
            List<float> frameMs = new(1024);
            for (int frame = 0; frame < 600; frame++){
                yield return null;
                frameMs.Add(Time.unscaledDeltaTime * 1000f);
                attribution.Sample();
            }

            Debug.Log(FrameAttribution.ListAvailable("Temp/profiler-marker-names.txt") + "\n" +
                      attribution.Table("check of the table, not a measurement", frameMs, 0, frameMs.Count));
#if UNITY_EDITOR
            using FrameHierarchy hierarchy = new(300);
            for (int frame = 0; frame < 150; frame++) yield return null;
            hierarchy.Stop();
            Debug.Log(hierarchy.Report("check of the sample tree, not a measurement", PipelineProbe.GoalFrameMs, 0.1f));
#endif
        }

#if UNITY_EDITOR
        /// <summary>Flight speeds and seconds of <see cref="FrameTimeGoal_FlightFrames_AreTakenApart" />.</summary>
        private static readonly float[] HierarchySpeeds ={ 80f, 40f };

        private const float HierarchySeconds = 12f;

        [UnityTest]
        [Order(8)]
        [Timeout(600000)]
        public IEnumerator FrameTimeGoal_FlightFrames_AreTakenApart(){
            // Not a timing: the Editor's profiler records a flight through the built world at the shipped draw
            // distance, and the report says what the frames over the goal are made of on the main thread, down
            // to the samples Unity itself emits (render, present, waits), next to the frames under the goal.
            yield return PlayModeFixtures.LoadGameScene(drawDistance: PlayModeFixtures.ShippedDrawDistance);
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1;
            yield return PipelineThroughputTests.WaitForBuiltWorld();
            VoxelWorld world = VoxelWorld.Instance;
            Transform focus = PlayModeFixtures.GetPrivateField<Transform>(world, "focus");
            int draw = world.Settings.Chunk.DrawDistance;

            string label = $"recording, draw {draw}, uncapped";
            // Standing where the flights start, on the rig of the flights
            PipelineThroughputTests.FlightBody(focus);
            string places = PipelineThroughputTests.Place(world, focus, label, "start of the standing phase");
            using (FrameHierarchy standing = new(600)){
                for (int frame = 0; frame < 500; frame++) yield return null;
                standing.Stop();
                places += PipelineThroughputTests.Place(world, focus, label, "end of the standing phase");
                Debug.Log(places + standing.Report($"draw {draw}, uncapped, standing in the built world",
                    PipelineProbe.GoalFrameMs, 0.1f));
            }

            foreach (float speed in HierarchySpeeds){
                yield return null;
                using FrameHierarchy hierarchy = new(2000);
                Transform body = PipelineThroughputTests.FlightBody(focus);
                Vector3 start = body.position;
                string flight = $"{label}, flight {speed:0} u/s for {HierarchySeconds:0} s";
                places = PipelineThroughputTests.Place(world, focus, flight, "start of the flight");
                float flightStart = Time.realtimeSinceStartup;
                while (true){
                    float elapsed = Time.realtimeSinceStartup - flightStart;
                    if (elapsed >= HierarchySeconds) break;
                    body.position = start + new Vector3(speed * elapsed, 0f, 0f);
                    yield return null;
                }

                hierarchy.Stop();
                places += PipelineThroughputTests.Place(world, focus, flight, "end of the flight");
                Debug.Log(places + hierarchy.Report(
                    $"draw {draw}, uncapped, flight {speed:0} u/s for {HierarchySeconds:0} s", PipelineProbe.GoalFrameMs,
                    0.1f));
                yield return PipelineThroughputTests.WaitForBuiltWorld();
                Debug.Log(PipelineThroughputTests.Place(world, focus, flight, "world complete after the flight"));
            }
        }
#endif

        [UnityTest]
        [Order(5)]
        [Timeout(1200000)]
        public IEnumerator FrameTimeGoal_OtherSizes_AreMeasured(){
            // The goal run with other mesh batches and GPU build slots than the derived ones: which are the
            // smallest that keep up with a sprint flight and have the first build done 20 s after the start?
            yield return PlayModeFixtures.LoadGameScene(drawDistance: PlayModeFixtures.ShippedDrawDistance);
            TakeSchedulerSettings();
            _scheduler.referenceDrawDistance = 0; // the variants are used as given, not scaled with the draw distance

            foreach ((int draw, int meshBatch, int buildSlots) in GoalVariants){
                _scheduler.meshingBatchSize = meshBatch;
                _scheduler.partitionBuildBatchSize = buildSlots;
                yield return PipelineThroughputTests.StandThenFly(0, $"mesh batch {meshBatch}, build slots {buildSlots}: ",
                    draw);
            }
        }
    }
}
