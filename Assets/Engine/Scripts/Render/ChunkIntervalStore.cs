using System;
using System.Runtime.InteropServices;
using Engine.Scripts.Utils.Collections;
using Engine.Scripts.Utils.Logger;
using Unity.Mathematics;
using UnityEngine;
using static Engine.Scripts.Utils.VoxelRenderConstants;
using static UnityEngine.GraphicsBuffer;

namespace Engine.Scripts.Render{
    /// <summary>
    ///     One structured GPU buffer for the compressed interval lists of every loaded chunk, voxel ids and light
    ///     alike. Each list is a range of pages (<see cref="IntervalPages" />); the decompress kernels find the pages
    ///     of a list through a page table (<see cref="WritePageTable" />). Chunk uploads create no managed objects, and
    ///     memory follows the actual list lengths instead of a fixed length per chunk.
    /// </summary>
    internal sealed class ChunkIntervalStore : IDisposable{
        private const int CopyThreads = 64;
        private const int MaxCopyGroups = 65535;

        private readonly ComputeShader _copyShader;
        private readonly int _copyKernel;
        private readonly IntervalPages _pages;
        private uint2[] _staging = Array.Empty<uint2>(); // reused upload staging

        /// <summary>Creates the store with every page free.</summary>
        /// <param name="pageCount">Initial pages (<see cref="IntervalPages.PageSize" /> elements each).</param>
        /// <param name="slotCount">Lists the bookkeeping is sized for.</param>
        /// <param name="pointBuilder">PointBuilder.compute; its CopyIntervals kernel copies the store when it grows.</param>
        internal ChunkIntervalStore(int pageCount, int slotCount, ComputeShader pointBuilder){
            _pages = new IntervalPages(pageCount, slotCount);
            _copyShader = pointBuilder;
            _copyKernel = pointBuilder.FindKernel("CopyIntervals");
            Buffer = CreateBuffer(pageCount);
        }

        /// <summary>The store buffer (uint2 per element). Replaced when the store grows; bind it per dispatch.</summary>
        internal GraphicsBuffer Buffer{ get; private set; }

        internal IntervalPages Pages => _pages;

        /// <summary>Times the store buffer was replaced by a larger one.</summary>
        internal int GrowCount{ get; private set; }

        public void Dispose(){
            Buffer?.Dispose();
            Buffer = null;
        }

        /// <summary>
        ///     Writes an interval list to <paramref name="range" />'s pages, resizing it as needed. Element 0 holds
        ///     (interval count, last end index), followed by (value, exclusive end) per interval.
        /// </summary>
        internal void Upload(ref IntervalRange range, UnsafeIntervalList<ushort> data){
            int intervalCount = data.CompressedLength;
            int length = intervalCount + 1;

            if (_staging.Length < length) _staging = new uint2[math.max(length, _staging.Length * 2)];
            uint2[] staging = _staging;
            int i = 1;
            foreach (UnsafeIntervalList<ushort>.Node n in data.Internal)
                staging[i++] = new uint2(n.Value, (uint)n.Count);
            staging[0] = new uint2((uint)intervalCount, staging[intervalCount].y);

            if (!_pages.TryResize(ref range, length)){
                Grow(IntervalPages.PagesFor(length));
                if (!_pages.TryResize(ref range, length)) throw new InvalidOperationException("Interval store full after growing.");
            }

            // One SetData per run of consecutive store pages; a new list usually gets a single run.
            int pageCount = IntervalPages.PagesFor(length);
            int runStart = 0;
            for (int p = 1; p <= pageCount; p++){
                if (p < pageCount && _pages.GetPage(range, p) == _pages.GetPage(range, p - 1) + 1) continue;
                int start = runStart * IntervalPages.PageSize;
                int count = math.min(p * IntervalPages.PageSize, length) - start;
                Buffer.SetData(staging, start, _pages.GetPage(range, runStart) * IntervalPages.PageSize, count);
                runStart = p;
            }
        }

        /// <summary>Frees the list's pages; releasing "no list" is a no-op.</summary>
        internal void Release(ref IntervalRange range){
            _pages.Release(ref range);
        }

        /// <summary>
        ///     Writes the store page of each page of <paramref name="range" /> to <paramref name="table" />.
        /// </summary>
        /// <returns>The number of entries written.</returns>
        internal int WritePageTable(in IntervalRange range, uint[] table, int offset){
            int pageCount = IntervalPages.PagesFor(range.Length);
            for (int p = 0; p < pageCount; p++) table[offset + p] = (uint)_pages.GetPage(range, p);
            return pageCount;
        }

        /// <summary>
        ///     Rare: replaces the buffer by one with at least <paramref name="minFreePages" /> more free pages and copies
        ///     the old contents on the GPU. Allocates a managed buffer object.
        /// </summary>
        private void Grow(int minFreePages){
            int added = math.max(minFreePages, _pages.PageCount / 2);
            GraphicsBuffer grown = CreateBuffer(_pages.PageCount + added);

            int count = _pages.PageCount * IntervalPages.PageSize;
            int groups = math.min((count + CopyThreads - 1) / CopyThreads, MaxCopyGroups);
            _copyShader.SetBuffer(_copyKernel, CopySourceNameID, Buffer);
            _copyShader.SetBuffer(_copyKernel, CopyTargetNameID, grown);
            _copyShader.SetInt(CopyCountNameID, count);
            _copyShader.SetInt(CopyStrideNameID, groups * CopyThreads);
            _copyShader.Dispatch(_copyKernel, groups, 1, 1);

            Buffer.Dispose();
            Buffer = grown;
            _pages.AddPages(added);
            GrowCount++;
            VoxelEngineLogger.Info<ChunkIntervalStore>(
                $"Interval store grown to {_pages.PageCount} pages ({_pages.PageCount * IntervalPages.PageSize} intervals)");
        }

        private static GraphicsBuffer CreateBuffer(int pageCount){
            return new GraphicsBuffer(Target.Structured, pageCount * IntervalPages.PageSize, Marshal.SizeOf<uint2>());
        }
    }
}
