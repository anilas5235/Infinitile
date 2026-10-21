using System;
using System.Reflection;
using System.Runtime.InteropServices;
using Engine.Scripts.Render;
using Engine.Scripts.VoxelConfig.Data.Mesh;
using Engine.Scripts.VoxelConfig.Data.Voxel;
using Engine.Scripts.VoxelConfig.Registry;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using static Engine.Scripts.Utils.VoxelRenderConstants;
using Object = UnityEngine.Object;

namespace Engine.Tests.EditMode{
    /// <summary>
    ///     GPU struct sizes, PageAllocator, RenderBufferManager spill-over, read-back clamp
    ///     and AllocInfo.ToIndexAndCount.
    /// </summary>
    public class RenderDataTests{
        private PageAllocator _pages;
        private RenderBufferManager _bufferManager;
        private Material _material;
        private NativeArray<uint> _counts;

        [SetUp]
        public void SetUp(){
            _pages = new PageAllocator(Allocator.Persistent);
        }

        [TearDown]
        public void TearDown(){
            _pages.Dispose();
            _bufferManager?.Dispose();
            _bufferManager = null;
            if (_material) Object.DestroyImmediate(_material);
            if (_counts.IsCreated) _counts.Dispose();
        }

        #region struct sizes

        [Test]
        public void Vertex_Is28Bytes(){
            Assert.AreEqual(28, Marshal.SizeOf<Vertex>());
        }

        [Test]
        public void QuadDef_Is116Bytes(){
            Assert.AreEqual(116, Marshal.SizeOf<QuadDefinition.QuadDef>());
        }

        [Test]
        public void PartitionMetadata_Is48Bytes(){
            Type type = typeof(VoxelWorldRenderer).Assembly.GetType("Engine.Scripts.Render.PartitionMetadata");
            Assert.IsNotNull(type, "PartitionMetadata not found");
            Assert.AreEqual(48, Marshal.SizeOf(type));
        }

        [Test]
        public void GpuVoxelDef_Is80Bytes(){
            Type type = typeof(VoxelRegistry).GetNestedType("GPUVoxelDef", BindingFlags.NonPublic);
            Assert.IsNotNull(type, "VoxelRegistry.GPUVoxelDef not found");
            Assert.AreEqual(80, Marshal.SizeOf(type));
            Assert.AreEqual(76, (int)Marshal.OffsetOf(type, "LightAttenuation"), "HLSL VoxelDef offset");
        }

