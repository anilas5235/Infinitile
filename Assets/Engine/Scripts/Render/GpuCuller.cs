using System;
using System.Collections.Generic;
using Engine.Scripts.Utils;
using Unity.Collections;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering;
using static Engine.Scripts.Utils.VoxelRenderConstants;
using static UnityEngine.GraphicsBuffer;

namespace Engine.Scripts.Render{
    /// <summary>
    ///     Phase C: GPU frustum culling of render pages (§E2). Owns the <c>PageCulling.compute</c> kernels and one
    ///     <see cref="CameraCullState" /> per camera that renders (CUL-04), created on first sight and released when
    ///     the camera is destroyed or has not rendered for <see cref="MaxIdleFrames" /> frames. Every draw of a
    ///     <see cref="RenderBuffer" /> first runs <see cref="Cull" /> for that buffer and camera (CUL-02).
    /// </summary>
    internal sealed class GpuCuller : IDisposable{
        /// <summary>Frames a camera may stay unseen before its buffers are released (about 5 s at 60 Hz).</summary>
        internal const int MaxIdleFrames = 300;

        private const int CullThreads = 128;
        private static readonly int CullGroups = (PagesPerBuffer + CullThreads - 1) / CullThreads;

        private readonly ComputeShader _shader;
        private readonly int _cullKernel;
        private readonly int _argsKernel;
        private readonly Dictionary<Camera, CameraCullState> _cameras = new();
        private readonly List<Camera> _stale = new(); // PERF-04: reused when releasing camera states
        private readonly Plane[] _planeScratch = new Plane[FrustumPlanes.Count];
        private readonly Vector4[] _planeUpload = new Vector4[FrustumPlanes.Count];
        private FrustumPlanes _gamePlanes; // CUL-06: last unfrozen game-camera frustum, used by every camera while frozen
        private Matrix4x4 _gameInverseViewProjection;
        private bool _hasGamePlanes;
        private bool _isDisposed;

        /// <param name="pageCullingShader">The <c>PageCulling.compute</c> asset (kernels CullPages and WriteArgs).</param>
        internal GpuCuller(ComputeShader pageCullingShader){
            _shader = pageCullingShader ?? throw new ArgumentNullException(nameof(pageCullingShader));
            _cullKernel = _shader.FindKernel("CullPages");
            _argsKernel = _shader.FindKernel("WriteArgs");
        }

        /// <summary>Number of cameras that currently own culling buffers.</summary>
        internal int CameraCount => _cameras.Count;

        public void Dispose(){
            if (_isDisposed) return;
            foreach (KeyValuePair<Camera, CameraCullState> pair in _cameras) pair.Value.Dispose();
            _cameras.Clear();
            _isDisposed = true;
        }

        /// <summary>
        ///     Returns the state of <paramref name="cam" /> for this frame, creating it on first sight (CUL-04), and
        ///     refreshes its frustum planes unless culling is frozen. CUL-06: while frozen, every camera culls with
        ///     the last unfrozen frustum of the game camera, so the Scene view (or a detached game camera) shows
        ///     exactly the pages the game camera kept and nothing it culled.
        /// </summary>
        internal CameraCullState BeginCamera(Camera cam, bool freeze, int frame){
            if (!_cameras.TryGetValue(cam, out CameraCullState state)){
                state = new CameraCullState();
                _cameras.Add(cam, state);
            }

            state.LastUsedFrame = frame;
            if (!freeze){
                state.Planes = FrustumPlanes.FromCamera(cam, _planeScratch);
                state.HasPlanes = true;
                if (cam.cameraType == CameraType.Game){
                    _gamePlanes = state.Planes;
                    _gameInverseViewProjection = (cam.projectionMatrix * cam.worldToCameraMatrix).inverse;
                    _hasGamePlanes = true;
                }
            }
            else if (_hasGamePlanes){
                state.Planes = _gamePlanes;
                state.HasPlanes = true;
            }
            else if (!state.HasPlanes){
                state.Planes = FrustumPlanes.FromCamera(cam, _planeScratch);
                state.HasPlanes = true;
            }

            return state;
        }

