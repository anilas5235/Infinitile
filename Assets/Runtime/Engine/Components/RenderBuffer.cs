using System;
using System.Runtime.InteropServices;
using Runtime.Engine.Behaviour;
using Runtime.Engine.Jobs.Meshing;
using Runtime.Engine.Utils.Logger;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using static Runtime.Engine.Utils.VoxelRenderConstants;
using static UnityEngine.GraphicsBuffer;

namespace Runtime.Engine.Components
{
    internal class RenderBuffer : IDisposable
    {
        private static readonly uint[] DefaultArgs = { 0u, 1u, 0u, 0u, 0u };

        private readonly RenderBufferManager _manager;
        private readonly GraphicsBuffer _buffer;
        private uint _totalValidPoints;
        private readonly GraphicsBuffer _argsBuffer;
        private readonly GraphicsBuffer _indexBuffer;
        private readonly GraphicsBuffer _countsPerPageBuffer;
        private readonly MaterialPropertyBlock _propertyBlock;


        private NativeArray<uint> _indices;
        private NativeArray<uint> _countsPerPage;

        private bool _stateBufferDirty;
        private int _usedPageCount;

        private int _lastFreePageIndex;
        private int _highestUsedPageIndex;

        public GraphicsBuffer Buffer => _buffer;
        public int FreePages => PagesPerBuffer - _usedPageCount;

        public RenderBuffer(RenderBufferManager manager)
        {
            _manager = manager;
            _buffer = new GraphicsBuffer(Target.Structured, RenderBufferSize,
                Marshal.SizeOf<Vertex>());
            _argsBuffer = new GraphicsBuffer(Target.IndirectArguments, 5, sizeof(uint));
            _argsBuffer.SetData(DefaultArgs);
            _indexBuffer = new GraphicsBuffer(Target.Append, RenderBufferSize,
                Marshal.SizeOf<uint>());
            _indices = new NativeArray<uint>(RenderBufferSize, Allocator.Domain);

            _countsPerPage = new NativeArray<uint>(PagesPerBuffer, Allocator.Domain);
            _countsPerPageBuffer = new GraphicsBuffer(Target.Structured, PagesPerBuffer, sizeof(uint));
            _lastFreePageIndex = 0;
            
            _propertyBlock = new MaterialPropertyBlock();
            _propertyBlock.SetBuffer(PointDataNameID, _buffer);
            _propertyBlock.SetBuffer(IndexBufferNameID, _indexBuffer);
        }

        private bool IsPageFree(int index) => _countsPerPage[index] == 0;
        private bool IsPageNotFree(int index) => _countsPerPage[index] != 0;

        private void SetPageCount(int index, uint count)
        {
            _totalValidPoints -= _countsPerPage[index];
            _totalValidPoints += count;
            _countsPerPage[index] = count;
        }

        internal void SetPage(in AllocInfo allocInfo)
        {
            int index = allocInfo.PageIndex;
            int count = allocInfo.PointCount;
            ValidOrThrow(index);
            if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
            if (IsPageFree(index))
            {
                _usedPageCount++;
                if (index > _highestUsedPageIndex) _highestUsedPageIndex = index;
            }

            SetPageCount(index, (uint)count);
            _stateBufferDirty = true;
        }

        public void ClearPage(int index)
        {
            ValidOrThrow(index);
            if (IsPageNotFree(index))
            {
                _usedPageCount--;
                if (index == _highestUsedPageIndex)
                {
                    _highestUsedPageIndex = FindUsedPageBefore(index);
                }
            }

            SetPageCount(index, 0);
            _stateBufferDirty = true;
        }
        
        private int FindUsedPageBefore(int index)
        {
            ValidOrThrow(index);
            for (int i = index - 1; i >= 0; i--)
            {
                if (IsPageFree(i)) continue;
                return i;
            }
            return 0;
        }

        private static void ValidOrThrow(int index)
        {
            if (index is < 0 or >= PagesPerBuffer) throw new ArgumentOutOfRangeException(nameof(index));
        }

        public void Draw(Material mat, Camera cam)
        {
            if (_usedPageCount == 0) return;
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
            if(VoxelWorldRenderer.Logging)VoxelEngineLogger.Info<RenderBuffer>($"Drawing buffer with {_propertyBlock}");
        }

        public void RebuildBuffers()
        {
            if (!_stateBufferDirty) return;

            _countsPerPageBuffer.SetData(_countsPerPage);
            _indexBuffer.SetCounterValue(0);

            ComputeShader rebuild = _manager.RebuildBufferShader;
            int kernel = _manager.RebuildKernel;

            int groupX = (int)math.ceil(_highestUsedPageIndex / 128f);

            rebuild.SetBuffer(kernel, IndexBufferNameID, _indexBuffer);
            rebuild.SetBuffer(kernel, ArgsBufferNameID, _argsBuffer);
            rebuild.SetBuffer(kernel, CountsPerPageNameID, _countsPerPageBuffer);

            rebuild.SetInt(TotalPointCountNameID, (int)_totalValidPoints);
            rebuild.SetInt(PointsPerPageNameID, PointsPerPage);
            rebuild.SetInt(PagesPerBufferNameID, PagesPerBuffer);
            rebuild.Dispatch(kernel, groupX, 1, 1);

            _stateBufferDirty = false;
        }

        public void Dispose()
        {
            _buffer?.Dispose();
            _argsBuffer?.Dispose();
            _indexBuffer?.Dispose();
            _indices.Dispose();
            _countsPerPageBuffer?.Dispose();
            _countsPerPage.Dispose();
        }

        public bool TryFindFreePage(out int pageIndex)
        {
            for (int i = _lastFreePageIndex; i < PagesPerBuffer; i++)
            {
                int index = i % PagesPerBuffer;
                if (IsPageNotFree(index)) continue;

                pageIndex = index;
                _lastFreePageIndex = index;
                return true;
            }

            pageIndex = -1;
            return false;
        }
    }
}