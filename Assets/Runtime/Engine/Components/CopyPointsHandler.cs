using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Runtime.Engine.Behaviour;
using Runtime.Engine.Utils.Logger;
using Unity.Mathematics;
using UnityEngine;
using static Runtime.Engine.Utils.VoxelRenderConstants;
using static UnityEngine.GraphicsBuffer;

namespace Runtime.Engine.Components
{
    public class CopyPointsHandler : IDisposable
    {
        private readonly ComputeShader _copyPoints;
        private readonly int _copyKernelID;
        private readonly RenderBufferManager _solidBufferManager;
        private readonly RenderBufferManager _transparentBufferManager;
        private readonly RenderBufferManager _foliageBufferManager;

        private readonly GraphicsBuffer _pageCountsBuffer;
        private readonly GraphicsBuffer _solidPagesBuffer;
        private readonly GraphicsBuffer _transparentPagesBuffer;
        private readonly GraphicsBuffer _foliagePagesBuffer;

        public CopyPointsHandler(ComputeShader copyPoints, RenderBufferManager solidBufferManager,
            RenderBufferManager transparentBufferManager, RenderBufferManager foliageBufferManager)
        {
            _copyPoints = copyPoints;
            _copyKernelID = copyPoints.FindKernel("CopyPoints");
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

            if(VoxelWorldRenderer.Logging)VoxelEngineLogger.Info<CopyPointsHandler>(
                $"Copying points for partition {partition}. Solid pages: {solidAlloc.Count}, Transparent pages: {transparentAlloc.Count}, Foliage pages: {foliageAlloc.Count}");
            if(VoxelWorldRenderer.Logging)VoxelEngineLogger.Info<CopyPointsHandler>(
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

                _copyPoints.SetBuffer(_copyKernelID, SolidPointsInNameID, pointBuilderHandler.SolidPointsOut);
                _copyPoints.SetBuffer(_copyKernelID, SolidPointsCopyOutNameID,
                    _solidBufferManager.GetBuffer(solidAlloc[0].BufferIndex));
                _copyPoints.SetBuffer(_copyKernelID, SolidPagesNameID, _solidPagesBuffer);
            }

            if (transparentPagesCount > 0)
            {
                uint2[] transparentPageData = transparentAlloc.Select(a => a.ToIndexAndCount()).ToArray();
                _transparentPagesBuffer.SetData(transparentPageData);

                _copyPoints.SetBuffer(_copyKernelID, TransparentPointsInNameID,
                    pointBuilderHandler.TransparentPointsOut);
                _copyPoints.SetBuffer(_copyKernelID, TransparentPointsCopyOutNameID,
                    _transparentBufferManager.GetBuffer(transparentAlloc[0].BufferIndex));
                _copyPoints.SetBuffer(_copyKernelID, TransparentPagesNameID, _transparentPagesBuffer);
            }

            if (foliagePagesCount > 0)
            {
                uint2[] foliagePageData = foliageAlloc.Select(a => a.ToIndexAndCount()).ToArray();
                _foliagePagesBuffer.SetData(foliagePageData);

                _copyPoints.SetBuffer(_copyKernelID, FoliagePointsInNameID, pointBuilderHandler.FoliagePointsOut);
                _copyPoints.SetBuffer(_copyKernelID, FoliagePointsCopyOutNameID,
                    _foliageBufferManager.GetBuffer(foliageAlloc[0].BufferIndex));
                _copyPoints.SetBuffer(_copyKernelID, FoliagePagesNameID, _foliagePagesBuffer);

                _copyPoints.SetBuffer(_copyKernelID, PageCountsNameID, _pageCountsBuffer);
                _copyPoints.SetInt(PointsPerPageNameID, PointsPerPage);
            }

            int maxPageCount = math.max(solidPagesCount, math.max(transparentPagesCount, foliagePagesCount));
            if (maxPageCount <= 0) return;

            _copyPoints.Dispatch(_copyKernelID, Mathf.CeilToInt(maxPageCount / 8f), 1, 1);
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