using System;
using System.Collections.Generic;
using Engine.Scripts.Utils.Logger;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using static Engine.Scripts.Utils.VoxelRenderConstants;

namespace Engine.Scripts.Render{
    /// <summary>
    ///     Manages multiple render buffers for a specific material, handling page allocation and rendering.
    ///     Automatically creates new buffers when capacity is exceeded.
    /// </summary>
    public class RenderBufferManager : IDisposable{
        private readonly string _layerName; // Material.name allocates on every read
        private readonly Material _material;
        private readonly Material[] _passes; // drawn in order from the same culled pages; clones are owned
        private readonly Dictionary<int3, AllocInfo> _partitionAllocations = new();
        private readonly List<RenderBuffer> _renderBuffers = new();
        private bool _isDisposed;

        /// <summary>
        ///     Initializes a new instance of the RenderBufferManager class.
        /// </summary>
        /// <param name="mat">The material to use for rendering.</param>
        /// <param name="initialBuffers">The initial number of buffers to create. Defaults to 1.</param>
        public RenderBufferManager(Material mat, int initialBuffers = 1){
            _material = mat;
            _layerName = mat != null ? mat.name : "<no material>";
            _passes = new[]{ mat };
            for (int i = 0; i < initialBuffers; i++) AddNewBuffer();
        }

        private RenderBufferManager(Material mat, Material[] passes){
            _material = mat;
            _layerName = mat != null ? mat.name : "<no material>";
            _passes = passes;
            AddNewBuffer();
        }

        /// <summary>
        ///     A manager for the transparent layer that draws its pages twice, from runtime clones of
        ///     <paramref name="mat" /> (TransparentVoxel.shader): plain transparents such as leaves first, then the
        ///     voxels with a volume slot (liquids). Inside one draw the order of the points follows the culled page
        ///     list, so a depth-writing liquid surface would hide the leaves behind it in some frames and not in
        ///     others.
        /// </summary>
        /// <param name="mat">The transparent material; it is not modified.</param>
        public static RenderBufferManager WithVolumePass(Material mat){
            if (mat == null || !mat.HasProperty(VoxelPassNameID)) return new RenderBufferManager(mat);

            Material plain = new(mat){ name = mat.name, renderQueue = mat.renderQueue - 2 };
            plain.SetFloat(VoxelPassNameID, 1f);
            Material volume = new(mat){ name = mat.name + " (volume)", renderQueue = mat.renderQueue };
            volume.SetFloat(VoxelPassNameID, 2f);
            return new RenderBufferManager(plain, new[]{ plain, volume });
        }

        /// <summary>Name of the material, used to label this layer in the culling stats.</summary>
        public string LayerName => _layerName;

        /// <summary>The render buffers of this manager, in buffer-index order.</summary>
        internal IReadOnlyList<RenderBuffer> Buffers => _renderBuffers;

        /// <summary>
        ///     Releases all GPU resources held by this manager.
        /// </summary>
        public void Dispose(){
            if (_isDisposed) return;

            foreach (RenderBuffer buffer in _renderBuffers) buffer.Dispose();
            if (_passes.Length > 1)
                foreach (Material pass in _passes) CoreUtils.Destroy(pass); // DestroyImmediate outside play mode

            _renderBuffers.Clear();
            _partitionAllocations.Clear();
            _isDisposed = true;
        }

        /// <summary>
        ///     Culls and draws all render buffers for the specified camera.
        /// </summary>
        /// <param name="cam">The camera to render with.</param>
        /// <param name="culler">The culler that dispatches the kernels.</param>
        /// <param name="state">The camera's culling state for this frame.</param>
        /// <param name="parameters">Culling switches and the shadow extrusion for this frame.</param>
        internal void Draw(Camera cam, GpuCuller culler, CameraCullState state, in CullingParameters parameters){
            foreach (RenderBuffer renderBuffer in _renderBuffers)
                renderBuffer.Draw(_passes, cam, culler, state, parameters);
        }

