using System;
using System.Collections;
using System.Collections.Generic;
using Engine.Scripts.Behaviour;
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
    ///     Eviction and scheduler behaviour that needs a running scene (chunk prefab instances, GPU buffers):
    ///     the UnloadChunk eviction event, bake results for a reclaimed partition, pool eviction order, and the
    ///     mesh handler idling once the vertical draw window is meshed.
    /// </summary>
    public class SchedulerEvictionTests{
        [UnitySetUp]
        public IEnumerator SetUp(){
            yield return PlayModeFixtures.LoadGameScene();
        }

        #region UnloadChunk eviction event

        [UnityTest]
        [Timeout(120000)]
        public IEnumerator UnloadChunk_RaisesEvictionAndReleasesRenderBuffer(){
            // ChunkManager.UnloadChunk must tell the world, otherwise the renderer keeps the chunk's buffer until the
            // pool happens to evict the same chunk.
            VoxelWorld world = VoxelWorld.Instance;
            VoxelWorldRenderer renderer = VoxelWorldRenderer.Instance;
            Assert.IsNotNull(renderer, "VoxelWorldRenderer missing in scene");
            Dictionary<int2, IntervalRange> buffers =
                PlayModeFixtures.GetPrivateField<Dictionary<int2, IntervalRange>>(renderer, "_voxelLists");

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

        #region Bake result for a reclaimed partition

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
            rig.Bake.Start(new HashSet<int3>{ pos });
            yield return null;
            rig.Bake.Complete();
            Assert.IsTrue(rig.Pool.IsPartitionActive(pos));
            Assert.IsTrue(rig.Pool.IsCollidable(pos));
        }

        [UnityTest]
        public IEnumerator ColliderBake_PartitionReclaimedDuringBake_IsNotMarkedCollidable(){
            // ColliderBakeScheduler.Complete must not mark a position collidable that the pool reclaimed while the
            // bake ran.
            using BakeRig rig = new();
            int3 pos = new(0, 3, 0);
            rig.Pool.GetOrClaimPartition(pos);
            rig.Bake.Start(new HashSet<int3>{ pos });
            yield return null;

            PlayModeFixtures.GetPrivateMethod(typeof(ChunkPool), "ReclaimPartition").Invoke(rig.Pool, new object[]{ pos });
            Assert.IsFalse(rig.Pool.IsPartitionActive(pos));

            rig.Bake.Complete();
            Assert.IsFalse(rig.Pool.IsCollidable(pos), "a reclaimed partition must not be marked collidable");
        }

        [UnityTest]
        public IEnumerator ColliderBake_PartitionReclaimedAndReclaimedDuringBake_IsNotMarkedCollidable(){
            // A re-claim at the same position hands back the same pooled ChunkPartition object, so an
            // object comparison alone lets the stale bake mark the freshly cleared partition collidable.
            using BakeRig rig = new();
            int3 pos = new(0, 3, 0);
            rig.Pool.GetOrClaimPartition(pos);
            rig.Bake.Start(new HashSet<int3>{ pos });
            yield return null;

            PlayModeFixtures.GetPrivateMethod(typeof(ChunkPool), "ReclaimPartition").Invoke(rig.Pool, new object[]{ pos });
            Assert.IsTrue(rig.Bake.IsComplete, "the bake must finish before the pool clears the mesh it reads");
            rig.Pool.GetOrClaimPartition(pos);
            Assert.IsTrue(rig.Pool.IsPartitionActive(pos));

            rig.Bake.Complete();
            Assert.IsFalse(rig.Pool.IsCollidable(pos), "reclaim -> claim -> bake complete must leave it non-collidable");
        }

        [UnityTest]
        public IEnumerator ColliderBake_PartitionReMeshedDuringBake_IsNotMarkedCollidable(){
            // New mesh data applied while the bake ran makes the bake result stale.
            using BakeRig rig = new();
            int3 pos = new(0, 3, 0);
            rig.Pool.GetOrClaimPartition(pos);
            rig.Bake.Start(new HashSet<int3>{ pos });
            yield return null;

            rig.Pool.NotifyPartitionMeshWriting();
            Assert.IsTrue(rig.Bake.IsComplete, "the bake must finish before the mesher rewrites the mesh it reads");
            rig.Pool.GetPartition(pos).MarkMeshChanged();

            rig.Bake.Complete();
            Assert.IsFalse(rig.Pool.IsCollidable(pos), "a re-meshed partition must be baked again");
        }

        [UnityTest]
        public IEnumerator ColliderBake_EmptyMesh_RemovesTheOldCollider(){
            // Digging out the last block of a partition leaves an empty mesh; the collider built from the previous
            // mesh must go with it.
            using BakeRig rig = new();
            int3 pos = new(0, 3, 0);
            ChunkPartition partition = rig.Pool.GetOrClaimPartition(pos);
            Mesh previous = new(){ vertices = new[]{ Vector3.zero, Vector3.right, Vector3.forward }, triangles = new[]{ 0, 1, 2 } };
            try{
                partition.Collider.sharedMesh = previous;
                rig.Bake.Start(new HashSet<int3>{ pos });
                yield return null;
                rig.Bake.Complete();

                Assert.IsTrue(rig.Pool.IsCollidable(pos));
                Assert.IsNull(partition.Collider.sharedMesh, "an empty mesh must clear the collider");
            }
            finally{
                Object.Destroy(previous);
            }
        }

        [Test]
        public void PoolPartitions_UseTheBakeCookingOptions(){
            // Unity reuses a Physics.BakeMesh result only for a collider with the same cooking options.
            using BakeRig rig = new();
            ChunkPartition partition = rig.Pool.GetOrClaimPartition(new int3(0, 3, 0));
            Assert.AreEqual(ColliderBuildJob.CookingOptions, partition.Collider.cookingOptions);
        }

        [Test]
        public void ReclaimChunk_WhileBakeRuns_CompletesTheBakeFirst(){
            // Reclaiming a chunk clears the collider meshes of all its partitions, which a bake may be reading.
            using BakeRig rig = new();
            int3 pos = new(0, 3, 0);
            rig.Pool.GetOrClaimPartition(pos);
            rig.Bake.Start(new HashSet<int3>{ pos });

            Assert.IsTrue(rig.Pool.TryReclaimChunk(pos.xz));
            Assert.IsTrue(rig.Bake.IsComplete, "the bake must finish before the pool clears the meshes it reads");
            rig.Bake.Complete();
            Assert.IsFalse(rig.Pool.IsCollidable(pos));
        }

        [UnityTest]
        [Timeout(120000)]
        public IEnumerator UnloadChunk_OfAPooledChunk_ReclaimsItsPartitions(){
            // A pooled chunk whose data the manager unloads would keep its partitions active, so after the reload
            // nothing meshes them again (the renderer already released their pages). VoxelWorld reclaims it.
            VoxelWorld world = VoxelWorld.Instance;
            ChunkPool pool = PlayModeFixtures.GetPrivateField<ChunkPool>(world, "_chunkPool");

            int3 target = default;
            bool found = false;
            float deadline = Time.realtimeSinceStartup + 60f;
            while (!found){
                int3 centre = world.Focus.Pos;
                for (int y = 0; y < PartitionsPerChunk && !found; y++){
                    int3 candidate = new(centre.x, y, centre.z);
                    if (!pool.IsPartitionActive(candidate)) continue;
                    target = candidate;
                    found = true;
                }

                if (Time.realtimeSinceStartup > deadline) Assert.Fail("no active partition at the focus chunk");
                yield return null;
            }

            int evictions = 0;
            Action<int2> handler = pos => {
                if (pos.Equals(target.xz)) evictions++;
            };
            world.ChunkEvicted += handler;
            try{
                world.ChunkManager.UnloadChunk(target.xz);
            }
            finally{
                world.ChunkEvicted -= handler;
            }

            for (int y = 0; y < PartitionsPerChunk; y++)
                Assert.IsFalse(pool.IsPartitionActive(new int3(target.x, y, target.z)), $"partition {y} must be reclaimed");
            Assert.AreEqual(1, evictions, "ChunkEvicted must be raised exactly once");
        }

        #endregion

        #region Pool eviction order

        [UnityTest]
        public IEnumerator PartitionPool_Full_EvictsTheFarthestPartitionNotTheOneBehindThePlayer(){
            // The pool queues use plain distance, not the view-weighted priority (x5 behind the player), which would
            // reclaim a partition 3 behind (weighted 45) before one 6 in front (weighted 36).
            VoxelEngineSettings settings = PlayModeFixtures.CreateSettings(0, VoxelWorld.Instance.Settings.Chunk.ChunkPrefab);
            GameObject root = new("SchedulerEvictionTests.PoolRig");
            ChunkPool pool = new(root.transform, settings); // partition pool = 1 chunk x PartitionsPerChunk = 8
            try{
                pool.FocusUpdate(new PriorityUtil.Focus(int3.zero, new float3(1, 0, 0)));
                int3 behind = new(-3, 0, 0);
                int3 front = new(6, 0, 0);
                pool.GetOrClaimPartition(behind);
                pool.GetOrClaimPartition(front);
                int3[] fillers = { new(1, 0, 0), new(2, 0, 0), new(1, 1, 0), new(2, 1, 0), new(1, 0, 1), new(2, 0, 1) };
                foreach (int3 filler in fillers) pool.GetOrClaimPartition(filler); // 8 claimed = pool size
                Assert.IsTrue(pool.IsPartitionActive(behind));
                Assert.IsTrue(pool.IsPartitionActive(front));
                yield return null;

                pool.GetOrClaimPartition(new int3(0, 1, 0)); // one over capacity: exactly one partition is reclaimed

                Assert.IsTrue(pool.IsPartitionActive(behind), "the closer partition behind the player must stay");
                Assert.IsFalse(pool.IsPartitionActive(front), "the farthest partition must be the one reclaimed");
            }
            finally{
                pool.Dispose();
                Object.Destroy(root);
                Object.Destroy(settings);
            }
        }

        #endregion

        #region Mesh handler idling

        [UnityTest]
        [Timeout(120000)]
        public IEnumerator MeshHandler_IdlesOnceVerticalDrawWindowIsMeshed(){
            // EnqueueStep queues every partition y of a chunk but JobUpdateStep drops the ones outside the vertical
            // draw window; with a focus near the top the handler must not re-queue y 0..4 forever and never sleep.
            VoxelEngineSettings settings = PlayModeFixtures.CreateSettings(2, VoxelWorld.Instance.Settings.Chunk.ChunkPrefab);
            settings.Chunk.LoadDistanceOverride = 6; // chunk store must hold the 7x7 air grid below
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
                // Meshing waits for lit 3 x 3 neighbourhoods; this rig has no light step, so light them here
                for (int x = -3; x <= 3; x++)
                for (int z = -3; z <= 3; z++)
                    manager.ApplyLight(new int2(x, z), new ChunkLightData(16), out _);

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