        /// <summary>
        ///     CUL-06: the frustum every camera uses while culling is frozen (inverse view-projection of the game
        ///     camera at its last unfrozen frame), for drawing it as a gizmo. False until a game camera has rendered.
        /// </summary>
        internal bool TryGetFrozenFrustum(out Matrix4x4 inverseViewProjection){
            inverseViewProjection = _gameInverseViewProjection;
            return _hasGamePlanes;
        }

        /// <summary>
        ///     CUL-02: builds the visible page list and indirect args of <paramref name="list" /> for one render
        ///     buffer: the cull kernel appends every page with points whose (extruded) AABB touches the frustum, the
        ///     counter is copied into the args and the args kernel writes the vertex count.
        /// </summary>
        /// <param name="buffer">Render buffer whose pages are tested.</param>
        /// <param name="list">Per-camera list and args for that buffer.</param>
        /// <param name="planes">Frustum planes of the camera.</param>
        /// <param name="extrusion">CUL-05: light direction times shadow distance for the shadow list, zero otherwise.</param>
        /// <param name="cullingEnabled">CUL-06: false appends every page with points ("all valid pages visible").</param>
        internal void Cull(RenderBuffer buffer, CullList list, in FrustumPlanes planes, float3 extrusion,
            bool cullingEnabled){
            using ProfilerMarker.AutoScope _ = VoxelProfilerMarkers.RenderCulling.Auto(); // PERF-01

            planes.CopyTo(_planeUpload);
            list.VisiblePages.SetCounterValue(0);

            _shader.SetBuffer(_cullKernel, CountsPerPageNameID, buffer.CountsPerPageBuffer);
            _shader.SetBuffer(_cullKernel, PageBoundsNameID, buffer.PageBoundsBuffer);
            _shader.SetBuffer(_cullKernel, VisiblePagesNameID, list.VisiblePages);
            _shader.SetVectorArray(FrustumPlanesNameID, _planeUpload);
            _shader.SetVector(ShadowExtrusionNameID, new Vector4(extrusion.x, extrusion.y, extrusion.z, 0f));
            _shader.SetInt(PagesPerBufferNameID, PagesPerBuffer);
            _shader.SetInt(PointsPerPageNameID, PointsPerPage);
            _shader.SetInt(CullingEnabledNameID, cullingEnabled ? 1 : 0);
            _shader.Dispatch(_cullKernel, CullGroups, 1, 1);

            GraphicsBuffer.CopyCount(list.VisiblePages, list.Args, CullList.VisibleCountOffsetBytes);

            _shader.SetBuffer(_argsKernel, ArgsBufferNameID, list.Args);
            _shader.SetInt(PointsPerPageNameID, PointsPerPage);
            _shader.Dispatch(_argsKernel, 1, 1, 1);
        }

        /// <summary>
        ///     CUL-04: releases the buffers of cameras that were destroyed or have not rendered for
        ///     <see cref="MaxIdleFrames" /> frames. Call once per frame from the main thread.
        /// </summary>
        internal void ReleaseStaleCameras(int frame){
            foreach (KeyValuePair<Camera, CameraCullState> pair in _cameras){
                if (pair.Key == null || frame - pair.Value.LastUsedFrame > MaxIdleFrames) _stale.Add(pair.Key);
            }

            for (int i = 0; i < _stale.Count; i++){
                Camera cam = _stale[i];
                _cameras[cam].Dispose();
                _cameras.Remove(cam);
            }

            _stale.Clear();
        }

        /// <summary>Looks up the state of a camera without creating one (debug overlay, tests).</summary>
        internal bool TryGetState(Camera cam, out CameraCullState state){
            return _cameras.TryGetValue(cam, out state);
        }
    }

    /// <summary>
    ///     CUL-04: everything one camera needs to cull and draw: its frustum planes (kept while culling is frozen)
    ///     and, per <see cref="RenderBuffer" />, a main and an optional shadow <see cref="CullList" />.
    /// </summary>
    internal sealed class CameraCullState : IDisposable{
        private readonly Dictionary<RenderBuffer, BufferCullLists> _buffers = new();

        internal FrustumPlanes Planes;
        internal bool HasPlanes;
        internal int LastUsedFrame;

