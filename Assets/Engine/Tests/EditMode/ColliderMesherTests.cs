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
    ///     Greedy collider mesher (CMeshBuildJob) on hand-built chunks. The scheduler race tests live in the
    ///     PlayMode assembly because the chunk prefab needs a live Awake.
    /// </summary>
    public class ColliderMesherTests{
        private NativeParallelHashMap<int2, ChunkVoxelData> _chunks;
        private NativeArray<VertexAttributeDescriptor> _vertexParams;
        private VoxelEngineRenderGenData _renderGenData;
        private NativeList<int3> _jobs;
        private NativeParallelHashMap<int3, CMeshBuildJob.PartitionJobResult> _results;
        private Mesh.MeshDataArray _meshData;
        private bool _meshDataAllocated;

        [SetUp]
        public void SetUp(){
            _chunks = new NativeParallelHashMap<int2, ChunkVoxelData>(16, Allocator.Persistent);
            _vertexParams = CVertex.CreateLayout(Allocator.Persistent);
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
            if (_results.Capacity < partitions.Length) _results.Capacity = partitions.Length; // a parallel writer cannot grow
            if (_meshDataAllocated) _meshData.Dispose();
            _meshData = Mesh.AllocateWritableMeshData(partitions.Length);
            _meshDataAllocated = true;

            CMeshBuildJob job = new(){
                ColliderVertexParams = _vertexParams,
                Accessor = new ChunkAccessor(_chunks.AsReadOnly()),
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
            // Partition-local voxel lookups must map to chunk-local coordinates for any partition y
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

        [Test]
        public void Checkerboard_DoesNotOverflow16BitIndices(){
            // A checkerboard partition has 16384 voxels * 24 vertices; UInt16 indices wrap above 65535.
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
            Assert.Less(MaxIndex(), vertices, "every index must address a written vertex (no wrap-around)");
        }

        [Test]
        public void MixedTerrain_MatchesRecordedMeshes(){
            // Golden hash of the quads (positions and indices, in order) for 16 partitions of noisy terrain with
            // caves, water, floating voxels at the world top and a missing neighbour chunk. Recorded from the
            // per-voxel mesher before the flat-mask rewrite; the colliders must not change.
            int2 north = new(-1, 2);
            int2 south = new(-1, 1);
            for (int x = -2; x <= 0; x++)
            for (int z = 0; z <= 3; z++){
                int2 pos = new(x, z);
                if (pos.Equals(new int2(0, 2))) continue; // east of the north chunk stays missing (reads as air)
                AddChunk(pos, p => MixedTerrain(pos * new int2(ChunkWidth, ChunkDepth) + p.xz, p.y));
            }

            int3[] partitions = new int3[2 * PartitionsPerChunk];
            for (int y = 0; y < PartitionsPerChunk; y++){
                partitions[y] = new int3(north.x, y, north.y);
                partitions[PartitionsPerChunk + y] = new int3(south.x, y, south.y);
            }

            RunJob(partitions);

            ulong hash = 14695981039346656037UL;
            int vertexTotal = 0;
            for (int i = 0; i < partitions.Length; i++){
                Mesh.MeshData mesh = _meshData[i];
                vertexTotal += mesh.vertexCount;
                hash = Fnv(hash, mesh.vertexCount);
                foreach (CVertex vertex in mesh.GetVertexData<CVertex>()){
                    float3 p = vertex.Position.xyz;
                    hash = Fnv(hash, math.asint(p.x));
                    hash = Fnv(hash, math.asint(p.y));
                    hash = Fnv(hash, math.asint(p.z));
                }

                if (mesh.indexFormat == IndexFormat.UInt16)
                    foreach (ushort index in mesh.GetIndexData<ushort>()) hash = Fnv(hash, index);
                else
                    foreach (uint index in mesh.GetIndexData<uint>()) hash = Fnv(hash, (int)index);
            }

            Assert.AreEqual((RecordedVertexTotal, RecordedHash), (vertexTotal, hash));
        }

        private const int RecordedVertexTotal = 311332;
        private const ulong RecordedHash = 17997073674349874371UL;

        /// <summary>Stepped plateaus with caves, water up to 124, ice spots and floating dirt up to the world top.</summary>
        private static ushort MixedTerrain(int2 world, int y){
            uint column = Hash((uint)world.x, 0, (uint)world.y);
            int height = 30 + (int)(Hash((uint)(world.x >> 2), 1, (uint)(world.y >> 2)) % 200) + (int)(column % 3);
            uint r = Hash((uint)world.x, (uint)y + 2, (uint)world.y);
            if (y < height) return r % 23 == 0 ? TestFixtures.Air : y == height - 1 ? TestFixtures.Grass : TestFixtures.Stone;
            if (y == 124 && column % 5 == 0) return TestFixtures.Ice;
            if (y < 124) return TestFixtures.Water;
            return r % 97 == 0 ? TestFixtures.Dirt : TestFixtures.Air;
        }

        private static uint Hash(uint x, uint y, uint z){
            uint h = x * 0x9E3779B1u ^ y * 0x85EBCA77u ^ z * 0xC2B2AE3Du;
            h ^= h >> 16;
            h *= 0x7feb352d;
            h ^= h >> 15;
            h *= 0x846ca68b;
            h ^= h >> 16;
            return h;
        }

        private static ulong Fnv(ulong hash, int value){
            for (int i = 0; i < 4; i++){
                hash ^= (byte)(value >> (8 * i));
                hash *= 1099511628211UL;
            }

            return hash;
        }

        private long MaxIndex(int index = 0){
            Mesh.MeshData mesh = _meshData[index];
            long max = -1;
            if (mesh.indexFormat == IndexFormat.UInt16)
                foreach (ushort i in mesh.GetIndexData<ushort>()) max = math.max(max, i);
            else
                foreach (uint i in mesh.GetIndexData<uint>()) max = math.max(max, i);
            return max;
        }

        #endregion
    }
}
