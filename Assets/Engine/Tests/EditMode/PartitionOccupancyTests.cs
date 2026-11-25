using Engine.Scripts.Data;
using Engine.Scripts.Settings;
using Engine.Scripts.Utils.Collections;
using Engine.Scripts.Utils.Extensions;
using Engine.Scripts.World;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using static Engine.Scripts.Utils.VoxelConstants;

namespace Engine.Tests.EditMode{
    /// <summary>
    ///     The chunk data knows which of its eight partitions may hold a voxel that is not air
    ///     (<see cref="ChunkVoxelData.OccupiedPartitions" />). The mesh stage builds nothing for the others, so a bit
    ///     must never be missing; and the remesh flags of an edit or a light change skip them.
    /// </summary>
    public class PartitionOccupancyTests{
        private const uint AllPartitions = (1u << PartitionsPerChunk) - 1;

        private VoxelEngineSettings _settings;
        private ChunkManager _manager;
        private NativeParallelHashMap<int2, ChunkVoxelData> _chunks;

        [TearDown]
        public void TearDown(){
            _manager?.Dispose();
            _manager = null;
            if (_chunks.IsCreated) _chunks.Dispose();
            if (_settings != null) Object.DestroyImmediate(_settings);
        }

        private static uint Bit(int flatIndex){
            return 1u << (flatIndex % ChunkHeight / PartitionHeight);
        }

        /// <summary>The partitions that hold a voxel, from a scan over every voxel of every run.</summary>
        private static uint ScanOccupied(ChunkVoxelData data){
            uint mask = 0;
            int start = 0;
            foreach (UnsafeIntervalList<ushort>.Node node in data.Data.Internal){
                if (node.Value != 0)
                    for (int i = start; i < node.Count; i++) mask |= Bit(i);
                start = node.Count;
            }

            return mask;
        }

        #region Chunk data

        [Test]
        public void AddVoxels_ARunInsideOneColumn_MarksThePartitionsItSpans(){
            ChunkVoxelData data = new(8);
            try{
                data.AddVoxels(TestFixtures.Air, 3 * ChunkHeight + 40); // column 3, up to y 39
                Assert.AreEqual(0u, data.OccupiedPartitions, "air marks nothing");
                data.AddVoxels(TestFixtures.Stone, 30); // y 40 to 69: partitions 1 and 2
                Assert.AreEqual(0b0000_0110u, data.OccupiedPartitions);
                data.AddVoxels(TestFixtures.Stone, 0);
                Assert.AreEqual(0b0000_0110u, data.OccupiedPartitions, "an empty run marks nothing");
                Assert.IsFalse(data.PartitionHasVoxels(0));
                Assert.IsTrue(data.PartitionHasVoxels(1));
                Assert.IsTrue(data.PartitionHasVoxels(2));
                Assert.IsFalse(data.PartitionHasVoxels(3));
                Assert.IsFalse(data.PartitionHasVoxels(-1), "below the chunk");
                Assert.IsFalse(data.PartitionHasVoxels(PartitionsPerChunk), "above the chunk");
            }
            finally{
                data.Dispose();
            }
        }

        [Test]
        public void AddVoxels_ARunOverTheTopOfAColumn_MarksTheTopAndTheBottom(){
            ChunkVoxelData data = new(8);
            try{
                data.AddVoxels(TestFixtures.Air, ChunkHeight - 6);
                data.AddVoxels(TestFixtures.Stone, 20); // the top 6 voxels of column 0, then y 0 to 13 of column 1
                Assert.AreEqual(1u << (PartitionsPerChunk - 1) | 1u, data.OccupiedPartitions);
            }
            finally{
                data.Dispose();
            }
        }

        [Test]
        public void AddVoxels_ARunAsLongAsAColumn_MarksEveryPartition(){
            ChunkVoxelData data = new(8);
            try{
                data.AddVoxels(TestFixtures.Air, 100);
                data.AddVoxels(TestFixtures.Stone, ChunkHeight);
                Assert.AreEqual(AllPartitions, data.OccupiedPartitions);
            }
            finally{
                data.Dispose();
            }
        }

