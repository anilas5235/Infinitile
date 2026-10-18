using System;
using Unity.Collections;
using Unity.Mathematics;
using static Engine.Scripts.Utils.VoxelRenderConstants;

namespace Engine.Scripts.Render{
    /// <summary>
    ///     Pure page bookkeeping for one <see cref="RenderBuffer" />: which of the <see cref="PagesPerBuffer" /> pages
    ///     are free and how many points each allocated page holds. Holds no GPU resources, so it can be unit-tested
    ///     and benchmarked without a graphics device (TST-E17, PERF-02). Extracted from <see cref="RenderBuffer" />
    ///     without behaviour changes.
    /// </summary>
    internal sealed class PageAllocator : IDisposable{
        private NativeArray<uint> _countsPerPage;
        private NativeQueue<int> _freePages;

        /// <summary>
        ///     Creates an allocator with every page free.
        /// </summary>
        /// <param name="allocator">Allocator for the internal containers (the engine uses <see cref="Allocator.Domain" />).</param>
        internal PageAllocator(Allocator allocator){
            _countsPerPage = new NativeArray<uint>(PagesPerBuffer, allocator);
            _freePages = new NativeQueue<int>(allocator);
            for (int i = 0; i < PagesPerBuffer; i++) _freePages.Enqueue(i);
            TotalValidPoints = 0;
        }

        /// <summary>Number of pages that are currently free.</summary>
        internal int FreePages => _freePages.Count;

        /// <summary>True when no page is allocated.</summary>
        internal bool IsEmpty => _freePages.Count == PagesPerBuffer;

        /// <summary>Sum of the point counts of all allocated pages.</summary>
        internal uint TotalValidPoints{ get; private set; }

        /// <summary>Point count per page, indexed by page; 0 means the page is free.</summary>
        internal NativeArray<uint> CountsPerPage => _countsPerPage;

        public void Dispose(){
            _countsPerPage.Dispose();
            _freePages.Dispose();
        }

        /// <summary>
        ///     Attempts to allocate <paramref name="numPages" /> pages for <paramref name="pointCount" /> points.
        ///     Pages are filled in order with up to <see cref="PointsPerPage" /> points each.
        /// </summary>
        /// <param name="pointCount">The total number of points to allocate space for.</param>
        /// <param name="numPages">The number of pages to allocate (must be at least 1).</param>
        /// <param name="bufferIndex">Buffer index stored in the resulting <see cref="AllocInfo" />.</param>
        /// <param name="allocation">The allocation info if successful, an empty allocation otherwise.</param>
        /// <returns>True if allocation was successful, false if not enough free pages.</returns>
        internal bool TryAllocPages(int pointCount, int numPages, int bufferIndex, out AllocInfo allocation){
            allocation = new AllocInfo(bufferIndex);
            if (numPages <= 0)
                throw new ArgumentOutOfRangeException(nameof(numPages), "Must allocate at least one page.");
            if (numPages > FreePages) return false;

            int remainingPoints = pointCount;
            for (int i = 0; i < numPages; i++){
                int pageIndex = _freePages.Dequeue();
                int pointsForPage = math.min(PointsPerPage, remainingPoints);
                allocation.AddPage(new AllocInfo.AllocPage(pageIndex, pointsForPage));
                SetPageCount(pageIndex, (uint)pointsForPage);

                remainingPoints -= PointsPerPage;
            }

            return true;
        }

        /// <summary>
        ///     Frees all pages of the allocation. Pages that are already free are skipped, so releasing twice is a no-op.
        /// </summary>
        /// <param name="allocInfo">The allocation whose pages are released.</param>
        /// <exception cref="ArgumentOutOfRangeException">Thrown if a page index is outside the buffer.</exception>
        internal void Release(in AllocInfo allocInfo){
            if (allocInfo.Count == 0) return;

            foreach (int index in allocInfo.GetPageIndices()){
                ValidOrThrow(index);
                if (IsPageFree(index)) continue;

                SetPageCount(index, 0);
                _freePages.Enqueue(index);
            }
        }

        /// <summary>Point count stored for a page (0 = free).</summary>
        internal uint GetPageCount(int index){
            ValidOrThrow(index);
            return _countsPerPage[index];
        }

        private bool IsPageFree(int index){
            return _countsPerPage[index] == 0;
        }

        private void SetPageCount(int index, uint count){
            TotalValidPoints -= _countsPerPage[index];
            TotalValidPoints += count;
            _countsPerPage[index] = count;
        }

        private static void ValidOrThrow(int index){
            if (index is < 0 or >= PagesPerBuffer) throw new ArgumentOutOfRangeException(nameof(index));
        }
    }
}
