using System;
using System.Collections;
using System.Collections.Generic;
using Engine.Scripts.Behaviour;
using Engine.Scripts.Data;
using Engine.Scripts.Jobs.ColliderBake;
using Engine.Scripts.Jobs.ColliderMeshing;
using Engine.Scripts.Jobs.Core;
using Engine.Scripts.Settings;
using Engine.Scripts.Utils;
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
    ///     Partitions that need no mesh. One without a voxel (air only) gets no mesh job and no GPU build: the mesh
    ///     stage makes it active at once, with an empty mesh, and it counts as collidable. One whose collider mesh
    ///     comes out empty (no open solid face) keeps its mesh job and GPU build but needs no bake. Both are meshed
    ///     as soon as an edit gives them something to show.
    /// </summary>
    public class EmptyPartitionTests{
        private const int Draw = 2;
        private const int Grid = Draw + 1; // the draw window and the neighbours its border columns need
        private const int MaxUpdates = 600;

        [UnitySetUp]
        public IEnumerator SetUp(){
            yield return PlayModeFixtures.LoadGameScene();
        }

        /// <summary>A chunk with grass up to <paramref name="height" /> in every column and air above.</summary>
        private static ChunkVoxelData GroundChunk(int height){
            ChunkVoxelData data = new(2 * ChunkWidth * ChunkDepth);
            for (int column = 0; column < ChunkWidth * ChunkDepth; column++){
                data.AddVoxels(PlayModeFixtures.Grass, height);
                data.AddVoxels(PlayModeFixtures.Air, ChunkHeight - height);
            }

            return data;
        }

        /// <summary>
        ///     The mesh and the bake stage over a grid of lit chunks, without a renderer: the GPU build requests are
        ///     recorded instead.
        /// </summary>
        private sealed class MeshRig : IDisposable{
            public readonly ChunkManager Manager;
            public readonly ChunkPool Pool;
            public readonly CMeshBuildScheduler MeshJobs;
            public readonly MeshJobStateHandler MeshHandler;
            public readonly ColliderJobStateHandler BakeHandler;

            /// <summary>Every partition a GPU build was requested for.</summary>
            public readonly HashSet<int3> Requested = new();

            private readonly ColliderBakeScheduler _bakeJobs;
            private readonly GameObject _root;
            private readonly GameObject _worldObject;
            private readonly VoxelWorld _world;
            private readonly VoxelEngineSettings _settings;

            public MeshRig(int groundHeight){
                _settings = PlayModeFixtures.CreateSettings(Draw, VoxelWorld.Instance.Settings.Chunk.ChunkPrefab);
                _settings.Chunk.LoadDistanceOverride = 2 * Grid; // the chunk store must hold the whole grid
                _root = new GameObject("EmptyPartitionTests.Pool");
                _worldObject = new GameObject("EmptyPartitionTests.World");
                _worldObject.SetActive(false); // only the event hub is used; Awake must not run
                _world = _worldObject.AddComponent<VoxelWorld>();
                _world.PartitionBuildRequested += OnBuildRequested;

                Manager = new ChunkManager(_settings);
                Pool = new ChunkPool(_root.transform, _settings);
                MeshJobs = new CMeshBuildScheduler(_settings, Manager, Pool, DataImporter.Instance.VoxelRegistry, _world);
                _bakeJobs = new ColliderBakeScheduler(Manager, Pool);
                BakeHandler = new ColliderJobStateHandler(_settings, Manager, Pool, _bakeJobs);
                MeshHandler = new MeshJobStateHandler(_settings, Manager, Pool, MeshJobs, BakeHandler);
                Manager.OnRemeshRequested += MeshHandler.WakeUp; // an edit wakes the mesh stage, as in the engine

                int side = 2 * Grid + 1;
                NativeParallelHashMap<int2, ChunkVoxelData> chunks = new(side * side, Allocator.Temp);
                for (int x = -Grid; x <= Grid; x++)
                for (int z = -Grid; z <= Grid; z++)
                    chunks.Add(new int2(x, z), GroundChunk(groundHeight));
                Manager.AddChunks(chunks); // the manager owns the chunk data from here on
                chunks.Dispose();
                // Meshing waits for lit 3 x 3 neighbourhoods; this rig has no light step, so light them here
                for (int x = -Grid; x <= Grid; x++)
                for (int z = -Grid; z <= Grid; z++)
                    Manager.ApplyLight(new int2(x, z), new ChunkLightData(16), out _);
            }

            private void OnBuildRequested(HashSet<int3> partitions){
                Requested.UnionWith(partitions);
            }

            /// <summary>A focus in the centre column at partition <paramref name="y" />.</summary>
            public PriorityUtil.Focus FocusAt(int y){
                PriorityUtil.Focus focus = new(new int3(0, y, 0), new float3(0, 0, 1));
                Pool.FocusUpdate(focus);
                MeshHandler.FocusUpdate(focus);
                BakeHandler.FocusUpdate(focus);
                return focus;
            }

            /// <param name="focus">The focus of the turns.</param>
            /// <param name="budget">The main-thread budget of every frame; null: no limit.</param>
            public IEnumerator MeshUntilAsleep(PriorityUtil.Focus focus, FrameBudget budget = null){
                for (int i = 0; i < MaxUpdates && !MeshHandler.Sleeping; i++){
                    budget?.BeginFrame();
                    MeshHandler.Update(focus, budget);
                    yield return null;
                }

                Assert.IsTrue(MeshHandler.Sleeping, "the mesh stage is still busy");
            }

            public IEnumerator BakeUntilAsleep(PriorityUtil.Focus focus){
                for (int i = 0; i < MaxUpdates && !BakeHandler.Sleeping; i++){
                    BakeHandler.Update(focus);
                    yield return null;
                }

                Assert.IsTrue(BakeHandler.Sleeping, "the bake stage is still busy");
            }

            public void Dispose(){
                _world.PartitionBuildRequested -= OnBuildRequested;
                if (!MeshJobs.IsReady) MeshJobs.Complete();
                MeshJobs.Dispose();
                _bakeJobs.Dispose();
                Manager.Dispose();
                Pool.Dispose();
                Object.Destroy(_root);
                Object.Destroy(_worldObject);
                Object.Destroy(_settings);
            }
        }

        [UnityTest]
        [Timeout(120000)]
        public IEnumerator PartitionsWithoutVoxels_BecomeActive_WithoutMeshJobOrGpuBuild(){
            // Ground in the lowest partition only; the vertical draw window of the focus holds partitions 0 to 3.
            using MeshRig rig = new(PartitionHeight);
            PriorityUtil.Focus focus = rig.FocusAt(1);
            yield return rig.MeshUntilAsleep(focus);

            int columns = (2 * Draw + 1) * (2 * Draw + 1);
            for (int x = -Draw; x <= Draw; x++)
            for (int z = -Draw; z <= Draw; z++)
            for (int y = 0; y <= 3; y++){
                int3 pos = new(x, y, z);
                Assert.IsTrue(rig.Pool.IsPartitionActive(pos), $"{pos} is active");
                Assert.AreEqual(y == 0, rig.Requested.Contains(pos), $"{pos}: GPU build requested");
                if (y == 0) continue;

                ChunkPartition partition = rig.Pool.GetPartition(pos);
                Assert.IsTrue(rig.Pool.IsCollidable(pos), $"{pos} counts as collidable without a bake");
                Assert.AreEqual(0, partition.ColliderMesh.vertexCount, $"{pos}: collider mesh");
                Assert.IsNull(partition.Collider.sharedMesh, $"{pos}: collider");
            }

            Assert.AreEqual(columns, rig.MeshJobs.MeshedPartitions, "partitions that went through the mesh job");
            Assert.AreEqual(3 * columns, rig.MeshJobs.PartitionsWithoutMesh, "partitions that became active without one");

            // The ground has a collider mesh and takes the usual way through the bake stage
            Assert.IsFalse(rig.Pool.IsCollidable(int3.zero), "the ground waits for its bake");
            yield return rig.BakeUntilAsleep(focus);
            Assert.IsTrue(rig.Pool.IsCollidable(int3.zero));
            Assert.IsNotNull(rig.Pool.GetPartition(int3.zero).Collider.sharedMesh, "collider of the ground");
        }

        /// <summary>
        ///     A budget of <paramref name="items" /> items of work per frame: its clock counts the items it is asked
        ///     for instead of the time, so a test with it does not depend on the machine.
        /// </summary>
        private static FrameBudget ItemsPerFrame(int items){
            long asked = 0;
            return new FrameBudget(items, () => asked++, 1d);
        }

        [UnityTest]
        [Timeout(120000)]
        public IEnumerator AMeshStageTheBudgetCuts_ReachesTheSameState(){
            // Two items of work per frame: a row of the scan, a partition without a voxel, a claim in the pool
            // and the start of a batch each wait for their frame. The world ends as without a limit (see
            // PartitionsWithoutVoxels_BecomeActive_WithoutMeshJobOrGpuBuild).
            using MeshRig rig = new(PartitionHeight);
            PriorityUtil.Focus focus = rig.FocusAt(1);
            FrameBudget budget = ItemsPerFrame(2);
            yield return rig.MeshUntilAsleep(focus, budget);
            Assert.Greater(budget.FramesCut, 0, "frames in which the budget stopped the turn");

            int columns = (2 * Draw + 1) * (2 * Draw + 1);
            for (int x = -Draw; x <= Draw; x++)
            for (int z = -Draw; z <= Draw; z++)
            for (int y = 0; y <= 3; y++){
                int3 pos = new(x, y, z);
                Assert.IsTrue(rig.Pool.IsPartitionActive(pos), $"{pos} is active");
                Assert.AreEqual(y == 0, rig.Requested.Contains(pos), $"{pos}: GPU build requested");
                if (y == 0) Assert.Greater(rig.Pool.GetPartition(pos).ColliderMesh.vertexCount, 0, $"{pos}: collider mesh");
            }

            Assert.AreEqual(columns, rig.MeshJobs.MeshedPartitions, "partitions that went through the mesh job");
            Assert.AreEqual(3 * columns, rig.MeshJobs.PartitionsWithoutMesh, "partitions that became active without one");

            yield return rig.BakeUntilAsleep(focus);
            Assert.IsTrue(rig.Pool.IsCollidable(int3.zero));
        }

        [UnityTest]
        [Timeout(120000)]
        public IEnumerator PartitionsOfACutDispatch_ThatLeftTheDrawWindow_AreNotMadeActive(){
            // One item of work per frame: the dispatch takes its partitions from the queue in one frame and makes
            // one partition without a voxel active in each of the next. When the focus moves on in between, the
            // ones that are waiting are outside the draw window and must not claim a place in the pool.
            using MeshRig rig = new(PartitionHeight);
            PriorityUtil.Focus focus = rig.FocusAt(1); // the vertical draw window holds the partitions 0 to 3
            FrameBudget budget = ItemsPerFrame(1);
            for (int i = 0; i < MaxUpdates && rig.MeshJobs.PartitionsWithoutMesh == 0; i++){
                budget.BeginFrame();
                rig.MeshHandler.Update(focus, budget);
                yield return null;
            }

            Assert.AreEqual(1, rig.MeshJobs.PartitionsWithoutMesh, "the first partition without a voxel");
            List<int3> waiting = PlayModeFixtures.GetPrivateField<List<int3>>(rig.MeshHandler, "_emptyPartitions");
            Assert.Greater(waiting.Count, 1, "the cut dispatch holds more partitions without a voxel");
            int3[] left = waiting.GetRange(1, waiting.Count - 1).ToArray();

            focus = rig.FocusAt(6); // the window holds the partitions 4 to 7 now, all of them air
            yield return rig.MeshUntilAsleep(focus, budget);

            foreach (int3 pos in left) Assert.IsFalse(rig.Pool.IsPartitionActive(pos), $"{pos} left the draw window");
            int columns = (2 * Draw + 1) * (2 * Draw + 1);
            Assert.AreEqual(1 + 4 * columns, rig.MeshJobs.PartitionsWithoutMesh,
                "the one before the focus moved, and the four layers of the new window");
        }

        [UnityTest]
        [Timeout(120000)]
        public IEnumerator SetVoxel_IntoAPartitionThatACutDispatchHolds_MeshesIt(){
            // One item of work per frame: the dispatch takes its partitions from the queue in one frame and makes
            // one partition without a voxel active in each of the next. A block placed in one that is still waiting
            // gives it something to show: it must not be made active with an empty mesh, but meshed like any
            // partition the edit flagged, with its collider.
            using MeshRig rig = new(PartitionHeight);
            PriorityUtil.Focus focus = rig.FocusAt(1);
            FrameBudget budget = ItemsPerFrame(1);
            for (int i = 0; i < MaxUpdates && rig.MeshJobs.PartitionsWithoutMesh == 0; i++){
                budget.BeginFrame();
                rig.MeshHandler.Update(focus, budget);
                yield return null;
            }

            List<int3> waiting = PlayModeFixtures.GetPrivateField<List<int3>>(rig.MeshHandler, "_emptyPartitions");
            Assert.Greater(waiting.Count, 1, "the cut dispatch holds more partitions without a voxel");
            int3 partition = waiting[waiting.Count - 1];
            Assert.IsFalse(rig.Pool.IsPartitionActive(partition), "the partition still waits");
            // Inside the partition, at none of its borders
            int3 block = new(partition.x * ChunkWidth + 5, partition.y * PartitionHeight + 6, partition.z * ChunkDepth + 5);
            Assert.IsTrue(rig.Manager.SetVoxel(PlayModeFixtures.Grass, block));

            yield return rig.MeshUntilAsleep(focus, budget);
            Assert.IsTrue(rig.Requested.Contains(partition), "GPU build of the partition that holds a block now");
            Assert.Greater(rig.Pool.GetPartition(partition).ColliderMesh.vertexCount, 0, "collider mesh of the block");

            yield return rig.BakeUntilAsleep(focus);
            Assert.IsTrue(rig.Pool.IsCollidable(partition));
            Assert.IsNotNull(rig.Pool.GetPartition(partition).Collider.sharedMesh, "collider of the block");
        }

        [UnityTest]
        [Timeout(120000)]
        public IEnumerator SetVoxel_IntoAPartitionWithoutVoxels_MeshesIt(){
            using MeshRig rig = new(PartitionHeight);
            PriorityUtil.Focus focus = rig.FocusAt(1);
            yield return rig.MeshUntilAsleep(focus);
            yield return rig.BakeUntilAsleep(focus);

            int3 partition = new(0, 2, 0);
            int3 block = new(5, 2 * PartitionHeight + 6, 5); // inside the partition, at none of its borders
            Assert.IsFalse(rig.Requested.Contains(partition));
            rig.Requested.Clear();

            Assert.IsTrue(rig.Manager.SetVoxel(PlayModeFixtures.Grass, block));
            Assert.IsFalse(rig.MeshHandler.Sleeping, "the edit wakes the mesh stage");
            yield return rig.MeshUntilAsleep(focus);

            Assert.IsTrue(rig.Requested.Contains(partition), "GPU build of the partition that holds a block now");
            Assert.AreEqual(1, rig.Requested.Count, "the partitions around it still hold no voxel");
            Assert.Greater(rig.Pool.GetPartition(partition).ColliderMesh.vertexCount, 0, "collider mesh of the block");

            yield return rig.BakeUntilAsleep(focus);
            Assert.IsTrue(rig.Pool.IsCollidable(partition));
            Assert.IsNotNull(rig.Pool.GetPartition(partition).Collider.sharedMesh, "collider of the block");
        }

        [UnityTest]
        [Timeout(120000)]
        public IEnumerator RemovingTheLastBlock_RemovesTheCollider_WithoutABake(){
            using MeshRig rig = new(PartitionHeight);
            PriorityUtil.Focus focus = rig.FocusAt(1);
            int3 partition = new(0, 2, 0);
            int3 block = new(5, 2 * PartitionHeight + 6, 5);
            Assert.IsTrue(rig.Manager.SetVoxel(PlayModeFixtures.Grass, block));
            yield return rig.MeshUntilAsleep(focus);
            yield return rig.BakeUntilAsleep(focus);
            Assert.IsNotNull(rig.Pool.GetPartition(partition).Collider.sharedMesh, "collider of the block");
            int withoutBake = rig.MeshJobs.PartitionsWithoutBake;
            rig.Requested.Clear();

            Assert.IsTrue(rig.Manager.SetVoxel(PlayModeFixtures.Air, block));
            yield return rig.MeshUntilAsleep(focus);

            // The partition held a voxel once, so it is meshed like any other; its collider mesh comes out empty
            Assert.IsTrue(rig.Requested.Contains(partition), "the GPU build removes the block's geometry");
            Assert.AreEqual(withoutBake + 1, rig.MeshJobs.PartitionsWithoutBake);
            Assert.IsNull(rig.Pool.GetPartition(partition).Collider.sharedMesh, "the collider went with the block");
            Assert.IsTrue(rig.Pool.IsCollidable(partition));
            Assert.IsFalse(rig.Manager.ShouldReCollide(partition), "nothing is left for the bake stage");
        }

        [UnityTest]
        [Timeout(120000)]
        public IEnumerator EditsAndLightNextToAPartitionWithoutVoxels_DoNotMeshIt(){
            using MeshRig rig = new(PartitionHeight);
            PriorityUtil.Focus focus = rig.FocusAt(1);
            yield return rig.MeshUntilAsleep(focus);
            rig.Requested.Clear();
            int3 above = new(0, 1, 0);

            // The top block of the ground borders the partition of air above it
            Assert.IsTrue(rig.Manager.SetVoxel(PlayModeFixtures.Air, new int3(5, PartitionHeight - 1, 5)));
            rig.Manager.FlagReMesh(above); // what a light change around it asks for
            Assert.IsFalse(rig.Manager.ShouldReMesh(above));
            yield return rig.MeshUntilAsleep(focus);

            Assert.IsTrue(rig.Requested.Contains(int3.zero), "the edited partition is meshed again");
            Assert.AreEqual(1, rig.Requested.Count, "and no other one");
            Assert.IsTrue(rig.Pool.IsPartitionActive(above));
            Assert.IsTrue(rig.Pool.IsCollidable(above));
        }

        [UnityTest]
        [Timeout(120000)]
        public IEnumerator EnclosedPartitions_AreCollidable_WithoutABake(){
            // Solid chunks: a partition inside them has voxels, so it gets its mesh job and its GPU build, but none
            // of its faces is open. The mesh stage sees the empty collider mesh and spares the bake stage the turn.
            using MeshRig rig = new(ChunkHeight);
            PriorityUtil.Focus focus = rig.FocusAt(3); // the vertical draw window holds partitions 1 to 5
            yield return rig.MeshUntilAsleep(focus);

            int3 inside = new(0, 3, 0);
            Assert.IsTrue(rig.Requested.Contains(inside), "a partition with voxels gets its GPU build");
            Assert.AreEqual(0, rig.Pool.GetPartition(inside).ColliderMesh.vertexCount, "no open face");
            Assert.IsTrue(rig.Pool.IsCollidable(inside), "collidable before any turn of the bake stage");
            Assert.AreEqual(0, rig.MeshJobs.PartitionsWithoutMesh, "every partition holds voxels");
            Assert.Greater(rig.MeshJobs.MeshedPartitions, 0);
            Assert.AreEqual(rig.MeshJobs.MeshedPartitions, rig.MeshJobs.PartitionsWithoutBake);

            rig.BakeHandler.Update(focus);
            Assert.IsTrue(rig.BakeHandler.Sleeping, "the bake stage finds nothing to do");
        }
    }
}
