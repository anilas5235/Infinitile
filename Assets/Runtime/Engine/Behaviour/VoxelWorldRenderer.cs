using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Runtime.Engine.Components;
using Runtime.Engine.Jobs.Meshing;
using Runtime.Engine.Utils.Collections;
using Runtime.Engine.Utils.Logger;
using Runtime.Engine.VoxelConfig.Data;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using static Runtime.Engine.Utils.VoxelConstants;
using static Runtime.Engine.Utils.VoxelRenderConstants;
using static Runtime.Engine.Components.RenderBufferManager;
using static UnityEngine.GraphicsBuffer;

namespace Runtime.Engine.Behaviour
{
    public class VoxelWorldRenderer : MonoBehaviour
    {
        public Material solidMaterial;
        public Material transparentMaterial;
        public Material foliageMaterial;

        public ComputeShader pointBuilder;
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
            _solidBufferManager = new RenderBufferManager(solidMaterial);
            _transparentBufferManager = new RenderBufferManager(transparentMaterial);
            _foliageBufferManager = new RenderBufferManager(foliageMaterial);

            VoxelRegistry voxelRegistry = VoxelDataImporter.Instance.VoxelRegistry;
            _pointBuilderHandler = new PointBuilderHandler(
                pointBuilder,
                voxelRegistry.VoxelRenderDefBuffer,
                voxelRegistry.QuadTexPairBuffer
            );
            _copyPointsHandler = new CopyPointsHandler(
                pointBuilder,
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

            VoxelEngineLogger.Info<VoxelWorldRenderer>(
                $"Adding/updating chunk {chunk} with {voxelData.Length} voxels in {voxelData.Internal.Length} intervals.");
            VoxelEngineLogger.Info<VoxelWorldRenderer>($"Intervals: {string.Join(", ", intervalData)}");
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
                    _pointBuilderHandler.BuildPoints(partition, dataBuffer);
                    int[] counts = await _pointBuilderHandler.ReadBackCounters();
                    VoxelEngineLogger.Info<VoxelWorldRenderer>(
                        $"Partition {partition}: Solid={counts[0]}, Transparent={counts[1]}, Foliage={counts[2]}");
                    _copyPointsHandler.CopyJob(_pointBuilderHandler, partition, counts);
                    updatedPartitions.Add(partition);
                }
                catch (Exception e)
                {
                    VoxelEngineLogger.Error<VoxelWorldRenderer>($"Error updating partition {partition}: {e}");
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

    public class PointBuilderHandler : IDisposable
    {
        private readonly ComputeShader _pointBuilder;
        private readonly int _pointBuilderKernelID;

        private readonly GraphicsBuffer _voxelRenderDefBuffer;
        private readonly GraphicsBuffer _voxelQuadTexPairBuffer;
        private readonly GraphicsBuffer _metadata;

        private readonly GraphicsBuffer _readBackCountBuffer;
        private NativeArray<uint> _counts;

        public GraphicsBuffer SolidPointsOut { get; }

        public GraphicsBuffer TransparentPointsOut { get; }

        public GraphicsBuffer FoliagePointsOut { get; }

        public PointBuilderHandler(ComputeShader pointBuilder, GraphicsBuffer voxelRenderDef,
            GraphicsBuffer voxelQuadTexPair)
        {
            _pointBuilder = pointBuilder;
            _voxelRenderDefBuffer = voxelRenderDef;
            _voxelQuadTexPairBuffer = voxelQuadTexPair;
            _pointBuilderKernelID = pointBuilder.FindKernel("RebuildPoints");

            int vSize = Marshal.SizeOf<Vertex>();
            SolidPointsOut = new GraphicsBuffer(Target.Append, MaxPointsPerPartition, vSize);
            TransparentPointsOut = new GraphicsBuffer(Target.Append, MaxPointsPerPartition, vSize);
            FoliagePointsOut = new GraphicsBuffer(Target.Append, MaxPointsPerPartition, vSize);

            _metadata = new GraphicsBuffer(Target.Structured, 1, Marshal.SizeOf<PartitionMetadata>());
            _readBackCountBuffer = new GraphicsBuffer(Target.Raw, 3, sizeof(uint));
            _counts = new NativeArray<uint>(_readBackCountBuffer.count, Allocator.Domain);
        }

        public void BuildPoints(int3 partition, GraphicsBuffer voxelData)
        {
            PartitionMetadata meta = new()
            {
                PartitionPos = partition,
                PartitionWorldPos = PartitionToWorldPos(partition),
            };
            _metadata.SetData(new[] { meta });

            _pointBuilder.SetBuffer(_pointBuilderKernelID, VoxelRenderDefNameID, _voxelRenderDefBuffer);
            _pointBuilder.SetInt(VoxelRenderDefCountNameID, _voxelRenderDefBuffer.count);

            _pointBuilder.SetBuffer(_pointBuilderKernelID, VoxelQuadTexPairNameID, _voxelQuadTexPairBuffer);
            _pointBuilder.SetInt(VoxelQuadTexPairCountNameID, _voxelQuadTexPairBuffer.count);

            _pointBuilder.SetBuffer(_pointBuilderKernelID, VoxelDataNameID, voxelData);
            _pointBuilder.SetInt(VoxelCompressedCountNameID, voxelData.count);

            _pointBuilder.SetBuffer(_pointBuilderKernelID, MetadataNameID, _metadata);
            _pointBuilder.SetBuffer(_pointBuilderKernelID, SolidPointsOutNameID, SolidPointsOut);
            _pointBuilder.SetBuffer(_pointBuilderKernelID, TransparentPointsOutNameID, TransparentPointsOut);
            _pointBuilder.SetBuffer(_pointBuilderKernelID, FoliagePointsOutNameID, FoliagePointsOut);

            _pointBuilder.SetInt(PartitionIndexNameID, 0);

            _pointBuilder.Dispatch(_pointBuilderKernelID, 4, 4, 4);
        }

        public async Awaitable<int[]> ReadBackCounters()
        {
            try
            {
                CopyCount(SolidPointsOut, _readBackCountBuffer, sizeof(uint) * 0);
                CopyCount(TransparentPointsOut, _readBackCountBuffer, sizeof(uint) * 1);
                CopyCount(FoliagePointsOut, _readBackCountBuffer, sizeof(uint) * 2);

                await AsyncGPUReadback.RequestIntoNativeArrayAsync(ref _counts, _readBackCountBuffer);
                return _counts.Select(c => (int)c).ToArray();
            }
            catch (Exception e)
            {
                VoxelEngineLogger.Error<PointBuilderHandler>($"Error reading back point counts: {e}");
                return Array.Empty<int>();
            }
        }

        public void ResetCounters()
        {
            SolidPointsOut.SetCounterValue(0);
            TransparentPointsOut.SetCounterValue(0);
            FoliagePointsOut.SetCounterValue(0);
        }

        public void Dispose()
        {
            SolidPointsOut?.Dispose();
            TransparentPointsOut?.Dispose();
            FoliagePointsOut?.Dispose();
            _metadata?.Dispose();
            _readBackCountBuffer?.Dispose();
            _counts.Dispose();
        }
    }

    public class CopyPointsHandler : IDisposable
    {
        private readonly ComputeShader _pointBuilder;
        private readonly int _copyKernelID;
        private readonly RenderBufferManager _solidBufferManager;
        private readonly RenderBufferManager _transparentBufferManager;
        private readonly RenderBufferManager _foliageBufferManager;

        private readonly GraphicsBuffer _pageCountsBuffer;
        private readonly GraphicsBuffer _solidPagesBuffer;
        private readonly GraphicsBuffer _transparentPagesBuffer;
        private readonly GraphicsBuffer _foliagePagesBuffer;

        public CopyPointsHandler(ComputeShader pointBuilder, RenderBufferManager solidBufferManager,
            RenderBufferManager transparentBufferManager, RenderBufferManager foliageBufferManager)
        {
            _pointBuilder = pointBuilder;
            _copyKernelID = pointBuilder.FindKernel("CopyPoints");
            _solidBufferManager = solidBufferManager;
            _transparentBufferManager = transparentBufferManager;
            _foliageBufferManager = foliageBufferManager;

            _pageCountsBuffer = new GraphicsBuffer(Target.Structured, 3, sizeof(uint));
            _solidPagesBuffer = new GraphicsBuffer(Target.Structured, PagesPerBuffer, Marshal.SizeOf<uint2>());
            _transparentPagesBuffer = new GraphicsBuffer(Target.Structured, PagesPerBuffer, Marshal.SizeOf<uint2>());
            _foliagePagesBuffer = new GraphicsBuffer(Target.Structured, PagesPerBuffer, Marshal.SizeOf<uint2>());
        }

        internal void CopyJob(PointBuilderHandler pointBuilderHandler, int3 partition, int[] counts)
        {
            List<AllocInfo> solidAlloc = _solidBufferManager.AllocBufferSpace(partition, counts[0]);
            List<AllocInfo> transparentAlloc = _transparentBufferManager.AllocBufferSpace(partition, counts[1]);
            List<AllocInfo> foliageAlloc = _foliageBufferManager.AllocBufferSpace(partition, counts[2]);

            VoxelEngineLogger.Info<CopyPointsHandler>(
                $"Copying points for partition {partition}. Solid pages: {solidAlloc.Count}, Transparent pages: {transparentAlloc.Count}, Foliage pages: {foliageAlloc.Count}");
            VoxelEngineLogger.Info<CopyPointsHandler>(
                $"Remaining Pages: Solid={_solidBufferManager.RemainingPages}, Transparent={_transparentBufferManager.RemainingPages}, Foliage={_foliageBufferManager.RemainingPages}");

            int solidPagesCount = solidAlloc.Count;
            int transparentPagesCount = transparentAlloc.Count;
            int foliagePagesCount = foliageAlloc.Count;

            uint[] pageCounts = { (uint)solidPagesCount, (uint)transparentPagesCount, (uint)foliagePagesCount };
            _pageCountsBuffer.SetData(pageCounts);

            if (solidPagesCount > 0)
            {
                uint2[] solidPageData = solidAlloc.Select(a => a.ToIndexAndCount()).ToArray();
                _solidPagesBuffer.SetData(solidPageData);

                _pointBuilder.SetBuffer(_copyKernelID, SolidPointsInNameID, pointBuilderHandler.SolidPointsOut);
                _pointBuilder.SetBuffer(_copyKernelID, SolidPointsCopyOutNameID,
                    _solidBufferManager.GetBuffer(solidAlloc[0].BufferIndex));
                _pointBuilder.SetBuffer(_copyKernelID, SolidPagesNameID, _solidPagesBuffer);
            }

            if (transparentPagesCount > 0)
            {
                uint2[] transparentPageData = transparentAlloc.Select(a => a.ToIndexAndCount()).ToArray();
                _transparentPagesBuffer.SetData(transparentPageData);

                _pointBuilder.SetBuffer(_copyKernelID, TransparentPointsInNameID,
                    pointBuilderHandler.TransparentPointsOut);
                _pointBuilder.SetBuffer(_copyKernelID, TransparentPointsCopyOutNameID,
                    _transparentBufferManager.GetBuffer(transparentAlloc[0].BufferIndex));
                _pointBuilder.SetBuffer(_copyKernelID, TransparentPagesNameID, _transparentPagesBuffer);
            }

            if (foliagePagesCount > 0)
            {
                uint2[] foliagePageData = foliageAlloc.Select(a => a.ToIndexAndCount()).ToArray();
                _foliagePagesBuffer.SetData(foliagePageData);

                _pointBuilder.SetBuffer(_copyKernelID, FoliagePointsInNameID, pointBuilderHandler.FoliagePointsOut);
                _pointBuilder.SetBuffer(_copyKernelID, FoliagePointsCopyOutNameID,
                    _foliageBufferManager.GetBuffer(foliageAlloc[0].BufferIndex));
                _pointBuilder.SetBuffer(_copyKernelID, FoliagePagesNameID, _foliagePagesBuffer);

                _pointBuilder.SetBuffer(_copyKernelID, PageCountsNameID, _pageCountsBuffer);
                _pointBuilder.SetInt(PointsPerPageNameID, PointsPerPage);
            }

            int maxPageCount = math.max(solidPagesCount, math.max(transparentPagesCount, foliagePagesCount));
            if (maxPageCount <= 0) return;

            _pointBuilder.Dispatch(_copyKernelID, Mathf.CeilToInt(maxPageCount / 8f), 1, 1);

            pointBuilderHandler.ResetCounters();
        }

        public void Dispose()
        {
            _pageCountsBuffer?.Dispose();
            _solidPagesBuffer?.Dispose();
            _transparentPagesBuffer?.Dispose();
            _foliagePagesBuffer?.Dispose();
        }
    }
}