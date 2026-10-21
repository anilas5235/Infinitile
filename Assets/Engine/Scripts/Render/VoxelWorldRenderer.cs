using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Engine.Scripts.Data;
using Engine.Scripts.Settings;
using Engine.Scripts.Utils;
using Engine.Scripts.Utils.Collections;
using Engine.Scripts.Utils.Extensions;
using Engine.Scripts.Utils.Logger;
using Engine.Scripts.VoxelConfig;
using Engine.Scripts.VoxelConfig.Registry;
using Engine.Scripts.World;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering;
using static Engine.Scripts.Utils.VoxelConstants;
using static UnityEngine.GraphicsBuffer;

namespace Engine.Scripts.Render{
    /// <summary>
    ///     Main renderer component for the voxel world, managing partition building and rendering.
    ///     Handles async GPU work for geometry generation and updates render buffers.
    /// </summary>
    [RequireComponent(typeof(VoxelWorld))]
    public class VoxelWorldRenderer : Singleton<VoxelWorldRenderer>{
        /// <summary>
        ///     Reference to the VoxelWorld instance.
        /// </summary>
        public VoxelWorld world;

        private readonly List<int3> _pendingBuilds = new();
        private readonly HashSet<int3> _requestedPartitions = new();

        private readonly Dictionary<int2, GraphicsBuffer> _voxelDataBuffers = new();
        private readonly Dictionary<int2, GraphicsBuffer> _lightDataBuffers = new(); // LGT-08

        // PERF-04: a GraphicsBuffer is a managed object; released chunk buffers are kept for reuse so that chunk
        // uploads during flight do not allocate. Lengths are rounded up so a free buffer fits more often; the
        // decompress kernel reads the interval count from element 0, not from the buffer length.
        private const int ChunkBufferGranularity = 512;

        /// <summary>
        ///     PERF-04: default element count of a pre-allocated chunk buffer. Eight intervals per voxel column cover
        ///     the spec terrain with flora (measured during the TST-E21 flight: median 4.6, maximum 6 per column;
        ///     four were too few, so new buffers were created during flight); a longer chunk gets a larger buffer on
        ///     demand.
        /// </summary>
        private const int DefaultChunkBufferLength =
            (ChunkWidth * ChunkDepth * 8 + 1 + ChunkBufferGranularity - 1) / ChunkBufferGranularity * ChunkBufferGranularity;

        private readonly List<GraphicsBuffer> _freeChunkBuffers = new();
        private int _chunkStoreSize;
        private bool _buffersRebuildPending;

        private int _copyKernelID;
        private CopyPointsHandler _copyPointsHandler;
        private RenderBufferManager _foliageBufferManager;

        // Phase C: GPU culling (§E2)
        private GpuCuller _culler;
        private Camera _overlayCamera; // CUL-06: the game camera whose counts the overlay shows
        private RenderBufferManager[] _layers; // PERF-04: fixed array for per-frame loops

        [Header("Culling debug (CUL-06)")]
        [Tooltip("GPU page culling; off draws every page with points. Initialised from RendererSettings.gpuCulling.")]
        [SerializeField] private bool gpuCulling = true;

        [Tooltip("Keeps the game camera's frustum: every camera (Scene view included) then draws exactly what the game camera kept. A gizmo shows the frozen frustum.")]
        [SerializeField] private bool freezeCulling;

        [Tooltip("Visible / allocated pages per layer and buffer for the game camera (allocates while on).")]
        [SerializeField] private bool showCullingOverlay;

        /// <summary>
        ///     CUL-06: GPU frustum culling on/off at runtime (initialised from <see cref="RendererSettings.gpuCulling" />).
        ///     Off falls back to "every page with points is visible"; the image must not change.
        /// </summary>
        public bool GpuCulling{ get => gpuCulling; set => gpuCulling = value; }

        /// <summary>
        ///     CUL-06: keeps the last unfrozen frustum of the game camera and culls every camera with it, so you can
        ///     fly out (Scene view, or a free game camera) and look at what was culled. The frozen frustum is drawn
        ///     as a yellow gizmo in the Scene view.
        /// </summary>
        public bool FreezeCulling{ get => freezeCulling; set => freezeCulling = value; }

