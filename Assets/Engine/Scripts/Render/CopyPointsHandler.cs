using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Engine.Scripts.Utils.Logger;
using Unity.Mathematics;
using UnityEngine;
using static Engine.Scripts.Utils.VoxelRenderConstants;
using static UnityEngine.GraphicsBuffer;

namespace Engine.Scripts.Render
{
    /// <summary>
    /// Handles copying of point data from point builders into render buffers using compute shaders.
    /// Manages point distribution across solid, transparent, and foliage buffer managers.
    /// </summary>
    public class CopyPointsHandler : IDisposable
    {
        private readonly int _copyKernelID;
        private readonly ComputeShader _copyPoints;
        private readonly RenderBufferManager _foliageBufferManager;
        private readonly GraphicsBuffer _foliagePagesBuffer;

        private readonly GraphicsBuffer _pageCountsBuffer;
        private readonly RenderBufferManager _solidBufferManager;
        private readonly GraphicsBuffer _solidPagesBuffer;
        private readonly RenderBufferManager _transparentBufferManager;
        private readonly GraphicsBuffer _transparentPagesBuffer;

        /// <summary>
        /// Initializes a new instance of the CopyPointsHandler class.
        /// </summary>
        /// <param name="copyPoints">The compute shader used for copying points.</param>
        /// <param name="solidBufferManager">The render buffer manager for solid points.</param>
        /// <param name="transparentBufferManager">The render buffer manager for transparent points.</param>
        /// <param name="foliageBufferManager">The render buffer manager for foliage points.</param>
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

        /// <summary>
        /// Releases all GPU resources held by this handler.
        /// </summary>
        public void Dispose()
        {
            _pageCountsBuffer?.Dispose();
            _solidPagesBuffer?.Dispose();
            _transparentPagesBuffer?.Dispose();
            _foliagePagesBuffer?.Dispose();
        }

        /// <summary>
        /// Copies point data from the point builder into the respective render buffers.
        /// </summary>
        /// <param name="pointBuilderHandler">The point builder handler containing built points.</param>
        /// <param name="partition">The partition coordinates for the data.</param>
        /// <param name="counts">Array of point counts for solid, transparent, and foliage types.</param>
        internal void CopyJob(PointBuilderHandler pointBuilderHandler, int3 partition, int[] counts)
        {
            AllocInfo solidAlloc = _solidBufferManager.AllocBufferSpace(partition, counts[0]);
            AllocInfo transparentAlloc = _transparentBufferManager.AllocBufferSpace(partition, counts[1]);
            AllocInfo foliageAlloc = _foliageBufferManager.AllocBufferSpace(partition, counts[2]);
            
            int solidPagesCount = solidAlloc.Count;
            int transparentPagesCount = transparentAlloc.Count;
            int foliagePagesCount = foliageAlloc.Count;

            uint[] pageCounts = { (uint)solidPagesCount, (uint)transparentPagesCount, (uint)foliagePagesCount };
            _pageCountsBuffer.SetData(pageCounts);

            if (solidPagesCount > 0)
            {
                uint2[] solidPageData = solidAlloc.ToIndexAndCount();
                _solidPagesBuffer.SetData(solidPageData);

                _copyPoints.SetBuffer(_copyKernelID, SolidPointsInNameID, pointBuilderHandler.SolidPointsOut);
                _copyPoints.SetBuffer(_copyKernelID, SolidPointsCopyOutNameID,
                    _solidBufferManager.GetBuffer(solidAlloc.BufferIndex));
                _copyPoints.SetBuffer(_copyKernelID, SolidPagesNameID, _solidPagesBuffer);
            }

            if (transparentPagesCount > 0)
            {
                uint2[] transparentPageData = transparentAlloc.ToIndexAndCount();
                _transparentPagesBuffer.SetData(transparentPageData);

                _copyPoints.SetBuffer(_copyKernelID, TransparentPointsInNameID,
                    pointBuilderHandler.TransparentPointsOut);
                _copyPoints.SetBuffer(_copyKernelID, TransparentPointsCopyOutNameID,
                    _transparentBufferManager.GetBuffer(transparentAlloc.BufferIndex));
                _copyPoints.SetBuffer(_copyKernelID, TransparentPagesNameID, _transparentPagesBuffer);
            }

            if (foliagePagesCount > 0)
            {
                uint2[] foliagePageData = foliageAlloc.ToIndexAndCount();
                _foliagePagesBuffer.SetData(foliagePageData);

                _copyPoints.SetBuffer(_copyKernelID, FoliagePointsInNameID, pointBuilderHandler.FoliagePointsOut);
                _copyPoints.SetBuffer(_copyKernelID, FoliagePointsCopyOutNameID,
                    _foliageBufferManager.GetBuffer(foliageAlloc.BufferIndex));
                _copyPoints.SetBuffer(_copyKernelID, FoliagePagesNameID, _foliagePagesBuffer);

                _copyPoints.SetBuffer(_copyKernelID, PageCountsNameID, _pageCountsBuffer);
                _copyPoints.SetInt(PointsPerPageNameID, PointsPerPage);
            }

            int maxPageCount = math.max(solidPagesCount, math.max(transparentPagesCount, foliagePagesCount));
            if (maxPageCount <= 0) return;

            _copyPoints.Dispatch(_copyKernelID, Mathf.CeilToInt(maxPageCount / 8f), 1, 1);
        }
    }
}