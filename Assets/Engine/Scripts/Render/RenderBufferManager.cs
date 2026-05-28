using System;
using System.Collections.Generic;
using Engine.Scripts.Settings;
using Engine.Scripts.Utils.Logger;
using Unity.Mathematics;
using UnityEngine;
using static Engine.Scripts.Utils.VoxelRenderConstants;

namespace Engine.Scripts.Render
{
    /// <summary>
    /// Manages multiple render buffers for a specific material, handling page allocation and rendering.
    /// Automatically creates new buffers when capacity is exceeded.
    /// </summary>
    public class RenderBufferManager : IDisposable
    {
        private readonly List<RenderBuffer> _renderBuffers = new();
        private readonly Material _material;
        private readonly Dictionary<int3, AllocInfo> _partitionAllocations = new();
        private bool _isDisposed;
        private readonly RendererSettings _renderSettings;

        /// <summary>
        /// Initializes a new instance of the RenderBufferManager class.
        /// </summary>
        /// <param name="mat">The material to use for rendering.</param>
        /// <param name="rebuildBufferShader">The compute shader for rebuilding buffers.</param>
        /// <param name="renderSettings">The renderer settings.</param>
        /// <param name="initialBuffers">The initial number of buffers to create. Defaults to 1.</param>
        public RenderBufferManager(Material mat, ComputeShader rebuildBufferShader, RendererSettings renderSettings,
            int initialBuffers = 1)
        {
            _material = mat;
            _renderSettings = renderSettings;
            RebuildBufferShader = rebuildBufferShader;
            RebuildKernel = rebuildBufferShader.FindKernel("ReBuildIndexAndArgs");
            for (int i = 0; i < initialBuffers; i++) AddNewBuffer();
        }

        /// <summary>
        /// Gets the compute shader used for rebuilding buffers.
        /// </summary>
        internal ComputeShader RebuildBufferShader { get; private set; }
        
        /// <summary>
        /// Gets the kernel index for the rebuild operation.
        /// </summary>
        internal int RebuildKernel { get; private set; }

        /// <summary>
        /// Releases all GPU resources held by this manager.
        /// </summary>
        public void Dispose()
        {
            if (_isDisposed) return;

            foreach (RenderBuffer buffer in _renderBuffers) buffer.Dispose();

            _renderBuffers.Clear();
            _partitionAllocations.Clear();
            _isDisposed = true;
        }

        /// <summary>
        /// Draws all render buffers using the specified camera.
        /// </summary>
        /// <param name="cam">The camera to render with.</param>
        public void Draw(Camera cam)
        {
            foreach (RenderBuffer renderBuffer in _renderBuffers) renderBuffer.Draw(_material, cam);
        }

        /// <summary>
        /// Allocates buffer space for a partition's points.
        /// </summary>
        /// <param name="partitionPos">The position of the partition.</param>
        /// <param name="pointCount">The number of points to allocate space for.</param>
        /// <returns>Allocation information, or default if pointCount is 0.</returns>
        /// <exception cref="ObjectDisposedException">Thrown if this manager has been disposed.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown if pointCount is negative.</exception>
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

        /// <summary>
        /// Releases the buffer space allocated to a partition.
        /// </summary>
        /// <param name="partitionPos">The position of the partition.</param>
        /// <returns>True if a partition was released, false if no allocation existed.</returns>
        /// <exception cref="ObjectDisposedException">Thrown if this manager has been disposed.</exception>
        public bool ReleasePartition(int3 partitionPos)
        {
            ThrowIfDisposed();

            if (!_partitionAllocations.TryGetValue(partitionPos, out AllocInfo allocInfo)) return false;

            Release(allocInfo, partitionPos);
            return true;
        }

        /// <summary>
        /// Attempts to allocate pages in existing buffers or creates new ones if needed.
        /// </summary>
        /// <param name="pointCount">The total number of points.</param>
        /// <param name="numPages">The number of pages needed.</param>
        /// <returns>The allocation info for the allocated pages.</returns>
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

        /// <summary>
        /// Releases an allocation from its buffer and removes the partition entry.
        /// </summary>
        /// <param name="allocInfo">The allocation to release.</param>
        /// <param name="partitionPos">The partition position.</param>
        private void Release(in AllocInfo allocInfo, in int3 partitionPos)
        {
            _renderBuffers[allocInfo.BufferIndex].ClearPage(allocInfo);
            _partitionAllocations.Remove(partitionPos);
        }

        /// <summary>
        /// Throws if this manager has been disposed.
        /// </summary>
        /// <exception cref="ObjectDisposedException">Thrown if disposed.</exception>
        private void ThrowIfDisposed()
        {
            if (_isDisposed) throw new ObjectDisposedException(nameof(RenderBufferManager));
        }

        /// <summary>
        /// Creates and adds a new render buffer to this manager.
        /// </summary>
        /// <returns>The newly created render buffer.</returns>
        private RenderBuffer AddNewBuffer()
        {
            RenderBuffer rBuffer = new(this, _renderBuffers.Count, _renderSettings);
            _renderBuffers.Add(rBuffer);
            VoxelEngineLogger.Info<RenderBufferManager>(
                $"Added new RenderBuffer for {_material.name}. Total buffers: {_renderBuffers.Count}");
            return rBuffer;
        }

        /// <summary>
        /// Gets the point buffer at the specified index.
        /// </summary>
        /// <param name="bufferIndex">The index of the buffer.</param>
        /// <returns>The graphics buffer at the specified index.</returns>
        /// <exception cref="ObjectDisposedException">Thrown if disposed.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown if bufferIndex is invalid.</exception>
        public GraphicsBuffer GetBuffer(int bufferIndex)
        {
            ThrowIfDisposed();

            if (bufferIndex < 0 || bufferIndex >= _renderBuffers.Count)
                throw new ArgumentOutOfRangeException(nameof(bufferIndex), "Invalid buffer index.");

            return _renderBuffers[bufferIndex].PointBuffer;
        }

        /// <summary>
        /// Rebuilds all render buffers that have pending state changes.
        /// </summary>
        public void RebuildBuffers()
        {
            foreach (RenderBuffer buffer in _renderBuffers) buffer.RebuildBuffers();
        }
    }
}