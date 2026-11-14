using System.Collections;
using System.Globalization;
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

        private const float FlightSeconds = 30f;

        /// <summary>Columns around the focus that count as "near" for the near-first check.</summary>
        private const int NearRadius = 4;

        /// <summary>
        ///     Frames a fresh world at the shipped draw distance may take until every stage sleeps and no GPU build
        ///     waits. The scheduler that gave every stage one of three steps on every fourth frame needed 7553
        ///     frames (125.9 s at 60 frames per second, measured 2026-11-14). The work-conserving one needs 2532
        ///     with the mesh batch of 8 and the 4 GPU build slots of that time, and 1345 with the shipped 16 and 8;
        ///     the GPU build slots limit the mesh stage now. The budget leaves a quarter of slack.
        /// </summary>
        private const int FreshBuildFrameBudget = 1700;

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
            yield return WaitForBuiltWorld();
        }

        /// <summary>A flight ends with chunks still loading; lets them finish (no assert: the test has its verdict).</summary>
        internal static IEnumerator WaitForBuiltWorld(){
            if (VoxelWorld.Instance == null) yield break;
            float deadline = Time.realtimeSinceStartup + BuildTimeoutSeconds;
            while (!PlayModeFixtures.SchedulerSleeping() && Time.realtimeSinceStartup < deadline) yield return null;
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
        }

        private static void SetFrameCap(int frameRate){
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = frameRate > 0 ? frameRate : -1;
        }

        private static string CapName(int frameRate){
            return frameRate > 0 ? $"{frameRate} fps cap" : "uncapped";
        }

        /// <summary>A world at the shipped draw distance that has not built anything yet.</summary>
        private static IEnumerator LoadFreshWorld(int frameRate){
            int draw = PlayModeFixtures.ShippedDrawDistance;
            bool fresh = VoxelWorld.Instance == null || VoxelWorld.Instance.Settings.Chunk.DrawDistance != draw;
            yield return PlayModeFixtures.LoadGameScene(drawDistance: draw);
            if (!fresh) yield return PlayModeFixtures.ReloadWorldWithDrawDistance(draw);
            Assert.AreEqual(draw, VoxelWorld.Instance.Settings.Chunk.DrawDistance);
            SetFrameCap(frameRate);
        }

        /// <summary>
        ///     Loads a fresh world and samples it until it is built. <paramref name="variant" /> names the settings
        ///     in the log lines when they are not the shipped ones.
        /// </summary>
        internal static IEnumerator BuildFreshWorld(int frameRate, PipelineRun run, string variant = ""){
            yield return LoadFreshWorld(frameRate);
            VoxelWorld world = VoxelWorld.Instance;
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
            probe.PeakQueue.CopyTo(run.PeakQueue, 0);
        }

        /// <summary>
        ///     Loads a fresh world, flies along +x as soon as the focus column is collidable, then stops and samples
        ///     until the world around the focus is complete.
        /// </summary>
        internal static IEnumerator FlyThroughBuildingWorld(int frameRate, PipelineRun run, string variant = ""){
            yield return LoadFreshWorld(frameRate);
            VoxelWorld world = VoxelWorld.Instance;
            int draw = world.Settings.Chunk.DrawDistance;
            string label = $"{variant}flight {FlightSpeed:0} u/s for {FlightSeconds:0} s, draw {draw}, {CapName(frameRate)}";
            Transform focus = PlayModeFixtures.GetPrivateField<Transform>(world, "focus");
            using PipelineProbe probe = new(world);

            // The player starts to fly as soon as there is ground under the focus.
            yield return AllocationTests.WaitForFocusColumn(world, 60f, true);
            Vector3 start = focus.position;
            float flightStart = Time.realtimeSinceStartup;
            double nearSum = 0, windowSum = 0;
            int coverageSamples = 0;
            while (true){
                float elapsed = Time.realtimeSinceStartup - flightStart;
                if (elapsed >= FlightSeconds) break;
                focus.position = start + new Vector3(FlightSpeed * elapsed, 0f, 0f);
                yield return null;
                probe.Sample();
                if (probe.Frames % 15 != 0) continue;
                nearSum += probe.Coverage(NearRadius);
                windowSum += probe.Coverage(draw);
                coverageSamples++;
            }

            CultureInfo inv = CultureInfo.InvariantCulture;
            float distance = (focus.position - start).magnitude;
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
            probe.PeakQueue.CopyTo(run.PeakQueue, 0);
            Debug.Log(string.Format(inv, "[PipelineProbe] {0}: world complete {1:0.0} s ({2} frames) after the flight; " +
                                         "mesh queue peak {3}",
                label, run.BuildSeconds, run.BuildFrames, run.PeakQueue[PipelineProbe.Mesh]));
        }
    }
}
