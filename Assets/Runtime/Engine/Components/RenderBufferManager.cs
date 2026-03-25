using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Runtime.Engine.Jobs.Meshing;
using Runtime.Engine.Utils.Logger;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using static Runtime.Engine.Utils.VoxelRenderConstants;

namespace Runtime.Engine.Components
{
    public class RenderBufferManager : IDisposable
    {
        private const int RenderBufferSize = PointsPerPage * PagesPerBuffer;
        internal const int PointsPerPage = 256;
        internal const int PagesPerBuffer = 2048;

        private class RenderBuffer : IDisposable
        {
            private static readonly uint[] DefaultArgs = { 0u, 1u, 0u, 0u, 0u };
            private readonly GraphicsBuffer _buffer;
            private uint _totalValidPoints;
            private readonly GraphicsBuffer _argsBuffer;
            private readonly GraphicsBuffer _indexBuffer;
            private readonly MaterialPropertyBlock _propertyBlock;
            
            private NativeArray<uint> _indices;
            private NativeArray<uint> _pagesPointCounts;

            private bool _stateBufferDirty;
            private int _usedPageCount;

            private int _lastFreePageIndex;

            public GraphicsBuffer Buffer => _buffer;
            public int FreePages => PagesPerBuffer - _usedPageCount;

            public RenderBuffer()
            {
                _buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, RenderBufferSize,
                    Marshal.SizeOf<Vertex>());
                _argsBuffer = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments, 5, sizeof(uint));
                _argsBuffer.SetData(DefaultArgs);
                _indexBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, RenderBufferSize,
                    Marshal.SizeOf<uint>());
                _indices = new NativeArray<uint>(RenderBufferSize, Allocator.Domain);

                _propertyBlock = new MaterialPropertyBlock();

                _pagesPointCounts = new NativeArray<uint>(PagesPerBuffer, Allocator.Domain);
                _lastFreePageIndex = 0;
            }
            
            private bool IsPageFree(int index) => _pagesPointCounts[index] == 0;
            private bool IsPageNotFree(int index) => _pagesPointCounts[index] != 0;

            private void SetPageCount(int index, uint count)
            {
                _totalValidPoints -= _pagesPointCounts[index];
                _totalValidPoints += count;
                _pagesPointCounts[index] = count;
            }

            internal void SetPage(in AllocInfo allocInfo)
            {
                int index = allocInfo.PageIndex;
                int count = allocInfo.PointCount;
                ValidOrThrow(index);
                if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
                if (IsPageFree(index)) _usedPageCount++;

                SetPageCount(index, (uint)count);
                _stateBufferDirty = true;
            }

            public void ClearPage(int index)
            {
                ValidOrThrow(index);
                if (IsPageNotFree(index)) _usedPageCount--;
                SetPageCount(index, 0);
                _stateBufferDirty = true;
            }

            private static void ValidOrThrow(int index)
            {
                if (index is < 0 or >= PagesPerBuffer) throw new ArgumentOutOfRangeException(nameof(index));
            }

            public void Draw(Material mat, Camera cam)
            {
                if (_usedPageCount == 0) return;
                _propertyBlock.SetBuffer(PointDataNameID, _buffer);
                _propertyBlock.SetBuffer(IndexBufferNameID, _indexBuffer);
                Graphics.DrawProceduralIndirect(
                    mat,
                    new Bounds(Vector3.zero, Vector3.one * 100000),
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
                double startTime = Time.realtimeSinceStartupAsDouble;
                if (!_stateBufferDirty) return;
                
                uint offset = 0;
                int writeIndex = 0;

                for (int pageIndex = 0; pageIndex < PagesPerBuffer; pageIndex++)
                {
                    uint count = _pagesPointCounts[pageIndex];
                    for (int i = 0; i < count; i++)
                    {
                        _indices[writeIndex++] = ++offset;
                    }

                    offset += PointsPerPage - count;
                }

                _indexBuffer.SetData(_indices);
                VoxelEngineLogger.Info<RenderBuffer>($"RebuildBuffers: Updated index buffer with {_totalValidPoints} valid points in {(Time.realtimeSinceStartupAsDouble - startTime)*1000:F4} ms.");
                uint[] tempArgs = DefaultArgs;
                tempArgs[0] = _totalValidPoints * 6u;
                VoxelEngineLogger.Info<RenderBuffer>(
                    $"Rebuilding RenderBuffer. Total Valid Points: {_totalValidPoints}, Total Pages Used: {_usedPageCount}");
                VoxelEngineLogger.Info<RenderBuffer>($"Args: {string.Join(", ", tempArgs)}");
                _argsBuffer.SetData(tempArgs);
                _stateBufferDirty = false;
            }

            public void Dispose()
            {
                _buffer?.Dispose();
                _argsBuffer?.Dispose();
                _indexBuffer?.Dispose();
                _indices.Dispose();
            }

            public bool TryFindFreePage(out int pageIndex)
            {
                for (int i = _lastFreePageIndex; i < PagesPerBuffer; i++)
                {
                    int index = i % PagesPerBuffer;
                    if (IsPageNotFree(index)) continue;

                    pageIndex = index;
                    _lastFreePageIndex = index;
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

        public int RemainingPages => _buffers.Sum(b => b.FreePages);

        public void Draw(Camera cam)
        {
            foreach (RenderBuffer renderBuffer in _buffers) renderBuffer.Draw(_material, cam);
        }

        public readonly struct AllocInfo
        {
            public readonly int BufferIndex;
            public readonly int PageIndex;
            public readonly int PointCount;

            public AllocInfo(int bufferIndex, int pageIndex, int pointCount)
            {
                BufferIndex = bufferIndex;
                PageIndex = pageIndex;
                PointCount = pointCount;
            }

            public uint2 ToIndexAndCount() =>
                new((uint)PageIndex, (uint)PointCount);

            public int2 ToBufferAndPageIndex() =>
                new(BufferIndex, PageIndex);
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


            List<AllocInfo> allocations = AllocPages(pointCount);
            List<int2> pageLocations = allocations.Select(alloc => alloc.ToBufferAndPageIndex()).ToList();

            _partitionAllocations[partitionPos] = pageLocations;
            return allocations;
        }

        private List<AllocInfo> AllocPages(int pointCount)
        {
            int numPages = (int)math.ceil(pointCount / (float)PointsPerPage);
            int bufferIndex = -1;
            for (int i = 0; i < _buffers.Count; i++)
            {
                RenderBuffer rb = _buffers[i];
                if (rb.FreePages < numPages) continue;
                bufferIndex = i;
                break;
            }

            if (bufferIndex < 0) bufferIndex = AddNewBuffer();

            List<AllocInfo> allocations = new();
            int remainingPoints = pointCount;
            RenderBuffer buffer = _buffers[bufferIndex];
            for (int i = 0; i < numPages; i++)
            {
                if (!buffer.TryFindFreePage(out int pageIndex))
                {
                    throw new InvalidOperationException("Expected to find a free page but none were available.");
                }

                AllocInfo allocInfo = new(bufferIndex, pageIndex, math.min(PointsPerPage, remainingPoints));

                buffer.SetPage(allocInfo);
                allocations.Add(allocInfo);
                remainingPoints -= PointsPerPage;
            }

            return allocations;
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

        private int AddNewBuffer()
        {
            _buffers.Add(new RenderBuffer());
            VoxelEngineLogger.Info<RenderBufferManager>($"Added new RenderBuffer. Total buffers: {_buffers.Count}");
            return _buffers.Count - 1;
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