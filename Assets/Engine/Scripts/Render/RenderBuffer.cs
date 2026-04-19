using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Engine.Scripts.Jobs.Meshing;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using static Engine.Scripts.Utils.VoxelRenderConstants;
using static UnityEngine.GraphicsBuffer;

namespace Engine.Scripts.Render
{
    internal class RenderBuffer : IDisposable
    {
        private static readonly uint[] DefaultArgs = { 0u, 1u, 0u, 0u, 0u };
        private readonly GraphicsBuffer _argsBuffer;
        private readonly GraphicsBuffer _countsPerPageBuffer;
        private readonly GraphicsBuffer _indexBuffer;
        public GraphicsBuffer PointBuffer { get; }

        private readonly RenderBufferManager _manager;
        private readonly MaterialPropertyBlock _propertyBlock;
        private NativeArray<uint> _countsPerPage;
        private NativeQueue<int> _freePages;

        private bool _stateBufferDirty;
        private bool _shouldDraw;
        private uint _totalValidPoints;
        public int FreePages => _freePages.Count;
        public int BufferIndex { get; }

        public RenderBuffer(RenderBufferManager manager, int bufferIndex)
        {
            _manager = manager;
            BufferIndex = bufferIndex;
            PointBuffer = new GraphicsBuffer(Target.Structured, RenderBufferSize,
                Marshal.SizeOf<Vertex>());
            _argsBuffer = new GraphicsBuffer(Target.IndirectArguments, 5, sizeof(uint));
            _argsBuffer.SetData(DefaultArgs);
            _indexBuffer = new GraphicsBuffer(Target.Append, RenderBufferSize,
                Marshal.SizeOf<uint>());

            _countsPerPage = new NativeArray<uint>(PagesPerBuffer, Allocator.Domain);
            _freePages = new NativeQueue<int>(Allocator.Domain);
            for (int i = 0; i < PagesPerBuffer; i++) _freePages.Enqueue(i);

            _countsPerPageBuffer = new GraphicsBuffer(Target.Structured, PagesPerBuffer, sizeof(uint));

            _propertyBlock = new MaterialPropertyBlock();
            _propertyBlock.SetBuffer(PointDataNameID, PointBuffer);
            _propertyBlock.SetBuffer(IndexBufferNameID, _indexBuffer);
        }

        public void Dispose()
        {
            PointBuffer?.Dispose();
            _argsBuffer?.Dispose();
            _indexBuffer?.Dispose();
            _countsPerPageBuffer?.Dispose();
            _countsPerPage.Dispose();
            _freePages.Dispose();
        }

        public bool TryAllocPages(int pointCount, int numPages, out AllocInfo allocation)
        {
            allocation = new AllocInfo(BufferIndex);
            if (numPages <= 0)
                throw new ArgumentOutOfRangeException(nameof(numPages), "Must allocate at least one page.");
            if (numPages > FreePages) return false;

            int remainingPoints = pointCount;
            for (int i = 0; i < numPages; i++)
            {
                int pageIndex = _freePages.Dequeue();
                int pointsForPage = math.min(PointsPerPage, remainingPoints);
                allocation.AddPage(new AllocInfo.AllocPage(pageIndex, pointsForPage));
                SetPageCount(pageIndex, (uint)pointsForPage);

                remainingPoints -= PointsPerPage;
            }

            _stateBufferDirty = true;
            if (!_shouldDraw) _shouldDraw = true;
            return true;
        }

        private bool IsPageFree(int index) => _countsPerPage[index] == 0;

        private void SetPageCount(int index, uint count)
        {
            _totalValidPoints -= _countsPerPage[index];
            _totalValidPoints += count;
            _countsPerPage[index] = count;
        }

        public void ClearPage(in AllocInfo allocInfo)
        {
            if(allocInfo.Count == 0) return;
            
            foreach (int index in allocInfo.GetPageIndices())
            {
                ValidOrThrow(index);
                if (IsPageFree(index)) continue;

                SetPageCount(index, 0);
                _freePages.Enqueue(index);
            }

            _stateBufferDirty = true;
            if (_shouldDraw && _freePages.Count == PagesPerBuffer) _shouldDraw = false;
        }

        public void Draw(Material mat, Camera cam)
        {
            if (!_shouldDraw) return;
            Graphics.DrawProceduralIndirect(
                mat,
                new Bounds(Vector3.zero, Vector3.one * 100000),
                MeshTopology.Triangles,
                _argsBuffer,
                0,
                cam,
                _propertyBlock,
                ShadowCastingMode.Off,
                false
            );
        }

        public void RebuildBuffers()
        {
            if (!_stateBufferDirty) return;

            _countsPerPageBuffer.SetData(_countsPerPage);
            _indexBuffer.SetCounterValue(0);

            ComputeShader rebuild = _manager.RebuildBufferShader;
            int kernel = _manager.RebuildKernel;

            int groupX = (int)math.ceil(PagesPerBuffer / 128f);

            rebuild.SetBuffer(kernel, IndexBufferNameID, _indexBuffer);
            rebuild.SetBuffer(kernel, ArgsBufferNameID, _argsBuffer);
            rebuild.SetBuffer(kernel, CountsPerPageNameID, _countsPerPageBuffer);

            rebuild.SetInt(TotalPointCountNameID, (int)_totalValidPoints);
            rebuild.SetInt(PointsPerPageNameID, PointsPerPage);
            rebuild.SetInt(PagesPerBufferNameID, PagesPerBuffer);
            rebuild.Dispatch(kernel, groupX, 1, 1);

            _stateBufferDirty = false;
        }

        private static void ValidOrThrow(int index)
        {
            if (index is < 0 or >= PagesPerBuffer) throw new ArgumentOutOfRangeException(nameof(index));
        }
    }
}