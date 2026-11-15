using System;
using System.Collections;
using System.Globalization;
using Engine.Scripts.Jobs.Core;
using Engine.Scripts.Render;
using Engine.Scripts.Render.Post;
using Engine.Scripts.Settings;
using Engine.Scripts.Utils.Extensions;
using Engine.Scripts.World;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;
using static Engine.Scripts.Utils.VoxelConstants;

namespace Engine.Tests.PlayMode{
    /// <summary>
    ///     How fast the pipeline (data, light, mesh, collider bake) builds the world at the shipped draw distance:
    ///     a fresh world, and a flight that starts while the world is still building. The numbers are logged as
    ///     "[PipelineProbe] ..." lines. The frame rate is capped, so the result depends on what the scheduler does
    ///     per frame and not on how many frames the machine renders; the budgets are counted in frames for the same
    ///     reason. <see cref="PipelineMeasurementTests" /> holds the runs that only measure.
    /// </summary>
    public class PipelineThroughputTests{
        internal const float BuildTimeoutSeconds = 600f;

        /// <summary>The frame rate of a player with vertical sync on a 60 Hz display.</summary>
        internal const int CappedFrameRate = 60;

        /// <summary>Spectator flight with sprint: 40 units per second times two.</summary>
        private const float FlightSpeed = 80f;

        /// <summary>
        ///     The flights of the goal run, in units per second: the spectator sprint, and the fastest movement
        ///     outside spectator mode (flying at 20 with sprint, or the spectator without).
        /// </summary>
        private static readonly float[] GoalFlightSpeeds ={ FlightSpeed, 40f };

        private const float FlightSeconds = 30f;

        /// <summary>
        ///     Height of every flight: above the terrain along the path (highest block y 123 to 139 near the spawn)
        ///     and under the world's top at 256 (owner, 2026-11-15).
        /// </summary>
        internal const float FlightHeight = 200f;

        /// <summary>Degrees the camera of a flight looks down, so the picture holds terrain and not only sky.</summary>
        internal const float FlightPitch = 20f;

        /// <summary>
        ///     The first world build at the start is exempt from the frame-time goal; from this many seconds after
        ///     the start the frame times must be stable (owner, 2026-11-14).
        /// </summary>
        private const float StableAfterSeconds = 20f;

        /// <summary>How long <see cref="StandThenFly" /> stands still before the flight, from the world start.</summary>
        private const float StandSeconds = 40f;

        /// <summary>Columns around the focus that count as "near" for the near-first check.</summary>
        private const int NearRadius = 4;

        /// <summary>
        ///     Frames a fresh world at the shipped draw distance may take until every stage sleeps and no GPU build
        ///     waits. The scheduler that gave every stage one of three steps on every fourth frame needed 7553
        ///     frames (125.9 s at 60 frames per second, measured 2026-11-14). The work-conserving one needs 2532
        ///     with the mesh batch of 8 and the 4 GPU build slots of that time, and 1345 with the shipped 16 and 8;
        ///     the GPU build slots limit the mesh stage now. Without the partitions that hold no voxel (48 % of the
        ///     draw window, no mesh job and no GPU build) it needs 701 to 703. Under the main-thread budget of 2 ms
        ///     per frame it needed 799 to 820 while the budget left the spare time of a frame of the 60 frames cap
        ///     unused; with half of it (shipped share 0.5) or all of it it needs 703 again (Editor, 2026-11-15). The limit
        ///     leaves 8 % of slack and fails when the spare time is lost.
        /// </summary>
        private const int FreshBuildFrameBudget = 760;

        private int _savedTargetFrameRate;
        private int _savedVSync;

        [SetUp]
        public void SaveFrameSettings(){
            _savedTargetFrameRate = Application.targetFrameRate;
            _savedVSync = QualitySettings.vSyncCount;
        }

        [UnityTearDown]
        public IEnumerator TearDown(){
            Application.targetFrameRate = _savedTargetFrameRate;
            QualitySettings.vSyncCount = _savedVSync;
            yield return FreshSceneAfterFlight();
            yield return WaitForBuiltWorld();
        }