        /// <summary>
        ///     CUL-06: draws visible pages / total pages per buffer and layer for the game camera. Debug only: the
        ///     read-backs and the label strings allocate while it is on.
        /// </summary>
        public bool ShowCullingOverlay{ get => showCullingOverlay; set => showCullingOverlay = value; }

        private InFlightBuild[] _inFlightBuilds;
        private PartitionBuildRequest[] _buildRequests; // PERF-04: one reusable request per in-flight slot
        private uint2[] _intervalUpload = Array.Empty<uint2>(); // PERF-04: reused chunk upload staging
        private bool _isDestroyed;
        private int _maxInFlight = 1;

        private PointBuilderHandler[] _pointBuilderHandlers;

        private int _pointBuilderKernelID;

        private VoxelEngineSettings _settings;

        private RenderBufferManager _solidBufferManager;
        private RenderBufferManager _transparentBufferManager;

        protected override void Awake(){
            base.Awake();

            // F-21: resolve the world before its settings are read; OnEnable's fallback ran too late for Awake.
            if (world == null) world = GetComponent<VoxelWorld>();
            if (world == null) world = VoxelWorld.Instance;
            _settings = world.Settings;
            VoxelLightingGlobals.SetDefaults(); // LGT-10: lit without a DayNightCycle, which overrides these per frame
            _maxInFlight = math.max(1, _settings.Scheduler.partitionBuildBatchSize);

            // PERF-04: sized for the partition pool so the request bookkeeping does not grow during flight
            int partitionPoolSize = _settings.Chunk.DrawDistance.SquareSize() * PartitionsPerChunk;
            _requestedPartitions.EnsureCapacity(partitionPoolSize);
            _pendingBuilds.Capacity = math.max(_pendingBuilds.Capacity, partitionPoolSize);
            AllocInfo.PrewarmPageLists(partitionPoolSize * 3); // solid, transparent and foliage per partition

            // PERF-04: one GPU buffer per chunk the manager's store can hold (same formula as ChunkManager), created
            // now instead of one managed GraphicsBuffer object per chunk upload during flight.
            // LGT-08: two per chunk, voxel data and light share the pool (same interval format).
            _chunkStoreSize = 2 * (_settings.Chunk.LoadDistance + 2).SquareSize();
            _freeChunkBuffers.Capacity = _chunkStoreSize;
            for (int i = 0; i < _chunkStoreSize; i++)
                _freeChunkBuffers.Add(new GraphicsBuffer(Target.Structured, DefaultChunkBufferLength, Marshal.SizeOf<uint2>()));

            _inFlightBuilds = new InFlightBuild[_maxInFlight];
            _buildRequests = new PartitionBuildRequest[_maxInFlight];
            for (int i = 0; i < _maxInFlight; i++) _buildRequests[i] = new PartitionBuildRequest(default);

            RendererSettings rSettings = _settings.Renderer;

            _solidBufferManager = new RenderBufferManager(rSettings.solidMaterial);
            _transparentBufferManager = new RenderBufferManager(rSettings.transparentMaterial);
            _foliageBufferManager = new RenderBufferManager(rSettings.foliageMaterial);
            _layers = new[]{ _solidBufferManager, _transparentBufferManager, _foliageBufferManager };

            _culler = new GpuCuller(rSettings.pageCulling);
            GpuCulling = rSettings.gpuCulling;

            VoxelRegistry voxelRegistry = DataImporter.Instance.VoxelRegistry;
            _pointBuilderHandlers = new PointBuilderHandler[_maxInFlight];
            for (int i = 0; i < _pointBuilderHandlers.Length; i++){
                _pointBuilderHandlers[i] = new PointBuilderHandler(
                    rSettings.pointBuilder,
                    voxelRegistry.VoxelRenderDefBuffer,
                    voxelRegistry.QuadTexPairBuffer
                );
            }

            _copyPointsHandler = new CopyPointsHandler(
                rSettings.copyPoints,
                _solidBufferManager,
                _transparentBufferManager,
                _foliageBufferManager
            );
        }

        /// <summary>
        ///     Updates pending and in-flight partition builds each frame.
        /// </summary>
        private void Update(){
            if (_isDestroyed) return;

            CompleteFinishedPartitionBuilds();
            StartPendingPartitionBuilds();
        }