        public void Dispose(){
            foreach (KeyValuePair<RenderBuffer, BufferCullLists> pair in _buffers) pair.Value.Dispose();
            _buffers.Clear();
        }

        /// <summary>The lists of <paramref name="buffer" /> for this camera, created lazily.</summary>
        internal BufferCullLists GetLists(RenderBuffer buffer){
            if (_buffers.TryGetValue(buffer, out BufferCullLists lists)) return lists;
            lists = new BufferCullLists(buffer);
            _buffers.Add(buffer, lists);
            return lists;
        }

        internal bool TryGetLists(RenderBuffer buffer, out BufferCullLists lists){
            return _buffers.TryGetValue(buffer, out lists);
        }
    }

    /// <summary>Main-pass and shadow-pass (CUL-05, created on first use) lists of one buffer for one camera.</summary>
    internal sealed class BufferCullLists : IDisposable{
        private readonly RenderBuffer _buffer;
        private CullList _shadow;

        internal BufferCullLists(RenderBuffer buffer){
            _buffer = buffer;
            Main = new CullList(buffer);
        }

        internal CullList Main{ get; }

        /// <summary>Shadow list, allocated on first access.</summary>
        internal CullList Shadow => _shadow ??= new CullList(_buffer);

        internal bool HasShadow => _shadow != null;

        public void Dispose(){
            Main.Dispose();
            _shadow?.Dispose();
            _shadow = null;
        }
    }

    /// <summary>
    ///     One visible page list with its indirect args and the property block that binds both (plus the point and
    ///     count buffers of the owning <see cref="RenderBuffer" />) for the vertex shader (CUL-03).
    /// </summary>
    internal sealed class CullList : IDisposable{
        /// <summary>Args layout: [0] vertex count, [1] instance count, [2] start vertex, [3] start instance, [4] visible pages.</summary>
        internal const int ArgsCount = 5;

        internal const int VisibleCountIndex = 4;
        internal const int VisibleCountOffsetBytes = VisibleCountIndex * sizeof(uint);
        private static readonly uint[] DefaultArgs = { 0u, 1u, 0u, 0u, 0u };

        private NativeArray<uint> _readback;
        private AsyncGPUReadbackRequest _pendingReadback;
        private bool _readbackPending;

        internal CullList(RenderBuffer buffer){
            VisiblePages = new GraphicsBuffer(Target.Append, PagesPerBuffer, sizeof(uint));
            Args = new GraphicsBuffer(Target.IndirectArguments, ArgsCount, sizeof(uint));
            Args.SetData(DefaultArgs);
            PropertyBlock = new MaterialPropertyBlock();
            PropertyBlock.SetBuffer(PointDataNameID, buffer.PointBuffer);
            PropertyBlock.SetBuffer(CountsPerPageNameID, buffer.CountsPerPageBuffer);
            PropertyBlock.SetBuffer(VisiblePagesNameID, VisiblePages);
        }

        internal GraphicsBuffer VisiblePages{ get; }
        internal GraphicsBuffer Args{ get; }
        internal MaterialPropertyBlock PropertyBlock{ get; }

        /// <summary>CUL-06: visible page count of the last completed read-back (debug overlay only).</summary>
        internal uint LastVisibleCount{ get; private set; }

        public void Dispose(){
            if (_readbackPending) _pendingReadback.WaitForCompletion(); // F-17: never free an array a read-back targets
            _readbackPending = false;
            VisiblePages.Dispose();
            Args.Dispose();
            if (_readback.IsCreated) _readback.Dispose();
        }

        /// <summary>
        ///     CUL-06: polls the previous read-back of the args and starts a new one, so the overlay shows the visible
        ///     page count with one frame of latency. Only called while the overlay is on.
        /// </summary>
        internal void UpdateVisibleCountReadback(){
            if (_readbackPending){
                if (!_pendingReadback.done) return;
                _readbackPending = false;
                if (!_pendingReadback.hasError) LastVisibleCount = _readback[VisibleCountIndex];
            }

            if (!_readback.IsCreated) _readback = new NativeArray<uint>(ArgsCount, Allocator.Persistent);
            _pendingReadback = AsyncGPUReadback.RequestIntoNativeArray(ref _readback, Args);
            _readbackPending = true;
        }
    }
}
