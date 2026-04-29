using System;
using System.Collections.Generic;
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
    public class VoxelWorldRenderer : Singleton<VoxelWorldRenderer>
    {
        public VoxelWorld world;
        private VoxelEngineSettings _settings;

        private readonly Dictionary<int2, GraphicsBuffer> _voxelDataBuffers = new();
        private readonly Queue<int3> _pendingPartitionBuilds = new();
        private readonly HashSet<int3> _pendingPartitionSet = new();
        private readonly HashSet<int3> _inFlightPartitionSet = new();
        private readonly List<InFlightBuild> _inFlightPartitionBuilds = new();
        private readonly Dictionary<int3, int> _partitionRequestVersions = new();
        private int _copyKernelID;
        private CopyPointsHandler _copyPointsHandler;
        private RenderBufferManager _foliageBufferManager;
        private bool _isDestroyed;

        private PointBuilderHandler[] _pointBuilderHandlers;

        private int _pointBuilderKernelID;
        private int _maxInFlight = 1;

        private RenderBufferManager _solidBufferManager;
        private RenderBufferManager _transparentBufferManager;
        private bool _buffersRebuildPending;

        protected override void Awake()
        {
            base.Awake();

            _settings = world.Settings;
            _maxInFlight = math.max(1, _settings.Scheduler.partitionBuildBatchSize);

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

        private void Update()
        {
            if (_isDestroyed) return;

            CompleteFinishedPartitionBuilds();
            StartPendingPartitionBuilds();
        }

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

        private void HandleChunkChange(Chunk chunk)
        {
            AddOrUpdateChunk(chunk.Position, chunk.VoxelData.GetData());
        }

        private void HandleChunkDataReady(Chunk chunk)
        {
            AddOrUpdateChunk(chunk.Position, chunk.VoxelData.GetData());
        }

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

            _pendingPartitionBuilds.Clear();
            _pendingPartitionSet.Clear();
            _inFlightPartitionSet.Clear();
            _inFlightPartitionBuilds.Clear();
            _partitionRequestVersions.Clear();

            _voxelDataBuffers.Clear();
        }

        private void Draw(ScriptableRenderContext context, Camera cam)
        {
            _solidBufferManager.Draw(cam);
            _transparentBufferManager.Draw(cam);
            _foliageBufferManager.Draw(cam);
        }

        private void LateUpdate()
        {
            if (_isDestroyed || !_buffersRebuildPending) return;

            _solidBufferManager.RebuildBuffers();
            _transparentBufferManager.RebuildBuffers();
            _foliageBufferManager.RebuildBuffers();
            _buffersRebuildPending = false;
        }

        private void RequestBuffersRebuild()
        {
            if (_isDestroyed) return;
            _buffersRebuildPending = true;
        }

        private void UpdatePartitions(HashSet<int3> partitions)
        {
            if (_isDestroyed || partitions == null) return;

            foreach (int3 partition in partitions) EnqueuePartitionBuild(partition);
        }

        private void EnqueuePartitionBuild(int3 partition, bool incrementVersion = true)
        {
            if (_isDestroyed) return;

            if (incrementVersion)
            {
                _partitionRequestVersions[partition] =
                    _partitionRequestVersions.TryGetValue(partition, out int version)
                        ? version + 1
                        : 1;
            }

            if (_pendingPartitionSet.Contains(partition) || _inFlightPartitionSet.Contains(partition)) return;

            _pendingPartitionBuilds.Enqueue(partition);
            _pendingPartitionSet.Add(partition);
        }

        private void StartPendingPartitionBuilds()
        {
            if (_pointBuilderHandlers == null || _pointBuilderHandlers.Length == 0) return;

            while (_inFlightPartitionSet.Count < _maxInFlight && _pendingPartitionBuilds.Count > 0)
            {
                int3 partition = _pendingPartitionBuilds.Dequeue();
                _pendingPartitionSet.Remove(partition);

                if (!_partitionRequestVersions.TryGetValue(partition, out int requestVersion)) continue;
                if (_inFlightPartitionSet.Contains(partition)) continue;

                PartitionBuildRequest request = new(partition);
                request.CollectBuffers(_voxelDataBuffers);

                if (!request.IsValid)
                {
                    VoxelEngineLogger.Error<VoxelWorldRenderer>(
                        $"Skipping partition {partition} due to missing neighbor data.");
                    continue;
                }

                int slotIndex = GetFreePointBuilderSlotIndex();
                if (slotIndex < 0)
                {
                    _pendingPartitionBuilds.Enqueue(partition);
                    _pendingPartitionSet.Add(partition);
                    break;
                }

                Awaitable<int[]> buildAwaitable = _pointBuilderHandlers[slotIndex].BuildPoints(request);
                _inFlightPartitionSet.Add(partition);
                _inFlightPartitionBuilds.Add(new InFlightBuild(partition, slotIndex, requestVersion,
                    buildAwaitable.GetAwaiter()));
            }
        }

        private int GetFreePointBuilderSlotIndex()
        {
            for (int i = 0; i < _pointBuilderHandlers.Length; i++)
            {
                if (!_inFlightPartitionBuilds.Exists(build => build.SlotIndex == i)) return i;
            }

            return -1;
        }

        private void CompleteFinishedPartitionBuilds()
        {
            bool updatedPartitions = false;

            for (int i = _inFlightPartitionBuilds.Count - 1; i >= 0; i--)
            {
                InFlightBuild build = _inFlightPartitionBuilds[i];
                if (!build.BuildAwaiter.IsCompleted) continue;

                _inFlightPartitionBuilds.RemoveAt(i);
                _inFlightPartitionSet.Remove(build.Partition);

                try
                {
                    if (!_partitionRequestVersions.TryGetValue(build.Partition, out int currentVersion))
                        continue;

                    Awaitable<int[]>.Awaiter buildAwaiter = build.BuildAwaiter;
                    int[] pointCounts = buildAwaiter.GetResult();
                    if (_isDestroyed) return;

                    if (build.RequestVersion != currentVersion)
                    {
                        EnqueuePartitionBuild(build.Partition, false);
                        continue;
                    }

                    _copyPointsHandler.CopyJob(_pointBuilderHandlers[build.SlotIndex], build.Partition, pointCounts);
                    updatedPartitions = true;
                }
                catch (Exception e)
                {
                    VoxelEngineLogger.Error<VoxelWorldRenderer>($"Error updating partition {build.Partition}: {e}");
                }
            }

            if (updatedPartitions) RequestBuffersRebuild();
        }


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

        private void RemoveChunkData(int2 chunk)
        {
            if (!_voxelDataBuffers.TryGetValue(chunk, out GraphicsBuffer existingBuffer)) return;

            existingBuffer.Dispose();
            _voxelDataBuffers.Remove(chunk);
        }

        private void RemovePartitionRenderData(int3 partition)
        {
            if (_isDestroyed) return;

            _partitionRequestVersions.Remove(partition);

            bool changed = false;
            changed |= _solidBufferManager.ReleasePartition(partition);
            changed |= _transparentBufferManager.ReleasePartition(partition);
            changed |= _foliageBufferManager.ReleasePartition(partition);

            if (!changed) return;

            RequestBuffersRebuild();
        }

        private struct InFlightBuild
        {
            public readonly int3 Partition;
            public readonly int SlotIndex;
            public readonly int RequestVersion;
            public readonly Awaitable<int[]>.Awaiter BuildAwaiter;

            public InFlightBuild(int3 partition, int slotIndex, int requestVersion,
                Awaitable<int[]>.Awaiter buildAwaiter)
            {
                Partition = partition;
                SlotIndex = slotIndex;
                RequestVersion = requestVersion;
                BuildAwaiter = buildAwaiter;
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PartitionMetadata
    {
        public int3 PartitionPos;
        public int3 PartitionWorldPos; // World partition coordinates
        public float3 BoundsMin; // AABB min for frustum culling
        public float3 BoundsMax; // AABB max
    }
}