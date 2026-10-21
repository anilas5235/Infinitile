using System;
using System.Collections.Generic;
using Engine.Scripts.Render;
using Engine.Scripts.Utils.Collections;
using NUnit.Framework;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using static Engine.Scripts.Utils.VoxelConstants;
using static Engine.Scripts.Utils.VoxelRenderConstants;
using Random = Unity.Mathematics.Random;

namespace Engine.Tests.EditMode{
    /// <summary>
    ///     §20.1: page bookkeeping of the shared interval store (<see cref="IntervalPages" />) and the decompress
    ///     kernels reading lists through the page table (<see cref="ChunkIntervalStore" />, BuildPrep / BuildPrepLight).
    /// </summary>
    public class IntervalStoreTests{
        private const int PageSize = IntervalPages.PageSize;

        #region IntervalPages

        [Test]
        public void NewList_TakesFreePagesLowestFirst(){
            IntervalPages pages = new(8, 4);
            IntervalRange range = default;
            Assert.IsTrue(pages.TryResize(ref range, 2 * PageSize + 1));
            Assert.IsTrue(range.IsCreated);
            Assert.AreEqual(new[]{ 0, 1, 2 }, PagesOf(pages, range));
            Assert.AreEqual(5, pages.FreePageCount);
            Assert.AreEqual(3, pages.PeakUsedPageCount);
        }

        [Test]
        public void Resize_KeepsThePagesItStillNeeds(){
            IntervalPages pages = new(8, 4);
            IntervalRange a = default, b = default;
            pages.TryResize(ref a, PageSize); // page 0
            pages.TryResize(ref b, PageSize); // page 1
            Assert.IsTrue(pages.TryResize(ref a, 3 * PageSize));
            Assert.AreEqual(new[]{ 0, 2, 3 }, PagesOf(pages, a), "a grows at its end");

            Assert.IsTrue(pages.TryResize(ref a, PageSize + 1));
            Assert.AreEqual(new[]{ 0, 2 }, PagesOf(pages, a), "a shrinks at its end");
            Assert.AreEqual(5, pages.FreePageCount);
            Assert.AreEqual(4, pages.PeakUsedPageCount);
        }

        [Test]
        public void Resize_WithoutEnoughFreePages_FailsAndKeepsTheList(){
            IntervalPages pages = new(4, 4);
            IntervalRange a = default, b = default;
            pages.TryResize(ref a, 3 * PageSize);
            IntervalRange before = a;
            Assert.IsFalse(pages.TryResize(ref b, 2 * PageSize), "a new list does not fit");
            Assert.IsFalse(b.IsCreated);
            Assert.IsFalse(pages.TryResize(ref a, 5 * PageSize), "a grown list does not fit");
            Assert.AreEqual(before, a);
            Assert.AreEqual(1, pages.FreePageCount);

            pages.AddPages(2);
            Assert.IsTrue(pages.TryResize(ref a, 5 * PageSize));
            Assert.AreEqual(new[]{ 0, 1, 2, 4, 5 }, PagesOf(pages, a), "added pages come before older free ones");
            Assert.AreEqual(6, pages.PageCount);
        }

        [Test]
        public void Release_FreesPagesAndSlot_AndIsANoOpOnNoList(){
            IntervalPages pages = new(4, 1);
            IntervalRange a = default, none = default;
            pages.TryResize(ref a, 4 * PageSize);
            pages.Release(ref a);
            Assert.IsFalse(a.IsCreated);
            Assert.AreEqual(4, pages.FreePageCount);
            pages.Release(ref none);
            Assert.AreEqual(4, pages.FreePageCount);

            IntervalRange again = default;
            Assert.IsTrue(pages.TryResize(ref again, 4 * PageSize));
            Assert.AreEqual(new[]{ 0, 1, 2, 3 }, PagesOf(pages, again), "released pages are reused in order");
        }

        [Test]
        public void MoreListsThanSlots_GrowsTheSlots(){
            IntervalPages pages = new(8, 2);
            IntervalRange[] lists = new IntervalRange[5];
            for (int i = 0; i < lists.Length; i++) Assert.IsTrue(pages.TryResize(ref lists[i], 1));
            Assert.GreaterOrEqual(pages.SlotCount, 5);
            for (int i = 0; i < lists.Length; i++) Assert.AreEqual(i, pages.GetPage(lists[i], 0));
        }

        [Test]
        public void RandomChurn_EveryPageHasAtMostOneOwner(){
            IntervalPages pages = new(64, 8);
            IntervalRange[] lists = new IntervalRange[16];
            Random random = new(201);
            for (int step = 0; step < 5000; step++){
                int i = random.NextInt(lists.Length);
                if (random.NextInt(4) == 0) pages.Release(ref lists[i]);
                else{
                    IntervalRange before = lists[i];
                    if (!pages.TryResize(ref lists[i], random.NextInt(1, 8 * PageSize)))
                        Assert.AreEqual(before, lists[i], "a failed resize keeps the list");
                }

                HashSet<int> owned = new();
                int used = 0;
                foreach (IntervalRange list in lists){
                    if (!list.IsCreated) continue;
                    foreach (int page in PagesOf(pages, list)){
                        Assert.IsTrue(page >= 0 && page < pages.PageCount, $"step {step}: page {page} out of range");
                        Assert.IsTrue(owned.Add(page), $"step {step}: page {page} owned twice");
                        used++;
                    }
                }

                Assert.AreEqual(pages.UsedPageCount, used, $"step {step}: used page count");
            }
        }

        private static int[] PagesOf(IntervalPages pages, IntervalRange range){
            int[] result = new int[IntervalPages.PagesFor(range.Length)];
            for (int i = 0; i < result.Length; i++) result[i] = pages.GetPage(range, i);
            return result;
        }