        /// <summary>A flight ends with chunks still loading; lets them finish (no assert: the test has its verdict).</summary>
        internal static IEnumerator WaitForBuiltWorld(){
            if (VoxelWorld.Instance == null) yield break;
            float deadline = Time.realtimeSinceStartup + BuildTimeoutSeconds;
            while (!PlayModeFixtures.SchedulerSleeping() && Time.realtimeSinceStartup < deadline) yield return null;
        }

        [UnityTest]
        [Order(0)]
        [Timeout(300000)]
        public IEnumerator DrawDistance_SetsMeshBatchAndBuildSlots(){
            // The mesh batch and the GPU build slots follow the draw distance. They are derived when the world is
            // built: the suite changes the draw distance on the shared settings object and reloads the scene, and
            // the renderer's point builders (with their GPU buffers) must follow.
            yield return PlayModeFixtures.LoadGameScene(drawDistance: 6);
            AssertDerivedSizes(6, 8, 4);
            yield return PlayModeFixtures.LoadGameScene(drawDistance: 12);
            AssertDerivedSizes(12, 16, 8);
        }

        private static void AssertDerivedSizes(int drawDistance, int meshBatch, int buildSlots){
            VoxelWorld world = VoxelWorld.Instance;
            Assert.AreEqual(drawDistance, world.Settings.Chunk.DrawDistance);
            VoxelWorldRenderer renderer = VoxelWorldRenderer.Instance;
            Assert.AreEqual(buildSlots, renderer.BuildSlots, $"GPU build slots at draw distance {drawDistance}");
            Array builders = PlayModeFixtures.GetPrivateField<Array>(renderer, "_pointBuilderHandlers");
            Assert.AreEqual(buildSlots, builders.Length, "one point builder with its GPU buffers per slot");

            VoxelEngineScheduler scheduler = PlayModeFixtures.GetPrivateField<VoxelEngineScheduler>(world, "_scheduler");
            MeshJobStateHandler mesh = PlayModeFixtures.GetPrivateField<MeshJobStateHandler>(scheduler, "_meshJobHandler");
            Assert.AreEqual(meshBatch, mesh.MeshBatchSize, $"mesh batch at draw distance {drawDistance}");
        }

        [UnityTest]
        [Order(1)]
        [Timeout(900000)]
        public IEnumerator FreshWorld_AtSixtyFrames_BuildsWithinBudget(){
            // No frame may pass unused while a stage has work: the whole world is built within the frame budget, and
            // no queue grows past the window it was reserved for.
            PipelineRun run = new();
            yield return BuildFreshWorld(CappedFrameRate, run);
            Assert.LessOrEqual(run.BuildFrames, FreshBuildFrameBudget,
                $"frames until the world was built ({run.BuildSeconds:0.0} s)");
            AssertQueuesInsideWindows(run);
            // The sky above the terrain: partitions without a voxel take no mesh job and no GPU build
            Assert.Greater(run.PartitionsWithoutMesh, 0, "partitions that became active without a mesh job");
            Assert.AreEqual(0, run.DispatchedWithoutVoxels, "partitions without a voxel that got a mesh job");
        }

