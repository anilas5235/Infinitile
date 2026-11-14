using System.Collections.Generic;
using System.Text.RegularExpressions;
using Engine.Scripts.Data;
using Engine.Scripts.Jobs.Core;
using Engine.Scripts.Settings;
using Engine.Scripts.Utils;
using Engine.Scripts.World;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;

namespace Engine.Tests.EditMode{
    /// <summary>
    ///     The chunk store evicts by plain distance. A chunk behind the player but inside the load
    ///     distance must survive a chunk in front of the player that is further away.
    /// </summary>
    public class EvictionOrderTests{
        private VoxelEngineSettings _settings;
        private ChunkManager _manager;

        [SetUp]
        public void SetUp(){
            _settings = TestFixtures.CreateSettings(0);
            _settings.Chunk.LoadDistanceOverride = 0; // chunk store = (0 + 2).SquareSize() = 25
            _manager = new ChunkManager(_settings);
        }

        [TearDown]
        public void TearDown(){
            _manager.Dispose();
            Object.DestroyImmediate(_settings);
        }

        private void AddChunks(IEnumerable<int2> positions){
            NativeParallelHashMap<int2, ChunkVoxelData> chunks = new(32, Allocator.Temp);
            foreach (int2 position in positions) chunks.Add(position, TestFixtures.CreateUniformChunk(TestFixtures.Air));
            _manager.AddChunks(chunks); // the manager owns the chunk data from here on
            chunks.Dispose();
        }

        [Test]
        public void ChunkStore_Full_EvictsTheFarthestChunkNotTheOneBehindThePlayer(){
            // With the view-weighted priority (x5 behind the player) a chunk 3 behind (weighted 45) would be evicted
            // before a chunk 6 in front (weighted 36) although it is twice as close.
            PriorityUtil.Focus focus = new(int3.zero, new float3(1, 0, 0));
            _manager.FocusUpdate(focus);

            int2 behind = new(-3, 0);
            int2 front = new(6, 0);
            List<int2> positions = new(){ behind, front };
            for (int x = 1; x <= 5; x++)
            for (int z = -2; z <= 1; z++)
                positions.Add(new int2(x, z)); // 20 fillers, all weighted below 36
            positions.Add(new int2(1, 2));
            positions.Add(new int2(2, 2));
            positions.Add(new int2(3, 2));
            Assert.AreEqual(25, positions.Count, "fills the store exactly");
            AddChunks(positions);
            Assert.IsTrue(_manager.IsChunkLoaded(behind));
            Assert.IsTrue(_manager.IsChunkLoaded(front));

            AddChunks(new[]{ new int2(0, 1) }); // one over capacity: exactly one chunk is evicted

            Assert.IsTrue(_manager.IsChunkLoaded(behind), "the closer chunk behind the player must stay loaded");
            Assert.IsFalse(_manager.IsChunkLoaded(front), "the farthest chunk must be the one evicted");
        }

        [Test]
        public void AddChunks_PositionAlreadyLoaded_KeepsTheLoadedChunk_AndTakesTheRestOfTheBatch(){
            // The manager owns every list of a batch. A position that is already loaded is not an exception in the
            // middle of the batch (the lists added before it would then belong to their chunk and to the caller's
            // map, and both dispose them): its new list is released, an error is logged, the batch goes on.
            int2 loaded = new(0, 0);
            int2 fresh = new(1, 0);
            NativeParallelHashMap<int2, ChunkVoxelData> first = new(4, Allocator.Temp);
            first.Add(loaded, TestFixtures.CreateUniformChunk(TestFixtures.Stone));
            _manager.AddChunks(first);
            first.Dispose();

            NativeParallelHashMap<int2, ChunkVoxelData> second = new(4, Allocator.Temp);
            second.Add(loaded, TestFixtures.CreateUniformChunk(TestFixtures.Air));
            second.Add(fresh, TestFixtures.CreateUniformChunk(TestFixtures.Dirt));
            LogAssert.Expect(LogType.Error, new Regex("already exists"));
            _manager.AddChunks(second);
            second.Dispose();

            Assert.AreEqual(2, _manager.ChunkCount());
            Assert.AreEqual(TestFixtures.Stone, _manager.GetVoxel(new int3(0, 10, 0)), "the loaded chunk keeps its data");
            Assert.AreEqual(TestFixtures.Dirt, _manager.GetVoxel(new int3(VoxelConstants.ChunkWidth, 10, 0)),
                "the other chunk of the batch is added");
        }
    }
}
