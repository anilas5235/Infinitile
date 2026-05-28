using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Engine.Scripts.Data;
using Engine.Scripts.Settings;
using Engine.Scripts.Utils;
using Engine.Scripts.Utils.Collections;
using Engine.Scripts.Utils.Logger;
using Engine.Scripts.VoxelConfig.Data;
using Engine.Scripts.World;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using static Engine.Scripts.Utils.VoxelConstants;
using static UnityEngine.GraphicsBuffer;

namespace Engine.Scripts.Render
{
    [RequireComponent(typeof(VoxelWorld))]
    /// <summary>
    /// Main renderer component for the voxel world, managing partition building and rendering.
    /// Handles async GPU work for geometry generation and updates render buffers.
    /// </summary>
    public class VoxelWorldRenderer : Singleton<VoxelWorldRenderer>
    {
        /// <summary>
        /// Reference to the VoxelWorld instance.
        /// </summary>
        public VoxelWorld world;
        private VoxelEngineSettings _settings;

        private readonly Dictionary<int2, GraphicsBuffer> _voxelDataBuffers = new();
        private readonly List<int3> _pendingBuilds = new();
        private readonly HashSet<int3> _requestedPartitions = new();

        private InFlightBuild[] _inFlightBuilds;

        private int _copyKernelID;
        private CopyPointsHandler _copyPointsHandler;
        private bool _isDestroyed;

        private PointBuilderHandler[] _pointBuilderHandlers;

        private int _pointBuilderKernelID;
        private int _maxInFlight = 1;

        private RenderBufferManager _solidBufferManager;
        private RenderBufferManager _transparentBufferManager;
        private RenderBufferManager _foliageBufferManager;
        private bool _buffersRebuildPending;