        /// <summary>
        ///     Rebuilds render buffers if changes were made during this frame.
        /// </summary>
        private void LateUpdate(){
            if (_isDestroyed) return;
            _culler.ReleaseStaleCameras(Time.frameCount); // CUL-04
            if (!_buffersRebuildPending) return;

            using ProfilerMarker.AutoScope _ = VoxelProfilerMarkers.RenderRebuildBuffers.Auto(); // PERF-01
            _solidBufferManager.RebuildBuffers();
            _transparentBufferManager.RebuildBuffers();
            _foliageBufferManager.RebuildBuffers();
            _buffersRebuildPending = false;
        }

        /// <summary>
        ///     Registers event handlers for world changes and rendering.
        /// </summary>
        private void OnEnable(){
            if (world == null) world = VoxelWorld.Instance;
            RenderPipelineManager.beginCameraRendering += Draw;
            world.ChunkChanged += HandleChunkChange;
            world.ChunkDataReady += HandleChunkDataReady;
            world.ChunkLightChanged += HandleChunkLightChanged; // LGT-08
            world.ChunkEvicted += RemoveChunkData;
            world.PartitionEvicted += RemovePartitionRenderData;
            world.PartitionBuildRequested += UpdatePartitions;
        }

        /// <summary>
        ///     Unregisters event handlers when disabled.
        /// </summary>
        private void OnDisable(){
            RenderPipelineManager.beginCameraRendering -= Draw;
            if (world == null) return;

            world.ChunkChanged -= HandleChunkChange;
            world.ChunkDataReady -= HandleChunkDataReady;
            world.ChunkLightChanged -= HandleChunkLightChanged;
            world.ChunkEvicted -= RemoveChunkData;
            world.PartitionEvicted -= RemovePartitionRenderData;
            world.PartitionBuildRequested -= UpdatePartitions;
        }

        /// <summary>
        ///     Cleans up all GPU resources when the renderer is destroyed.
        /// </summary>
        protected override void OnDestroy(){
            base.OnDestroy();
            _isDestroyed = true;
            // F-17: every resource is released on its own, so one failing Dispose does not leak the rest.
            SafeDispose(_copyPointsHandler);

            // F-17: each handler waits for its pending read-back and releases its resources independently.
            if (_pointBuilderHandlers != null){
                foreach (PointBuilderHandler handler in _pointBuilderHandlers)
                    SafeDispose(handler);
            }

            SafeDispose(_culler); // per-camera lists before the buffers they point at
            SafeDispose(_solidBufferManager);
            SafeDispose(_transparentBufferManager);
            SafeDispose(_foliageBufferManager);

            foreach (GraphicsBuffer buffer in _voxelDataBuffers.Values) SafeDispose(buffer);
            foreach (GraphicsBuffer buffer in _lightDataBuffers.Values) SafeDispose(buffer);
            foreach (GraphicsBuffer buffer in _freeChunkBuffers) SafeDispose(buffer);
            _freeChunkBuffers.Clear();

            _pendingBuilds.Clear();
            _requestedPartitions.Clear();

            _voxelDataBuffers.Clear();
            _lightDataBuffers.Clear();
        }

        private static void SafeDispose(IDisposable resource){
            try{
                resource?.Dispose();
            }
            catch (Exception e){
                VoxelEngineLogger.Error<VoxelWorldRenderer>($"Failed to release a render resource: {e}");
            }
        }

        /// <summary>
        ///     Handles chunk change events by updating the voxel data buffer.
        /// </summary>
        /// <param name="chunk">The chunk that changed.</param>
        private void HandleChunkChange(Chunk chunk){
            AddOrUpdateChunk(chunk.Position, chunk.VoxelData.GetData());
        }

        /// <summary>
        ///     Handles chunk data ready events by updating the voxel data buffer.
        /// </summary>
        /// <param name="chunk">The chunk whose data is ready.</param>
        private void HandleChunkDataReady(Chunk chunk){
            AddOrUpdateChunk(chunk.Position, chunk.VoxelData.GetData());
        }

        /// <summary>
        ///     LGT-08: uploads a chunk's light after the light job or an edit changed it.
        /// </summary>
        /// <param name="chunk">The chunk whose light changed.</param>
        private void HandleChunkLightChanged(Chunk chunk){
            AddOrUpdateLight(chunk.Position, chunk.LightData.GetData());
        }

