using System;
using System.Collections.Generic;
using Engine.Scripts.Settings;
using Engine.Scripts.Utils.Logger;
using Unity.Mathematics;
using UnityEngine;
using static Engine.Scripts.Utils.VoxelRenderConstants;

namespace Engine.Scripts.Render
{
    public class RenderBufferManager : IDisposable
    {
        private readonly List<RenderBuffer> _renderBuffers = new();
        private readonly Material _material;
        private readonly Dictionary<int3, AllocInfo> _partitionAllocations = new();
        private bool _isDisposed;
        private readonly RendererSettings _renderSettings;

        public RenderBufferManager(Material mat, ComputeShader rebuildBufferShader, RendererSettings renderSettings,
            int initialBuffers = 1)
        {
            _material = mat;
            _renderSettings = renderSettings;
            RebuildBufferShader = rebuildBufferShader;
            RebuildKernel = rebuildBufferShader.FindKernel("ReBuildIndexAndArgs");
            for (int i = 0; i < initialBuffers; i++) AddNewBuffer();
        }

        internal ComputeShader RebuildBufferShader { get; private set; }
        internal int RebuildKernel { get; private set; }

        public void Dispose()
        {
            if (_isDisposed) return;

            foreach (RenderBuffer buffer in _renderBuffers) buffer.Dispose();

            _renderBuffers.Clear();
            _partitionAllocations.Clear();
            _isDisposed = true;
        }

        public void Draw(Camera cam)
        {
            foreach (RenderBuffer renderBuffer in _renderBuffers) renderBuffer.Draw(_material, cam);
        }

        public AllocInfo AllocBufferSpace(int3 partitionPos, int pointCount)
        {
            ThrowIfDisposed();

            if (pointCount < 0) throw new ArgumentOutOfRangeException(nameof(pointCount), "Point count must be >= 0.");

            bool hasAlloc = _partitionAllocations.TryGetValue(partitionPos, out AllocInfo allocInfo);

            if (pointCount == 0)
            {
                if (hasAlloc) Release(allocInfo, partitionPos);
                return default;
            }

            int numPages = (int)math.ceil(pointCount / (float)PointsPerPage);

            if (hasAlloc) Release(allocInfo, partitionPos);
            AllocInfo allocation = AllocPages(pointCount, numPages);
            _partitionAllocations[partitionPos] = allocation;
            return allocation;
        }

        public bool ReleasePartition(int3 partitionPos)
        {
            ThrowIfDisposed();

            if (!_partitionAllocations.TryGetValue(partitionPos, out AllocInfo allocInfo)) return false;

            Release(allocInfo, partitionPos);
            return true;
        }

        private AllocInfo AllocPages(int pointCount, int numPages)
        {
            bool success = false;
            AllocInfo allocation = default;
            foreach (RenderBuffer rBuffer in _renderBuffers)
            {
                success = rBuffer.TryAllocPages(pointCount, numPages, out allocation);
                if (success) break;
            }

            if (!success) AddNewBuffer().TryAllocPages(pointCount, numPages, out allocation);

            return allocation;
        }

        private void Release(in AllocInfo allocInfo, in int3 partitionPos)
        {
            _renderBuffers[allocInfo.BufferIndex].ClearPage(allocInfo);
            _partitionAllocations.Remove(partitionPos);
        }

        private void ThrowIfDisposed()
        {
            if (_isDisposed) throw new ObjectDisposedException(nameof(RenderBufferManager));
        }

        private RenderBuffer AddNewBuffer()
        {
            RenderBuffer rBuffer = new(this, _renderBuffers.Count, _renderSettings);
            _renderBuffers.Add(rBuffer);
            VoxelEngineLogger.Info<RenderBufferManager>(
                $"Added new RenderBuffer for {_material.name}. Total buffers: {_renderBuffers.Count}");
            return rBuffer;
        }

        public GraphicsBuffer GetBuffer(int bufferIndex)
        {
            ThrowIfDisposed();

            if (bufferIndex < 0 || bufferIndex >= _renderBuffers.Count)
                throw new ArgumentOutOfRangeException(nameof(bufferIndex), "Invalid buffer index.");

            return _renderBuffers[bufferIndex].PointBuffer;
        }

        public void RebuildBuffers()
        {
            foreach (RenderBuffer buffer in _renderBuffers) buffer.RebuildBuffers();
        }
    }
}