        [UnityTest]
        [Order(2)]
        [Timeout(900000)]
        public IEnumerator Flight_AtSixtyFrames_KeepsMeshQueueInsideDrawWindow(){
            // The regression of the mesh backlog: in a sprint flight through a world that is still building, the
            // mesh queue (the "Mesh" number of the HUD) held 5504 partitions at 60 frames per second and 13 328
            // uncapped, with a draw window of 5000, because entries the focus had left behind stayed queued. Every
            // queue now stays inside its window, and the columns around the focus are meshed before the rest.
            PipelineRun run = new();
            yield return FlyThroughBuildingWorld(CappedFrameRate, run);
            AssertQueuesInsideWindows(run);
            Assert.Greater(run.NearCoverage, run.WindowCoverage,
                $"meshed share of the columns within {NearRadius} of the focus against the whole draw window");
            Assert.AreEqual(0, run.DispatchedWithoutVoxels, "partitions without a voxel that got a mesh job");
            // The main-thread budget bounds the scheduler's share of a frame: the flight has more work than fits,
            // so turns are cut, and a frame exceeds the budget by one item of work at most. Under the 60 frames cap
            // the budget takes half the frame's spare time (2 ms + 4.17 ms with the shipped share 0.5): the draw
            // window is meshed 0.587 to 0.594 on average in the flight (0.60 without a budget, 0.40 with the budget
            // alone), the scheduler's 95th percentile is 6.2 ms against the limit of 8.17 ms, and 115 to 130
            // frames are cut (Editor, 2026-11-15). Uncapped the effective budget is the budget, so the limit is
            // twice the budget there.
            float budget = VoxelWorld.Instance.Settings.Scheduler.mainThreadBudgetMs;
            Assert.Greater(budget, 0f, "the shipped settings bound the main-thread work per frame");
            Assert.Greater(run.FramesCut, 0, "frames in which the budget stopped a turn");
            Assert.LessOrEqual(run.SchedulerP95Ms, run.EffectiveBudgetMs + budget,
                $"scheduler main-thread ms per frame at the 95th percentile, against the budget after the frame cap " +
                $"({run.EffectiveBudgetMs:0.##} ms) plus the budget");
            Assert.GreaterOrEqual(run.WindowCoverage, 0.5f,
                "meshed share of the draw window during the flight (the spare time of a capped frame is used)");
        }

        [UnityTest]
        [Order(3)]
        [Timeout(900000)]
        public IEnumerator FreshWorld_UnderATightBudget_IsBuiltTheSame(){
            // The main-thread budget only spreads a stage's turn over more frames: a world built with four items
            // of work per frame (a claim in the pool, the upload of a chunk, a row of a queue scan, the start of
            // a batch) ends in the same state as one built without a limit. The budget's clock counts the items
            // it is asked for instead of the time, so the test does not depend on the machine.
            const int draw = 4;
            PipelineRun whole = new();
            yield return BuildFreshWorld(0, whole, "no budget: ", draw, FrameBudget.Unlimited);

            long asked = 0;
            FrameBudget budget = new(4f, () => asked++, 1d);
            PipelineRun cut = new();
            yield return BuildFreshWorld(0, cut, "four items per frame: ", draw, budget);

            Assert.Greater(budget.FramesCut, 0, "frames in which the budget stopped a turn");
            Assert.Greater(cut.BuildFrames, whole.BuildFrames, "the cut turns took more frames");
            Assert.AreEqual(whole.LoadedChunks, cut.LoadedChunks, "chunks loaded");
            Assert.AreEqual(whole.ActivePartitions, cut.ActivePartitions, "active partitions");
            Assert.AreEqual(whole.CollidablePartitions, cut.CollidablePartitions, "collidable partitions");
            AssertQueuesInsideWindows(cut);
            Assert.AreEqual(0, cut.DispatchedWithoutVoxels, "partitions without a voxel that got a mesh job");
        }

        private static void AssertQueuesInsideWindows(PipelineRun run){
            ChunkSettings chunk = VoxelWorld.Instance.Settings.Chunk;
            int drawSide = 2 * chunk.DrawDistance + 1;
            int meshWindow = drawSide * drawSide * math.min(PartitionsPerChunk, drawSide);
            Assert.LessOrEqual(run.PeakQueue[PipelineProbe.Mesh], meshWindow, "mesh queue against the draw window");
            Assert.LessOrEqual(run.PeakQueue[PipelineProbe.Data], chunk.LoadDistance.SquareSize(),
                "data queue against the load window");
            Assert.LessOrEqual(run.PeakQueue[PipelineProbe.Light], (chunk.LoadDistance - 1).SquareSize(),
                "light queue against the light window");
            Assert.LessOrEqual(run.PeakQueue[PipelineProbe.Bake], chunk.UpdateDistance.SquareSize() * PartitionsPerChunk,
                "bake queue against the update window");
        }

        /// <summary>What a run measured, for the asserts of the caller.</summary>
        internal sealed class PipelineRun{
            /// <summary>Frames and seconds until the world was built (fresh world) or complete again (after a flight).</summary>
            internal int BuildFrames;

            internal float BuildSeconds;

            /// <summary>Longest queue per stage over the whole run.</summary>
            internal int[] PeakQueue = new int[4];

