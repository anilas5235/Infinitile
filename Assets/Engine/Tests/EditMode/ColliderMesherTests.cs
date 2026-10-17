using System;
using System.Collections.Generic;
using Engine.Scripts.Data;
using Engine.Scripts.Jobs.ColliderMeshing;
using Engine.Scripts.VoxelConfig.Data.Internal;
using NUnit.Framework;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using static Engine.Scripts.Utils.VoxelConstants;

namespace Engine.Tests.EditMode{
    /// <summary>
    ///     TST-E18: greedy collider mesher (CMeshBuildJob) on hand-built chunks. The scheduler race tests (F-01)
    ///     live in the PlayMode assembly because the chunk prefab needs a live Awake.
    /// </summary>
    public class ColliderMesherTests{
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
    }
}