        /// <summary>
        ///     Culls and draws all render buffers for the given camera (CUL-02, CUL-04). Runs once per camera that
        ///     renders, from <see cref="RenderPipelineManager.beginCameraRendering" />.
        /// </summary>
        /// <param name="context">The scriptable render context.</param>
        /// <param name="cam">The camera to render with.</param>
        private void Draw(ScriptableRenderContext context, Camera cam){
            using ProfilerMarker.AutoScope _ = VoxelProfilerMarkers.RenderDraw.Auto(); // PERF-01
            if (_isDestroyed || _culler == null) return;

            CameraCullState state = _culler.BeginCamera(cam, FreezeCulling, Time.frameCount);
            CullingParameters parameters = BuildCullingParameters();

            _solidBufferManager.Draw(cam, _culler, state, parameters);
            _transparentBufferManager.Draw(cam, _culler, state, parameters);
            _foliageBufferManager.Draw(cam, _culler, state, parameters);

            if (ShowCullingOverlay && cam.cameraType == CameraType.Game) UpdateOverlayReadbacks(cam, state);
        }

        /// <summary>
        ///     CUL-05: the shadow list tests page bounds extruded along the main light by the shadow distance. Without
        ///     a main light or with shadows off only the main pass is drawn.
        /// </summary>
        private CullingParameters BuildCullingParameters(){
            bool shadows = _settings.Renderer.shadows != ShadowCastingMode.Off;
            float3 extrusion = float3.zero;
            if (shadows){
                Light sun = RenderSettings.sun;
                if (sun == null) shadows = false;
                else extrusion = (float3)sun.transform.forward * ShadowDistance();
            }

            return new CullingParameters(GpuCulling, shadows, extrusion);
        }

        private static float ShadowDistance(){
            RenderPipelineAsset asset = GraphicsSettings.currentRenderPipeline;
            if (asset is UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset urp) return urp.shadowDistance;
            return QualitySettings.shadowDistance;
        }

        /// <summary>CUL-06: polls and re-requests the visible page counts of every buffer for the overlay.</summary>
        private void UpdateOverlayReadbacks(Camera cam, CameraCullState state){
            _overlayCamera = cam;
            for (int l = 0; l < _layers.Length; l++){
                IReadOnlyList<RenderBuffer> buffers = _layers[l].Buffers;
                for (int b = 0; b < buffers.Count; b++){
                    if (!state.TryGetLists(buffers[b], out BufferCullLists lists)) continue;
                    lists.Main.UpdateVisibleCountReadback();
                    if (lists.HasShadow) lists.Shadow.UpdateVisibleCountReadback();
                }
            }
        }

        /// <summary>
        ///     CUL-06 debug overlay: visible pages / allocated pages per buffer and layer for the game camera, plus the
        ///     culling and freeze switches. Allocates strings; only active while <see cref="ShowCullingOverlay" /> is on.
        /// </summary>
        private void OnGUI(){
            if (!ShowCullingOverlay || _isDestroyed || _layers == null) return;

            GUILayout.BeginArea(new Rect(10, 10, 420, 400), GUI.skin.box);
            GUILayout.Label($"GPU culling: {(GpuCulling ? "on" : "off")}   freeze: {(FreezeCulling ? "on" : "off")}");
            CameraCullState state = null;
            if (_overlayCamera == null || !_culler.TryGetState(_overlayCamera, out state)){
                GUILayout.Label("no game camera culled yet");
            }
            else{
                for (int l = 0; l < _layers.Length; l++){
                    RenderBufferManager layer = _layers[l];
                    IReadOnlyList<RenderBuffer> buffers = layer.Buffers;
                    uint visible = 0;
                    int total = 0;
                    for (int b = 0; b < buffers.Count; b++){
                        total += buffers[b].Pages.AllocatedPages;
                        if (state.TryGetLists(buffers[b], out BufferCullLists lists)) visible += lists.Main.LastVisibleCount;
                    }

                    GUILayout.Label($"{layer.LayerName}: {visible} / {total} pages visible");
                    for (int b = 0; b < buffers.Count; b++){
                        string shadow = "";
                        uint bufferVisible = 0;
                        if (state.TryGetLists(buffers[b], out BufferCullLists lists)){
                            bufferVisible = lists.Main.LastVisibleCount;
                            if (lists.HasShadow) shadow = $"   shadow {lists.Shadow.LastVisibleCount}";
                        }

                        GUILayout.Label($"    buffer {b}: {bufferVisible} / {buffers[b].Pages.AllocatedPages}{shadow}");
                    }
                }
            }

            GUILayout.EndArea();
        }

#if UNITY_EDITOR
        private static readonly Vector3[] FrustumCorners = new Vector3[8];

