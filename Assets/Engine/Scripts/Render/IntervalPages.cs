using System;
using static Engine.Scripts.Utils.VoxelConstants;
using static Engine.Scripts.Utils.VoxelRenderConstants;

namespace Engine.Scripts.Render{
    /// <summary>
    ///     Handle of one chunk's interval list in the <see cref="ChunkIntervalStore" />. The default value is "no list".
    /// </summary>
    internal readonly struct IntervalRange : IEquatable<IntervalRange>{
        private readonly int _slotPlusOne;

        /// <summary>Elements of the list including the header element 0 (interval count, last end index).</summary>
        internal readonly int Length;

        internal IntervalRange(int slot, int length){
            _slotPlusOne = slot + 1;
            Length = length;
        }

        /// <summary>Index of the list's page table in <see cref="IntervalPages" />.</summary>
        internal int Slot => _slotPlusOne - 1;

        internal bool IsCreated => _slotPlusOne != 0;

        /// <summary>Intervals of the list, without the header.</summary>
        internal int IntervalCount => Length - 1;

        public bool Equals(IntervalRange other){
            return _slotPlusOne == other._slotPlusOne && Length == other.Length;
        }

        public override bool Equals(object obj){
            return obj is IntervalRange other && Equals(other);
        }

        public override int GetHashCode(){
            return HashCode.Combine(_slotPlusOne, Length);
        }
    }

    /// <summary>
    ///     Page bookkeeping of the <see cref="ChunkIntervalStore" />. The store is split into pages of
    ///     <see cref="PageSize" /> elements; a list owns the pages for its length, in order, at any place in the store.
    ///     Pages never need compaction, and a list that grows or shrinks only adds or frees pages at its end. Holds no
    ///     GPU resources, so it can be unit-tested without a graphics device.
    /// </summary>
    internal sealed class IntervalPages{
        /// <summary>Elements (uint2) per page, 512; the kernels address pages with COMP_PAGE_SHIFT.</summary>
        internal const int PageSize = 1 << CompPageShift;

        /// <summary>Pages of the longest possible list: one interval per voxel plus the header.</summary>
        internal const int MaxPagesPerList = (VoxelsPerChunk + 1 + PageSize - 1) / PageSize;

        private int[] _slotPages; // page of each list page: slot * MaxPagesPerList + i
        private int[] _freeSlots;
        private int _freeSlotCount;
        private int[] _freePages;
        private int _freePageCount;

        /// <summary>Creates the bookkeeping with every page and slot free.</summary>
        /// <param name="pageCount">Pages in the store.</param>
        /// <param name="slotCount">Lists that fit without growing the slot arrays.</param>
        internal IntervalPages(int pageCount, int slotCount){
            if (pageCount < 1) throw new ArgumentOutOfRangeException(nameof(pageCount));
            if (slotCount < 1) throw new ArgumentOutOfRangeException(nameof(slotCount));
            _slotPages = new int[slotCount * MaxPagesPerList];
            _freeSlots = new int[slotCount];
            for (int i = 0; i < slotCount; i++) _freeSlots[i] = slotCount - 1 - i; // popped in ascending order
            _freeSlotCount = slotCount;
            _freePages = Array.Empty<int>();
            AddPages(pageCount);
        }

        /// <summary>Pages in the store.</summary>
        internal int PageCount{ get; private set; }

        internal int FreePageCount => _freePageCount;

        internal int UsedPageCount => PageCount - _freePageCount;

        /// <summary>Most pages in use at once since creation.</summary>
        internal int PeakUsedPageCount{ get; private set; }

        /// <summary>Lists that currently fit without growing the slot arrays.</summary>
        internal int SlotCount => _freeSlots.Length;

        /// <summary>Pages a list of <paramref name="length" /> elements needs.</summary>
        internal static int PagesFor(int length){
            return (length + PageSize - 1) / PageSize;
        }

        /// <summary>
        ///     Gives <paramref name="range" /> the pages for <paramref name="length" /> elements. A list that already
        ///     exists keeps its pages and gains or frees pages at its end; a new list takes a free slot.
        /// </summary>
        /// <returns>False, with <paramref name="range" /> unchanged, if there are not enough free pages.</returns>
        internal bool TryResize(ref IntervalRange range, int length){
            if (length is < 1 or > MaxPagesPerList * PageSize) throw new ArgumentOutOfRangeException(nameof(length));
            int need = PagesFor(length);
            int have = range.IsCreated ? PagesFor(range.Length) : 0;
            if (need - have > _freePageCount) return false;

            int slot = range.IsCreated ? range.Slot : TakeSlot();
            int first = slot * MaxPagesPerList;
            for (int i = have; i < need; i++) _slotPages[first + i] = _freePages[--_freePageCount];
            for (int i = need; i < have; i++) _freePages[_freePageCount++] = _slotPages[first + i];
            PeakUsedPageCount = Math.Max(PeakUsedPageCount, UsedPageCount);
            range = new IntervalRange(slot, length);
            return true;
        }

        /// <summary>Frees the list's pages and slot; releasing "no list" is a no-op.</summary>
        internal void Release(ref IntervalRange range){
            if (!range.IsCreated) return;
            int first = range.Slot * MaxPagesPerList;
            for (int i = PagesFor(range.Length) - 1; i >= 0; i--) _freePages[_freePageCount++] = _slotPages[first + i];
            _freeSlots[_freeSlotCount++] = range.Slot;
            range = default;
        }

        /// <summary>Store page of page <paramref name="index" /> of the list.</summary>
        internal int GetPage(in IntervalRange range, int index){
            if (!range.IsCreated) throw new ArgumentException("No list.", nameof(range));
            if ((uint)index >= (uint)PagesFor(range.Length)) throw new ArgumentOutOfRangeException(nameof(index));
            return _slotPages[range.Slot * MaxPagesPerList + index];
        }

        /// <summary>
        ///     Adds <paramref name="count" /> free pages at the end of the store (after the store buffer grew). They are
        ///     handed out before the older free pages, lowest first.
        /// </summary>
        internal void AddPages(int count){
            if (count < 1) throw new ArgumentOutOfRangeException(nameof(count));
            int total = PageCount + count;
            if (_freePages.Length < total) Array.Resize(ref _freePages, total);
            for (int page = total - 1; page >= PageCount; page--) _freePages[_freePageCount++] = page;
            PageCount = total;
        }

        private int TakeSlot(){
            if (_freeSlotCount == 0){
                // Rare: more lists than the store was sized for. Doubles the slot arrays (allocates).
                int old = _freeSlots.Length;
                Array.Resize(ref _slotPages, 2 * old * MaxPagesPerList);
                Array.Resize(ref _freeSlots, 2 * old);
                for (int slot = 2 * old - 1; slot >= old; slot--) _freeSlots[_freeSlotCount++] = slot;
            }

            return _freeSlots[--_freeSlotCount];
        }
    }
}
