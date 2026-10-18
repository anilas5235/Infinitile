using System;
using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using static Engine.Scripts.Utils.VoxelRenderConstants;
using static UnityEngine.GraphicsBuffer;

namespace Engine.Scripts.Render{
    /// <summary>
    ///     Manages a single render buffer for storing and rendering voxel point data.
    ///     Page bookkeeping lives in <see cref="PageAllocator" /> (TST-E17); this class owns the GPU side: the point
    ///     buffer plus the per-page count and bounds buffers the culling kernel reads (CUL-01). The visible page lists
    ///     and indirect args are per camera and live in <see cref="CameraCullState" /> (CUL-04).
    /// </summary>
    internal class RenderBuffer : IDisposable{
        /// <summary>Points are already in world space, so the draw bounds only have to be large enough to never cull.</summary>
        private static readonly Bounds DrawBounds = new(Vector3.zero, Vector3.one * 100000);

        private readonly PageAllocator _pages;
        private bool _shouldDraw;

        private bool _stateBufferDirty;

        /// <summary>
        ///     Initializes a new instance of the RenderBuffer class.
        /// </summary>
        /// <param name="bufferIndex">The index of this buffer.</param>
        public RenderBuffer(int bufferIndex){
            BufferIndex = bufferIndex;
            PointBuffer = new GraphicsBuffer(Target.Structured, RenderBufferSize, Marshal.SizeOf<Vertex>());

            _pages = new PageAllocator(Allocator.Domain);

            CountsPerPageBuffer = new GraphicsBuffer(Target.Structured, PagesPerBuffer, sizeof(uint));
            PageBoundsBuffer = new GraphicsBuffer(Target.Structured, PagesPerBuffer, Marshal.SizeOf<PageBounds>());
        }

        /// <summary>
        ///     Gets the GPU buffer containing vertex/point data.
        /// </summary>
        public GraphicsBuffer PointBuffer{ get; }

        /// <summary>Points per page on the GPU (0 = free page); read by the culling kernel and the vertex shader.</summary>
        internal GraphicsBuffer CountsPerPageBuffer{ get; }

        /// <summary>CUL-01: world-space AABB per page on the GPU; read by the culling kernel.</summary>
        internal GraphicsBuffer PageBoundsBuffer{ get; }

        /// <summary>CPU-side page state (counts and bounds), the reference for the GPU copies.</summary>
        internal PageAllocator Pages => _pages;

        /// <summary>
        ///     Gets the number of free pages available in this buffer.
        /// </summary>
        public int FreePages => _pages.FreePages;

        /// <summary>
        ///     Gets the index of this buffer within the buffer manager.
        /// </summary>
        public int BufferIndex{ get; }

        /// <summary>True while the buffer holds at least one allocated page.</summary>
        internal bool ShouldDraw => _shouldDraw;

        /// <summary>
        ///     Releases all GPU resources held by this buffer.
        /// </summary>
        public void Dispose(){
            PointBuffer?.Dispose();
            CountsPerPageBuffer?.Dispose();
            PageBoundsBuffer?.Dispose();
            _pages.Dispose();
        }

        /// <summary>
        ///     Attempts to allocate pages in this buffer for the given point count.
        /// </summary>
        /// <param name="pointCount">The total number of points to allocate space for.</param>
        /// <param name="numPages">The number of pages to allocate.</param>
        /// <param name="bounds">CUL-01: bounds of the partition the points belong to.</param>
        /// <param name="allocation">The allocation info if successful, default otherwise.</param>
        /// <returns>True if allocation was successful, false if not enough free pages.</returns>
        public bool TryAllocPages(int pointCount, int numPages, in PageBounds bounds, out AllocInfo allocation){
            if (!_pages.TryAllocPages(pointCount, numPages, BufferIndex, bounds, out allocation)) return false;

            _stateBufferDirty = true;
            if (!_shouldDraw) _shouldDraw = true;
            return true;
        }

        /// <summary>
        ///     Clears all pages specified in the allocation, freeing them for reuse.
        /// </summary>
        /// <param name="allocInfo">The allocation info containing pages to clear.</param>
        public void ClearPage(in AllocInfo allocInfo){
            if (allocInfo.Count == 0) return;

            _pages.Release(allocInfo);

            _stateBufferDirty = true;
            if (_shouldDraw && _pages.IsEmpty) _shouldDraw = false;
        }

        /// <summary>
        ///     Culls this buffer's pages for <paramref name="cam" /> and draws the visible ones with indirect
        ///     rendering (CUL-02). With shadows on, a second list of pages whose extruded bounds reach the frustum is
        ///     drawn as shadow casters only (CUL-05).
        /// </summary>
        /// <param name="mat">The material to use for rendering.</param>
        /// <param name="cam">The camera to render with.</param>
        /// <param name="culler">The culler that dispatches the kernels.</param>
        /// <param name="state">The camera's culling state (planes and per-buffer lists).</param>
        /// <param name="parameters">Culling switches and the shadow extrusion for this frame.</param>
        public void Draw(Material mat, Camera cam, GpuCuller culler, CameraCullState state,
            in CullingParameters parameters){
            if (!_shouldDraw) return;

            BufferCullLists lists = state.GetLists(this);

            culler.Cull(this, lists.Main, state.Planes, float3.zero, parameters.CullingEnabled);
            // the main pass only stops casting; it still receives shadows when they are on, as before Phase C
            Graphics.DrawProceduralIndirect(mat, DrawBounds, MeshTopology.Triangles, lists.Main.Args, 0, cam,
                lists.Main.PropertyBlock, ShadowCastingMode.Off, parameters.Shadows);

            if (!parameters.Shadows) return;
            culler.Cull(this, lists.Shadow, state.Planes, parameters.ShadowExtrusion, parameters.CullingEnabled);
            Graphics.DrawProceduralIndirect(mat, DrawBounds, MeshTopology.Triangles, lists.Shadow.Args, 0, cam,
                lists.Shadow.PropertyBlock, ShadowCastingMode.ShadowsOnly, true);
        }

        /// <summary>
        ///     Uploads the page counts and bounds to the GPU if the page state has changed. The visible page lists
        ///     are rebuilt per camera and frame by the culling kernel, so nothing else is derived here (CUL-03).
        /// </summary>
        public void RebuildBuffers(){
            if (!_stateBufferDirty) return;

            CountsPerPageBuffer.SetData(_pages.CountsPerPage);
            PageBoundsBuffer.SetData(_pages.BoundsPerPage);

            _stateBufferDirty = false;
        }
    }

    /// <summary>Per-frame culling switches handed from <see cref="VoxelWorldRenderer" /> to every buffer draw.</summary>
    internal readonly struct CullingParameters{
        /// <summary>CUL-06: false draws every page with points.</summary>
        public readonly bool CullingEnabled;

        /// <summary>CUL-05: true adds the shadow-caster draw with the extruded list.</summary>
        public readonly bool Shadows;

        /// <summary>CUL-05: main light direction times shadow distance.</summary>
        public readonly float3 ShadowExtrusion;

        public CullingParameters(bool cullingEnabled, bool shadows, float3 shadowExtrusion){
            CullingEnabled = cullingEnabled;
            Shadows = shadows;
            ShadowExtrusion = shadowExtrusion;
        }
    }
}