            /// <summary>Average meshed share during the flight: columns near the focus, and the whole draw window.</summary>
            internal float NearCoverage;

            internal float WindowCoverage;

            /// <summary>Partitions that went through a mesh job, and the ones without a voxel that needed none.</summary>
            internal int MeshedPartitions;

            internal int PartitionsWithoutMesh;

            /// <summary>Partitions without a voxel that were handed to a mesh job all the same.</summary>
            internal int DispatchedWithoutVoxels;

            /// <summary>The world at the end of the run: chunks in the store, active and collidable partitions.</summary>
            internal int LoadedChunks;

            internal int ActivePartitions;

            internal int CollidablePartitions;

            /// <summary>Frames in which the main-thread budget stopped a stage's turn.</summary>
            internal int FramesCut;

            /// <summary>The scheduler's main-thread milliseconds of a frame that 95 % of the frames stay under.</summary>
            internal float SchedulerP95Ms;

            /// <summary>The main-thread budget of the last frame, after the frame cap (0: no limit).</summary>
            internal float EffectiveBudgetMs;

            internal void CopyCounts(PipelineProbe probe){
                FramesCut = probe.FramesCut;
                EffectiveBudgetMs = probe.EffectiveBudgetMs;
                SchedulerP95Ms = probe.SchedulerP95Ms;
                probe.PeakQueue.CopyTo(PeakQueue, 0);
                LoadedChunks = probe.LoadedChunks;
                ActivePartitions = probe.ActivePartitions;
                CollidablePartitions = probe.CollidablePartitions;
                MeshedPartitions = probe.MeshedPartitions;
                PartitionsWithoutMesh = probe.PartitionsWithoutMesh;
                DispatchedWithoutVoxels = probe.DispatchedWithoutVoxels;
            }
        }

        private static void SetFrameCap(int frameRate){
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = frameRate > 0 ? frameRate : -1;
        }

        private static string CapName(int frameRate){
            return frameRate > 0 ? $"{frameRate} fps cap" : "uncapped";
        }

        /// <summary>
        ///     A world that has not built anything yet, at <paramref name="drawDistance" /> or, for 0, at the shipped
        ///     draw distance.
        /// </summary>
        private static IEnumerator LoadFreshWorld(int frameRate, int drawDistance){
            int draw = drawDistance > 0 ? drawDistance : PlayModeFixtures.ShippedDrawDistance;
            bool fresh = VoxelWorld.Instance == null || VoxelWorld.Instance.Settings.Chunk.DrawDistance != draw;
            yield return PlayModeFixtures.LoadGameScene(drawDistance: draw);
            if (!fresh) yield return PlayModeFixtures.ReloadWorldWithDrawDistance(draw);
            Assert.AreEqual(draw, VoxelWorld.Instance.Settings.Chunk.DrawDistance);
            SetFrameCap(frameRate);
        }

        /// <summary>
        ///     Loads a fresh world and samples it until it is built. <paramref name="variant" /> names the settings
        ///     in the log lines when they are not the shipped ones. <paramref name="budget" /> replaces the
        ///     main-thread budget of the world's scheduler.
        /// </summary>
        internal static IEnumerator BuildFreshWorld(int frameRate, PipelineRun run, string variant = "",
            int drawDistance = 0, FrameBudget budget = null){
            yield return LoadFreshWorld(frameRate, drawDistance);
            VoxelWorld world = VoxelWorld.Instance;
            if (budget != null)
                PlayModeFixtures.GetPrivateField<VoxelEngineScheduler>(world, "_scheduler").Budget = budget;
            int draw = world.Settings.Chunk.DrawDistance;
            using PipelineProbe probe = new(world);
            while (!probe.Built){
                if (probe.Seconds > BuildTimeoutSeconds) Assert.Fail($"world not built after {BuildTimeoutSeconds} s");
                yield return null;
                probe.Sample();
            }

            Debug.Log(probe.Report($"{variant}fresh world, draw {draw}, {CapName(frameRate)}"));
            run.BuildFrames = probe.Frames;
            run.BuildSeconds = probe.Seconds;
            run.CopyCounts(probe);
        }

