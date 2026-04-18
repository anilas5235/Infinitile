using System;
using System.Collections.Generic;
using System.Linq;
using Engine.Scripts.Utils.Logger;
using Unity.Mathematics;
using UnityEngine;
using static Engine.Scripts.Utils.VoxelRenderConstants;

namespace Engine.Scripts.Render
{
    public class RenderBufferManager : IDisposable
    {
        private readonly List<RenderBuffer> _buffers = new();
        private readonly Material _material;
        private readonly Dictionary<int3, List<int2>> _partitionAllocations = new();
        private bool _isDisposed;

        public RenderBufferManager(Material mat, ComputeShader rebuildBufferShader, int initialBuffers = 1)
        {
            _material = mat;
            RebuildBufferShader = rebuildBufferShader;
            RebuildKernel = rebuildBufferShader.FindKernel("ReBuildIndexAndArgs");
            for (int i = 0; i < initialBuffers; i++) AddNewBuffer();
        }

        internal ComputeShader RebuildBufferShader { get; private set; }
        internal int RebuildKernel { get; private set; }

        public void Dispose()
        {
            if (_isDisposed) return;

            foreach (RenderBuffer buffer in _buffers) buffer.Dispose();

            _buffers.Clear();
            _partitionAllocations.Clear();
            _isDisposed = true;
        }

        public void Draw(Camera cam)
        {
            foreach (RenderBuffer renderBuffer in _buffers) renderBuffer.Draw(_material, cam);
        }

        public List<AllocInfo> AllocBufferSpace(int3 partitionPos, int pointCount)
        {
            ThrowIfDisposed();

            if (pointCount < 0) throw new ArgumentOutOfRangeException(nameof(pointCount), "Point count must be >= 0.");

            ReleasePartition(partitionPos);
            if (pointCount == 0) return new List<AllocInfo>();


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
                    throw new InvalidOperationException("Expected to find a free page but none were available.");

                AllocInfo allocInfo = new(bufferIndex, pageIndex, math.min(PointsPerPage, remainingPoints));

                buffer.SetPage(allocInfo);
                allocations.Add(allocInfo);
                remainingPoints -= PointsPerPage;
            }

            return allocations;
        }

        public void ReleasePartition(int3 partitionPos)
        {
            if (!_partitionAllocations.TryGetValue(partitionPos, out List<int2> allocations)) return;

            foreach (int2 location in allocations) _buffers[location.x].ClearPage(location.y);

            _partitionAllocations.Remove(partitionPos);
        }

        private void ThrowIfDisposed()
        {
            if (_isDisposed) throw new ObjectDisposedException(nameof(RenderBufferManager));
        }

        private int AddNewBuffer()
        {
            _buffers.Add(new RenderBuffer(this));
            VoxelEngineLogger.Info<RenderBufferManager>($"Added new RenderBuffer for {_material.name}. Total buffers: {_buffers.Count}");
            return _buffers.Count - 1;
        }

        public GraphicsBuffer GetBuffer(int bufferIndex)
        {
            ThrowIfDisposed();

            if (bufferIndex < 0 || bufferIndex >= _buffers.Count)
                throw new ArgumentOutOfRangeException(nameof(bufferIndex), "Invalid buffer index.");

            return _buffers[bufferIndex].Buffer;
        }

        public void RebuildBuffers()
        {
            foreach (RenderBuffer buffer in _buffers) buffer.RebuildBuffers();
        }
    }
}