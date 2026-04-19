using System.Collections.Generic;
using System.Linq;
using Unity.Mathematics;

namespace Engine.Scripts.Render
{
    public readonly struct AllocInfo
    {
        public readonly int BufferIndex;
        private readonly List<AllocPage> _pages;
        public int Count => _pages?.Count ?? 0;
        
        public void AddPage(AllocPage page)
        {
            if (_pages == null) throw new System.InvalidOperationException("Cannot add pages to an AllocInfo with no page list.");
            _pages.Add(page);
        }
        
        public int[] GetPageIndices() => _pages.Select(p => p.PageIndex).ToArray();

        public readonly struct AllocPage
        {
            public readonly int PageIndex;
            public readonly int PointCount;

            public AllocPage(int pageIndex, int pointCount)
            {
                PageIndex = pageIndex;
                PointCount = pointCount;
            }
        }

        public AllocInfo(int bufferIndex)
        {
            BufferIndex = bufferIndex;
            _pages = new List<AllocPage>();
        }

        public uint2[] ToIndexAndCount()
        {
            return _pages.Select(p => new uint2((uint)p.PageIndex, (uint)p.PointCount)).ToArray();
        }
    }
}