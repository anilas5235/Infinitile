using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine.Pool;

namespace Engine.Scripts.Render{
    /// <summary>
    ///     Represents allocation information for buffer pages, tracking which pages are allocated and how many points they
    ///     contain.
    /// </summary>
    public readonly struct AllocInfo{
        /// <summary>
        ///     The index of the render buffer this allocation belongs to.
        /// </summary>
        public readonly int BufferIndex;

        /// <summary>
        ///     PERF-04: initial capacity of a pooled page list. Lists that had to grow go back to the pool with their
        ///     larger capacity. Sizing every list for <see cref="CopyPointsHandler.MaxPagesPerPartition" /> pinned about
        ///     3 KB per list, although typical partitions need a few pages.
        /// </summary>
        private const int InitialPageCapacity = 16;

        /// <summary>
        ///     PERF-04: page lists are pooled, one was allocated per build. An own pool instead of
        ///     <see cref="ListPool{T}" />, because that pool's collection check scans every pooled list on each release.
        /// </summary>
        private static readonly ObjectPool<List<AllocPage>> PageListPool = new(
            static () => new List<AllocPage>(InitialPageCapacity), null, static list => list.Clear(), null,
            false, 64, 1 << 16
        );

        private readonly List<AllocPage> _pages;

        /// <summary>
        ///     Gets the number of pages allocated in this allocation.
        /// </summary>
        public int Count => _pages?.Count ?? 0;

        public bool IsEmpty => _pages == null || _pages.Count == 0;

        /// <summary>
        ///     Adds a page to this allocation.
        /// </summary>
        /// <param name="page">The page to add.</param>
        /// <exception cref="InvalidOperationException">Thrown when trying to add a page to an uninitialized allocation.</exception>
        public void AddPage(AllocPage page){
            if (_pages == null)
                throw new InvalidOperationException("Cannot add pages to an AllocInfo with no page list.");
            _pages.Add(page);
        }

        /// <summary>
        ///     Gets the indices of all allocated pages.
        /// </summary>
        /// <returns>An array of page indices.</returns>
        public int[] GetPageIndices(){
            int[] indices = new int[Count];
            for (int i = 0; i < indices.Length; i++) indices[i] = _pages[i].PageIndex;
            return indices;
        }

        /// <summary>Page at <paramref name="index" /> (0 .. <see cref="Count" /> - 1).</summary>
        public AllocPage GetPage(int index){
            return _pages[index];
        }

        /// <summary>
        ///     PERF-04: writes the (page index, point count) pairs into a caller-owned array instead of allocating
        ///     one per copy; returns the number of pairs written.
        /// </summary>
        public int CopyIndexAndCount(uint2[] target){
            int count = Count;
            for (int i = 0; i < count; i++) target[i] = new uint2((uint)_pages[i].PageIndex, (uint)_pages[i].PointCount);
            return count;
        }

        /// <summary>
        ///     Represents a single allocated page with its index and point count.
        /// </summary>
        public readonly struct AllocPage{
            /// <summary>
            ///     The index of the page within the buffer.
            /// </summary>
            public readonly int PageIndex;

            /// <summary>
            ///     The number of points stored in this page.
            /// </summary>
            public readonly int PointCount;

            /// <summary>
            ///     Initializes a new instance of the AllocPage struct.
            /// </summary>
            /// <param name="pageIndex">The index of the page.</param>
            /// <param name="pointCount">The number of points in the page.</param>
            public AllocPage(int pageIndex, int pointCount){
                PageIndex = pageIndex;
                PointCount = pointCount;
            }
        }

        /// <summary>
        ///     Initializes a new instance of the AllocInfo struct.
        /// </summary>
        /// <param name="bufferIndex">The index of the render buffer.</param>
        public AllocInfo(int bufferIndex){
            BufferIndex = bufferIndex;
            _pages = PageListPool.Get();
        }

        /// <summary>
        ///     PERF-04: fills the list pool with <paramref name="count" /> lists so allocations made during flight never
        ///     create a list.
        /// </summary>
        internal static void PrewarmPageLists(int count){
            List<AllocPage>[] lists = new List<AllocPage>[count];
            for (int i = 0; i < count; i++) lists[i] = PageListPool.Get();
            for (int i = 0; i < count; i++) PageListPool.Release(lists[i]);
        }

        /// <summary>
        ///     PERF-04: returns the page list to the pool. Called by the buffer manager when the allocation is
        ///     released; the struct must not be used afterwards.
        /// </summary>
        internal void Recycle(){
            if (_pages != null) PageListPool.Release(_pages);
        }

        /// <summary>
        ///     Converts the allocation information to an array of index-count pairs.
        /// </summary>
        /// <returns>An array of uint2 values where x is the page index and y is the point count.</returns>
        public uint2[] ToIndexAndCount(){
            if (IsEmpty) return Array.Empty<uint2>();
            uint2[] pairs = new uint2[Count];
            CopyIndexAndCount(pairs);
            return pairs;
        }
    }
}
