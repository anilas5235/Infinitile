using System;
using System.Collections.Generic;
using System.Reflection;
using Engine.Scripts.Components;
using Engine.Scripts.Data;
using Engine.Scripts.Jobs.ColliderMeshing;
using Engine.Scripts.Settings;
using Engine.Scripts.VoxelConfig.Data.Internal;
using Engine.Scripts.VoxelConfig.Data.Voxel;
using Engine.Scripts.VoxelConfig.Registry;
using Engine.Scripts.World;
using NUnit.Framework;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using static Engine.Scripts.Utils.VoxelConstants;
using Object = UnityEngine.Object;

namespace Engine.Tests.EditMode{
    /// <summary>
    ///     TST-E18: greedy collider mesher (CMeshBuildJob) on hand-built chunks, plus the scheduler's behaviour
    ///     when a chunk is edited or unloaded while a build job is in flight (F-01).
    /// </summary>
    public class ColliderMesherTests{
        private const string ChunkPrefabPath = "Assets/Engine/Prefabs/Chunk.prefab";

        private NativeParallelHashMap<int2, ChunkVoxelData> _chunks;
        private NativeParallelHashMap<int2, ChunkLightData> _lights;
        private NativeArray<VertexAttributeDescriptor> _vertexParams;
        private VoxelEngineRenderGenData _renderGenData;
        private NativeList<int3> _jobs;
        private NativeParallelHashMap<int3, CMeshBuildJob.PartitionJobResult> _results;
        private Mesh.MeshDataArray _meshData;
        private bool _meshDataAllocated;