        #endregion

        #region decompress kernels

        [Test]
        public void BuildPrep_ThroughPageTable_EqualsTheLists_AlsoAfterGrowing(){
            ComputeShader shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(
                "Assets/Engine/Shaders/Urp/ComputeShaders/PointBuilder.compute");
            Assert.IsNotNull(shader, "PointBuilder.compute not found");

            // A small store (10 pages):
            // 1. the light lists (two pages each) are written first and never again, so their contents must survive
            //    the store growing (GPU copy);
            // 2. the voxel lists start as one page each, and the store grows;
            // 3. their real data then grows each list at its end, onto pages that are not next to its first one.
            const int lists = 4;
            ChunkIntervalStore store = new(10, 2 * lists, shader);
            Random random = new(2001);
            ushort[][] voxelValues = new ushort[lists][], lightValues = new ushort[lists][];
            IntervalRange[] voxelLists = new IntervalRange[lists], lightLists = new IntervalRange[lists];
            try{
                for (int i = 0; i < lists; i++){
                    lightValues[i] = RandomRuns(ref random, 900, ushort.MaxValue);
                    Upload(store, ref lightLists[i], lightValues[i]);
                }

                for (int i = 0; i < lists; i++) Upload(store, ref voxelLists[i], new ushort[VoxelsPerChunk]);
                Assert.Greater(store.GrowCount, 0, "the store should have grown");

                for (int i = 0; i < lists; i++){
                    voxelValues[i] = RandomRuns(ref random, 600 + 500 * i, 60);
                    Upload(store, ref voxelLists[i], voxelValues[i]);
                }

                bool scattered = false;
                foreach (IntervalRange list in voxelLists)
                    for (int p = 1; p < IntervalPages.PagesFor(list.Length); p++)
                        scattered |= store.Pages.GetPage(list, p) != store.Pages.GetPage(list, p - 1) + 1;
                Assert.IsTrue(scattered, "at least one list should span non-consecutive pages");

                for (int i = 0; i < lists; i++){
                    uint[] voxels = Decompress(shader, store, voxelLists[i], lightLists[i]);
                    for (int v = 0; v < VoxelsPerChunk; v++){
                        uint expected = voxelValues[i][v] | ((uint)lightValues[i][v] << 16);
                        if (voxels[v] != expected) Assert.Fail($"list {i}, voxel {v}: expected {expected:X8} but was {voxels[v]:X8}");
                    }
                }
            }
            finally{
                store.Dispose();
            }
        }

        /// <summary>A chunk of runs of 1…<paramref name="maxRun" /> voxels with values below <paramref name="valueRange" />.</summary>
        private static ushort[] RandomRuns(ref Random random, int runCount, int valueRange){
            ushort[] values = new ushort[VoxelsPerChunk];
            int averageRun = VoxelsPerChunk / runCount;
            int v = 0;
            while (v < VoxelsPerChunk){
                int run = Math.Min(random.NextInt(1, 2 * averageRun), VoxelsPerChunk - v);
                ushort value = (ushort)random.NextInt(valueRange);
                for (int k = 0; k < run; k++) values[v++] = value;
            }

            return values;
        }

        private static void Upload(ChunkIntervalStore store, ref IntervalRange range, ushort[] values){
            UnsafeIntervalList<ushort> list = new(64, Allocator.Temp);
            int start = 0;
            for (int v = 1; v <= values.Length; v++){
                if (v < values.Length && values[v] == values[start]) continue;
                list.AddInterval(values[start], v - start);
                start = v;
            }

            store.Upload(ref range, list);
            list.Dispose();
        }

        /// <summary>Runs BuildPrep and BuildPrepLight like <see cref="PointBuilderHandler" /> and reads the result.</summary>
        private static uint[] Decompress(ComputeShader shader, ChunkIntervalStore store, IntervalRange voxelList,
            IntervalRange lightList){
            uint[] table = new uint[2 * IntervalPages.MaxPagesPerList];
            store.WritePageTable(voxelList, table, 0);
            store.WritePageTable(lightList, table, IntervalPages.MaxPagesPerList);
            using GraphicsBuffer pageTable = new(GraphicsBuffer.Target.Structured, table.Length, sizeof(uint));
            using GraphicsBuffer target = new(GraphicsBuffer.Target.Structured, VoxelsPerChunk, sizeof(uint));
            pageTable.SetData(table);

            shader.SetInt(VoxelsPerChunkNameID, VoxelsPerChunk);
            Dispatch(shader, shader.FindKernel("BuildPrep"), store, pageTable, target, voxelList, 0);
            Dispatch(shader, shader.FindKernel("BuildPrepLight"), store, pageTable, target, lightList,
                IntervalPages.MaxPagesPerList);

            uint[] result = new uint[VoxelsPerChunk];
            target.GetData(result);
            return result;
        }

        private static void Dispatch(ComputeShader shader, int kernel, ChunkIntervalStore store, GraphicsBuffer pageTable,
            GraphicsBuffer target, IntervalRange list, int pageTableOffset){
            shader.SetBuffer(kernel, CompChunkNameID, store.Buffer);
            shader.SetBuffer(kernel, CompPagesNameID, pageTable);
            shader.SetBuffer(kernel, UnCompChunkNameID, target);
            shader.SetInt(CompPagesOffsetNameID, pageTableOffset);
            const int intervalsPerGroup = 64 * 8;
            shader.Dispatch(kernel, Math.Max(1, (list.IntervalCount + intervalsPerGroup - 1) / intervalsPerGroup), 1, 1);
        }

        #endregion
    }
}
