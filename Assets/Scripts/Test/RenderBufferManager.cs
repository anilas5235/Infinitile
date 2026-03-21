using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Runtime.Engine.Jobs.Meshing;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using static Runtime.Engine.Utils.VoxelRenderConstants;

namespace Test
{
    public class RenderBufferManager : IDisposable
    {
        private const int RenderBufferSize = PageSize * PagesPerBuffer;
        internal const int PageSize = 128;
        internal const int PagesPerBuffer = 512;

        private class BufferPage
        {
            public int3 PartitionPos;
            public int PointCount;

            public void Clear()
            {
                PointCount = 0;
                PartitionPos = default;
            }
        }

        private class RenderBuffer : IDisposable
        {
            private static readonly uint[] DefaultArgs = { 0u, 1u, 0u, 0u, 0u };
            private readonly GraphicsBuffer _buffer;
            private uint _totalValidPoints;
            private readonly BufferPage[] _pages;
            private readonly GraphicsBuffer _argsBuffer;
            private readonly GraphicsBuffer _pageStateBuffer;
            private readonly MaterialPropertyBlock _propertyBlock;

            private bool _stateBufferDirty;

            public GraphicsBuffer Buffer => _buffer;

            public RenderBuffer()
            {
                _buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, RenderBufferSize,
                    Marshal.SizeOf<Vertex>());
                _argsBuffer = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments, 5, sizeof(uint));
                _argsBuffer.SetData(DefaultArgs);
                _pageStateBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, PagesPerBuffer,
                    Marshal.SizeOf<uint>());

                _propertyBlock = new MaterialPropertyBlock();
                _propertyBlock.SetInteger(PointsPerPageNameID, PageSize);