        [Test]
        public void GpuVoxelDef_DepthFadeDistance_IsFp16BitsInA32BitSlot(){
            // HLSL `half` in a structured buffer is a 32-bit float under fxc, so a 2-byte C# half would be misread.
            // The slot is a uint holding the fp16 bits.
            Type type = typeof(VoxelRegistry).GetNestedType("GPUVoxelDef", BindingFlags.NonPublic);
            Assert.IsNotNull(type, "VoxelRegistry.GPUVoxelDef not found");
            FieldInfo field = type.GetField("DepthFadeDistance", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(field, "GPUVoxelDef.DepthFadeDistance not found");
            Assert.AreEqual(typeof(uint), field.FieldType);
            Assert.AreEqual(8, (int)Marshal.OffsetOf(type, "DepthFadeDistance"), "HLSL VoxelDef offset");
            Assert.AreEqual(12, (int)Marshal.OffsetOf(type, "Glow"), "HLSL VoxelDef offset");

            Voxel.VoxelDef def = new(){ DepthFadeDistance = (half)4.5f, Glow = 200 };
            object gpuDef = Activator.CreateInstance(type, def, (byte)0);
            uint bits = (uint)field.GetValue(gpuDef);
            Assert.AreEqual(math.f32tof16(4.5f), bits);
            Assert.AreEqual(4.5f, math.f16tof32(bits));
        }

        #endregion

        #region PageAllocator

        [TestCase(1, 1, 1)]
        [TestCase(256, 1, 256)]
        [TestCase(257, 2, 1)]
        [TestCase(MaxPointsPerPartition, 384, 256)]
        public void PageAllocator_AllocatesCeilOfPointsOverPageSize(int points, int expectedPages, int lastPageCount){
            int numPages = (int)math.ceil(points / (float)PointsPerPage);
            Assert.AreEqual(expectedPages, numPages);
            Assert.IsTrue(_pages.TryAllocPages(points, numPages, 3, default, out AllocInfo info));
            Assert.AreEqual(3, info.BufferIndex);
            Assert.AreEqual(expectedPages, info.Count);
            Assert.AreEqual(PagesPerBuffer - expectedPages, _pages.FreePages);
            Assert.AreEqual((uint)points, _pages.TotalValidPoints);

            uint2[] pages = info.ToIndexAndCount();
            Assert.AreEqual((uint)lastPageCount, pages[^1].y);
            for (int i = 0; i < pages.Length - 1; i++) Assert.AreEqual((uint)PointsPerPage, pages[i].y, $"page {i}");
            foreach (int index in info.GetPageIndices())
                Assert.AreNotEqual(0u, _pages.GetPageCount(index), $"page {index} must be marked used");
        }

        [Test]
        public void PageAllocator_ReleaseReturnsAllPages_AndDoubleReleaseIsNoOp(){
            Assert.IsTrue(_pages.TryAllocPages(700, 3, 0, default, out AllocInfo info));
            Assert.IsFalse(_pages.IsEmpty);

            _pages.Release(info);
            Assert.IsTrue(_pages.IsEmpty);
            Assert.AreEqual(PagesPerBuffer, _pages.FreePages);
            Assert.AreEqual(0u, _pages.TotalValidPoints);
            foreach (int index in info.GetPageIndices()) Assert.AreEqual(0u, _pages.GetPageCount(index));

            Assert.DoesNotThrow(() => _pages.Release(info));
            Assert.AreEqual(PagesPerBuffer, _pages.FreePages, "releasing twice must not duplicate free pages");
        }

        [Test]
        public void PageAllocator_FailsWhenNotEnoughFreePages(){
            Assert.IsTrue(_pages.TryAllocPages(PagesPerBuffer * PointsPerPage, PagesPerBuffer, 0, default, out AllocInfo all));
            Assert.AreEqual(0, _pages.FreePages);
            Assert.IsFalse(_pages.TryAllocPages(1, 1, 0, default, out AllocInfo none));
            Assert.IsTrue(none.IsEmpty);
            _pages.Release(all);
            Assert.IsFalse(_pages.TryAllocPages(1, PagesPerBuffer + 1, 0, default, out _));
        }

        [Test]
        public void PageAllocator_ZeroPages_Throws(){
            Assert.Throws<ArgumentOutOfRangeException>(() => _pages.TryAllocPages(1, 0, 0, default, out _));
        }

        [Test]
        public void PageAllocator_ReleaseOfDefaultAllocInfo_IsNoOp(){
            Assert.DoesNotThrow(() => _pages.Release(default));
            Assert.AreEqual(PagesPerBuffer, _pages.FreePages);
        }

        [Test]
        public void PageAllocator_StoresPartitionBoundsPerPage_AndClearsThemOnRelease(){
            // Every page carries the AABB of its partition; a released page holds an empty box again.
            PageBounds bounds = PageBounds.FromPartition(new int3(2, 3, -1));
            Assert.AreEqual(new float3(64f, 96f, -32f), bounds.Min);
            Assert.AreEqual(new float3(96f, 128f, 0f), bounds.Max);

            Assert.IsTrue(_pages.TryAllocPages(700, 3, 0, bounds, out AllocInfo info));
            foreach (int index in info.GetPageIndices()){
                PageBounds stored = _pages.GetPageBounds(index);
                Assert.AreEqual(bounds.Min, stored.Min, $"page {index} min");
                Assert.AreEqual(bounds.Max, stored.Max, $"page {index} max");
            }

            _pages.Release(info);
            foreach (int index in info.GetPageIndices()){
                PageBounds stored = _pages.GetPageBounds(index);
                Assert.AreEqual(float3.zero, stored.Min, $"page {index} min after release");
                Assert.AreEqual(float3.zero, stored.Max, $"page {index} max after release");
            }
        }

        [Test]
        public void PageBounds_Is24Bytes(){
            Assert.AreEqual(24, Marshal.SizeOf<PageBounds>(), "must match the PageBounds struct in PageCulling.compute");
        }

        #endregion

        #region RenderBufferManager

        private void CreateBufferManager(){
            if (!SystemInfo.supportsComputeShaders) Assert.Ignore("compute shaders not supported on this device");
            _material = new Material(Shader.Find("Hidden/InternalErrorShader")){ name = "RenderDataTests" };
            _bufferManager = new RenderBufferManager(_material);
        }

        [Test]
        public void RenderBufferManager_SpillsIntoSecondBufferWhenFirstIsFull(){
            CreateBufferManager();
            AllocInfo first = _bufferManager.AllocBufferSpace(new int3(0, 0, 0), MaxPointsPerPartition);
            AllocInfo second = _bufferManager.AllocBufferSpace(new int3(1, 0, 0), MaxPointsPerPartition);
            AllocInfo third = _bufferManager.AllocBufferSpace(new int3(2, 0, 0), MaxPointsPerPartition);

            Assert.AreEqual(0, first.BufferIndex);
            Assert.AreEqual(0, second.BufferIndex);
            Assert.AreEqual(1, third.BufferIndex, "two full partitions fill 768 of 1024 pages; the third must spill");
            Assert.AreEqual(384, third.Count);
            Assert.IsNotNull(_bufferManager.GetBuffer(1));
            Assert.Throws<ArgumentOutOfRangeException>(() => _bufferManager.GetBuffer(2));
        }

        [Test]
        public void RenderBufferManager_ZeroPointsReleasesPartition(){
            CreateBufferManager();
            int3 pos = new(4, 2, 1);
            AllocInfo info = _bufferManager.AllocBufferSpace(pos, 300);
            Assert.AreEqual(2, info.Count);

            AllocInfo released = _bufferManager.AllocBufferSpace(pos, 0);
            Assert.IsTrue(released.IsEmpty);
            Assert.IsFalse(_bufferManager.ReleasePartition(pos), "partition was already released");

            AllocInfo again = _bufferManager.AllocBufferSpace(pos, 1);
            Assert.AreEqual(1, again.Count);
            Assert.IsTrue(_bufferManager.ReleasePartition(pos));
        }

        [Test]
        public void RenderBufferManager_NegativePoints_Throws(){
            CreateBufferManager();
            Assert.Throws<ArgumentOutOfRangeException>(() => _bufferManager.AllocBufferSpace(int3.zero, -1));
        }

        [Test]
        public void RenderBufferManager_ReallocatingPartition_FreesOldPages(){
            CreateBufferManager();
            int3 pos = new(1, 1, 1);
            _bufferManager.AllocBufferSpace(pos, MaxPointsPerPartition);
            _bufferManager.AllocBufferSpace(pos, MaxPointsPerPartition);
            AllocInfo other = _bufferManager.AllocBufferSpace(new int3(2, 2, 2), MaxPointsPerPartition);
            Assert.AreEqual(0, other.BufferIndex, "re-allocating the same partition must not leak its old pages");
        }

        #endregion

        #region read-back clamp

        [Test]
        public void PointCounts_AreClampedToMaxPointsPerPartition(){
            // A corrupt or overflowing GPU counter must never request more than a partition can hold.
            _counts = new NativeArray<uint>(3, Allocator.Persistent);
            _counts[0] = MaxPointsPerPartition + 1;
            _counts[1] = 5;
            _counts[2] = uint.MaxValue;

            int[] results = PointBuilderHandler.ToPointCounts(_counts);
            Assert.AreEqual(3, results.Length);
            Assert.AreEqual(5, results[1]);
            for (int i = 0; i < results.Length; i++)
                Assert.That(results[i], Is.InRange(0, MaxPointsPerPartition), $"count {i}");
        }

        #endregion

        #region AllocInfo

        [Test]
        public void AllocInfo_ToIndexAndCount_ListsPagesInOrder(){
            AllocInfo info = new(2);
            info.AddPage(new AllocInfo.AllocPage(5, 256));
            info.AddPage(new AllocInfo.AllocPage(9, 1));

            uint2[] pairs = info.ToIndexAndCount();
            Assert.AreEqual(2, pairs.Length);
            Assert.AreEqual(new uint2(5, 256), pairs[0]);
            Assert.AreEqual(new uint2(9, 1), pairs[1]);
            CollectionAssert.AreEqual(new[]{ 5, 9 }, info.GetPageIndices());
        }

        [Test]
        public void AllocInfo_Default_IsEmptyAndRejectsPages(){
            AllocInfo info = default;
            Assert.IsTrue(info.IsEmpty);
            Assert.AreEqual(0, info.Count);
            Assert.IsEmpty(info.ToIndexAndCount());
            Assert.Throws<InvalidOperationException>(() => info.AddPage(new AllocInfo.AllocPage(0, 1)));
        }

        #endregion
    }
}