        /// <summary>
        ///     Loads a fresh world, flies along +x as soon as the focus column is collidable, then stops and samples
        ///     until the world around the focus is complete.
        /// </summary>
        internal static IEnumerator FlyThroughBuildingWorld(int frameRate, PipelineRun run, string variant = "",
            int drawDistance = 0){
            yield return LoadFreshWorld(frameRate, drawDistance);
            VoxelWorld world = VoxelWorld.Instance;
            int draw = world.Settings.Chunk.DrawDistance;
            string label = $"{variant}flight {FlightSpeed:0} u/s for {FlightSeconds:0} s, draw {draw}, {CapName(frameRate)}";
            Transform focus = PlayModeFixtures.GetPrivateField<Transform>(world, "focus");
            using PipelineProbe probe = new(world);

            // The player starts to fly as soon as there is ground under the focus.
            yield return AllocationTests.WaitForFocusColumn(world, 60f, true);
            Transform body = FlightBody(focus);
            Vector3 start = body.position;
            float flightStart = Time.realtimeSinceStartup;
            double nearSum = 0, windowSum = 0;
            int coverageSamples = 0;
            PathGround ground = new();
            while (true){
                float elapsed = Time.realtimeSinceStartup - flightStart;
                if (elapsed >= FlightSeconds) break;
                body.position = start + new Vector3(FlightSpeed * elapsed, 0f, 0f);
                yield return null;
                probe.Sample();
                if (probe.Frames % 15 != 0) continue;
                nearSum += probe.Coverage(NearRadius);
                windowSum += probe.Coverage(draw);
                coverageSamples++;
                ground.Sample(world, focus);
            }

            CultureInfo inv = CultureInfo.InvariantCulture;
            float distance = (focus.position - start).magnitude;
            Debug.Log(ground.Report(label));
            ground.AssertBelow(focus);
            run.NearCoverage = (float)(nearSum / math.max(coverageSamples, 1));
            run.WindowCoverage = (float)(windowSum / math.max(coverageSamples, 1));
            Debug.Log(probe.Report(label) + string.Format(inv,
                "[PipelineProbe] {0}: flew {1:0} units ({2:0} chunks); meshed share of the columns within {3} of the " +
                "focus: average {4:0.000}, at the end {5:0.000}; of the draw window: average {6:0.000}, at the end {7:0.000}",
                label, distance, distance / ChunkWidth, NearRadius, run.NearCoverage, probe.Coverage(NearRadius),
                run.WindowCoverage, probe.Coverage(draw)));

            // The player stops; how long until the world around them is complete?
            probe.RestartCompletion();
            float settleStart = probe.Seconds;
            int settleStartFrame = probe.Frames;
            while (!probe.Built){
                if (probe.Seconds - settleStart > BuildTimeoutSeconds)
                    Assert.Fail($"world not built {BuildTimeoutSeconds} s after the flight");
                yield return null;
                probe.Sample();
            }

            run.BuildFrames = probe.Frames - settleStartFrame;
            run.BuildSeconds = probe.Seconds - settleStart;
            run.CopyCounts(probe);
            Debug.Log(string.Format(inv, "[PipelineProbe] {0}: world complete {1:0.0} s ({2} frames) after the flight; " +
                                         "mesh queue peak {3}",
                label, run.BuildSeconds, run.BuildFrames, run.PeakQueue[PipelineProbe.Mesh]));
        }