        [SetUp]
        public void SetUp(){
            _chunks = new NativeParallelHashMap<int2, ChunkVoxelData>(16, Allocator.Persistent);
            _lights = new NativeParallelHashMap<int2, ChunkLightData>(1, Allocator.Persistent);
            _vertexParams = new NativeArray<VertexAttributeDescriptor>(2, Allocator.Persistent){
                [0] = new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float16, 4),
                [1] = new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.Float16, 4)
            };
            _renderGenData = TestFixtures.CreateRenderGenData(Allocator.Persistent);
            _jobs = new NativeList<int3>(8, Allocator.Persistent);
            _results = new NativeParallelHashMap<int3, CMeshBuildJob.PartitionJobResult>(8, Allocator.Persistent);
        }

        [TearDown]
        public void TearDown(){
            if (_meshDataAllocated) _meshData.Dispose();
            _meshDataAllocated = false;
            _results.Dispose();
            _jobs.Dispose();
            _renderGenData.Dispose();
            _vertexParams.Dispose();
            foreach (KeyValue<int2, ChunkVoxelData> kv in _chunks) kv.Value.Dispose();
            _chunks.Dispose();
            _lights.Dispose();
        }

        #region helpers

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

        private void AddChunk(int2 position, Func<int3, ushort> voxelAt){
            if (_chunks.TryGetValue(position, out ChunkVoxelData old)){
                old.Dispose();
                _chunks.Remove(position);
            }

            _chunks.Add(position, BuildChunk(voxelAt));
        }

        private void AddAirNeighbours(int2 centre){
            for (int x = -1; x <= 1; x++)
            for (int z = -1; z <= 1; z++){
                int2 pos = centre + new int2(x, z);
                if (!_chunks.ContainsKey(pos)) _chunks.Add(pos, TestFixtures.CreateUniformChunk(TestFixtures.Air));
            }
        }

        private void RunJob(params int3[] partitions){
            _jobs.Clear();
            _results.Clear();
            foreach (int3 partition in partitions) _jobs.Add(partition);
            if (_meshDataAllocated) _meshData.Dispose();
            _meshData = Mesh.AllocateWritableMeshData(partitions.Length);
            _meshDataAllocated = true;

            CMeshBuildJob job = new(){
                ColliderVertexParams = _vertexParams,
                Accessor = new ChunkAccessor(_chunks.AsReadOnly(), _lights.AsReadOnly()),
                Jobs = _jobs,
                RenderGenData = _renderGenData,
                Results = _results.AsParallelWriter(),
                ColliderMeshDataArray = _meshData
            };
            job.Run(partitions.Length);
        }

        private (int vertices, int indices) Counts(int index = 0){
            Mesh.MeshData mesh = _meshData[index];
            int indices = mesh.indexFormat == IndexFormat.UInt16
                ? mesh.GetIndexData<ushort>().Length
                : mesh.GetIndexData<uint>().Length;
            return (mesh.vertexCount, indices);
        }

        private static Func<int3, ushort> Voxels(params int3[] solid){
            HashSet<int3> set = new(solid);
            return p => set.Contains(p) ? TestFixtures.Stone : TestFixtures.Air;
        }

        #endregion

        #region CMeshBuildJob

        [Test]
        public void SingleVoxel_ProducesSixQuads(){
            AddAirNeighbours(int2.zero);
            AddChunk(int2.zero, Voxels(new int3(5, 5, 5)));
            RunJob(new int3(0, 0, 0));
            Assert.AreEqual((24, 36), Counts());
            Assert.IsTrue(_results.ContainsKey(new int3(0, 0, 0)));
            Assert.AreEqual(0, _results[new int3(0, 0, 0)].Index);
        }

        [Test]
        public void TwoAdjacentVoxels_AreMergedIntoSixQuads(){
            AddAirNeighbours(int2.zero);
            AddChunk(int2.zero, Voxels(new int3(5, 5, 5), new int3(6, 5, 5)));
            RunJob(new int3(0, 0, 0));
            Assert.AreEqual((24, 36), Counts());
        }

        [Test]
        public void FullPartition_IsMergedIntoSixQuads(){
            AddAirNeighbours(int2.zero);
            AddChunk(int2.zero, p => p.y < PartitionHeight ? TestFixtures.Stone : TestFixtures.Air);
            RunJob(new int3(0, 0, 0));
            Assert.AreEqual((24, 36), Counts());
        }

        [Test]
        public void EmptyPartition_ProducesNoGeometry(){
            AddAirNeighbours(int2.zero);
            RunJob(new int3(0, 0, 0));
            Assert.AreEqual((0, 0), Counts());
        }

        [Test]
        public void SolidNeighbourInNextChunk_OmitsSharedFace(){
            AddAirNeighbours(int2.zero);
            AddChunk(int2.zero, Voxels(new int3(ChunkWidth - 1, 5, 5)));
            AddChunk(new int2(1, 0), Voxels(new int3(0, 5, 5)));
            RunJob(new int3(0, 0, 0));
            Assert.AreEqual((20, 30), Counts(), "the face shared with the neighbouring chunk must be culled");
        }

        [Test]
        public void UpperPartitionInNegativeChunk_MeshesLocalVoxel(){
            // F-14: partition-local voxel lookups must map to chunk-local coordinates for any partition y
            // and for negative chunk coordinates.
            int2 chunkPos = new(-1, -1);
            const int partitionY = 3;
            int3 local = new(5, 5, 5);
            AddAirNeighbours(chunkPos);
            AddChunk(chunkPos, Voxels(local + new int3(0, partitionY * PartitionHeight, 0)));
            RunJob(new int3(chunkPos.x, partitionY, chunkPos.y));
            Assert.AreEqual((24, 36), Counts());

            NativeArray<CVertex> vertices = _meshData[0].GetVertexData<CVertex>();
            foreach (CVertex vertex in vertices){
                float3 p = vertex.Position.xyz;
                Assert.That(p.x, Is.InRange(local.x, local.x + 1), $"x of {p}");
                Assert.That(p.y, Is.InRange(local.y, local.y + 1), $"y of {p}");
                Assert.That(p.z, Is.InRange(local.z, local.z + 1), $"z of {p}");
            }
        }

        [Category("KnownBug")] // F-15
        [Test]
        public void Checkerboard_DoesNotOverflow16BitIndices(){
            // F-15: a checkerboard partition has 16384 voxels * 24 vertices; UInt16 indices wrap above 65535.
            AddAirNeighbours(int2.zero);
            AddChunk(int2.zero, p => p.y < PartitionHeight && ((p.x + p.y + p.z) & 1) == 0
                ? TestFixtures.Stone
                : TestFixtures.Air);
            RunJob(new int3(0, 0, 0));
            (int vertices, int indices) = Counts();
            Assert.AreEqual(16384 * 24, vertices);
            Assert.AreEqual(16384 * 36, indices);
            Assert.IsTrue(vertices <= ushort.MaxValue + 1 || _meshData[0].indexFormat == IndexFormat.UInt32,
                "meshes with more than 65536 vertices need 32-bit indices");
        }

        #endregion

        #region CMeshBuildScheduler (F-01)

        private sealed class SchedulerRig : IDisposable{
            private readonly List<Object> _objects = new();
            private readonly RegistryTestFixtures _fixtures = new();
            private readonly VoxelRegistry _registry;
            private readonly NativeParallelHashMap<int2, ChunkVoxelData> _generated;

            public readonly ChunkManager Manager;
            public readonly ChunkPool Pool;
            public readonly CMeshBuildScheduler Scheduler;

            public SchedulerRig(){
                VoxelEngineSettings settings = TestFixtures.CreateSettings(2);
                settings.Chunk.ChunkPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(ChunkPrefabPath);
                Assert.IsNotNull(settings.Chunk.ChunkPrefab, ChunkPrefabPath);

                GameObject root = new("ColliderMesherTests.Pool");
                _objects.Add(root);
                GameObject worldObject = new("ColliderMesherTests.World");
                worldObject.SetActive(false); // Awake must not run; only the event hub is used
                _objects.Add(worldObject);
                VoxelWorld world = worldObject.AddComponent<VoxelWorld>();

                _registry = _fixtures.CreateStandardVoxelRegistry(out _, out Voxel[] voxels);
                Assert.AreEqual(TestFixtures.Stone, _registry.GetIdOrThrow(voxels[2].GetFullName()));

                Manager = new ChunkManager(settings);
                Pool = new ChunkPool(root.transform, settings);
                Scheduler = new CMeshBuildScheduler(settings, Manager, Pool, _registry, world);

                // Centre chunk: checkerboard in every partition so the build job runs long enough to race.
                _generated = new NativeParallelHashMap<int2, ChunkVoxelData>(9, Allocator.Persistent);
                for (int x = -1; x <= 1; x++)
                for (int z = -1; z <= 1; z++){
                    int2 pos = new(x, z);
                    _generated.Add(pos, pos.Equals(int2.zero)
                        ? BuildChunk(p => ((p.x + p.y + p.z) & 1) == 0 ? TestFixtures.Stone : TestFixtures.Air)
                        : TestFixtures.CreateUniformChunk(TestFixtures.Air));
                }

                Manager.AddChunks(_generated); // the manager owns the chunk data from here on
            }

            public void StartCentreChunkBuild(){
                HashSet<int3> jobs = new();
                for (int y = 0; y < PartitionsPerChunk; y++) jobs.Add(new int3(0, y, 0));
                Scheduler.Start(jobs);
            }

            public void Dispose(){
                Scheduler.Dispose(); // completes the handle
                // Complete() never ran, so the writable mesh data the scheduler allocated is still alive.
                FieldInfo field = typeof(CMeshBuildScheduler).GetField("_colliderMeshDataArray",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.IsNotNull(field);
                Mesh.MeshDataArray array = (Mesh.MeshDataArray)field.GetValue(Scheduler);
                if (array.Length > 0) array.Dispose();

                Manager.Dispose();
                Pool.Dispose();
                _registry.Dispose();
                _generated.Dispose();
                _fixtures.DestroyAll();
                foreach (Object obj in _objects)
                    if (obj) Object.DestroyImmediate(obj);
            }
        }

        [Category("KnownBug")] // F-01
        [Test]
        public void SetVoxel_WhileBuildJobRuns_CompletesJobFirst(){
            // F-01: editing chunk data that a scheduled build job is reading must wait for the job.
            using SchedulerRig rig = new();
            rig.StartCentreChunkBuild();
            Assert.IsFalse(rig.Scheduler.IsReady);

            rig.Manager.SetVoxel(TestFixtures.Grass, new int3(5, 40, 5));
            Assert.IsTrue(rig.Scheduler.IsComplete, "SetVoxel must not modify data while a job reads it");
        }

        [Category("KnownBug")] // F-01
        [Test]
        public void UnloadChunk_WhileBuildJobRuns_CompletesJobFirst(){
            // F-01: unloading a chunk that a scheduled build job is reading must wait for the job.
            using SchedulerRig rig = new();
            rig.StartCentreChunkBuild();
            Assert.IsFalse(rig.Scheduler.IsReady);

            rig.Manager.UnloadChunk(new int2(1, 0));
            Assert.IsTrue(rig.Scheduler.IsComplete, "UnloadChunk must not dispose data while a job reads it");
        }

        #endregion
    }
}