        /// <summary>CUL-06: draws the frozen game-camera frustum in the Scene view while culling is frozen.</summary>
        private void OnDrawGizmos(){
            if (!freezeCulling || _culler == null || !_culler.TryGetFrozenFrustum(out Matrix4x4 inverseViewProjection))
                return;

            // corners of the clip-space cube (GL convention: z from -1 near to +1 far) back in world space
            for (int i = 0; i < 8; i++){
                Vector3 ndc = new((i & 1) == 0 ? -1f : 1f, (i & 2) == 0 ? -1f : 1f, (i & 4) == 0 ? -1f : 1f);
                FrustumCorners[i] = inverseViewProjection.MultiplyPoint(ndc);
            }

            Gizmos.color = Color.yellow;
            for (int i = 0; i < 8; i++){
                if ((i & 1) == 0) Gizmos.DrawLine(FrustumCorners[i], FrustumCorners[i | 1]); // x edges
                if ((i & 2) == 0) Gizmos.DrawLine(FrustumCorners[i], FrustumCorners[i | 2]); // y edges
                if ((i & 4) == 0) Gizmos.DrawLine(FrustumCorners[i], FrustumCorners[i | 4]); // near-far edges
            }
        }
#endif

        /// <summary>
        ///     Requests that render buffers be rebuilt on the next LateUpdate.
        /// </summary>
        private void RequestBuffersRebuild(){
            if (_isDestroyed) return;
            _buffersRebuildPending = true;
        }

        /// <summary>
        ///     Processes partition update requests from the world.
        /// </summary>
        /// <param name="partitions">The set of partitions to update.</param>
        private void UpdatePartitions(HashSet<int3> partitions){
            if (_isDestroyed || partitions == null) return;

            foreach (int3 partition in partitions){
                _requestedPartitions.Add(partition);
                EnqueuePartitionBuild(partition);
            }
        }

        /// <summary>
        ///     Adds a partition to the build queue if not already present.
        /// </summary>
        /// <param name="partition">The partition to build.</param>
        private void EnqueuePartitionBuild(int3 partition){
            if (_isDestroyed) return;
            if (_pendingBuilds.Contains(partition)) return;

            _pendingBuilds.Add(partition);
        }

        /// <summary>
        ///     Starts pending partition builds up to the max in-flight limit.
        /// </summary>
        private void StartPendingPartitionBuilds(){
            if (_pointBuilderHandlers == null || _pointBuilderHandlers.Length == 0) return;

            using ProfilerMarker.AutoScope _ = VoxelProfilerMarkers.PointBuildDispatch.Auto(); // PERF-01
            for (int i = 0; i < _maxInFlight; i++){
                if (_pendingBuilds.Count < 1) break;
                if (_inFlightBuilds[i].Active) continue;

                // F-24 / PERF-04: plain loops; LINQ First/Any with a capturing lambda allocated every frame.
                int3 partition = _pendingBuilds[0];
                _pendingBuilds.RemoveAt(0);
                if (IsBuildInFlight(partition)){
                    EnqueuePartitionBuild(partition);
                    continue;
                }

                PartitionBuildRequest request = _buildRequests[i];
                request.Reset(partition);
                request.CollectBuffers(_voxelDataBuffers, _lightDataBuffers);

                if (!request.IsValid){
                    VoxelEngineLogger.Error<VoxelWorldRenderer>(
                        $"Skipping partition {partition} due to missing neighbor data.");
                    continue;
                }

                PointBuilderHandler handler = _pointBuilderHandlers[i];
                try{
                    // PERF-04: a plain dispatch plus a polled read-back; the async version boxed a state machine per build
                    handler.BuildPoints(request);
                }
                catch (Exception e){
                    VoxelEngineLogger.Error<VoxelWorldRenderer>($"Error updating partition {partition}: {e}");
                    continue;
                }

                _inFlightBuilds[i] = new InFlightBuild(partition, handler);
            }
        }