        /// <summary>
        ///     The run for the frame-time goal (120 frames per second at the 95th percentile, no stutter the player
        ///     notices): a fresh world with the focus standing still for <see cref="StandSeconds" /> where the
        ///     flights start (above the spawn at <see cref="FlightHeight" />, see <see cref="FlightBody" />), then
        ///     one flight through the built world per entry of <see cref="GoalFlightSpeeds" />, each followed by
        ///     the stop. It prints when the world was built, the frame times from <see cref="StableAfterSeconds" />
        ///     after the start to the end of the standing phase, the frame times of every flight with its queue
        ///     peaks and the meshed share near the focus, and how long the world takes to settle after it; with
        ///     <paramref name="attribute" /> also where the main thread spent the frames of each phase. Asserts
        ///     nothing but the time limit.
        /// </summary>
        internal static IEnumerator StandThenFly(int frameRate, string variant = "", int drawDistance = 0,
            bool attribute = false){
            yield return LoadFreshWorld(frameRate, drawDistance);
            VoxelWorld world = VoxelWorld.Instance;
            int draw = world.Settings.Chunk.DrawDistance;
            CultureInfo inv = CultureInfo.InvariantCulture;
            string label = $"{variant}goal run, draw {draw}, {CapName(frameRate)}";
            Transform focus = PlayModeFixtures.GetPrivateField<Transform>(world, "focus");
            FlightBody(focus);
            using PipelineProbe probe = new(world, attribute);

            // Standing where the flights start: the first build, and the frames after it
            int stableFrom = -1;
            string places = Place(world, focus, label, "start of the standing phase");
            while (probe.Seconds < StandSeconds){
                yield return null;
                if (stableFrom < 0 && probe.Seconds >= StableAfterSeconds){
                    stableFrom = probe.Frames;
                    places += Place(world, focus, label, $"{StableAfterSeconds:0} s after the start");
                }

                probe.Sample();
            }

            int standEnd = probe.Frames;
            places += Place(world, focus, label, "end of the standing phase");
            Debug.Log(places + probe.Report($"{label}, standing {StandSeconds:0} s") +
                      string.Format(inv, "[PipelineProbe] {0}: world built after {1:0.0} s (frame {2}; -1: not built)\n",
                          label, probe.Built ? probe.BuiltSeconds : -1f, probe.Built ? probe.BuiltFrames : -1) +
                      probe.FrameTimes($"{label}, standing, first {StableAfterSeconds:0} s (not judged)", 0, stableFrom) +
                      probe.FrameTimes($"{label}, standing, {StableAfterSeconds:0} s to {StandSeconds:0} s after the start",
                          stableFrom, standEnd) +
                      probe.Attribution($"{label}, standing, first {StableAfterSeconds:0} s (not judged)", 0, stableFrom) +
                      probe.Attribution(
                          $"{label}, standing, {StableAfterSeconds:0} s to {StandSeconds:0} s after the start", stableFrom,
                          standEnd));

            foreach (float speed in GoalFlightSpeeds) yield return FlyAndSettle(probe, world, focus, label, speed);
        }

        /// <summary>
        ///     What a flight moves: the focus alone, with nothing in the scene that can fall. In the game scene the
        ///     focus is the camera of a player with physics. Moving the camera alone leaves the player behind, the
        ///     ground under it unloads, it falls, and the camera, its child, sinks with it whenever the test does
        ///     not hold it (between two phases). A measurement of the pipeline needs the focus and the rendering
        ///     camera, not the player: the focus is taken out of the player and the player object is switched off.
        ///     The HUD and the rest of the scene stay as they are.
        ///     The flight goes above the terrain (the spawn is on the sea floor, 46 blocks under water): the focus
        ///     is lifted to <see cref="FlightHeight" /> and looks along the flight, <see cref="FlightPitch" /> degrees
        ///     down. The effect of the water around the spawn would stay on the picture, because the handler that
        ///     follows the camera sits on the player: it is cleared here. <see cref="FreshSceneAfterFlight" /> in the
        ///     fixture's tear-down gives the tests that follow a scene with a player again.
        /// </summary>
        internal static Transform FlightBody(Transform focus){
            if (focus.parent != null){
                GameObject player = focus.root.gameObject;
                focus.SetParent(null, true);
                player.SetActive(false);
                PostProcessManager post = PostProcessManager.Instance;
                if (post){
                    post.ResetLiftGammaGain();
                    post.ResetColorAdjustments();
                }

                UnderwaterFogFeature.Enabled = false;
            }

            Vector3 eye = focus.position;
            focus.SetPositionAndRotation(new Vector3(eye.x, FlightHeight, eye.z), Quaternion.Euler(FlightPitch, 90f, 0f));
            return focus;
        }