        /// <summary>
        ///     Allocates buffer space for a partition's points.
        /// </summary>
        /// <param name="partitionPos">The position of the partition.</param>
        /// <param name="pointCount">The number of points to allocate space for.</param>
        /// <returns>Allocation information, or default if pointCount is 0.</returns>
        /// <exception cref="ObjectDisposedException">Thrown if this manager has been disposed.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown if pointCount is negative.</exception>
        public AllocInfo AllocBufferSpace(int3 partitionPos, int pointCount){
            ThrowIfDisposed();

            if (pointCount < 0) throw new ArgumentOutOfRangeException(nameof(pointCount), "Point count must be >= 0.");

            bool hasAlloc = _partitionAllocations.TryGetValue(partitionPos, out AllocInfo allocInfo);

            if (pointCount == 0){
                if (hasAlloc) Release(allocInfo, partitionPos);
                return default;
            }

            int numPages = (int)math.ceil(pointCount / (float)PointsPerPage);

            if (hasAlloc) Release(allocInfo, partitionPos);
            AllocInfo allocation = AllocPages(pointCount, numPages, PageBounds.FromPartition(partitionPos));
            _partitionAllocations[partitionPos] = allocation;
            return allocation;
        }

        /// <summary>
        ///     Releases the buffer space allocated to a partition.
        /// </summary>
        /// <param name="partitionPos">The position of the partition.</param>
        /// <returns>True if a partition was released, false if no allocation existed.</returns>
        /// <exception cref="ObjectDisposedException">Thrown if this manager has been disposed.</exception>
        public bool ReleasePartition(int3 partitionPos){
            ThrowIfDisposed();

            if (!_partitionAllocations.TryGetValue(partitionPos, out AllocInfo allocInfo)) return false;

            Release(allocInfo, partitionPos);
            return true;
        }

        /// <summary>
        ///     Attempts to allocate pages in existing buffers or creates new ones if needed.
        /// </summary>
        /// <param name="pointCount">The total number of points.</param>
        /// <param name="numPages">The number of pages needed.</param>
        /// <param name="bounds">Bounds of the owning partition, stored per page.</param>
        /// <returns>The allocation info for the allocated pages.</returns>
        private AllocInfo AllocPages(int pointCount, int numPages, in PageBounds bounds){
            bool success = false;
            AllocInfo allocation = default;
            foreach (RenderBuffer rBuffer in _renderBuffers){
                success = rBuffer.TryAllocPages(pointCount, numPages, bounds, out allocation);
                if (success) break;
            }

            if (!success) AddNewBuffer().TryAllocPages(pointCount, numPages, bounds, out allocation);

            return allocation;
        }

        /// <summary>
        ///     Releases an allocation from its buffer and removes the partition entry.
        /// </summary>
        /// <param name="allocInfo">The allocation to release.</param>
        /// <param name="partitionPos">The partition position.</param>
        private void Release(in AllocInfo allocInfo, in int3 partitionPos){
            _renderBuffers[allocInfo.BufferIndex].ClearPage(allocInfo);
            _partitionAllocations.Remove(partitionPos);
            allocInfo.Recycle();
        }

        /// <summary>
        ///     Throws if this manager has been disposed.
        /// </summary>
        /// <exception cref="ObjectDisposedException">Thrown if disposed.</exception>
        private void ThrowIfDisposed(){
            if (_isDisposed) throw new ObjectDisposedException(nameof(RenderBufferManager));
        }

        /// <summary>
        ///     Creates and adds a new render buffer to this manager.
        /// </summary>
        /// <returns>The newly created render buffer.</returns>
        private RenderBuffer AddNewBuffer(){
            RenderBuffer rBuffer = new(_renderBuffers.Count);
            _renderBuffers.Add(rBuffer);
            VoxelEngineLogger.Info<RenderBufferManager>(
                $"Added new RenderBuffer for {_material.name}. Total buffers: {_renderBuffers.Count}");
            return rBuffer;
        }

        /// <summary>
        ///     Gets the point buffer at the specified index.
        /// </summary>
        /// <param name="bufferIndex">The index of the buffer.</param>
        /// <returns>The graphics buffer at the specified index.</returns>
        /// <exception cref="ObjectDisposedException">Thrown if disposed.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown if bufferIndex is invalid.</exception>
        public GraphicsBuffer GetBuffer(int bufferIndex){
            ThrowIfDisposed();

            if (bufferIndex < 0 || bufferIndex >= _renderBuffers.Count)
                throw new ArgumentOutOfRangeException(nameof(bufferIndex), "Invalid buffer index.");

            return _renderBuffers[bufferIndex].PointBuffer;
        }

        /// <summary>
        ///     Uploads the page state of all render buffers that have pending changes.
        /// </summary>
        public void RebuildBuffers(){
            foreach (RenderBuffer buffer in _renderBuffers) buffer.RebuildBuffers();
        }
    }
}
