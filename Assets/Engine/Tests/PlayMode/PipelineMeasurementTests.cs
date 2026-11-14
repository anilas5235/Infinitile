using System.Collections;
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

        private int _savedTargetFrameRate;
        private int _savedVSync;
        private SchedulerSettings _scheduler;
        private int _savedMeshBatch;
        private int _savedBuildSlots;

        [SetUp]
        public void SaveFrameSettings(){
            _savedTargetFrameRate = Application.targetFrameRate;
            _savedVSync = QualitySettings.vSyncCount;
            _scheduler = null;
        }

        [UnityTearDown]
        public IEnumerator TearDown(){
            Application.targetFrameRate = _savedTargetFrameRate;
            QualitySettings.vSyncCount = _savedVSync;
            yield return PipelineThroughputTests.WaitForBuiltWorld();
            if (_scheduler == null) yield break;

            // The settings asset is shared in memory: put the shipped values back and rebuild the world with them.
            _scheduler.meshingBatchSize = _savedMeshBatch;
            _scheduler.partitionBuildBatchSize = _savedBuildSlots;
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
            _scheduler = VoxelWorld.Instance.Settings.Scheduler;
            _savedMeshBatch = _scheduler.meshingBatchSize;
            _savedBuildSlots = _scheduler.partitionBuildBatchSize;

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
    }
}