        private bool IsBuildInFlight(int3 partition){
            for (int i = 0; i < _inFlightBuilds.Length; i++){
                InFlightBuild build = _inFlightBuilds[i];
                if (build.Active && build.Partition.Equals(partition)) return true;
            }

            return false;
        }

        /// <summary>
        ///     Completes finished partition builds and applies their results.
        /// </summary>
        private void CompleteFinishedPartitionBuilds(){
            bool updatedPartitions = false;

            for (int i = 0; i < _maxInFlight; i++){
                InFlightBuild build = _inFlightBuilds[i];
                if (!build.Active) continue;
                if (!build.Handler.IsReadBackDone) continue;

                _inFlightBuilds[i] = default;

                try{
                    int[] pointCounts = build.Handler.CompleteBuild();
                    if (_isDestroyed) return;

                    if (!_requestedPartitions.Contains(build.Partition) || _pendingBuilds.Contains(build.Partition))
                        continue;

                    using (VoxelProfilerMarkers.PointBuildCopy.Auto()) // PERF-01
                        _copyPointsHandler.CopyJob(build.Handler, build.Partition, pointCounts);
                    updatedPartitions = true;
                }
                catch (Exception e){
                    VoxelEngineLogger.Error<VoxelWorldRenderer>($"Error updating partition {build.Partition}: {e}");
                }
            }

            if (updatedPartitions) RequestBuffersRebuild();
        }


        /// <summary>
        ///     Adds or updates a chunk's voxel data in the GPU buffer.
        /// </summary>
        /// <param name="chunk">The chunk position.</param>
        /// <param name="voxelData">The voxel data to store.</param>
        private void AddOrUpdateChunk(int2 chunk, UnsafeIntervalList<ushort> voxelData){
            using ProfilerMarker.AutoScope _ = VoxelProfilerMarkers.GpuUploadChunk.Auto(); // PERF-01
            // F-18: validate before the old buffer is released, otherwise a rejected update leaves a disposed
            // buffer in the map that the next partition build binds.
            if (voxelData.Length != VoxelsPerChunk) throw new Exception("Voxel data length mismatch!");
            UploadIntervals(_voxelDataBuffers, chunk, voxelData);
        }

        /// <summary>
        ///     LGT-08: adds or updates a chunk's light in the GPU buffer (same interval format as the voxel data).
        /// </summary>
        /// <param name="chunk">The chunk position.</param>
        /// <param name="lightData">The light data to store.</param>
        private void AddOrUpdateLight(int2 chunk, UnsafeIntervalList<ushort> lightData){
            using ProfilerMarker.AutoScope _ = VoxelProfilerMarkers.GpuUploadChunk.Auto(); // PERF-01
            if (lightData.Length != VoxelsPerChunk) throw new Exception("Light data length mismatch!");
            UploadIntervals(_lightDataBuffers, chunk, lightData);
        }

        /// <summary>
        ///     Writes an interval list to the chunk's buffer in <paramref name="buffers" />. Element 0 holds
        ///     (interval count, last end index), followed by (value, exclusive end) per interval.
        /// </summary>
        private void UploadIntervals(Dictionary<int2, GraphicsBuffer> buffers, int2 chunk, UnsafeIntervalList<ushort> data){
            int compLength = data.CompressedLength;
            int uploadLength = compLength + 1;

            // PERF-04: keep the chunk's buffer when it is large enough, otherwise release it for reuse
            GraphicsBuffer dataBuffer = null;
            if (buffers.TryGetValue(chunk, out GraphicsBuffer existingBuffer)){
                if (existingBuffer.count >= uploadLength) dataBuffer = existingBuffer;
                else ReleaseChunkBuffer(existingBuffer);
            }
            dataBuffer ??= RentChunkBuffer(uploadLength);
            // PERF-04: staging array is kept and grown, not allocated per upload
            if (_intervalUpload.Length < uploadLength) _intervalUpload = new uint2[math.max(uploadLength, _intervalUpload.Length * 2)];
            uint2[] intervalData = _intervalUpload;
            int i = 1;
            foreach (UnsafeIntervalList<ushort>.Node n in data.Internal)
                intervalData[i++] = new uint2(n.Value, (uint)n.Count);

            intervalData[0] = new uint2((uint)compLength, intervalData[compLength].y);

            dataBuffer.SetData(intervalData, 0, 0, uploadLength);
            buffers[chunk] = dataBuffer;
        }

