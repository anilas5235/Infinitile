using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Runtime.Engine.Components;
using Runtime.Engine.Utils.Collections;
using Runtime.Engine.Utils.Logger;
using Runtime.Engine.VoxelConfig.Data;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using static Runtime.Engine.Utils.VoxelConstants;
using static UnityEngine.GraphicsBuffer;

namespace Runtime.Engine.Behaviour
{
    public class VoxelWorldRenderer : MonoBehaviour
    {
        public const bool Logging = false;
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

        private PointBuilderHandler _pointBuilderHandler;
        private CopyPointsHandler _copyPointsHandler;

        private void Awake()
        {
            _solidBufferManager = new RenderBufferManager(solidMaterial, rebuildBuffers);
            _transparentBufferManager = new RenderBufferManager(transparentMaterial, rebuildBuffers);
            _foliageBufferManager = new RenderBufferManager(foliageMaterial, rebuildBuffers);

            VoxelRegistry voxelRegistry = VoxelDataImporter.Instance.VoxelRegistry;
            _pointBuilderHandler = new PointBuilderHandler(
                pointBuilder,
                voxelRegistry.VoxelRenderDefBuffer,
                voxelRegistry.QuadTexPairBuffer
            );
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

        private void OnDestroy()
        {
            _solidBufferManager.Dispose();
            _transparentBufferManager.Dispose();
            _foliageBufferManager.Dispose();

            foreach (GraphicsBuffer buffer in _voxelDataBuffers.Values)
            {
                buffer.Dispose();
            }

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

            uint2[] intervalData = new uint2[voxelData.CompressedLength];
            int i = 0;
            foreach (UnsafeIntervalList<ushort>.Node n in voxelData.Internal)
            {
                intervalData[i++] = new uint2(n.Value, (uint)n.Count);
            }

            if(Logging) VoxelEngineLogger.Info<VoxelWorldRenderer>(
                $"Adding/updating chunk {chunk} with {voxelData.Length} voxels in {voxelData.Internal.Length} intervals.");
            if(Logging) VoxelEngineLogger.Info<VoxelWorldRenderer>($"Intervals: {string.Join(", ", intervalData)}");
            if (voxelData.Length != VoxelsPerChunk) throw new Exception("Voxel data length mismatch!");
            GraphicsBuffer dataBuffer = new(Target.Structured, voxelData.CompressedLength, Marshal.SizeOf<uint2>());
            dataBuffer.SetData(intervalData);
            _voxelDataBuffers.Add(chunk, dataBuffer);
        }

        public async Awaitable<HashSet<int3>> UpdatePartitions(HashSet<int3> partitions)
        {
            HashSet<int3> updatedPartitions = new();
            foreach (int3 partition in partitions)
            {
                try
                {
                    if (!_voxelDataBuffers.TryGetValue(PartitionToChunkPos(partition), out GraphicsBuffer dataBuffer))
                        throw new Exception($"Voxel data buffer for partition {partition} not found.");
                    int[] counts = await _pointBuilderHandler.BuildPoints(partition, dataBuffer);
                    if(Logging)VoxelEngineLogger.Info<VoxelWorldRenderer>(
                        $"Partition {partition}: Solid={counts[0]}, Transparent={counts[1]}, Foliage={counts[2]}");
                    _copyPointsHandler.CopyJob(_pointBuilderHandler, partition, counts);
                    updatedPartitions.Add(partition);
                }
                catch (Exception e)
                {
                    if(Logging)VoxelEngineLogger.Error<VoxelWorldRenderer>($"Error updating partition {partition}: {e}");
                }
            }

            _solidBufferManager.RebuildBuffers();
            _transparentBufferManager.RebuildBuffers();
            _foliageBufferManager.RebuildBuffers();

            return updatedPartitions;
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