        [Test]
        public void AddVoxels_RandomRuns_MarkExactlyThePartitionsThatHoldAVoxel(){
            // Chunks of air with a few runs of voxels at random places, so most masks have clear bits
            for (int seed = 0; seed < 300; seed++){
                System.Random rng = new(seed);
                ChunkVoxelData data = new(32);
                try{
                    uint expected = 0;
                    int filled = 0;
                    while (filled < VoxelsPerChunk){
                        int air = math.min(rng.Next(0, 120000), VoxelsPerChunk - filled);
                        data.AddVoxels(TestFixtures.Air, air);
                        filled += air;
                        if (filled == VoxelsPerChunk) break;

                        int count = rng.Next(4) == 0 ? rng.Next(200, 600) : rng.Next(1, 48);
                        count = math.min(count, VoxelsPerChunk - filled);
                        for (int i = filled; i < filled + count; i++) expected |= Bit(i);
                        data.AddVoxels((ushort)rng.Next(1, 4), count);
                        filled += count;
                    }

                    Assert.AreEqual(expected, data.OccupiedPartitions, $"seed {seed}");
                    Assert.AreEqual(expected, ScanOccupied(data), $"seed {seed}: scan of the stored runs");
                }
                finally{
                    data.Dispose();
                }
            }
        }

        [Test]
        public void WriteToChunkData_MarksThePartitionsOfTheBuffer(){
            // Ground up to y 69 at most (partitions 0 to 2) and one block in partition 6
            System.Random rng = new(5);
            ushort[] vox = new ushort[VoxelsPerChunk];
            for (int x = 0; x < ChunkWidth; x++)
            for (int z = 0; z < ChunkDepth; z++){
                int height = rng.Next(40, 70);
                for (int y = 0; y < height; y++) vox[ChunkSize.Flatten(x, y, z)] = TestFixtures.Stone;
            }

            vox[ChunkSize.Flatten(3, 6 * PartitionHeight + 8, 9)] = TestFixtures.Grass;

            using NativeArray<ushort> native = new(vox, Allocator.Persistent);
            ChunkVoxelData data = ChunkDataTests.WriteToChunkData(native);
            try{
                Assert.AreEqual(0b0100_0111u, data.OccupiedPartitions);
                Assert.AreEqual(ScanOccupied(data), data.OccupiedPartitions);
            }
            finally{
                data.Dispose();
            }
        }

        [Test]
        public void SetVoxel_MarksThePartition_AndTheMarkStaysWhenTheVoxelGoes(){
            ChunkVoxelData data = TestFixtures.CreateUniformChunk(TestFixtures.Air);
            try{
                Assert.AreEqual(0u, data.OccupiedPartitions);
                int3 pos = new(3, 2 * PartitionHeight + 6, 9);

                data.SetVoxel(new int3(1, 200, 1), TestFixtures.Air);
                Assert.AreEqual(0u, data.OccupiedPartitions, "writing air marks nothing");

                Assert.IsTrue(data.SetVoxel(pos, TestFixtures.Stone));
                Assert.AreEqual(0b0000_0100u, data.OccupiedPartitions);

                // The mark only says "may hold a voxel": the partition is then meshed as before, to an empty mesh
                Assert.IsTrue(data.SetVoxel(pos, TestFixtures.Air));
                Assert.AreEqual(0b0000_0100u, data.OccupiedPartitions);
            }
            finally{
                data.Dispose();
            }
        }

        #endregion

        #region Remesh flags

        /// <summary>A chunk with stone up to <paramref name="height" /> in every column and air above.</summary>
        private static ChunkVoxelData GroundChunk(int height){
            ChunkVoxelData data = new(2 * ChunkWidth * ChunkDepth);
            for (int column = 0; column < ChunkWidth * ChunkDepth; column++){
                data.AddVoxels(TestFixtures.Stone, height);
                data.AddVoxels(TestFixtures.Air, ChunkHeight - height);
            }

            return data;
        }

        /// <summary>3 x 3 chunks with ground in the lowest partition only.</summary>
        private void CreateManagerWithLowGround(){
            _settings = TestFixtures.CreateSettings(2);
            _manager = new ChunkManager(_settings);
            _chunks = new NativeParallelHashMap<int2, ChunkVoxelData>(9, Allocator.Persistent);
            for (int x = -1; x <= 1; x++)
            for (int z = -1; z <= 1; z++)
                _chunks.Add(new int2(x, z), GroundChunk(PartitionHeight));
            _manager.AddChunks(_chunks); // the manager owns the chunk data from here on
        }