        protected override void Awake()
        {
            base.Awake();

            _settings = world.Settings;
            _maxInFlight = math.max(1, _settings.Scheduler.partitionBuildBatchSize);

            _inFlightBuilds = new InFlightBuild[_maxInFlight];

            RendererSettings rSettings = _settings.Renderer;

            _solidBufferManager = new RenderBufferManager(
                rSettings.solidMaterial,
                rSettings.rebuildBuffers,
                rSettings
            );
            _transparentBufferManager = new RenderBufferManager(
                rSettings.transparentMaterial,
                rSettings.rebuildBuffers,
                rSettings
            );
            _foliageBufferManager = new RenderBufferManager(
                rSettings.foliageMaterial,
                rSettings.rebuildBuffers,
                rSettings
            );

            VoxelRegistry voxelRegistry = VoxelDataImporter.Instance.VoxelRegistry;
            _pointBuilderHandlers = new PointBuilderHandler[_maxInFlight];
            for (int i = 0; i < _pointBuilderHandlers.Length; i++)
            {
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
        /// Updates pending and in-flight partition builds each frame.
        /// </summary>
        private void Update()
        {
            if (_isDestroyed) return;

            CompleteFinishedPartitionBuilds();
            StartPendingPartitionBuilds();
        }

        /// <summary>
        /// Registers event handlers for world changes and rendering.
        /// </summary>
        private void OnEnable()
        {
            if (world == null) world = VoxelWorld.Instance;
            RenderPipelineManager.beginCameraRendering += Draw;
            world.ChunkChanged += HandleChunkChange;
            world.ChunkDataReady += HandleChunkDataReady;
            world.ChunkEvicted += RemoveChunkData;
            world.PartitionEvicted += RemovePartitionRenderData;
            world.PartitionBuildRequested += UpdatePartitions;
        }

        /// <summary>
        /// Unregisters event handlers when disabled.
        /// </summary>
        private void OnDisable()
        {
            RenderPipelineManager.beginCameraRendering -= Draw;
            if (world == null) return;

            world.ChunkChanged -= HandleChunkChange;
            world.ChunkDataReady -= HandleChunkDataReady;
            world.ChunkEvicted -= RemoveChunkData;
            world.PartitionEvicted -= RemovePartitionRenderData;
            world.PartitionBuildRequested -= UpdatePartitions;
        }

        /// <summary>
        /// Handles chunk change events by updating the voxel data buffer.
        /// </summary>
        /// <param name="chunk">The chunk that changed.</param>
        private void HandleChunkChange(Chunk chunk)
        {
            AddOrUpdateChunk(chunk.Position, chunk.VoxelData.GetData());
        }

        /// <summary>
        /// Handles chunk data ready events by updating the voxel data buffer.
        /// </summary>
        /// <param name="chunk">The chunk whose data is ready.</param>
        private void HandleChunkDataReady(Chunk chunk)
        {
            AddOrUpdateChunk(chunk.Position, chunk.VoxelData.GetData());
        }

        /// <summary>
        /// Cleans up all GPU resources when the renderer is destroyed.
        /// </summary>
        protected override void OnDestroy()
        {
            base.OnDestroy();
            _isDestroyed = true;
            _copyPointsHandler?.Dispose();

            if (_pointBuilderHandlers != null)
                foreach (PointBuilderHandler handler in _pointBuilderHandlers)
                    handler?.Dispose();

            _solidBufferManager.Dispose();
            _transparentBufferManager.Dispose();
            _foliageBufferManager.Dispose();

            foreach (GraphicsBuffer buffer in _voxelDataBuffers.Values) buffer.Dispose();

            _pendingBuilds.Clear();
            _requestedPartitions.Clear();

            _voxelDataBuffers.Clear();
        }

        /// <summary>
        /// Draws all render buffers for the given camera.
        /// </summary>
        /// <param name="context">The scriptable render context.</param>
        /// <param name="cam">The camera to render with.</param>
        private void Draw(ScriptableRenderContext context, Camera cam)
        {
            _solidBufferManager.Draw(cam);
            _transparentBufferManager.Draw(cam);
            _foliageBufferManager.Draw(cam);
        }

        /// <summary>
        /// Rebuilds render buffers if changes were made during this frame.
        /// </summary>
        private void LateUpdate()
        {
            if (_isDestroyed || !_buffersRebuildPending) return;

            _solidBufferManager.RebuildBuffers();
            _transparentBufferManager.RebuildBuffers();
            _foliageBufferManager.RebuildBuffers();
            _buffersRebuildPending = false;
        }

        /// <summary>
        /// Requests that render buffers be rebuilt on the next LateUpdate.
        /// </summary>
        private void RequestBuffersRebuild()
        {
            if (_isDestroyed) return;
            _buffersRebuildPending = true;
        }

        /// <summary>
        /// Processes partition update requests from the world.
        /// </summary>
        /// <param name="partitions">The set of partitions to update.</param>
        private void UpdatePartitions(HashSet<int3> partitions)
        {
            if (_isDestroyed || partitions == null) return;

            foreach (int3 partition in partitions)
            {
                _requestedPartitions.Add(partition);
                EnqueuePartitionBuild(partition);
            }
        }

        /// <summary>
        /// Adds a partition to the build queue if not already present.
        /// </summary>
        /// <param name="partition">The partition to build.</param>
        private void EnqueuePartitionBuild(int3 partition)
        {
            if (_isDestroyed) return;
            if (_pendingBuilds.Contains(partition)) return;

            _pendingBuilds.Add(partition);
        }

        /// <summary>
        /// Starts pending partition builds up to the max in-flight limit.
        /// </summary>
        private void StartPendingPartitionBuilds()
        {
            if (_pointBuilderHandlers == null || _pointBuilderHandlers.Length == 0) return;

            for (int i = 0; i < _maxInFlight; i++)
            {
                if (_pendingBuilds.Count < 1) break;
                if (_inFlightBuilds[i] != null) continue;

                int3 partition = _pendingBuilds.First();
                _pendingBuilds.Remove(partition);
                if (_inFlightBuilds.Any(build => build != null && build.Partition.Equals(partition)))
                {
                    EnqueuePartitionBuild(partition);
                    continue;
                }

                PartitionBuildRequest request = new(partition);
                request.CollectBuffers(_voxelDataBuffers);

                if (!request.IsValid)
                {
                    VoxelEngineLogger.Error<VoxelWorldRenderer>(
                        $"Skipping partition {partition} due to missing neighbor data.");
                    continue;
                }

                PointBuilderHandler handler = _pointBuilderHandlers[i];
                Awaitable<int[]> buildAwaitable = handler.BuildPoints(request);
                _inFlightBuilds[i] = new InFlightBuild(partition, handler, buildAwaitable.GetAwaiter());
            }
        }

        /// <summary>
        /// Completes finished partition builds and applies their results.
        /// </summary>
        private void CompleteFinishedPartitionBuilds()
        {
            bool updatedPartitions = false;

            for (int i = 0; i < _maxInFlight; i++)
            {
                InFlightBuild build = _inFlightBuilds[i];
                if(build == null) continue;
                if (!build.BuildAwaiter.IsCompleted) continue;

                _inFlightBuilds[i] = null;

                try
                {
                    Awaitable<int[]>.Awaiter buildAwaiter = build.BuildAwaiter;
                    int[] pointCounts = buildAwaiter.GetResult();
                    if (_isDestroyed) return;

                    if (!_requestedPartitions.Contains(build.Partition) || _pendingBuilds.Contains(build.Partition))
                        continue;

                    _copyPointsHandler.CopyJob(build.Handler, build.Partition, pointCounts);
                    updatedPartitions = true;
                }
                catch (Exception e)
                {
                    VoxelEngineLogger.Error<VoxelWorldRenderer>($"Error updating partition {build.Partition}: {e}");
                }
            }

            if (updatedPartitions) RequestBuffersRebuild();
        }


        /// <summary>
        /// Adds or updates a chunk's voxel data in the GPU buffer.
        /// </summary>
        /// <param name="chunk">The chunk position.</param>
        /// <param name="voxelData">The voxel data to store.</param>
        private void AddOrUpdateChunk(int2 chunk, UnsafeIntervalList<ushort> voxelData)
        {
            if (_voxelDataBuffers.TryGetValue(chunk, out GraphicsBuffer existingBuffer)) existingBuffer.Dispose();

            int compLength = voxelData.CompressedLength;
            uint2[] intervalData = new uint2[compLength + 1];
            int i = 1;
            foreach (UnsafeIntervalList<ushort>.Node n in voxelData.Internal)
                intervalData[i++] = new uint2(n.Value, (uint)n.Count);

            intervalData[0] = new uint2((uint)compLength, intervalData[compLength].y);

            if (voxelData.Length != VoxelsPerChunk) throw new Exception("Voxel data length mismatch!");
            GraphicsBuffer dataBuffer = new(Target.Structured, intervalData.Length, Marshal.SizeOf<uint2>());
            dataBuffer.SetData(intervalData);
            _voxelDataBuffers[chunk] = dataBuffer;
        }

        /// <summary>
        /// Removes a chunk's voxel data from the GPU buffer when evicted.
        /// </summary>
        /// <param name="chunk">The chunk position.</param>
        private void RemoveChunkData(int2 chunk)
        {
            if (!_voxelDataBuffers.TryGetValue(chunk, out GraphicsBuffer existingBuffer)) return;

            existingBuffer.Dispose();
            _voxelDataBuffers.Remove(chunk);
        }

        /// <summary>
        /// Removes render data for a partition when it's evicted.
        /// </summary>
        /// <param name="partition">The partition position.</param>
        private void RemovePartitionRenderData(int3 partition)
        {
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
        /// Represents an in-flight partition build operation.
        /// </summary>
        private class InFlightBuild
        {
            /// <summary>
            /// The partition coordinates being built.
            /// </summary>
            public readonly int3 Partition;
            
            /// <summary>
            /// The point builder handler responsible for this build.
            /// </summary>
            public readonly PointBuilderHandler Handler;
            
            /// <summary>
            /// The awaiter for the build operation's result.
            /// </summary>
            public readonly Awaitable<int[]>.Awaiter BuildAwaiter;

            /// <summary>
            /// Initializes a new instance of the InFlightBuild class.
            /// </summary>
            /// <param name="partition">The partition position.</param>
            /// <param name="handler">The point builder handler.</param>
            /// <param name="buildAwaiter">The build awaiter.</param>
            public InFlightBuild(int3 partition, PointBuilderHandler handler, Awaitable<int[]>.Awaiter buildAwaiter)
            {
                Partition = partition;
                Handler = handler;
                BuildAwaiter = buildAwaiter;
            }
        }
    }

    /// <summary>
    /// Contains metadata about a partition for GPU processing.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct PartitionMetadata
    {
        /// <summary>
        /// The partition grid coordinates.
        /// </summary>
        public int3 PartitionPos;
        
        /// <summary>
        /// The world space position of the partition.
        /// </summary>
        public int3 PartitionWorldPos;
        
        /// <summary>
        /// The minimum corner of the partition's axis-aligned bounding box.
        /// </summary>
        public float3 BoundsMin;
        
        /// <summary>
        /// The maximum corner of the partition's axis-aligned bounding box.
        /// </summary>
        public float3 BoundsMax;
    }
}