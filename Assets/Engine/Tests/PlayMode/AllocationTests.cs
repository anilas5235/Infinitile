using System;
using System.Collections;
using Engine.Scripts.Jobs.Core;
using Engine.Scripts.Render;
using Engine.Scripts.World;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.TestTools.Constraints;
using static Engine.Scripts.Utils.VoxelConstants;
using Is = UnityEngine.TestTools.Constraints.Is;

namespace Engine.Tests.PlayMode{
    /// <summary>
    ///     TST-21 smoke test (scene loads, terrain around the focus becomes collidable, no errors) and
    ///     TST-E21 (PERF-04): the per-frame engine step allocates no managed memory during flight (F-24).
    /// </summary>
    public class AllocationTests{
        private const int FlightFrames = 300;
        // TST-E21: a fixed distance per frame, not speed * deltaTime; headless frames are so short that 300 frames
        // barely moved the focus. 300 * 0.5 units cross several chunk borders, so new chunks load during flight.
        private const float FlightStepPerFrame = 0.5f;

        [UnitySetUp]
        public IEnumerator SetUp(){
            yield return PlayModeFixtures.LoadGameScene();
        }

        private static bool FocusColumnCollidable(VoxelWorld world){
            int3 focus = world.Focus.Pos;
            for (int y = 0; y < PartitionsPerChunk; y++)
                if (world.IsCollidable(new int3(focus.x, y, focus.z))) return true;
            return false;
        }

        private static IEnumerator WaitForFocusColumn(VoxelWorld world, float timeoutSeconds, bool failOnTimeout){
            float deadline = Time.realtimeSinceStartup + timeoutSeconds;
            while (!FocusColumnCollidable(world)){
                if (Time.realtimeSinceStartup > deadline){
                    if (failOnTimeout) Assert.Fail($"no collidable partition around the focus after {timeoutSeconds}s");
                    yield break;
                }

                yield return null;
            }
        }

        [UnityTest]
        [Timeout(120000)]
        public IEnumerator SampleScene_GeneratesAndMeshesAroundFocus_WithoutErrors(){
            // TST-21: the game scene reaches a playable state within 30 s and logs no errors on the way.
            VoxelWorld world = VoxelWorld.Instance;
            yield return WaitForFocusColumn(world, 30f, true);
            Assert.Greater(world.ChunkManager.ChunkCount(), 0);
            LogAssert.NoUnexpectedReceived();
        }

        private static IEnumerator WaitForSteadyState(VoxelEngineScheduler scheduler, float timeoutSeconds){
            // PERF-04 measures the steady state: the world around the focus is generated, meshed and collided, so
            // every handler sleeps once. One-time warm-up work (first job schedules, render buffer growth) is over.
            DataJobStateHandler data = PlayModeFixtures.GetPrivateField<DataJobStateHandler>(scheduler, "_dataJobHandler");
            MeshJobStateHandler mesh = PlayModeFixtures.GetPrivateField<MeshJobStateHandler>(scheduler, "_meshJobHandler");
            ColliderJobStateHandler collider =
                PlayModeFixtures.GetPrivateField<ColliderJobStateHandler>(scheduler, "_colliderJobHandler");
            float deadline = Time.realtimeSinceStartup + timeoutSeconds;
            while (!(data.Sleeping && mesh.Sleeping && collider.Sleeping)){
                if (Time.realtimeSinceStartup > deadline)
                    Assert.Fail($"the scheduler did not reach a steady state within {timeoutSeconds}s");
                yield return null;
            }
        }

        [UnityTest]
        [Timeout(300000)]
        public IEnumerator EngineStep_DoesNotAllocateDuringFlight(){
            // TST-E21 / PERF-04: scheduler update, renderer update and buffer rebuild must not allocate managed
            // memory while the focus flies through loaded and loading terrain (F-24: LINQ in the renderer).
            VoxelWorld world = VoxelWorld.Instance;
            VoxelWorldRenderer renderer = VoxelWorldRenderer.Instance;
            Assert.IsNotNull(renderer, "VoxelWorldRenderer missing in scene");
            yield return WaitForFocusColumn(world, 45f, false);

            VoxelEngineScheduler scheduler = PlayModeFixtures.GetPrivateField<VoxelEngineScheduler>(world, "_scheduler");
            yield return WaitForSteadyState(scheduler, 120f);
            Transform focus = PlayModeFixtures.GetPrivateField<Transform>(world, "focus");
            Action rendererUpdate = (Action)Delegate.CreateDelegate(typeof(Action), renderer,
                PlayModeFixtures.GetPrivateMethod(typeof(VoxelWorldRenderer), "Update"));
            Action rendererLateUpdate = (Action)Delegate.CreateDelegate(typeof(Action), renderer,
                PlayModeFixtures.GetPrivateMethod(typeof(VoxelWorldRenderer), "LateUpdate"));

            TestDelegate engineStep = () => {
                scheduler.ScheduleUpdate(world.Focus);
                rendererUpdate();
                rendererLateUpdate();
            };

            int allocatingFrames = 0;
            int firstAllocatingFrame = -1;
            Vector3 start = focus.position;
            for (int frame = 0; frame < FlightFrames; frame++){
                focus.position += new Vector3(FlightStepPerFrame, 0f, 0f);
                try{
                    Assert.That(engineStep, Is.Not.AllocatingGCMemory());
                }
                catch (AssertionException){
                    allocatingFrames++;
                    if (firstAllocatingFrame < 0) firstAllocatingFrame = frame;
                }

                yield return null;
            }

            Debug.Log($"flight covered {(focus.position - start).magnitude:0} units; {allocatingFrames}/{FlightFrames} frames allocated");
            Assert.Greater((focus.position - start).magnitude, ChunkWidth, "the flight must cross a chunk border");
            Assert.AreEqual(0, allocatingFrames,
                $"{allocatingFrames} of {FlightFrames} frames allocated managed memory (first at frame {firstAllocatingFrame})");
        }

        [UnityTest]
        public IEnumerator FocusUpdate_DoesNotAllocate(){
            // PERF-04 for the state handlers: re-prioritising the queues on a focus change must not allocate.
            VoxelWorld world = VoxelWorld.Instance;
            VoxelEngineScheduler scheduler = PlayModeFixtures.GetPrivateField<VoxelEngineScheduler>(world, "_scheduler");
            PriorityUtil.Focus original = world.Focus;
            PriorityUtil.Focus shifted = new(original.Pos + new int3(1, 0, 0), original.Forward);
            try{
                Assert.That(() => scheduler.FocusUpdate(shifted), Is.Not.AllocatingGCMemory());
            }
            finally{
                scheduler.FocusUpdate(original);
            }

            yield return null;
        }
    }
}
