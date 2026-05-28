using System.Collections.Generic;
using System.Linq;
using Unity.Mathematics;

namespace Engine.Scripts.Render
{
    /// <summary>
    /// Represents allocation information for buffer pages, tracking which pages are allocated and how many points they contain.
    /// </summary>
    public readonly struct AllocInfo
    {
        /// <summary>
        /// The index of the render buffer this allocation belongs to.
        /// </summary>
        public readonly int BufferIndex;
        private readonly List<AllocPage> _pages;
        
        /// <summary>
        /// Gets the number of pages allocated in this allocation.
        /// </summary>
        public int Count => _pages?.Count ?? 0;
        
        /// <summary>
        /// Adds a page to this allocation.
        /// </summary>
        /// <param name="page">The page to add.</param>
        /// <exception cref="System.InvalidOperationException">Thrown when trying to add a page to an uninitialized allocation.</exception>
        public void AddPage(AllocPage page)
        {
            if (_pages == null) throw new System.InvalidOperationException("Cannot add pages to an AllocInfo with no page list.");
            _pages.Add(page);
        }
        
        /// <summary>
        /// Gets the indices of all allocated pages.
        /// </summary>
        /// <returns>An array of page indices.</returns>
        public int[] GetPageIndices() => _pages.Select(p => p.PageIndex).ToArray();

        /// <summary>
        /// Represents a single allocated page with its index and point count.
        /// </summary>
        public readonly struct AllocPage
        {
            /// <summary>
            /// The index of the page within the buffer.
            /// </summary>
            public readonly int PageIndex;
            
            /// <summary>
            /// The number of points stored in this page.
            /// </summary>
            public readonly int PointCount;

            /// <summary>
            /// Initializes a new instance of the AllocPage struct.
            /// </summary>
            /// <param name="pageIndex">The index of the page.</param>
            /// <param name="pointCount">The number of points in the page.</param>
            public AllocPage(int pageIndex, int pointCount)
            {
                PageIndex = pageIndex;
                PointCount = pointCount;
            }
        }

        /// <summary>
        /// Initializes a new instance of the AllocInfo struct.
        /// </summary>
        /// <param name="bufferIndex">The index of the render buffer.</param>
        public AllocInfo(int bufferIndex)
        {
            BufferIndex = bufferIndex;
            _pages = new List<AllocPage>();
        }

        /// <summary>
        /// Converts the allocation information to an array of index-count pairs.
        /// </summary>
        /// <returns>An array of uint2 values where x is the page index and y is the point count.</returns>
        public uint2[] ToIndexAndCount()
        {
            return _pages.Select(p => new uint2((uint)p.PageIndex, (uint)p.PointCount)).ToArray();
        }
    }
}