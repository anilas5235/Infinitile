using System;
using System.Reflection;
using Engine.Scripts.Data;
using Engine.Scripts.Jobs.Chunk;
using Engine.Scripts.Utils;
using Engine.Scripts.Utils.Extensions;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using static Engine.Scripts.Utils.VoxelConstants;

namespace Engine.Tests.EditMode{
    /// <summary>
    ///     TST-E02: ChunkJob.WriteToChunkData packing order round-trips through ChunkVoxelData.GetVoxel.
    ///     TST-E03 / LGT-01: ChunkLightData (chunk-wide 16-bit light) round trips.
    /// </summary>
    public class ChunkDataTests{
        private bool _hasVoxelData;
        private ChunkVoxelData _voxelData;
        private bool _hasLightData;
        private ChunkLightData _lightData;

        [TearDown]
        public void TearDown(){
            if (_hasVoxelData) _voxelData.Dispose();
            if (_hasLightData) _lightData.Dispose();
            _hasVoxelData = _hasLightData = false;
        }

        /// <summary>Runs the real (private) ChunkJob.WriteToChunkData on a voxel buffer.</summary>
        internal static ChunkVoxelData WriteToChunkData(NativeArray<ushort> vox){
            MethodInfo method = typeof(ChunkJob).GetMethod("WriteToChunkData",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(method, "ChunkJob.WriteToChunkData not found");
            object job = new ChunkJob();
            object[] args ={ vox, null };
            method.Invoke(job, args);
            return (ChunkVoxelData)args[1];
        }

        private static ushort[] TerrainLikeBuffer(int seed){
            System.Random rng = new(seed);
            ushort[] vox = new ushort[VoxelsPerChunk];
            for (int x = 0; x < ChunkWidth; x++)
            for (int z = 0; z < ChunkDepth; z++){
                int height = rng.Next(90, 140);
                for (int y = 0; y < ChunkHeight; y++){
                    ushort v = y < height - 4 ? TestFixtures.Stone :
                        y < height ? TestFixtures.Dirt :
                        y == height ? TestFixtures.Grass : TestFixtures.Air;
                    if (rng.Next(50) == 0) v = (ushort)rng.Next(0, 6);
                    vox[ChunkSize.Flatten(x, y, z)] = v;
                }
            }

            return vox;
        }

        private static ushort[] NoiseBuffer(int seed){
            System.Random rng = new(seed);
            ushort[] vox = new ushort[VoxelsPerChunk];
            for (int i = 0; i < vox.Length; i++) vox[i] = (ushort)rng.Next(0, 3);
            return vox;
        }

        private void AssertRoundTrip(ushort[] expected){
            using NativeArray<ushort> vox = new(expected, Allocator.Persistent);
            _voxelData = WriteToChunkData(vox);
            _hasVoxelData = true;
            Assert.AreEqual(VoxelsPerChunk, _voxelData.GetData().Length);
            for (int x = 0; x < ChunkWidth; x++)
            for (int y = 0; y < ChunkHeight; y++)
            for (int z = 0; z < ChunkDepth; z++){
                ushort e = expected[ChunkSize.Flatten(x, y, z)];
                ushort actual = _voxelData.GetVoxel(new int3(x, y, z));
                if (e != actual) Assert.Fail($"({x},{y},{z}): expected {e}, got {actual}");
            }
        }

        [Test]
        public void WriteToChunkData_TerrainLikeBuffer_ReadsBackIdentically(){
            AssertRoundTrip(TerrainLikeBuffer(42));
        }

        [Test]
        public void WriteToChunkData_RandomBuffer_ReadsBackIdentically(){
            AssertRoundTrip(NoiseBuffer(7));
        }

        [Test]
        public void WriteToChunkData_PacksInXzyOrderMatchingFlatten(){
            // Columns differ only in their (x, z) index: packing must produce one run per column in x-major, z, y order.
            ushort[] vox = new ushort[VoxelsPerChunk];
            for (int x = 0; x < ChunkWidth; x++)
            for (int z = 0; z < ChunkDepth; z++)
            for (int y = 0; y < ChunkHeight; y++)
                vox[ChunkSize.Flatten(x, y, z)] = (ushort)(x * ChunkDepth + z);
            using NativeArray<ushort> native = new(vox, Allocator.Persistent);
            _voxelData = WriteToChunkData(native);
            _hasVoxelData = true;
            Assert.AreEqual(ChunkWidth * ChunkDepth, _voxelData.Data.CompressedLength);
            int i = 0;
            foreach (Scripts.Utils.Collections.UnsafeIntervalList<ushort>.Node node in _voxelData.Data.Internal){
                Assert.AreEqual(i, node.Value, "run order");
                Assert.AreEqual((i + 1) * ChunkHeight, node.Count, "run end");
                i++;
            }
        }

        [Test]
        public void SetVoxel_ReturnsFalseWhenUnchanged(){
            _voxelData = TestFixtures.CreateUniformChunk(TestFixtures.Stone);
            _hasVoxelData = true;
            int3 pos = new(3, 70, 9);
            Assert.IsFalse(_voxelData.SetVoxel(pos, TestFixtures.Stone));
            Assert.IsTrue(_voxelData.SetVoxel(pos, TestFixtures.Grass));
            Assert.AreEqual(TestFixtures.Grass, _voxelData.GetVoxel(pos));
            Assert.IsFalse(_voxelData.SetVoxel(pos, TestFixtures.Grass));
            Assert.AreEqual(TestFixtures.Stone, _voxelData.GetVoxel(pos + new int3(0, 1, 0)));
            Assert.AreEqual(TestFixtures.Stone, _voxelData.GetVoxel(pos - new int3(0, 1, 0)));
        }

        #region TST-E03

        [Test]
        public void ChunkLightData_StartsDark_CoversWholeChunk(){
            _lightData = new ChunkLightData(16);
            _hasLightData = true;
            Assert.IsTrue(_lightData.IsCreated);
            Assert.AreEqual(VoxelsPerChunk, _lightData.GetData().Length);
            Assert.AreEqual(1, _lightData.GetData().CompressedLength);
            Assert.AreEqual(0, _lightData.GetLight(new int3(0, 0, 0)));
            Assert.AreEqual(0, _lightData.GetLight(new int3(31, 255, 31)));
            Assert.IsFalse(default(ChunkLightData).IsCreated);
        }

        [Test]
        public void ChunkLightData_SetLight_GetLight_RoundTrip(){
            _lightData = new ChunkLightData(16);
            _hasLightData = true;
            int3 pos = new(1, 200, 3);
            ushort light = VoxelLight.Pack(12, 15, 9, 3);
            Assert.IsTrue(_lightData.SetLight(pos, light));
            Assert.AreEqual(light, _lightData.GetLight(pos));
            Assert.IsFalse(_lightData.SetLight(pos, light), "unchanged");
            Assert.AreEqual(0, _lightData.GetLight(pos + new int3(1, 0, 0)));
            Assert.AreEqual(0, _lightData.GetLight(pos + new int3(0, 1, 0)));
            Assert.AreEqual(0, _lightData.GetLight(pos + new int3(0, 0, 1)));
        }

        [Test]
        public void ChunkLightData_UsesVoxelFlattenOrder(){
            // Same layout as the voxel data, so a column (x, z) is one contiguous run of 256 values.
            _lightData = ChunkLightData.CreateEmpty(16);
            _hasLightData = true;
            _lightData.AddLights(VoxelLight.FullSky, ChunkHeight); // column (0, 0)
            _lightData.AddLights(0, VoxelsPerChunk - ChunkHeight);
            Assert.AreEqual(VoxelLight.FullSky, _lightData.GetLight(new int3(0, 0, 0)));
            Assert.AreEqual(VoxelLight.FullSky, _lightData.GetLight(new int3(0, 255, 0)));
            Assert.AreEqual(0, _lightData.GetLight(new int3(0, 0, 1)));
            Assert.AreEqual(0, _lightData.GetLight(new int3(1, 0, 0)));
        }

        [Test]
        public void VoxelLight_PackAndChannels(){
            ushort light = VoxelLight.Pack(14, 15, 9, 3);
            Assert.AreEqual(14, VoxelLight.GetSky(light));
            Assert.AreEqual(15, VoxelLight.GetRed(light));
            Assert.AreEqual(9, VoxelLight.GetGreen(light));
            Assert.AreEqual(3, VoxelLight.GetBlue(light));
            Assert.AreEqual((14 << 12) | (15 << 8) | (9 << 4) | 3, light, "sky(4) | R(4) | G(4) | B(4)");
            Assert.AreEqual(VoxelLight.Pack(14, 15, 2, 3), VoxelLight.With(light, VoxelLight.Green, 2));
            Assert.AreEqual(0xF000, VoxelLight.FullSky);
        }

        #endregion
    }
}
