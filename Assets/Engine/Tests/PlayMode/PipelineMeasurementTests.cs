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
        private SchedulerSettings _scheduler;
        private int _savedMeshBatch;
        private int _savedBuildSlots;
        private int _savedReference;

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
            _scheduler.referenceDrawDistance = _savedReference;
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
            _savedReference = _scheduler.referenceDrawDistance;
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
        [Order(5)]
        [Timeout(1200000)]
        public IEnumerator FrameTimeGoal_OtherSizes_AreMeasured(){
            // The goal run with other mesh batches and GPU build slots than the derived ones: which are the
            // smallest that keep up with a sprint flight and have the first build done 20 s after the start?
            yield return PlayModeFixtures.LoadGameScene(drawDistance: PlayModeFixtures.ShippedDrawDistance);
            _scheduler = VoxelWorld.Instance.Settings.Scheduler;
            _savedMeshBatch = _scheduler.meshingBatchSize;
            _savedBuildSlots = _scheduler.partitionBuildBatchSize;
            _savedReference = _scheduler.referenceDrawDistance;
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
