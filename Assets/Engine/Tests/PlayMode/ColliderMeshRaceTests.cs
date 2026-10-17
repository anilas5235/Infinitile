using System;
using System.Collections;
using System.Collections.Generic;
using Engine.Scripts.Components;
using Engine.Scripts.Data;
using Engine.Scripts.Jobs.ColliderMeshing;
using Engine.Scripts.Settings;
using Engine.Scripts.VoxelConfig;
using Engine.Scripts.World;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;
using static Engine.Scripts.Utils.VoxelConstants;
using Object = UnityEngine.Object;

namespace Engine.Tests.PlayMode{
    /// <summary>
    ///     TST-E18 (F-01): the mesh build scheduler's behaviour when a chunk is edited or unloaded while a build
    ///     job is in flight. Needs PlayMode because completing a batch claims live chunk prefab instances.
    /// </summary>
    public class ColliderMeshRaceTests{
        [UnitySetUp]
        public IEnumerator SetUp(){
            yield return PlayModeFixtures.LoadGameScene();
        }

        /// <summary>Builds a chunk from a per-voxel function (chunk-local coordinates) as run-length intervals.</summary>
        private static ChunkVoxelData BuildChunk(Func<int3, ushort> voxelAt){
            ChunkVoxelData data = new(1024);
            ushort current = voxelAt(int3.zero);
            int run = 0;
            for (int x = 0; x < ChunkWidth; x++)
            for (int z = 0; z < ChunkDepth; z++)
            for (int y = 0; y < ChunkHeight; y++){
                ushort id = voxelAt(new int3(x, y, z));
                if (id == current){
                    run++;
                    continue;
                }

                data.AddVoxels(current, run);
                current = id;
                run = 1;
            }

            data.AddVoxels(current, run);
            return data;
        }

        private sealed class SchedulerRig : IDisposable{
            private readonly GameObject _root;
            private readonly GameObject _worldObject;
            private readonly VoxelEngineSettings _settings;

            public readonly ChunkManager Manager;
            public readonly ChunkPool Pool;
            public readonly CMeshBuildScheduler Scheduler;

            public SchedulerRig(){
                _settings = PlayModeFixtures.CreateSettings(2, VoxelWorld.Instance.Settings.Chunk.ChunkPrefab);
                _root = new GameObject("ColliderMeshRaceTests.Pool");
                _worldObject = new GameObject("ColliderMeshRaceTests.World");
                _worldObject.SetActive(false); // Awake must not run; only the event hub is used
                VoxelWorld world = _worldObject.AddComponent<VoxelWorld>();

                Manager = new ChunkManager(_settings);
                Pool = new ChunkPool(_root.transform, _settings);
                Scheduler = new CMeshBuildScheduler(_settings, Manager, Pool, DataImporter.Instance.VoxelRegistry, world);

                // Centre chunk: checkerboard in every partition so the build job runs long enough to race.
                NativeParallelHashMap<int2, ChunkVoxelData> generated = new(9, Allocator.Temp);
                for (int x = -1; x <= 1; x++)
                for (int z = -1; z <= 1; z++){
                    int2 pos = new(x, z);
                    generated.Add(pos, pos.Equals(int2.zero)
                        ? BuildChunk(p => ((p.x + p.y + p.z) & 1) == 0 ? PlayModeFixtures.Grass : PlayModeFixtures.Air)
                        : PlayModeFixtures.CreateUniformChunk(PlayModeFixtures.Air));
                }

                Manager.AddChunks(generated); // the manager owns the chunk data from here on
                generated.Dispose();
            }

            public void StartCentreChunkBuild(){
                HashSet<int3> jobs = new();
                for (int y = 0; y < PartitionsPerChunk; y++) jobs.Add(new int3(0, y, 0));
                Scheduler.Start(jobs);
            }

            public void Dispose(){
                if (!Scheduler.IsReady) Scheduler.Complete();
                Scheduler.Dispose();
                Manager.Dispose();
                Pool.Dispose();
                Object.Destroy(_root);
                Object.Destroy(_worldObject);
                Object.Destroy(_settings);
            }
        }

        [Test]
        public void SetVoxel_WhileBuildJobRuns_CompletesJobFirst(){
            // F-01: editing chunk data that a scheduled build job is reading must wait for the job.
            using SchedulerRig rig = new();
            rig.StartCentreChunkBuild();
            Assert.IsFalse(rig.Scheduler.IsReady);

            rig.Manager.SetVoxel(PlayModeFixtures.Grass, new int3(5, 40, 5));
            Assert.IsTrue(rig.Scheduler.IsComplete, "SetVoxel must not modify data while a job reads it");
            Assert.IsTrue(rig.Scheduler.IsReady, "the completed batch must be applied so the next one can start");
            Assert.IsTrue(rig.Pool.IsPartitionActive(new int3(0, 2, 0)), "batch results applied");
        }

        [Test]
        public void UnloadChunk_WhileBuildJobRuns_CompletesJobFirst(){
            // F-01: unloading a chunk that a scheduled build job is reading must wait for the job.
            using SchedulerRig rig = new();
            rig.StartCentreChunkBuild();
            Assert.IsFalse(rig.Scheduler.IsReady);

            rig.Manager.UnloadChunk(new int2(1, 0));
            Assert.IsTrue(rig.Scheduler.IsComplete, "UnloadChunk must not dispose data while a job reads it");
            Assert.IsTrue(rig.Scheduler.IsReady);
            Assert.IsFalse(rig.Manager.IsChunkLoaded(new int2(1, 0)));
        }

        [Test]
        public void UnloadChunk_OutsideAccessor_DoesNotCompleteJob(){
            // A chunk the job does not read may be unloaded without waiting (no stall for unrelated evictions).
            using SchedulerRig rig = new();
            NativeParallelHashMap<int2, ChunkVoxelData> extra = new(1, Allocator.Temp);
            extra.Add(new int2(5, 5), PlayModeFixtures.CreateUniformChunk(PlayModeFixtures.Air));
            rig.Manager.AddChunks(extra);
            extra.Dispose();

            rig.StartCentreChunkBuild();
            rig.Manager.UnloadChunk(new int2(5, 5));
            Assert.IsFalse(rig.Scheduler.IsReady, "an unrelated unload must not force the batch to complete");
        }
    }
}
