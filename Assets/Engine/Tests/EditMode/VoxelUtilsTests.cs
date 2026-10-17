using System;
using Engine.Scripts.Utils;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using static Engine.Scripts.Utils.VoxelConstants;

namespace Engine.Tests.EditMode{
    /// <summary>TST-E05: VoxelUtils and VoxelConstants coordinate helpers, including the D-08 check.</summary>
    public class VoxelUtilsTests{
        private static readonly int[] EdgeValues ={ -33, -32, -1, 0, 31, 32 };
        private static readonly int[] HeightValues ={ 0, 31, 32, 255 };

        private static int FloorDiv(int value, int divisor){
            return (int)Math.Floor(value / (double)divisor);
        }

        private static void ForEachPosition(Action<int3> action, bool includeNegative = true){
            foreach (int x in EdgeValues)
            foreach (int z in EdgeValues)
            foreach (int y in HeightValues){
                if (!includeNegative && (x < 0 || z < 0)) continue;
                action(new int3(x, y, z));
            }
        }

        [Test]
        public void GetChunkCoords_UsesFloorDivision(){
            ForEachPosition(p => {
                int2 expected = new(FloorDiv(p.x, ChunkWidth), FloorDiv(p.z, ChunkDepth));
                Assert.AreEqual(expected, VoxelUtils.GetChunkCoords(p), $"int3 {p}");
                Assert.AreEqual(expected, VoxelUtils.GetChunkCoords(new Vector3Int(p.x, p.y, p.z)), $"Vector3Int {p}");
            });
        }

        [Test]
        public void GetLocalVoxelCoords_IsInsideChunkAndRoundTrips(){
            ForEachPosition(p => {
                int2 chunk = VoxelUtils.GetChunkCoords(p);
                int3 local = VoxelUtils.GetLocalVoxelCoords(p);
                Assert.GreaterOrEqual(local.x, 0, $"{p}");
                Assert.Less(local.x, ChunkWidth, $"{p}");
                Assert.GreaterOrEqual(local.z, 0, $"{p}");
                Assert.Less(local.z, ChunkDepth, $"{p}");
                Assert.AreEqual(p.y, local.y, $"{p}");
                int3 world = new(chunk.x * ChunkWidth + local.x, local.y, chunk.y * ChunkDepth + local.z);
                Assert.AreEqual(p, world, $"round trip {p}");
            });
        }

        [Test]
        public void GetPartitionCoords_UsesChunkCoordsAndPartitionHeight(){
            ForEachPosition(p => {
                int2 chunk = VoxelUtils.GetChunkCoords(p);
                int3 expected = new(chunk.x, p.y / PartitionHeight, chunk.y);
                Assert.AreEqual(expected, VoxelUtils.GetPartitionCoords(p), $"int3 {p}");
                Assert.AreEqual(expected, VoxelUtils.GetPartitionCoords(new Vector3(p.x, p.y, p.z)), $"Vector3 {p}");
            });
        }

        [Test]
        public void GetPartitionLocalVoxelCoords_IsInsidePartitionAndRoundTrips(){
            ForEachPosition(p => {
                int3 partition = VoxelUtils.GetPartitionCoords(p);
                int3 local = VoxelUtils.GetPartitionLocalVoxelCoords(partition, p);
                Assert.IsTrue(math.all(local >= 0) && math.all(local < PartitionSize), $"{p} -> local {local}");
                Assert.AreEqual(p, VoxelConstants.PartitionToWorldPos(partition) + local, $"round trip {p}");
            });
        }

        [Test]
        public void PartitionToWorldPos_And_PartitionToChunkPos(){
            Assert.AreEqual(new int3(32, 64, -32), VoxelConstants.PartitionToWorldPos(new int3(1, 2, -1)));
            Assert.AreEqual(new int2(1, -1), VoxelConstants.PartitionToChunkPos(new int3(1, 2, -1)));
        }

        [Test]
        public void WorldToPartitionPos_AgreesWithVoxelUtils_ForNonNegativePositions(){
            ForEachPosition(p => {
                Assert.AreEqual(VoxelUtils.GetPartitionCoords(p), VoxelConstants.WorldToPartitionPos(p), $"{p}");
                Assert.AreEqual(VoxelUtils.GetChunkCoords(p), VoxelConstants.WorldToChunkPos(p), $"{p}");
            }, false);
        }

        [Category("KnownBug")] // D-08
        [Test]
        public void WorldToPartitionPos_AgreesWithVoxelUtils_ForNegativePositions(){
            // D-08: WorldToPartitionPos uses truncating division; must match the floor division of VoxelUtils.
            ForEachPosition(p => {
                if (p.x >= 0 && p.z >= 0) return;
                Assert.AreEqual(VoxelUtils.GetPartitionCoords(p), VoxelConstants.WorldToPartitionPos(p), $"{p}");
            });
        }

        [Category("KnownBug")] // D-08
        [Test]
        public void WorldToChunkPos_AgreesWithVoxelUtils_ForNegativePositions(){
            // D-08
            ForEachPosition(p => {
                if (p.x >= 0 && p.z >= 0) return;
                Assert.AreEqual(VoxelUtils.GetChunkCoords(p), VoxelConstants.WorldToChunkPos(p), $"{p}");
            });
        }

        [Test]
        public void Constants_AreConsistent(){
            Assert.AreEqual(ChunkWidth * ChunkHeight * ChunkDepth, VoxelsPerChunk);
            Assert.AreEqual(PartitionWidth * PartitionHeight * PartitionDepth, VoxelsPerPartition);
            Assert.AreEqual(ChunkHeight / PartitionHeight, PartitionsPerChunk);
            Assert.AreEqual(new int3(ChunkWidth, ChunkHeight, ChunkDepth), ChunkSize);
            Assert.AreEqual(new int3(PartitionWidth, PartitionHeight, PartitionDepth), PartitionSize);
            Assert.AreEqual(MaxTerrainHeight - MinTerrainHeight, TerrainHeightRange);
        }
    }
}
