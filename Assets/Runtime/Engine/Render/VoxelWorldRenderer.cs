using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Runtime.Engine.Utils;
using Runtime.Engine.Utils.Collections;
using Runtime.Engine.Utils.Logger;
using Runtime.Engine.VoxelConfig.Data;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using static Runtime.Engine.Utils.VoxelRenderConstants;
using static Runtime.Engine.Utils.VoxelConstants;
using static UnityEngine.GraphicsBuffer;

namespace Runtime.Engine.Render
{
    public class VoxelWorldRenderer : Singleton<VoxelWorldRenderer>
    {
        public static bool Logging = true;
        public Material solidMaterial;
        public Material transparentMaterial;
        public Material foliageMaterial;

        public ComputeShader pointBuilder;
        public ComputeShader copyPoints;
        public ComputeShader rebuildBuffers;

        private int _pointBuilderKernelID;
        private int _copyKernelID;

        private RenderBufferManager _solidBufferManager;
        private RenderBufferManager _transparentBufferManager;
        private RenderBufferManager _foliageBufferManager;

        private readonly Dictionary<int2, GraphicsBuffer> _voxelDataBuffers = new();

        private PointBuilderHandler[] _pointBuilderHandlers;
        private CopyPointsHandler _copyPointsHandler;
        private bool _isDestroyed;

        private readonly struct InFlightBuild
        {
            public readonly int3 Partition;
            public readonly int SlotIndex;
            public readonly Awaitable<int[]> BuildAwaitable;

            public InFlightBuild(int3 partition, int slotIndex, Awaitable<int[]> buildAwaitable)
            {
                Partition = partition;
                SlotIndex = slotIndex;
                BuildAwaitable = buildAwaitable;
            }
        }

        protected override void Awake()
        {
            base.Awake();
            _solidBufferManager = new RenderBufferManager(solidMaterial, rebuildBuffers);
            _transparentBufferManager = new RenderBufferManager(transparentMaterial, rebuildBuffers);
            _foliageBufferManager = new RenderBufferManager(foliageMaterial, rebuildBuffers);

            VoxelRegistry voxelRegistry = VoxelDataImporter.Instance.VoxelRegistry;
            int maxInFlight = math.max(1, MaxDirtyUploadsPerFrame);
            _pointBuilderHandlers = new PointBuilderHandler[maxInFlight];
            for (int i = 0; i < _pointBuilderHandlers.Length; i++)
            {
                _pointBuilderHandlers[i] = new PointBuilderHandler(
                    pointBuilder,
                    voxelRegistry.VoxelRenderDefBuffer,
                    voxelRegistry.QuadTexPairBuffer
                );
            }

            _copyPointsHandler = new CopyPointsHandler(
                copyPoints,
                _solidBufferManager,
                _transparentBufferManager,
                _foliageBufferManager
            );
        }

        private void OnEnable()
        {
            RenderPipelineManager.beginCameraRendering += Draw;
        }

        private void OnDisable()
        {
            RenderPipelineManager.beginCameraRendering -= Draw;
        }

        protected override void OnDestroy()
        {
            base.OnDestroy();
            _isDestroyed = true;
            _copyPointsHandler?.Dispose();

            if (_pointBuilderHandlers != null)
            {
                foreach (PointBuilderHandler handler in _pointBuilderHandlers)
                {
                    handler?.Dispose();
                }
            }

            _solidBufferManager.Dispose();
            _transparentBufferManager.Dispose();
            _foliageBufferManager.Dispose();

            foreach (GraphicsBuffer buffer in _voxelDataBuffers.Values) buffer.Dispose();

            _voxelDataBuffers.Clear();
        }

        private void Draw(ScriptableRenderContext context, Camera cam)
        {
            _solidBufferManager.Draw(cam);
            _transparentBufferManager.Draw(cam);
            _foliageBufferManager.Draw(cam);
        }


