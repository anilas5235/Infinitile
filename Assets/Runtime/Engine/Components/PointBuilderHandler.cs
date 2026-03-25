using System;
using System.Linq;
using System.Runtime.InteropServices;
using Runtime.Engine.Behaviour;
using Runtime.Engine.Jobs.Meshing;
using Runtime.Engine.Utils;
using Runtime.Engine.Utils.Logger;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace Runtime.Engine.Components
{
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
            SolidPointsOut = new GraphicsBuffer(GraphicsBuffer.Target.Append, VoxelRenderConstants.MaxPointsPerPartition, vSize);
            TransparentPointsOut = new GraphicsBuffer(GraphicsBuffer.Target.Append, VoxelRenderConstants.MaxPointsPerPartition, vSize);
            FoliagePointsOut = new GraphicsBuffer(GraphicsBuffer.Target.Append, VoxelRenderConstants.MaxPointsPerPartition, vSize);

            _metadata = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, Marshal.SizeOf<PartitionMetadata>());
            _readBackCountBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Raw, 3, sizeof(uint));
            _counts = new NativeArray<uint>(_readBackCountBuffer.count, Allocator.Domain);
        }

        public void BuildPoints(int3 partition, GraphicsBuffer voxelData)
        {
            PartitionMetadata meta = new()
            {
                PartitionPos = partition,
                PartitionWorldPos = VoxelConstants.PartitionToWorldPos(partition),
            };
            _metadata.SetData(new[] { meta });

            _pointBuilder.SetBuffer(_pointBuilderKernelID, VoxelRenderConstants.VoxelRenderDefNameID, _voxelRenderDefBuffer);
            _pointBuilder.SetInt(VoxelRenderConstants.VoxelRenderDefCountNameID, _voxelRenderDefBuffer.count);

            _pointBuilder.SetBuffer(_pointBuilderKernelID, VoxelRenderConstants.VoxelQuadTexPairNameID, _voxelQuadTexPairBuffer);
            _pointBuilder.SetInt(VoxelRenderConstants.VoxelQuadTexPairCountNameID, _voxelQuadTexPairBuffer.count);

            _pointBuilder.SetBuffer(_pointBuilderKernelID, VoxelRenderConstants.VoxelDataNameID, voxelData);
            _pointBuilder.SetInt(VoxelRenderConstants.VoxelCompressedCountNameID, voxelData.count);

            _pointBuilder.SetBuffer(_pointBuilderKernelID, VoxelRenderConstants.MetadataNameID, _metadata);
            _pointBuilder.SetBuffer(_pointBuilderKernelID, VoxelRenderConstants.SolidPointsOutNameID, SolidPointsOut);
            _pointBuilder.SetBuffer(_pointBuilderKernelID, VoxelRenderConstants.TransparentPointsOutNameID, TransparentPointsOut);
            _pointBuilder.SetBuffer(_pointBuilderKernelID, VoxelRenderConstants.FoliagePointsOutNameID, FoliagePointsOut);

            _pointBuilder.SetInt(VoxelRenderConstants.PartitionIndexNameID, 0);

            _pointBuilder.Dispatch(_pointBuilderKernelID, 4, 4, 4);
        }

        public async Awaitable<int[]> ReadBackCounters()
        {
            try
            {
                GraphicsBuffer.CopyCount(SolidPointsOut, _readBackCountBuffer, sizeof(uint) * 0);
                GraphicsBuffer.CopyCount(TransparentPointsOut, _readBackCountBuffer, sizeof(uint) * 1);
                GraphicsBuffer.CopyCount(FoliagePointsOut, _readBackCountBuffer, sizeof(uint) * 2);

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
}