using Engine.Scripts.Settings;
using NUnit.Framework;
using UnityEditor;

namespace Engine.Tests.EditMode{
    /// <summary>
    ///     The mesh batch size and the GPU build slots follow the draw distance: the settings give both for a
    ///     reference draw distance, and the engine scales them with the side of the draw window (2 x distance + 1),
    ///     the row of chunks a flight across a chunk border exposes.
    /// </summary>
    public class SchedulerSettingsTests{
        private const string SettingsAssetPath = "Assets/EngineSettings/VoxelEngineSettings.asset";

        private static SchedulerSettings Shipped(){
            VoxelEngineSettings asset = AssetDatabase.LoadAssetAtPath<VoxelEngineSettings>(SettingsAssetPath);
            Assert.IsNotNull(asset, SettingsAssetPath);
            return asset.Scheduler;
        }

        // 8 and 4 were written for draw distance 6; 16 and 8 are what draw distance 12 needs (measured 2026-11-14)
        [TestCase(6, 8, 4)]
        [TestCase(12, 16, 8)]
        public void ShippedSettings_GiveTheMeasuredSizes(int drawDistance, int meshBatch, int buildSlots){
            SchedulerSettings shipped = Shipped();
            Assert.AreEqual(meshBatch, shipped.MeshBatchSizeAt(drawDistance), "mesh batch");
            Assert.AreEqual(buildSlots, shipped.BuildSlotsAt(drawDistance), "GPU build slots");
        }

        [TestCase(6, 8, 4)]
        [TestCase(12, 16, 8)]
        public void ClassDefaults_GiveTheSameSizesAsTheShippedSettings(int drawDistance, int meshBatch, int buildSlots){
            SchedulerSettings defaults = new();
            Assert.AreEqual(meshBatch, defaults.MeshBatchSizeAt(drawDistance), "mesh batch");
            Assert.AreEqual(buildSlots, defaults.BuildSlotsAt(drawDistance), "GPU build slots");
        }

        [Test]
        public void ShippedSettings_BoundTheMainThreadWorkPerFrame(){
            // The owner's pick from the comparison of 2, 3 and 4 ms (2026-11-15); the class default is the same
            // value, so a world built from fresh settings behaves like the shipped one
            Assert.AreEqual(2f, Shipped().mainThreadBudgetMs, "main-thread budget of the shipped settings in ms");
            Assert.AreEqual(Shipped().mainThreadBudgetMs, new SchedulerSettings().mainThreadBudgetMs, "class default");
        }

        [Test]
        public void ShippedSettings_GiveHalfTheSpareTimeOfACappedFrame(){
            // The owner's pick of 2026-11-15 from shares 0, 0.5 and 1: half the spare time keeps about 4 ms of a
            // 60 frames frame free; the class default is the same value
            Assert.AreEqual(0.5f, Shipped().cappedFrameSpareShare, "spare share of the shipped settings");
            Assert.AreEqual(Shipped().cappedFrameSpareShare, new SchedulerSettings().cappedFrameSpareShare,
                "class default");
        }

        [TestCase(2, 3, 2)]
        [TestCase(4, 6, 3)]
        [TestCase(8, 11, 5)]
        [TestCase(16, 21, 11)]
        [TestCase(24, 31, 16)]
        public void Sizes_GrowWithTheSideOfTheDrawWindow(int drawDistance, int meshBatch, int buildSlots){
            // 16 and 8 at draw distance 12 (side 25), times the side of the window, rounded
            SchedulerSettings settings = new(){ meshingBatchSize = 16, partitionBuildBatchSize = 8, referenceDrawDistance = 12 };
            Assert.AreEqual(meshBatch, settings.MeshBatchSizeAt(drawDistance), "mesh batch");
            Assert.AreEqual(buildSlots, settings.BuildSlotsAt(drawDistance), "GPU build slots");
        }

        [Test]
        public void Sizes_StopAtTheirCaps(){
            // Every build slot holds its own GPU buffers, so a huge draw distance does not buy more of them
            SchedulerSettings settings = new(){ meshingBatchSize = 16, partitionBuildBatchSize = 8, referenceDrawDistance = 12 };
            Assert.AreEqual(SchedulerSettings.MaxScaledMeshBatchSize, settings.MeshBatchSizeAt(40));
            Assert.AreEqual(SchedulerSettings.MaxScaledBuildSlots, settings.BuildSlotsAt(40));
        }

        [Test]
        public void AValueAboveTheCap_IsScaledDownButNotCut(){
            SchedulerSettings settings = new(){ meshingBatchSize = 48, partitionBuildBatchSize = 24, referenceDrawDistance = 12 };
            Assert.AreEqual(48, settings.MeshBatchSizeAt(12), "the value as given, at its reference");
            Assert.AreEqual(24, settings.BuildSlotsAt(12));
            Assert.AreEqual(48, settings.MeshBatchSizeAt(20), "not scaled up past the value");
            Assert.AreEqual(24, settings.BuildSlotsAt(20));
            Assert.AreEqual(25, settings.MeshBatchSizeAt(6), "48 x 13 / 25");
            Assert.AreEqual(12, settings.BuildSlotsAt(6), "24 x 13 / 25");
        }

        [Test]
        public void Sizes_AreAtLeastOne(){
            SchedulerSettings settings = new(){ meshingBatchSize = 16, partitionBuildBatchSize = 8, referenceDrawDistance = 12 };
            Assert.AreEqual(1, settings.MeshBatchSizeAt(0));
            Assert.AreEqual(1, settings.BuildSlotsAt(0));

            settings.meshingBatchSize = 0;
            settings.partitionBuildBatchSize = -3;
            Assert.AreEqual(1, settings.MeshBatchSizeAt(12));
            Assert.AreEqual(1, settings.BuildSlotsAt(6));
        }

        [TestCase(2)]
        [TestCase(12)]
        [TestCase(30)]
        public void WithoutAReferenceDrawDistance_NothingIsScaled(int drawDistance){
            SchedulerSettings settings = new(){ meshingBatchSize = 16, partitionBuildBatchSize = 8, referenceDrawDistance = 0 };
            Assert.AreEqual(16, settings.MeshBatchSizeAt(drawDistance));
            Assert.AreEqual(8, settings.BuildSlotsAt(drawDistance));
        }
    }
}
