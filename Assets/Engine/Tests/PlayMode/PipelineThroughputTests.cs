using System;
using System.Collections;
using System.Globalization;
using Engine.Scripts.Jobs.Core;
using Engine.Scripts.Render;
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
        ///     draw window, no mesh job and no GPU build) it needs 701. The limit leaves a quarter of slack.
        /// </summary>
        private const int FreshBuildFrameBudget = 900;

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

            internal void CopyCounts(PipelineProbe probe){
                probe.PeakQueue.CopyTo(PeakQueue, 0);
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
        ///     in the log lines when they are not the shipped ones.
        /// </summary>
        internal static IEnumerator BuildFreshWorld(int frameRate, PipelineRun run, string variant = "",
            int drawDistance = 0){
            yield return LoadFreshWorld(frameRate, drawDistance);
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
            run.CopyCounts(probe);
            Debug.Log(string.Format(inv, "[PipelineProbe] {0}: world complete {1:0.0} s ({2} frames) after the flight; " +
                                         "mesh queue peak {3}",
                label, run.BuildSeconds, run.BuildFrames, run.PeakQueue[PipelineProbe.Mesh]));
        }

        /// <summary>
        ///     The run for the frame-time goal (120 frames per second at the 95th percentile, no stutter the player
        ///     notices): a fresh world with the player standing still for <see cref="StandSeconds" />, then a sprint
        ///     flight through the built world, then the stop. It prints when the world was built, the frame times
        ///     from <see cref="StableAfterSeconds" /> after the start to the end of the standing phase, the frame
        ///     times of the flight with its queue peaks and the meshed share near the focus, and how long the world
        ///     takes to settle after the flight. Asserts nothing but the time limit.
        /// </summary>
        internal static IEnumerator StandThenFly(int frameRate, string variant = "", int drawDistance = 0){
            yield return LoadFreshWorld(frameRate, drawDistance);
            VoxelWorld world = VoxelWorld.Instance;
            int draw = world.Settings.Chunk.DrawDistance;
            CultureInfo inv = CultureInfo.InvariantCulture;
            string label = $"{variant}goal run, draw {draw}, {CapName(frameRate)}";
            Transform focus = PlayModeFixtures.GetPrivateField<Transform>(world, "focus");
            using PipelineProbe probe = new(world);

            // Standing: the first build, and the frames after it
            int stableFrom = -1;
            while (probe.Seconds < StandSeconds){
                yield return null;
                if (stableFrom < 0 && probe.Seconds >= StableAfterSeconds) stableFrom = probe.Frames;
                probe.Sample();
            }

            int standEnd = probe.Frames;
            Debug.Log(probe.Report($"{label}, standing {StandSeconds:0} s") +
                      string.Format(inv, "[PipelineProbe] {0}: world built after {1:0.0} s (frame {2}; -1: not built)\n",
                          label, probe.Built ? probe.BuiltSeconds : -1f, probe.Built ? probe.BuiltFrames : -1) +
                      probe.FrameTimes($"{label}, standing, first {StableAfterSeconds:0} s (not judged)", 0, stableFrom) +
                      probe.FrameTimes($"{label}, standing, {StableAfterSeconds:0} s to {StandSeconds:0} s after the start",
                          stableFrom, standEnd));

            // The sprint flight
            probe.ResetPeaks();
            Vector3 start = focus.position;
            float flightStart = Time.realtimeSinceStartup;
            double nearSum = 0, windowSum = 0;
            float nearMin = 1f;
            int coverageSamples = 0;
            while (true){
                float elapsed = Time.realtimeSinceStartup - flightStart;
                if (elapsed >= FlightSeconds) break;
                focus.position = start + new Vector3(FlightSpeed * elapsed, 0f, 0f);
                yield return null;
                probe.Sample();
                if (probe.Frames % 15 != 0) continue;
                float near = probe.Coverage(NearRadius);
                nearSum += near;
                nearMin = math.min(nearMin, near);
                windowSum += probe.Coverage(draw);
                coverageSamples++;
            }

            int flightEnd = probe.Frames;
            string flight = $"{label}, flight {FlightSpeed:0} u/s for {FlightSeconds:0} s";
            Debug.Log(probe.FrameTimes(flight, standEnd, flightEnd) + string.Format(inv,
                "[PipelineProbe] {0}: {1} frames ({2:0} fps); queue peaks data {3}, light {4}, mesh {5}, bake {6}, " +
                "renderer pending builds {7}; at the end of the flight mesh queue {8}, bake queue {9}; meshed share of " +
                "the columns within {10} of the focus: average {11:0.000}, lowest {12:0.000}, at the end {13:0.000}; " +
                "of the draw window: average {14:0.000}, at the end {15:0.000}",
                flight, flightEnd - standEnd, (flightEnd - standEnd) / FlightSeconds, probe.PeakQueue[PipelineProbe.Data],
                probe.PeakQueue[PipelineProbe.Light], probe.PeakQueue[PipelineProbe.Mesh],
                probe.PeakQueue[PipelineProbe.Bake], probe.PeakPendingBuilds, world.Stats.MeshQueue,
                world.Stats.BakeQueue, NearRadius, nearSum / math.max(coverageSamples, 1), nearMin,
                probe.Coverage(NearRadius), windowSum / math.max(coverageSamples, 1), probe.Coverage(draw)));

            // The player stops; how long until the world around them is complete?
            probe.RestartCompletion();
            float settleStart = probe.Seconds;
            while (!probe.Built){
                if (probe.Seconds - settleStart > BuildTimeoutSeconds)
                    Assert.Fail($"world not built {BuildTimeoutSeconds} s after the flight");
                yield return null;
                probe.Sample();
            }

            Debug.Log(string.Format(inv, "[PipelineProbe] {0}: world complete {1:0.0} s ({2} frames) after the flight\n",
                          label, probe.Seconds - settleStart, probe.Frames - flightEnd) +
                      probe.FrameTimes($"{label}, settling after the flight", flightEnd, probe.Frames));
        }
    }
}
