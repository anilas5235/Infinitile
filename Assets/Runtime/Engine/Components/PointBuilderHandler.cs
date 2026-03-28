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
using static Runtime.Engine.Utils.VoxelRenderConstants;
using static UnityEngine.GraphicsBuffer;

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
            SolidPointsOut = new GraphicsBuffer(Target.Append, MaxPointsPerPartition, vSize);
            TransparentPointsOut = new GraphicsBuffer(Target.Append, MaxPointsPerPartition, vSize);
            FoliagePointsOut = new GraphicsBuffer(Target.Append, MaxPointsPerPartition, vSize);

            _metadata = new GraphicsBuffer(Target.Structured, 1, Marshal.SizeOf<PartitionMetadata>());
            _readBackCountBuffer = new GraphicsBuffer(Target.Raw, 3, sizeof(uint));
            _counts = new NativeArray<uint>(_readBackCountBuffer.count, Allocator.Domain);
        }

        public async Awaitable<int[]> BuildPoints(int3 partition, GraphicsBuffer voxelData)
        {
            ResetCounters();
            PartitionMetadata meta = new()
            {
                PartitionPos = partition,
                PartitionWorldPos = VoxelConstants.PartitionToWorldPos(partition),
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

            try
            {
                CopyCount(SolidPointsOut, _readBackCountBuffer, sizeof(uint) * 0);
                CopyCount(TransparentPointsOut, _readBackCountBuffer, sizeof(uint) * 1);
                CopyCount(FoliagePointsOut, _readBackCountBuffer, sizeof(uint) * 2);

                await AsyncGPUReadback.RequestIntoNativeArrayAsync(ref _counts, _readBackCountBuffer);
                int[] results = _counts.Select(c => (int)c).ToArray();
                return results;
            }
            catch (Exception e)
            {
                if(VoxelWorldRenderer.Logging)VoxelEngineLogger.Error<PointBuilderHandler>($"Error reading back point counts: {e}");
                return new[] { 0, 0, 0 };
            }
        }

        private void ResetCounters()
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