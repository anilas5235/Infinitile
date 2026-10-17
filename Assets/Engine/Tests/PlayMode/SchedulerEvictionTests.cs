using System;
using System.Collections;
using System.Collections.Generic;
using Engine.Scripts.Components;
using Engine.Scripts.Data;
using Engine.Scripts.Jobs.ColliderBake;
using Engine.Scripts.Jobs.ColliderMeshing;
using Engine.Scripts.Jobs.Core;
using Engine.Scripts.Render;
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
    ///     TST-E20: eviction and scheduler behaviour that needs a running scene (chunk prefab instances,
    ///     GPU buffers): F-05 (UnloadChunk eviction event), F-04 (bake result for a reclaimed partition)
    ///     and F-10 (mesh handler idles once the vertical draw window is meshed).
    /// </summary>
    public class SchedulerEvictionTests{
        [UnitySetUp]
        public IEnumerator SetUp(){
            yield return PlayModeFixtures.LoadGameScene();
        }

        #region F-05

        [Category("KnownBug")] // F-05
        [UnityTest]
        [Timeout(120000)]
        public IEnumerator UnloadChunk_RaisesEvictionAndReleasesRenderBuffer(){
            // F-05: ChunkManager.UnloadChunk disposes the chunk but never tells the world, so the renderer keeps
            // the chunk's GraphicsBuffer until the pool happens to evict the same chunk.
            VoxelWorld world = VoxelWorld.Instance;
            VoxelWorldRenderer renderer = VoxelWorldRenderer.Instance;
            Assert.IsNotNull(renderer, "VoxelWorldRenderer missing in scene");
            Dictionary<int2, GraphicsBuffer> buffers =
                PlayModeFixtures.GetPrivateField<Dictionary<int2, GraphicsBuffer>>(renderer, "_voxelDataBuffers");

            int draw = world.Settings.Chunk.DrawDistance;
            int load = world.Settings.Chunk.LoadDistance;
            int2 target = default;
            bool found = false;
            float deadline = Time.realtimeSinceStartup + 60f;
            while (!found){
                // A chunk outside draw + 1 is never part of a mesh accessor, so unloading it races nothing.
                int2 centre = world.Focus.Pos.xz;
                for (int d = load; d >= draw + 2 && !found; d--)
                foreach (int2 offset in new[]{ new int2(d, 0), new int2(-d, 0), new int2(0, d), new int2(0, -d) }){
                    int2 candidate = centre + offset;
                    if (!world.ChunkManager.IsChunkLoaded(candidate) || !buffers.ContainsKey(candidate)) continue;
                    target = candidate;
                    found = true;
                    break;
                }

                if (Time.realtimeSinceStartup > deadline) Assert.Fail("no loaded chunk with a render buffer outside draw distance");
                yield return null;
            }

            int2? evicted = null;
            Action<int2> handler = pos => evicted = pos;
            world.ChunkEvicted += handler;
            try{
                world.ChunkManager.UnloadChunk(target);
            }
            finally{
                world.ChunkEvicted -= handler;
            }

            Assert.IsFalse(world.ChunkManager.IsChunkLoaded(target));
            Assert.AreEqual(target, evicted, "VoxelWorld.ChunkEvicted must be raised for an unloaded chunk");
            Assert.IsFalse(buffers.ContainsKey(target), "renderer must release the chunk's voxel buffer");
        }

        #endregion

        #region F-04

        private sealed class BakeRig : IDisposable{
            public readonly ChunkManager Manager;
            public readonly ChunkPool Pool;
            public readonly ColliderBakeScheduler Bake;
            private readonly GameObject _root;
            private readonly VoxelEngineSettings _settings;

            public BakeRig(){
                _settings = PlayModeFixtures.CreateSettings(2, VoxelWorld.Instance.Settings.Chunk.ChunkPrefab);
                _root = new GameObject("SchedulerEvictionTests.BakeRig");
                Manager = new ChunkManager(_settings);
                Pool = new ChunkPool(_root.transform, _settings);
                Bake = new ColliderBakeScheduler(Manager, Pool);

                NativeParallelHashMap<int2, ChunkVoxelData> chunks = new(1, Allocator.Temp);
                chunks.Add(int2.zero, PlayModeFixtures.CreateUniformChunk(PlayModeFixtures.Air));
                Manager.AddChunks(chunks); // the manager owns the chunk data from here on
                chunks.Dispose();
            }

            public void Dispose(){
                Bake.Dispose();
                Manager.Dispose();
                Pool.Dispose();
                Object.Destroy(_root);
                Object.Destroy(_settings);
            }
        }

        [UnityTest]
        public IEnumerator ColliderBake_MarksPartitionCollidable(){
            using BakeRig rig = new();
            int3 pos = new(0, 3, 0);
            rig.Pool.GetOrClaimPartition(pos);
            rig.Bake.Start(new List<int3>{ pos });
            yield return null;
            rig.Bake.Complete();
            Assert.IsTrue(rig.Pool.IsPartitionActive(pos));
            Assert.IsTrue(rig.Pool.IsCollidable(pos));
        }

        [UnityTest]
        public IEnumerator ColliderBake_PartitionReclaimedDuringBake_IsNotMarkedCollidable(){
            // F-04: ColliderBakeScheduler.Complete marks every started position collidable, even one the pool
            // reclaimed while the bake ran.
            using BakeRig rig = new();
            int3 pos = new(0, 3, 0);
            rig.Pool.GetOrClaimPartition(pos);
            rig.Bake.Start(new List<int3>{ pos });
            yield return null;

            PlayModeFixtures.GetPrivateMethod(typeof(ChunkPool), "ReclaimPartition").Invoke(rig.Pool, new object[]{ pos });
            Assert.IsFalse(rig.Pool.IsPartitionActive(pos));

            rig.Bake.Complete();
            Assert.IsFalse(rig.Pool.IsCollidable(pos), "a reclaimed partition must not be marked collidable");
        }

        #endregion

        #region F-10

        [Category("KnownBug")] // F-10
        [UnityTest]
        [Timeout(120000)]
        public IEnumerator MeshHandler_IdlesOnceVerticalDrawWindowIsMeshed(){
            // F-10: EnqueueStep queues every partition y of a chunk but JobUpdateStep drops the ones outside the
            // vertical draw window, so with a focus near the top the handler re-queues y 0..4 forever and never sleeps.
            VoxelEngineSettings settings = PlayModeFixtures.CreateSettings(2, VoxelWorld.Instance.Settings.Chunk.ChunkPrefab);
            settings.Chunk.LoadDistance = 6; // chunk store must hold the 7x7 air grid below
            GameObject root = new("SchedulerEvictionTests.MeshRig");
            GameObject worldObject = new("SchedulerEvictionTests.World");
            worldObject.SetActive(false); // only the event hub is used; Awake must not run
            VoxelWorld world = worldObject.AddComponent<VoxelWorld>();

            ChunkManager manager = new(settings);
            ChunkPool pool = new(root.transform, settings);
            CMeshBuildScheduler meshScheduler = new(settings, manager, pool, DataImporter.Instance.VoxelRegistry, world);
            ColliderBakeScheduler bakeScheduler = new(manager, pool);
            ColliderJobStateHandler colliderHandler = new(settings, manager, pool, bakeScheduler);
            MeshJobStateHandler meshHandler = new(settings, manager, pool, meshScheduler, colliderHandler);

            try{
                NativeParallelHashMap<int2, ChunkVoxelData> chunks = new(49, Allocator.Temp);
                for (int x = -3; x <= 3; x++)
                for (int z = -3; z <= 3; z++)
                    chunks.Add(new int2(x, z), PlayModeFixtures.CreateUniformChunk(PlayModeFixtures.Air));
                manager.AddChunks(chunks);
                chunks.Dispose();

                PriorityUtil.Focus focus = new(new int3(0, PartitionsPerChunk - 1, 0), new float3(0, 0, 1));
                pool.FocusUpdate(focus);
                meshHandler.FocusUpdate(focus);

                const int maxUpdates = 600;
                int updates = 0;
                while (!meshHandler.Sleeping && updates < maxUpdates){
                    meshHandler.Update(focus);
                    updates++;
                    yield return null;
                }

                Assert.IsTrue(pool.IsPartitionActive(new int3(0, PartitionsPerChunk - 1, 0)), "focus partition meshed");
                Assert.IsTrue(meshHandler.Sleeping,
                    $"mesh handler still busy after {updates} updates with nothing left to mesh in the draw window");
            }
            finally{
                if (!meshScheduler.IsReady) meshScheduler.Complete();
                meshScheduler.Dispose();
                bakeScheduler.Dispose();
                manager.Dispose();
                pool.Dispose();
                Object.Destroy(root);
                Object.Destroy(worldObject);
                Object.Destroy(settings);
            }
        }

        #endregion
    }
}