                _pages = new BufferPage[PagesPerBuffer];
                for (int i = 0; i < _pages.Length; i++) _pages[i] = new BufferPage();
            }

            public void SetPage(int index, int3 partition, int count)
            {
                ValidOrThrow(index);
                if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));

                _totalValidPoints += (uint)count - (uint)_pages[index].PointCount;

                _pages[index].PartitionPos = partition;
                _pages[index].PointCount = count;
                _stateBufferDirty = true;
            }

            public (int3 partition, int pointCount) GetPage(int index)
            {
                ValidOrThrow(index);
                return (_pages[index].PartitionPos, _pages[index].PointCount);
            }

            public void ClearPage(int index)
            {
                ValidOrThrow(index);
                _totalValidPoints -= (uint)_pages[index].PointCount;
                _pages[index].Clear();
                _stateBufferDirty = true;
            }

            private void ValidOrThrow(int index)
            {
                if (index < 0 || index >= _pages.Length) throw new ArgumentOutOfRangeException(nameof(index));
            }

            public void Draw(Material mat, Camera cam)
            {
                _propertyBlock.SetBuffer(PointDataNameID, _buffer);
                _propertyBlock.SetBuffer(PageStatesNameID, _pageStateBuffer);
                _propertyBlock.SetInteger(PointsPerPageNameID, PageSize);
                Graphics.DrawProceduralIndirect(
                    mat,
                    new Bounds(Vector3.zero, Vector3.one * 100),
                    MeshTopology.Triangles,
                    _argsBuffer,
                    0,
                    cam,
                    _propertyBlock,
                    ShadowCastingMode.Off,
                    false
                );
            }

            public void RebuildBuffers()
            {
                if (_stateBufferDirty)
                {
                    uint[] pageCounts = new uint[_pages.Length];
                    for (int i = 0; i < _pages.Length; i++) pageCounts[i] = (uint)_pages[i].PointCount;

                    _pageStateBuffer.SetData(pageCounts);
                    uint[] tempArgs = DefaultArgs;
                    tempArgs[0] = _totalValidPoints * 6u;
                    _argsBuffer.SetData(tempArgs);
                    _stateBufferDirty = false;
                }
            }

            public void Dispose()
            {
                _buffer?.Dispose();
                _argsBuffer?.Dispose();
                _pageStateBuffer?.Dispose();
            }

            public bool TryFindFreePage(out int pageIndex)
            {
                for (int i = 0; i < _pages.Length; i++)
                {
                    if (_pages[i].PointCount != 0) continue;

                    pageIndex = i;
                    return true;
                }

                pageIndex = -1;
                return false;
            }
        }

        private readonly List<RenderBuffer> _buffers = new();
        private readonly Dictionary<int3, List<int2>> _partitionAllocations = new();
        private bool _isDisposed;
        private readonly Material _material;

        public RenderBufferManager(Material mat, int initialBuffers = 1)
        {
            _material = mat;
            for (int i = 0; i < initialBuffers; i++)
            {
                AddNewBuffer();
            }
        }

        public void Draw(Camera cam)
        {
            foreach (RenderBuffer renderBuffer in _buffers) renderBuffer.Draw(_material, cam);
        }

        public readonly struct AllocInfo
        {
            public readonly int BufferIndex;
            public readonly int PageIndex;
            public readonly int PointCount;
            public int Offset => PageIndex * PageSize;

            public AllocInfo(int bufferIndex, int pageIndex, int pointCount)
            {
                BufferIndex = bufferIndex;
                PageIndex = pageIndex;
                PointCount = pointCount;
            }
        }

        public List<AllocInfo> AllocBufferSpace(int3 partitionPos, int pointCount)
        {
            ThrowIfDisposed();

            if (pointCount < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(pointCount), "Point count must be >= 0.");
            }

            ReleasePartition(partitionPos);
            if (pointCount == 0)
            {
                return new List<AllocInfo>();
            }

            List<AllocInfo> allocations = new();
            List<int2> pageLocations = new();

            int remaining = pointCount;

            while (remaining > 0)
            {
                int count = math.min(PageSize, remaining);
                (int bufferIndex, int pageIndex) = ReservePage();
                _buffers[bufferIndex].SetPage(pageIndex, partitionPos, count);
                pageLocations.Add(new int2(bufferIndex, pageIndex));
                allocations.Add(new AllocInfo(bufferIndex, pageIndex, count));
                remaining -= count;
            }

            _partitionAllocations[partitionPos] = pageLocations;
            return allocations;
        }

        private (int BufferIndex, int PageIndex) ReservePage()
        {
            for (int i = 0; i < _buffers.Count; i++)
            {
                if (_buffers[i].TryFindFreePage(out int pageIndex))
                {
                    return (i, pageIndex);
                }
            }

            AddNewBuffer();
            int newBufferIndex = _buffers.Count - 1;
            if (!_buffers[newBufferIndex].TryFindFreePage(out int firstPageIndex))
            {
                throw new InvalidOperationException("Newly allocated render buffer has no free page.");
            }

            return (newBufferIndex, firstPageIndex);
        }

        public void ReleasePartition(int3 partitionPos)
        {
            if (!_partitionAllocations.TryGetValue(partitionPos, out List<int2> allocations))
            {
                return;
            }

            foreach (int2 location in allocations) _buffers[location.x].ClearPage(location.y);

            _partitionAllocations.Remove(partitionPos);
        }

        private void ThrowIfDisposed()
        {
            if (_isDisposed)
            {
                throw new ObjectDisposedException(nameof(RenderBufferManager));
            }
        }

        private void AddNewBuffer()
        {
            _buffers.Add(new RenderBuffer());
        }

        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            foreach (RenderBuffer buffer in _buffers)
            {
                buffer.Dispose();
            }

            _buffers.Clear();
            _partitionAllocations.Clear();
            _isDisposed = true;
        }

        public GraphicsBuffer GetBuffer(int bufferIndex)
        {
            ThrowIfDisposed();

            if (bufferIndex < 0 || bufferIndex >= _buffers.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(bufferIndex), "Invalid buffer index.");
            }

            return _buffers[bufferIndex].Buffer;
        }

        public void RebuildBuffers()
        {
            foreach (RenderBuffer buffer in _buffers) buffer.RebuildBuffers();
        }
    }
}