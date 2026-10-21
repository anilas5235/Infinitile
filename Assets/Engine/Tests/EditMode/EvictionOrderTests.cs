using System.Collections.Generic;
using Engine.Scripts.Components;
using Engine.Scripts.Data;
using Engine.Scripts.Jobs.Core;
using Engine.Scripts.Settings;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

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
            _settings.Chunk.LoadDistance = 0; // chunk store = (0 + 2).SquareSize() = 25
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
    }
}