        public void AddOrUpdateChunk(int2 chunk, UnsafeIntervalList<ushort> voxelData)
        {
            if (_voxelDataBuffers.TryGetValue(chunk, out GraphicsBuffer existingBuffer))
            {
                existingBuffer.Dispose();
            }

            int compLength = voxelData.CompressedLength;
            uint2[] intervalData = new uint2[compLength + 1];
            int i = 1;
            foreach (UnsafeIntervalList<ushort>.Node n in voxelData.Internal)
            {
                intervalData[i++] = new uint2(n.Value, (uint)n.Count);
            }

            intervalData[0] = new uint2((uint)compLength, intervalData[compLength].y);

            if (Logging)
                VoxelEngineLogger.Info<VoxelWorldRenderer>(
                    $"Adding/updating chunk {chunk} with {voxelData.Length} voxels in {voxelData.Internal.Length} intervals.");
            if (Logging) VoxelEngineLogger.Info<VoxelWorldRenderer>($"Intervals: {string.Join(", ", intervalData)}");
            if (voxelData.Length != VoxelsPerChunk) throw new Exception("Voxel data length mismatch!");
            GraphicsBuffer dataBuffer = new(Target.Structured, intervalData.Length, Marshal.SizeOf<uint2>());
            dataBuffer.SetData(intervalData);
            _voxelDataBuffers[chunk] = dataBuffer;
        }

        public async Awaitable<HashSet<int3>> UpdatePartitions(HashSet<int3> partitions)
        {
            HashSet<int3> updatedPartitions = new();

            if (_isDestroyed || _pointBuilderHandlers == null || _pointBuilderHandlers.Length == 0)
            {
                return updatedPartitions;
            }

            int maxInFlight = math.max(1, MaxDirtyUploadsPerFrame);
            Queue<InFlightBuild> inFlight = new(maxInFlight);
            int nextSlotIndex = 0;

            foreach (int3 partition in partitions)
            {
                if (!_voxelDataBuffers.TryGetValue(PartitionToChunkPos(partition), out GraphicsBuffer dataBuffer))
                {
                    if (Logging)
                        VoxelEngineLogger.Error<VoxelWorldRenderer>(
                            $"Voxel data buffer for partition {partition} not found.");
                    continue;
                }

                GraphicsBuffer[] neighbors = new GraphicsBuffer[8];
                int2[] neighborsOffsets =
                    { new(0, 1), new(1, 1), new(1, 0), new(1, -1), new(0, -1), new(-1, -1), new(-1, 0), new(-1, 1) };
                bool vaild = true;
                for (int i = 0; i < neighborsOffsets.Length; i++)
                {
                    int2 neighborChunkPos = PartitionToChunkPos(partition) + neighborsOffsets[i];
                    if (!_voxelDataBuffers.TryGetValue(neighborChunkPos, out GraphicsBuffer neighborBuffer))
                    {
                        if (Logging)
                            VoxelEngineLogger.Error<VoxelWorldRenderer>(
                                $"Neighbor voxel data buffer for partition {partition} not found at offset {neighborsOffsets[i]}.");
                        vaild = false;
                        break;
                    }

                    neighbors[i] = neighborBuffer;
                }

                if (!vaild)
                {
                    if (Logging)
                        VoxelEngineLogger.Error<VoxelWorldRenderer>(
                            $"Skipping partition {partition} due to missing neighbor data.");
                    continue;
                }

                int slotIndex = nextSlotIndex % _pointBuilderHandlers.Length;
                nextSlotIndex++;
                Awaitable<int[]> buildAwaitable =
                    _pointBuilderHandlers[slotIndex].BuildPoints(partition, dataBuffer, neighbors);
                inFlight.Enqueue(new InFlightBuild(partition, slotIndex, buildAwaitable));

                if (inFlight.Count >= maxInFlight)
                {
                    await CompleteBuild(inFlight.Dequeue(), updatedPartitions);
                    if (_isDestroyed) return updatedPartitions;
                }
            }

            while (inFlight.Count > 0)
            {
                await CompleteBuild(inFlight.Dequeue(), updatedPartitions);
                if (_isDestroyed) return updatedPartitions;
            }

            if (updatedPartitions.Count > 0)
            {
                _solidBufferManager.RebuildBuffers();
                _transparentBufferManager.RebuildBuffers();
                _foliageBufferManager.RebuildBuffers();
            }

            return updatedPartitions;
        }

        private async Awaitable CompleteBuild(InFlightBuild build, HashSet<int3> updatedPartitions)
        {
            try
            {
                int[] counts = await build.BuildAwaitable;
                if (Logging)
                    VoxelEngineLogger.Info<VoxelWorldRenderer>(
                        $"Partition {build.Partition}: Solid={counts[0]}, Transparent={counts[1]}, Foliage={counts[2]}");

                if (_isDestroyed) return;

                _copyPointsHandler.CopyJob(_pointBuilderHandlers[build.SlotIndex], build.Partition, counts);
                updatedPartitions.Add(build.Partition);
            }
            catch (Exception e)
            {
                if (Logging)
                    VoxelEngineLogger.Error<VoxelWorldRenderer>($"Error updating partition {build.Partition}: {e}");
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
    };
}