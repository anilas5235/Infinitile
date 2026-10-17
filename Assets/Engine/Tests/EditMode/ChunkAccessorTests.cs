using Engine.Scripts.Data;
using NUnit.Framework;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using static Engine.Scripts.Utils.VoxelConstants;

namespace Engine.Tests.EditMode{
    /// <summary>TST-E06: ChunkAccessor with a hand-built 3-chunk neighbourhood around the origin.</summary>
    public class ChunkAccessorTests{
        private const ushort Marker = 77;
        private const ushort YMarker = 66;
        private static readonly int3 MarkerLocal = new(31, 5, 3); // in chunk (-1, 0)
        private static readonly int3 YMarkerLocal = new(0, 64, 0); // in chunk (0, 0)

        private ChunkAccessor _accessor;
        private NativeParallelHashMap<int2, ChunkVoxelData> _chunks;
        private NativeParallelHashMap<int2, ChunkLightData> _lights;

        [SetUp]
        public void SetUp(){
            _chunks = new NativeParallelHashMap<int2, ChunkVoxelData>(8, Allocator.Persistent);
            _lights = new NativeParallelHashMap<int2, ChunkLightData>(8, Allocator.Persistent);

            ChunkVoxelData origin = TestFixtures.CreateUniformChunk(TestFixtures.Grass);
            origin.SetVoxel(YMarkerLocal, YMarker); // modify before the copy goes into the map
            _chunks.Add(new int2(0, 0), origin);

            _chunks.Add(new int2(1, 0), TestFixtures.CreateUniformChunk(TestFixtures.Dirt));

            ChunkVoxelData left = TestFixtures.CreateUniformChunk(TestFixtures.Stone);
            left.SetVoxel(MarkerLocal, Marker);
            _chunks.Add(new int2(-1, 0), left);

            _chunks.Add(new int2(0, -1), TestFixtures.CreateUniformChunk(TestFixtures.Water));
            _chunks.Add(new int2(-1, -1), TestFixtures.CreateUniformChunk(TestFixtures.Ice));

            _lights.Add(new int2(0, 0), new ChunkLightData(16));

            _accessor = new ChunkAccessor(_chunks.AsReadOnly(), _lights.AsReadOnly());
        }

        [TearDown]
        public void TearDown(){
            foreach (KeyValue<int2, ChunkVoxelData> kv in _chunks) kv.Value.Dispose();
            foreach (KeyValue<int2, ChunkLightData> kv in _lights) kv.Value.Dispose();
            _chunks.Dispose();
            _lights.Dispose();
        }

        private ushort Get(int3 partition, int3 voxel){
            return _accessor.GetVoxelInPartition(partition, voxel);
        }

        [Test]
        public void InsidePartition_ReadsOwnChunk(){
            Assert.AreEqual(TestFixtures.Grass, Get(new int3(0, 0, 0), new int3(5, 5, 5)));
            Assert.AreEqual(TestFixtures.Grass, Get(new int3(0, 0, 0), new int3(0, 0, 0)));
            Assert.AreEqual(TestFixtures.Grass, Get(new int3(0, 0, 0), new int3(31, 31, 31)));
        }

        [Test]
        public void PartitionY_OffsetsIntoChunkColumn(){
            Assert.AreEqual(YMarker, Get(new int3(0, 2, 0), new int3(0, 0, 0)));
            Assert.AreEqual(YMarker, Get(new int3(0, 1, 0), new int3(0, 32, 0)), "y overflow into the partition above");
            Assert.AreEqual(YMarker, Get(new int3(0, 3, 0), new int3(0, -32, 0)), "y underflow into the partition below");
            Assert.AreEqual(TestFixtures.Grass, Get(new int3(0, 2, 0), new int3(0, 1, 0)));
        }

        [Test]
        public void OutsideY_ReturnsAir(){
            Assert.AreEqual(0, Get(new int3(0, 0, 0), new int3(0, -1, 0)));
            Assert.AreEqual(0, Get(new int3(0, 7, 0), new int3(0, 32, 0)));
            Assert.AreEqual(0, Get(new int3(0, 0, 0), new int3(0, 256, 0)));
        }

        [Test]
        public void PositiveXZOverflow_ResolvesIntoNeighbourChunk(){
            Assert.AreEqual(TestFixtures.Dirt, Get(new int3(0, 0, 0), new int3(32, 5, 5)));
            Assert.AreEqual(TestFixtures.Dirt, Get(new int3(0, 0, 0), new int3(63, 5, 5)));
            Assert.AreEqual(0, Get(new int3(0, 0, 0), new int3(64, 5, 5)), "chunk (2,0) is missing");
            Assert.AreEqual(0, Get(new int3(0, 0, 0), new int3(5, 5, 32)), "chunk (0,1) is missing");
        }

