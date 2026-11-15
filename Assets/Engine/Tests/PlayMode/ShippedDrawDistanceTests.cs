using System.Collections;
using Engine.Scripts.Jobs.Core;
using Engine.Scripts.Utils.Extensions;
using Engine.Scripts.World;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Engine.Tests.PlayMode{
    /// <summary>
    ///     The suite runs at <see cref="PlayModeFixtures.BaselineDrawDistance" />; these tests repeat the checks that
    ///     depend on the size of the world at the shipped draw distance: the scene becomes playable and builds
    ///     completely, and the engine step stays free of managed allocations during a flight. Every test leaves a
    ///     sleeping scheduler behind, so the next fixture can reload its own world at once.
    /// </summary>
    public class ShippedDrawDistanceTests{
        /// <summary>Seconds the whole world around the focus may take to generate, mesh and collide.</summary>
        private const float FullBuildSeconds = 300f;

        [UnityTest]
        [Order(1)]
        [Timeout(900000)]
        public IEnumerator World_BecomesPlayableAndBuildsCompletely_WithoutErrors(){
            // A fresh world at the shipped draw distance: the focus column is collidable within the 30 s of the
            // baseline smoke test, the scheduler reaches its steady state, every chunk within the load distance is
            // there and nothing logged an error (an error log fails the test by itself; warnings are allowed, the
            // post-process handler warns about the camera's chunk until it is loaded). The build times are logged.
            int draw = PlayModeFixtures.ShippedDrawDistance;
            yield return PlayModeFixtures.LoadGameScene();
            yield return PlayModeFixtures.ReloadWorldWithDrawDistance(draw);
            VoxelWorld world = VoxelWorld.Instance;
            Assert.AreEqual(draw, world.Settings.Chunk.DrawDistance);
            float start = Time.realtimeSinceStartup;
            yield return AllocationTests.WaitForFocusColumn(world, 30f, true);
            float playable = Time.realtimeSinceStartup - start;

            VoxelEngineScheduler scheduler = PlayModeFixtures.GetPrivateField<VoxelEngineScheduler>(world, "_scheduler");
            yield return AllocationTests.WaitForSteadyState(scheduler, FullBuildSeconds);
            float built = Time.realtimeSinceStartup - start;
            int chunks = world.ChunkManager.ChunkCount();
            Debug.Log($"draw distance {draw}: focus column collidable after {playable:0.0} s, world built after {built:0.0} s, " +
                      $"{chunks} chunks loaded");
            Assert.GreaterOrEqual(chunks, world.Settings.Chunk.LoadDistance.SquareSize(), "every chunk within the load distance");
        }

        [UnityTest]
        [Order(2)]
        [Timeout(900000)]
        public IEnumerator EngineStep_DoesNotAllocateDuringFlight(){
            // The allocation rule of AllocationTests with the pools and queues of the shipped draw distance.
            yield return PlayModeFixtures.LoadGameScene(drawDistance: PlayModeFixtures.ShippedDrawDistance);
            yield return AllocationTests.FlightDoesNotAllocate(FullBuildSeconds);
        }

        [UnityTearDown]
        public IEnumerator TearDown(){
            // The flight ends with chunks still loading; let them finish (no assert: the test has its verdict).
            if (VoxelWorld.Instance == null) yield break;
            yield return PipelineThroughputTests.FreshSceneAfterFlight();
            float deadline = Time.realtimeSinceStartup + FullBuildSeconds;
            while (!PlayModeFixtures.SchedulerSleeping() && Time.realtimeSinceStartup < deadline) yield return null;
        }
    }
}
