using System.Collections;
using Engine.Scripts.Jobs.Core;
using Engine.Scripts.Settings;
using Engine.Scripts.World;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Engine.Tests.PlayMode{
    /// <summary>
    ///     A batch size of 0 in the settings must not stall its stage: every stage takes at least one item per
    ///     batch. Each case builds the world anew with one of the batch sizes at 0 and waits until the world around
    ///     the focus is built; the tear-down builds it again with the sizes as they were.
    /// </summary>
    public class SchedulerBatchSizeTests{
        private const float BuildTimeoutSeconds = 120f;

        private int _chunkGen, _light, _collider;

        [UnitySetUp]
        public IEnumerator SetUp(){
            yield return PlayModeFixtures.LoadGameScene();
            SchedulerSettings scheduler = VoxelWorld.Instance.Settings.Scheduler;
            _chunkGen = scheduler.chunkGenBatchSize;
            _light = scheduler.lightBatchSize;
            _collider = scheduler.colliderBatchSize;
        }

        [UnityTearDown]
        public IEnumerator TearDown(){
            // The settings object is the asset in memory: the next tests must find it, and the world, as before
            SchedulerSettings scheduler = VoxelWorld.Instance.Settings.Scheduler;
            bool changed = scheduler.chunkGenBatchSize != _chunkGen || scheduler.lightBatchSize != _light ||
                           scheduler.colliderBatchSize != _collider;
            scheduler.chunkGenBatchSize = _chunkGen;
            scheduler.lightBatchSize = _light;
            scheduler.colliderBatchSize = _collider;
            if (!changed) yield break;

            yield return Rebuild();
            Assert.IsTrue(AllStagesSleep(), "the world with the sizes as they were was not built within {0} s",
                BuildTimeoutSeconds);
        }

        [UnityTest]
        [Timeout(300000)]
        public IEnumerator ABatchSizeOfZero_DoesNotStallItsStage([Values("data", "light", "collider")] string stage){
            // An idle scheduler before the scene is unloaded (see ReloadWorldWithDrawDistance), and before the
            // setting changes: the running world must not see it
            yield return WaitUntilAllStagesSleep();
            Assert.IsTrue(AllStagesSleep(), "the world was not built before the test");

            SchedulerSettings scheduler = VoxelWorld.Instance.Settings.Scheduler;
            switch (stage){
                case "data": scheduler.chunkGenBatchSize = 0; break;
                case "light": scheduler.lightBatchSize = 0; break;
                default: scheduler.colliderBatchSize = 0; break;
            }

            yield return Rebuild();
            Assert.IsTrue(AllStagesSleep(), "the world around the focus was not built within {0} s", BuildTimeoutSeconds);
            Assert.Greater(VoxelWorld.Instance.ChunkManager.ChunkCount(), 0, "chunks loaded");
        }

        /// <summary>
        ///     Loads the game scene anew once the scheduler is idle, and waits until every stage sleeps again or
        ///     the time limit is over.
        /// </summary>
        private static IEnumerator Rebuild(){
            yield return WaitUntilAllStagesSleep();
            AsyncOperation load = SceneManager.LoadSceneAsync(PlayModeFixtures.SceneName, LoadSceneMode.Single);
            Assert.IsNotNull(load);
            while (!load.isDone) yield return null;
            yield return PlayModeFixtures.LoadGameScene(); // waits for the engine; the draw distance is unchanged
            yield return null; // the first scheduler update wakes the stages
            yield return WaitUntilAllStagesSleep();
        }

        private static IEnumerator WaitUntilAllStagesSleep(){
            float deadline = Time.realtimeSinceStartup + BuildTimeoutSeconds;
            while (!AllStagesSleep() && Time.realtimeSinceStartup < deadline) yield return null;
        }

        /// <summary>
        ///     True when all four stages sleep. The light stage is asked as well: when it stalls, the mesh stage
        ///     finds no lit chunk and goes to sleep with nothing built.
        /// </summary>
        private static bool AllStagesSleep(){
            VoxelEngineScheduler scheduler =
                PlayModeFixtures.GetPrivateField<VoxelEngineScheduler>(VoxelWorld.Instance, "_scheduler");
            LightJobStateHandler light = PlayModeFixtures.GetPrivateField<LightJobStateHandler>(scheduler, "_lightJobHandler");
            return PlayModeFixtures.SchedulerSleeping() && light.Sleeping;
        }
    }
}