        /// <summary>PERF-04: first free buffer that holds <paramref name="uploadLength" /> elements, or a new one.</summary>
        private GraphicsBuffer RentChunkBuffer(int uploadLength){
            for (int i = 0; i < _freeChunkBuffers.Count; i++){
                GraphicsBuffer free = _freeChunkBuffers[i];
                if (free.count < uploadLength) continue;
                _freeChunkBuffers.RemoveAt(i);
                return free;
            }

            int length = (uploadLength + ChunkBufferGranularity - 1) / ChunkBufferGranularity * ChunkBufferGranularity;
            return new GraphicsBuffer(Target.Structured, length, Marshal.SizeOf<uint2>());
        }

        private void ReleaseChunkBuffer(GraphicsBuffer buffer){
            if (_isDestroyed || _freeChunkBuffers.Count >= _chunkStoreSize){
                buffer.Dispose();
                return;
            }

            _freeChunkBuffers.Add(buffer);
        }

        /// <summary>
        ///     Removes a chunk's voxel data and light (LGT-08) from the GPU buffers when evicted.
        /// </summary>
        /// <param name="chunk">The chunk position.</param>
        private void RemoveChunkData(int2 chunk){
            if (_voxelDataBuffers.Remove(chunk, out GraphicsBuffer voxelBuffer)) ReleaseChunkBuffer(voxelBuffer); // PERF-04
            if (_lightDataBuffers.Remove(chunk, out GraphicsBuffer lightBuffer)) ReleaseChunkBuffer(lightBuffer); // LGT-08
        }

        /// <summary>
        ///     Removes render data for a partition when it's evicted.
        /// </summary>
        /// <param name="partition">The partition position.</param>
        private void RemovePartitionRenderData(int3 partition){
            if (_isDestroyed) return;

            _requestedPartitions.Remove(partition);
            _pendingBuilds.Remove(partition);

            bool changed = false;
            changed |= _solidBufferManager.ReleasePartition(partition);
            changed |= _transparentBufferManager.ReleasePartition(partition);
            changed |= _foliageBufferManager.ReleasePartition(partition);

            if (!changed) return;

            RequestBuffersRebuild();
        }

        /// <summary>
        ///     Represents an in-flight partition build operation.
        /// </summary>
        private struct InFlightBuild{ // PERF-04: a struct slot, not a class allocated per build
            /// <summary>
            ///     True while this slot holds a build.
            /// </summary>
            public readonly bool Active;

            /// <summary>
            ///     The point builder handler responsible for this build.
            /// </summary>
            public readonly PointBuilderHandler Handler;

            /// <summary>
            ///     The partition coordinates being built.
            /// </summary>
            public readonly int3 Partition;

            /// <summary>
            ///     Initializes a new instance of the InFlightBuild class.
            /// </summary>
            /// <param name="partition">The partition position.</param>
            /// <param name="handler">The point builder handler; its read-back state tells when the build is done.</param>
            public InFlightBuild(int3 partition, PointBuilderHandler handler){
                Active = true;
                Partition = partition;
                Handler = handler;
            }
        }
    }

    /// <summary>
    ///     Contains metadata about a partition for GPU processing.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct PartitionMetadata{
        /// <summary>
        ///     The partition grid coordinates.
        /// </summary>
        public int3 PartitionPos;

        /// <summary>
        ///     The world space position of the partition.
        /// </summary>
        public int3 PartitionWorldPos;

        /// <summary>
        ///     The minimum corner of the partition's axis-aligned bounding box.
        /// </summary>
        public float3 BoundsMin;

        /// <summary>
        ///     The maximum corner of the partition's axis-aligned bounding box.
        /// </summary>
        public float3 BoundsMax;
    }
}