        /// <summary>
        ///     After a flight the player is switched off and the focus is far from the spawn. Reloads the scene when
        ///     that is the case (the focus of the game scene hangs on the player unless <see cref="FlightBody" />
        ///     took it out), so the next test finds the scene as it ships.
        /// </summary>
        internal static IEnumerator FreshSceneAfterFlight(){
            VoxelWorld world = VoxelWorld.Instance;
            if (world == null) yield break;

            Transform focus = PlayModeFixtures.GetPrivateField<Transform>(world, "focus");
            if (focus == null || focus.parent != null) yield break;

            yield return PlayModeFixtures.ReloadWorldWithDrawDistance(world.Settings.Chunk.DrawDistance);
        }

        /// <summary>
        ///     Where the focus (the transform the tests move, the camera in the game scene) and the object it
        ///     hangs on (the player with its physics) are, and the highest block under the focus. A camera under
        ///     the ground, or one that sinks with a falling player between two phases, draws another picture than
        ///     a flight does, so every phase says where it was.
        /// </summary>
        internal static string Place(VoxelWorld world, Transform focus, string label, string moment){
            Vector3 eye = focus.position;
            Transform root = focus.root;
            int3 column = eye.Int3();
            int top = HighestBlock(world, column);
            ushort inside = top >= 0 && eye.y >= 0f && eye.y < ChunkHeight ? world.GetVoxel(column) : (ushort)0;
            Vector3 body = root.position;
            bool rootLoaded = world.IsChunkLoaded(new int3((int)math.floor(body.x), 0, (int)math.floor(body.z)));
            return string.Format(CultureInfo.InvariantCulture,
                "[PipelineProbe] {0}: place at the {1}: focus '{2}' at ({3:0.0}, {4:0.0}, {5:0.0}) in voxel id {12} " +
                "(0: air), highest block of its column y {6} (-1: column not loaded); its root '{7}' at ({8:0.0}, " +
                "{9:0.0}, {10:0.0}), the column under the root is {11}\n", label, moment, focus.name, eye.x, eye.y,
                eye.z, top, root.name, body.x, body.y, body.z, rootLoaded ? "loaded" : "not loaded", inside);
        }

        /// <summary>The highest block of the column of <paramref name="column" />; -1 when it is not loaded or empty.</summary>
        private static int HighestBlock(VoxelWorld world, int3 column){
            if (!world.IsChunkLoaded(column)) return -1;

            for (int y = ChunkHeight - 1; y >= 0; y--)
                if (world.GetVoxel(new int3(column.x, y, column.z)) != 0)
                    return y;
            return -1;
        }

        /// <summary>
        ///     The ground under a flight: the highest block of the focus column, read at every sample. A flight
        ///     that is meant to go above the terrain says with it how much room it had, and fails when the ground
        ///     reached the focus.
        /// </summary>
        internal struct PathGround{
            private int _samples, _notLoaded, _highest;
            private float _highestAtX;

            internal void Sample(VoxelWorld world, Transform focus){
                Vector3 eye = focus.position;
                int top = HighestBlock(world, eye.Int3());
                _samples++;
                if (top < 0) _notLoaded++;
                if (top <= _highest) return;

                _highest = top;
                _highestAtX = eye.x;
            }

            internal string Report(string label){
                return string.Format(CultureInfo.InvariantCulture,
                    "[PipelineProbe] {0}: ground under the flight: highest block y {1} (at x {2:0}) in {3} samples of " +
                    "the focus column, {4} of them without loaded voxels\n", label, _highest, _highestAtX, _samples,
                    _notLoaded);
            }

            internal void AssertBelow(Transform focus){
                Assert.Less(_highest, focus.position.y - 1f, "the ground reached the height of the flight");
            }
        }

