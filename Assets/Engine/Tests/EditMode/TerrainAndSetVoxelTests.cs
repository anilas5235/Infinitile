using Engine.Scripts.Data;
using Engine.Scripts.Jobs.Chunk;
using Engine.Scripts.Settings;
using Engine.Scripts.Utils.Extensions;
using Engine.Scripts.World;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using static Engine.Scripts.Utils.VoxelConstants;
using Object = UnityEngine.Object;

namespace Engine.Tests.EditMode{
    /// <summary>FillTerrain ice placement and ChunkManager.SetVoxel Y bounds.</summary>
    public class TerrainAndSetVoxelTests{
        private static readonly int2 WarmColumn = new(7, 9);
        private static readonly int2 ColdColumn = new(3, 4);
        private static readonly int2 HighColumn = new(20, 1);

        private GeneratorConfig _config;
        private NativeArray<ushort> _vox;
        private NativeArray<ChunkColumn> _columns;
        private ChunkManager _manager;
        private NativeParallelHashMap<int2, ChunkVoxelData> _chunks;
        private VoxelEngineSettings _settings;

        [SetUp]
        public void SetUp(){
            _config = TestFixtures.CreateGeneratorConfig(Allocator.Persistent);
            _vox = new NativeArray<ushort>(VoxelsPerChunk, Allocator.Persistent);
            _columns = new NativeArray<ChunkColumn>(ChunkWidth * ChunkDepth, Allocator.Persistent);
        }

        [TearDown]
        public void TearDown(){
            TestFixtures.DisposeGeneratorConfig(ref _config);
            _vox.Dispose();
            _columns.Dispose();
            _manager?.Dispose();
            if (_chunks.IsCreated) _chunks.Dispose();
            if (_settings != null) Object.DestroyImmediate(_settings);
        }

        private void PrepareColumns(){
            for (int x = 0; x < ChunkWidth; x++)
            for (int z = 0; z < ChunkDepth; z++){
                int2 column = new(x, z);
                _columns[ChunkJob.GetColumnIdx(x, z, ChunkDepth)] = new ChunkColumn{
                    Height = column.Equals(HighColumn) ? 130 : 100,
                    Biome = 0,
                    TopBlock = TestFixtures.Grass,
                    UnderBlock = TestFixtures.Dirt,
                    StoneBlock = TestFixtures.Stone,
                    Temperature = column.Equals(ColdColumn) ? 0.1f : 0.5f,
                    Humidity = 0.5f,
                    // PrepareChunkMaps decides the freeze from the effective temperature; FillTerrain follows the flag.
                    Frozen = column.Equals(ColdColumn) ? (byte)1 : (byte)0
                };
            }

            ChunkJob.FillTerrain(_vox, _columns, ref _config);
        }

        private ushort VoxelAt(int2 column, int y){
            return _vox[ChunkSize.Flatten(column.x, y, column.y)];
        }

        #region FillTerrain

        [Test]
        public void FillTerrain_LayersFollowColumnHeight(){
            PrepareColumns();
            int water = _config.WorldGen.WaterLevel;
            Assert.AreEqual(TestFixtures.Stone, VoxelAt(WarmColumn, 0));
            Assert.AreEqual(TestFixtures.Stone, VoxelAt(WarmColumn, 95));
            Assert.AreEqual(TestFixtures.Dirt, VoxelAt(WarmColumn, 96));
            Assert.AreEqual(TestFixtures.Dirt, VoxelAt(WarmColumn, 99));
            Assert.AreEqual(TestFixtures.Grass, VoxelAt(WarmColumn, 100), "the top block sits at h_int even under water");
            Assert.AreEqual(TestFixtures.Water, VoxelAt(WarmColumn, water - 1));
            Assert.AreEqual(TestFixtures.Air, VoxelAt(WarmColumn, water));
            Assert.AreEqual(TestFixtures.Air, VoxelAt(WarmColumn, ChunkHeight - 1));

            Assert.AreEqual(TestFixtures.Grass, VoxelAt(HighColumn, 130), "surface above water level keeps its top block");
            Assert.AreEqual(TestFixtures.Dirt, VoxelAt(HighColumn, 129));
            Assert.AreEqual(TestFixtures.Air, VoxelAt(HighColumn, 131));
        }

        [Test]
        public void FillTerrain_WarmColumnHasNoIce(){
            PrepareColumns();
            for (int y = 0; y < ChunkHeight; y++)
                Assert.AreNotEqual(TestFixtures.Ice, VoxelAt(WarmColumn, y), $"y={y}");
        }

        [Test]
        public void FillTerrain_ColdColumnFreezesWaterSurface(){
            // The water surface is the topmost water voxel at waterLevel - 1 (at waterLevel the voxel is already air).
            PrepareColumns();
            int water = _config.WorldGen.WaterLevel;
            Assert.AreEqual(TestFixtures.Water, VoxelAt(ColdColumn, water - 2), "below the surface stays water");
            Assert.AreEqual(TestFixtures.Air, VoxelAt(ColdColumn, water), "above the surface stays air");
            Assert.AreEqual(TestFixtures.Ice, VoxelAt(ColdColumn, water - 1), "cold water surface must be ice");
        }

        #endregion

        #region SetVoxel

        private void CreateManagerWithStoneChunk(){
            _settings = TestFixtures.CreateSettings(2);
            _manager = new ChunkManager(_settings);
            _chunks = new NativeParallelHashMap<int2, ChunkVoxelData>(1, Allocator.Persistent);
            _chunks.Add(int2.zero, TestFixtures.CreateUniformChunk(TestFixtures.Stone));
            _manager.AddChunks(_chunks); // the manager owns the chunk data from here on
        }

        [Test]
        public void SetVoxel_InsideChunk_ReturnsTrueAndWrites(){
            CreateManagerWithStoneChunk();
            int3 position = new(5, 10, 5);
            Assert.IsTrue(_manager.SetVoxel(TestFixtures.Grass, position));
            Assert.AreEqual(TestFixtures.Grass, _manager.GetVoxel(position));
            Assert.AreEqual(TestFixtures.Stone, _manager.GetVoxel(new int3(5, 11, 5)));
        }

        [TestCase(ChunkHeight)]
        [TestCase(-1)]
        public void SetVoxel_OutsideChunkHeight_IsRejected(int y){
            // Without a Y check, y = 256 would wrap into the next column and y = -1 into the previous one.
            CreateManagerWithStoneChunk();
            bool result = true;
            Assert.DoesNotThrow(() => result = _manager.SetVoxel(TestFixtures.Grass, new int3(5, y, 5)));
            Assert.IsFalse(result, "out-of-range Y must be rejected");
            Assert.IsTrue(_manager.TryGetChunk(int2.zero, out Chunk chunk));
            Assert.AreEqual(1, chunk.VoxelData.GetData().CompressedLength, "chunk must stay uniform stone");
        }

        #endregion
    }
}