        [Test]
        public void NegativeXZUnderflow_ResolvesIntoNeighbourChunk(){
            Assert.AreEqual(TestFixtures.Stone, Get(new int3(0, 0, 0), new int3(-1, 5, 5)));
            Assert.AreEqual(Marker, Get(new int3(0, 0, 0), new int3(-1, 5, 3)), "local coordinate inside the left chunk");
            Assert.AreEqual(TestFixtures.Stone, Get(new int3(0, 0, 0), new int3(-32, 5, 5)));
            Assert.AreEqual(0, Get(new int3(0, 0, 0), new int3(-33, 5, 5)), "chunk (-2,0) is missing");
            Assert.AreEqual(TestFixtures.Water, Get(new int3(0, 0, 0), new int3(5, 5, -1)));
            Assert.AreEqual(TestFixtures.Ice, Get(new int3(0, 0, 0), new int3(-1, 5, -1)));
        }

        [Test]
        public void NegativePartitionCoordinates_ResolveNeighbours(){
            int3 partition = new(-1, 0, -1);
            Assert.AreEqual(TestFixtures.Ice, Get(partition, new int3(5, 5, 5)));
            Assert.AreEqual(TestFixtures.Water, Get(partition, new int3(32, 5, 5)), "x overflow into chunk (0,-1)");
            Assert.AreEqual(TestFixtures.Stone, Get(partition, new int3(5, 5, 32)), "z overflow into chunk (-1,0)");
            Assert.AreEqual(TestFixtures.Grass, Get(partition, new int3(32, 5, 32)), "diagonal into chunk (0,0)");
            Assert.AreEqual(0, Get(partition, new int3(-1, 5, 5)), "chunk (-2,-1) is missing");
            Assert.AreEqual(0, Get(partition, new int3(5, 5, -1)), "chunk (-1,-2) is missing");
        }

        [Test]
        public void TryGetChunk_And_ContainsChunk(){
            Assert.IsTrue(_accessor.ContainsChunk(new int2(0, 0)));
            Assert.IsTrue(_accessor.ContainsChunk(new int2(-1, -1)));
            Assert.IsFalse(_accessor.ContainsChunk(new int2(2, 2)));
            Assert.IsTrue(_accessor.TryGetChunk(new int2(1, 0), out ChunkVoxelData chunk));
            Assert.AreEqual(TestFixtures.Dirt, chunk.GetVoxel(new int3(1, 1, 1)));
            Assert.IsFalse(_accessor.TryGetChunk(new int2(2, 2), out _));
        }

        [Test]
        public void Lights_ResolveAndFallBack(){
            Assert.IsTrue(_accessor.TryGetLightData(new int3(0, 3, 0), out PartitionLightData light));
            Assert.IsTrue(light.IsAllZero);
            Assert.IsFalse(_accessor.TryGetLightData(new int3(0, 8, 0), out _), "partition index out of range");
            Assert.IsFalse(_accessor.TryGetLightData(new int3(1, 0, 0), out _), "chunk without light data");
            Assert.AreEqual(0, _accessor.GetLightInPartition(new int3(0, 0, 0), new int3(5, 5, 5)));
            Assert.AreEqual(0, _accessor.GetLightInPartition(new int3(0, 0, 0), new int3(-1, 40, 5)));
        }

        [Test]
        public void InChunkBounds(){
            Assert.IsTrue(ChunkAccessor.InChunkBounds(new int3(0, 0, 0)));
            Assert.IsTrue(ChunkAccessor.InChunkBounds(new int3(31, 255, 31)));
            Assert.IsFalse(ChunkAccessor.InChunkBounds(new int3(-1, 0, 0)));
            Assert.IsFalse(ChunkAccessor.InChunkBounds(new int3(32, 0, 0)));
            Assert.IsFalse(ChunkAccessor.InChunkBounds(new int3(0, -1, 0)));
            Assert.IsFalse(ChunkAccessor.InChunkBounds(new int3(0, 256, 0)));
            Assert.IsFalse(ChunkAccessor.InChunkBounds(new int3(0, 0, -1)));
            Assert.IsFalse(ChunkAccessor.InChunkBounds(new int3(0, 0, 32)));
        }

        [Test]
        public void InPartitionBounds(){
            Assert.IsTrue(ChunkAccessor.InPartitionBounds(new int3(0, 0, 0)));
            Assert.IsTrue(ChunkAccessor.InPartitionBounds(new int3(31, 31, 31)));
            Assert.IsFalse(ChunkAccessor.InPartitionBounds(new int3(0, 32, 0)));
            Assert.IsFalse(ChunkAccessor.InPartitionBounds(new int3(-1, 0, 0)));
            Assert.IsFalse(ChunkAccessor.InPartitionBounds(new int3(0, 0, 32)));
            Assert.IsFalse(ChunkAccessor.InPartitionBounds(new int3(0, -1, 0)));
        }
    }
}