        [Test]
        public void PartitionHasVoxels_FollowsTheChunkData(){
            CreateManagerWithLowGround();
            Assert.IsTrue(_manager.PartitionHasVoxels(new int3(0, 0, 0)));
            Assert.IsFalse(_manager.PartitionHasVoxels(new int3(0, 1, 0)));
            Assert.IsFalse(_manager.PartitionHasVoxels(new int3(0, PartitionsPerChunk - 1, 0)));
            Assert.IsFalse(_manager.PartitionHasVoxels(new int3(0, -1, 0)), "below the world");
            Assert.IsFalse(_manager.PartitionHasVoxels(new int3(0, PartitionsPerChunk, 0)), "above the world");
            Assert.IsFalse(_manager.PartitionHasVoxels(new int3(7, 0, 7)), "a chunk that is not loaded");
        }

        [Test]
        public void FlagReMesh_SkipsAPartitionWithoutVoxels(){
            // A light change flags every partition around it; one without a voxel has no face to light
            CreateManagerWithLowGround();
            int requests = 0;
            _manager.OnRemeshRequested += () => requests++;

            _manager.FlagReMesh(new int3(0, 3, 0));
            Assert.IsFalse(_manager.ShouldReMesh(new int3(0, 3, 0)));
            Assert.AreEqual(0, requests, "nobody is woken for it");

            _manager.FlagReMesh(new int3(0, 0, 0));
            Assert.IsTrue(_manager.ShouldReMesh(new int3(0, 0, 0)));
            Assert.AreEqual(1, requests);
        }

        [Test]
        public void ApplyLight_OfAChunkLitAgain_FlagsOnlyPartitionsWithVoxels(){
            CreateManagerWithLowGround();
            Assert.IsTrue(_manager.ApplyLight(int2.zero, new ChunkLightData(16), out _));
            _manager.InvalidateLight(int2.zero);
            Assert.IsTrue(_manager.ApplyLight(int2.zero, new ChunkLightData(16), out _));

            for (int x = -1; x <= 1; x++)
            for (int z = -1; z <= 1; z++)
            for (int y = 0; y < PartitionsPerChunk; y++)
                Assert.AreEqual(y == 0, _manager.ShouldReMesh(new int3(x, y, z)), $"partition ({x},{y},{z})");
        }

        [Test]
        public void SetVoxel_AtAPartitionBorder_FlagsOnlyNeighboursWithVoxels(){
            CreateManagerWithLowGround();

            // The top block of the ground borders the partition of air above it
            Assert.IsTrue(_manager.SetVoxel(TestFixtures.Air, new int3(5, PartitionHeight - 1, 5)));
            Assert.IsTrue(_manager.ShouldReMesh(new int3(0, 0, 0)), "the edited partition");
            Assert.IsFalse(_manager.ShouldReMesh(new int3(0, 1, 0)), "air above: nothing of it changes");

            // A block at the chunk border: the ground of the neighbour chunk shows a new face
            Assert.IsTrue(_manager.SetVoxel(TestFixtures.Air, new int3(0, 10, 5)));
            Assert.IsTrue(_manager.ShouldReMesh(new int3(-1, 0, 0)), "a neighbour with voxels is flagged as before");
        }

        [Test]
        public void SetVoxel_IntoAPartitionWithoutVoxels_MarksAndFlagsIt(){
            CreateManagerWithLowGround();
            int3 partition = new(0, 2, 0);
            int requests = 0;
            _manager.OnRemeshRequested += () => requests++;

            // The lowest layer of partition 2: partition 1 below it holds no voxel and is left alone
            Assert.IsTrue(_manager.SetVoxel(TestFixtures.Stone, new int3(5, 2 * PartitionHeight, 5)));
            Assert.IsTrue(_manager.PartitionHasVoxels(partition));
            Assert.IsTrue(_manager.ShouldReMesh(partition), "the partition must be meshed now");
            Assert.AreEqual(1, requests, "the mesh stage is woken");
            Assert.IsFalse(_manager.ShouldReMesh(new int3(0, 1, 0)));

            // A block right under it, in partition 1: now both hold voxels and border each other
            _manager.ReMeshStarted(partition);
            Assert.IsTrue(_manager.SetVoxel(TestFixtures.Stone, new int3(5, 2 * PartitionHeight - 1, 5)));
            Assert.IsTrue(_manager.ShouldReMesh(new int3(0, 1, 0)));
            Assert.IsTrue(_manager.ShouldReMesh(partition), "the block above shows one face less");
        }

        #endregion
    }
}
