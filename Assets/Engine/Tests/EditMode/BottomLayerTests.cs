using Engine.Scripts.Jobs.Chunk;
using Engine.Scripts.Utils.Extensions;
using Engine.Scripts.WorldGen.Settings;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEditor;
using static Engine.Scripts.Utils.VoxelConstants;
using Voxel = Engine.Scripts.VoxelConfig.Data.Voxel.Voxel;

namespace Engine.Tests.EditMode{
    /// <summary>The bedrock floor: always at y 0, ragged at y 1 and y 2, nothing above; the Bedrock asset.</summary>
    public class BottomLayerTests{
        private const ushort Bedrock = TestFixtures.VoxelTypeCount;

        private static readonly int2[] Chunks = { new(0, 0), new(1, 0), new(-3, 2), new(5, -7) };

        private GeneratorConfig _config;
        private NativeArray<ushort> _vox;

        [SetUp]
        public void SetUp(){
            _config = TestFixtures.CreateGeneratorConfig(Allocator.Persistent);
            _config.WorldGen.Caves.BedrockVoxel = Bedrock;
            _vox = new NativeArray<ushort>(VoxelsPerChunk, Allocator.Persistent);
        }

        [TearDown]
        public void TearDown(){
            TestFixtures.DisposeGeneratorConfig(ref _config);
            _vox.Dispose();
        }

        /// <summary>Fills the chunk with stone and runs the bottom-layer pass.</summary>
        private void Run(int2 chunk){
            for (int i = 0; i < _vox.Length; i++) _vox[i] = TestFixtures.Stone;
            ChunkJob.ApplyBottomLayer(_vox, new int3(chunk.x * ChunkWidth, 0, chunk.y * ChunkDepth), _config.WorldGen);
        }

        [Test]
        public void BottomLayer_FloorIsSolidAndRaggedShares(){
            int3 count = int3.zero;
            int columns = 0;
            foreach (int2 chunk in Chunks){
                Run(chunk);
                for (int x = 0; x < ChunkWidth; x++)
                for (int z = 0; z < ChunkDepth; z++){
                    columns++;
                    Assert.AreEqual(Bedrock, _vox[ChunkSize.Flatten(x, 0, z)], "y 0 is always bedrock");
                    for (int y = 1; y <= 2; y++)
                        if (_vox[ChunkSize.Flatten(x, y, z)] == Bedrock)
                            count[y]++;
                    for (int y = 3; y < ChunkHeight; y++)
                        Assert.AreEqual(TestFixtures.Stone, _vox[ChunkSize.Flatten(x, y, z)], $"nothing above y 2 ({x},{y},{z})");
                }
            }

            Assert.AreEqual(.5f, count[1] / (float)columns, .04f, "share of bedrock at y 1");
            Assert.AreEqual(.25f, count[2] / (float)columns, .04f, "share of bedrock at y 2");
        }

        [Test]
        public void BottomLayer_SameVoxelsInEveryChunkContainingThem(){
            // A voxel's bedrock decision depends on its world position only: shifting the chunk by one world
            // column moves the pattern along with it.
            Run(new int2(2, 3));
            ushort[] a = _vox.ToArray();
            Run(new int2(2, 3));
            CollectionAssert.AreEqual(a, _vox.ToArray(), "deterministic");

            for (int i = 0; i < _vox.Length; i++) _vox[i] = TestFixtures.Stone;
            ChunkJob.ApplyBottomLayer(_vox, new int3(2 * ChunkWidth + 1, 0, 3 * ChunkDepth), _config.WorldGen);
            for (int x = 0; x < ChunkWidth - 1; x++)
            for (int z = 0; z < ChunkDepth; z++)
            for (int y = 1; y <= 2; y++)
                Assert.AreEqual(a[ChunkSize.Flatten(x + 1, y, z)], _vox[ChunkSize.Flatten(x, y, z)], $"({x},{y},{z})");
        }

        [Test]
        public void BottomLayer_OffWhenCavesDisabled(){
            _config.WorldGen.Caves.Enabled = false;
            Run(new int2(0, 0));
            for (int i = 0; i < _vox.Length; i++) Assert.AreEqual(TestFixtures.Stone, _vox[i]);
        }

        [Test]
        public void BedrockAsset_IsUnbreakableAndAssignedInShippedSettings(){
            Voxel bedrock = AssetDatabase.LoadAssetAtPath<Voxel>("Assets/Engine/Resources/VoxelDataPackages/Standard/Solid/Bedrock.asset");
            Assert.IsNotNull(bedrock, "Bedrock asset");
            Assert.IsTrue(bedrock.unbreakable, "Bedrock is unbreakable");
            Assert.IsTrue(bedrock.collision, "Bedrock is solid");

            Voxel stone = AssetDatabase.LoadAssetAtPath<Voxel>("Assets/Engine/Resources/VoxelDataPackages/Standard/Solid/StoneGrey.asset");
            Assert.IsFalse(stone.unbreakable, "ordinary voxels stay breakable");

            WorldGenSettings shipped = AssetDatabase.LoadAssetAtPath<WorldGenSettings>("Assets/EngineSettings/WorldGenSettings.asset");
            Assert.AreSame(bedrock, shipped.Caves.BedrockVoxel, "shipped settings use Bedrock for the floor");
        }
    }
}