        /// <summary>One flight of <see cref="StandThenFly" /> along +x at <paramref name="speed" />, and the stop.</summary>
        private static IEnumerator FlyAndSettle(PipelineProbe probe, VoxelWorld world, Transform focus, string label,
            float speed){
            CultureInfo inv = CultureInfo.InvariantCulture;
            int draw = world.Settings.Chunk.DrawDistance;

            // The frame that wrote the report of the phase before is the test's own cost, not the flight's
            yield return null;
            probe.Sample();
            int flightBegin = probe.Frames;
            probe.ResetPeaks();
            Transform body = FlightBody(focus);
            Vector3 start = body.position;
            string flight = $"{label}, flight {speed:0} u/s for {FlightSeconds:0} s";
            string places = Place(world, focus, flight, "start of the flight");
            float sankAt = -1f;
            float flightStart = Time.realtimeSinceStartup;
            double nearSum = 0, windowSum = 0;
            float nearMin = 1f;
            int coverageSamples = 0;
            PathGround ground = new();
            while (true){
                float elapsed = Time.realtimeSinceStartup - flightStart;
                if (elapsed >= FlightSeconds) break;
                body.position = start + new Vector3(speed * elapsed, 0f, 0f);
                yield return null;
                probe.Sample();
                if (sankAt < 0f && math.abs(focus.position.y - start.y) > 0.5f){
                    sankAt = elapsed;
                    places += Place(world, focus, flight, string.Format(inv,
                        "{0:0.0} s into the flight, the focus has left the height of its start", elapsed));
                }

                if (probe.Frames % 15 != 0) continue;
                float near = probe.Coverage(NearRadius);
                nearSum += near;
                nearMin = math.min(nearMin, near);
                windowSum += probe.Coverage(draw);
                coverageSamples++;
                ground.Sample(world, focus);
            }

            // What the flight left behind is read now and written after the stop: a report costs frame time
            int flightEnd = probe.Frames;
            places += Place(world, focus, flight, "end of the flight");
            int peakData = probe.PeakQueue[PipelineProbe.Data], peakLight = probe.PeakQueue[PipelineProbe.Light];
            int peakMesh = probe.PeakQueue[PipelineProbe.Mesh], peakBake = probe.PeakQueue[PipelineProbe.Bake];
            int peakBuilds = probe.PeakPendingBuilds, meshQueue = world.Stats.MeshQueue, bakeQueue = world.Stats.BakeQueue;
            float nearEnd = probe.Coverage(NearRadius), windowEnd = probe.Coverage(draw);

            // The player stops; how long until the world around them is complete?
            probe.RestartCompletion();
            float settleStart = probe.Seconds;
            while (!probe.Built){
                if (probe.Seconds - settleStart > BuildTimeoutSeconds)
                    Assert.Fail($"world not built {BuildTimeoutSeconds} s after the flight");
                yield return null;
                probe.Sample();
            }

            int settleEnd = probe.Frames;
            float settleSeconds = probe.Seconds - settleStart;
            places += Place(world, focus, flight, "world complete after the flight");
            string report = places + ground.Report(flight) + probe.FrameTimes(flight, flightBegin, flightEnd) +
                            string.Format(inv,
                "[PipelineProbe] {0}: {1} frames ({2:0} fps); queue peaks data {3}, light {4}, mesh {5}, bake {6}, " +
                "renderer pending builds {7}; at the end of the flight mesh queue {8}, bake queue {9}; meshed share of " +
                "the columns within {10} of the focus: average {11:0.000}, lowest {12:0.000}, at the end {13:0.000}; " +
                "of the draw window: average {14:0.000}, at the end {15:0.000}\n",
                flight, flightEnd - flightBegin, (flightEnd - flightBegin) / FlightSeconds, peakData, peakLight, peakMesh,
                peakBake, peakBuilds, meshQueue, bakeQueue, NearRadius, nearSum / math.max(coverageSamples, 1), nearMin,
                nearEnd, windowSum / math.max(coverageSamples, 1), windowEnd) +
                            probe.Attribution(flight, flightBegin, flightEnd) + string.Format(inv,
                                "[PipelineProbe] {0}: world complete {1:0.0} s ({2} frames) after the flight\n",
                                flight, settleSeconds, settleEnd - flightEnd);
            if (settleEnd > flightEnd)
                report += probe.FrameTimes($"{flight}, settling after it", flightEnd, settleEnd) +
                          probe.Attribution($"{flight}, settling after it", flightEnd, settleEnd);
            Debug.Log(report);
            Assert.AreEqual(start.y, focus.position.y, 0.01f, "the focus left its height during the flight or the stop");
            ground.AssertBelow(focus);
        }
    }
}
