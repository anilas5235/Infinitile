using System;
using System.Linq;
using System.Runtime.InteropServices;
using Engine.Scripts.Jobs.Meshing;
using Engine.Scripts.Utils;
using Engine.Scripts.Utils.Logger;
using Engine.Scripts.VoxelConfig.Data;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using static Engine.Scripts.Utils.VoxelRenderConstants;
using static Engine.Scripts.Utils.VoxelConstants;
using static UnityEngine.GraphicsBuffer;

namespace Engine.Scripts.Render
{
    /// <summary>
    /// Handles the building of point geometry from voxel data using compute shaders.
    /// Manages point preparation, building, and provides GPU buffers for solid, transparent, and foliage points.
    /// </summary>
    public class PointBuilderHandler : IDisposable
    {
        private const int PrepChunkCount = 9;
        private const int PrepIntervalsPerThread = 8;
        private const int PrepThreadsX = 64;

        private readonly GraphicsBuffer _metadata;
        private readonly ComputeShader _pointBuilder;
        private readonly int _buildPrepKernelID;
        private readonly int _pointBuilderKernelID;
        private readonly GraphicsBuffer[] _preparedChunks;

        private readonly GraphicsBuffer _readBackCountBuffer;
        private readonly GraphicsBuffer _voxelQuadTexPairBuffer;

        private readonly GraphicsBuffer _voxelRenderDefBuffer;
        private NativeArray<uint> _counts;

        /// <summary>
        /// Initializes a new instance of the PointBuilderHandler class.
        /// </summary>
        /// <param name="pointBuilder">The compute shader for building points.</param>
        /// <param name="voxelRenderDef">The GPU buffer containing voxel render definitions.</param>
        /// <param name="voxelQuadTexPair">The GPU buffer containing voxel quad texture pairs.</param>
        public PointBuilderHandler(ComputeShader pointBuilder, GraphicsBuffer voxelRenderDef,
            GraphicsBuffer voxelQuadTexPair)
        {
            _pointBuilder = pointBuilder;
            _voxelRenderDefBuffer = voxelRenderDef;
            _voxelQuadTexPairBuffer = voxelQuadTexPair;
            _buildPrepKernelID = pointBuilder.FindKernel("BuildPrep");
            _pointBuilderKernelID = pointBuilder.FindKernel("BuildPoints");
            _preparedChunks = new GraphicsBuffer[PrepChunkCount];
            for (int i = 0; i < _preparedChunks.Length; i++)
                _preparedChunks[i] = new GraphicsBuffer(Target.Structured, VoxelsPerChunk, sizeof(uint));

            int vSize = Marshal.SizeOf<Vertex>();
            SolidPointsOut = new GraphicsBuffer(Target.Append, MaxPointsPerPartition, vSize);
            TransparentPointsOut = new GraphicsBuffer(Target.Append, MaxPointsPerPartition, vSize);
            FoliagePointsOut = new GraphicsBuffer(Target.Append, MaxPointsPerPartition, vSize);

            _metadata = new GraphicsBuffer(Target.Structured, 1, Marshal.SizeOf<PartitionMetadata>());
            _readBackCountBuffer = new GraphicsBuffer(Target.Raw, 3, sizeof(uint));
            _counts = new NativeArray<uint>(_readBackCountBuffer.count, Allocator.Domain);
            pointBuilder.SetBuffer(_pointBuilderKernelID, QuadBufferNameID,
                VoxelDataImporter.Instance.VoxelRegistry.QuadBuffer);
        }

        /// <summary>
        /// Gets the GPU buffer containing solid (opaque) points.
        /// </summary>
        public GraphicsBuffer SolidPointsOut { get; }

        /// <summary>
        /// Gets the GPU buffer containing transparent points.
        /// </summary>
        public GraphicsBuffer TransparentPointsOut { get; }

        /// <summary>
        /// Gets the GPU buffer containing foliage points.
        /// </summary>
        public GraphicsBuffer FoliagePointsOut { get; }

        /// <summary>
        /// Releases all GPU resources held by this handler.
        /// </summary>
        public void Dispose()
        {
            SolidPointsOut?.Dispose();
            TransparentPointsOut?.Dispose();
            FoliagePointsOut?.Dispose();
            _metadata?.Dispose();
            _readBackCountBuffer?.Dispose();
            foreach (GraphicsBuffer chunkBuffer in _preparedChunks) chunkBuffer?.Dispose();
            _counts.Dispose();
        }

        /// <summary>
        /// Asynchronously builds points for the given partition from its voxel data.
        /// </summary>
        /// <param name="data">The partition build request containing chunk data.</param>
        /// <returns>An awaitable that returns an array with solid, transparent, and foliage point counts.</returns>
        internal async Awaitable<int[]> BuildPoints(PartitionBuildRequest data)
        {
            ResetCounters();
            PartitionMetadata meta = data.GetMetadata();
            _metadata.SetData(new[] { meta });

            PrepareChunks(data);

            _pointBuilder.SetBuffer(_pointBuilderKernelID, VoxelRenderDefNameID, _voxelRenderDefBuffer);
            _pointBuilder.SetInt(VoxelRenderDefCountNameID, _voxelRenderDefBuffer.count);

            _pointBuilder.SetBuffer(_pointBuilderKernelID, VoxelQuadTexPairNameID, _voxelQuadTexPairBuffer);
            _pointBuilder.SetInt(VoxelQuadTexPairCountNameID, _voxelQuadTexPairBuffer.count);

            _pointBuilder.SetInt(VoxelsPerChunkNameID, VoxelsPerChunk);
            _pointBuilder.SetInts(ChunkSizeNameID, ChunkSize.x, ChunkSize.y, ChunkSize.z);
            _pointBuilder.SetInts(PartitionSizeNameID, PartitionSize.x, PartitionSize.y, PartitionSize.z);

            _pointBuilder.SetBuffer(_pointBuilderKernelID, MainChunkNameID, _preparedChunks[0]);
            _pointBuilder.SetBuffer(_pointBuilderKernelID, NeighborChunkUpNameID, _preparedChunks[1]);
            _pointBuilder.SetBuffer(_pointBuilderKernelID, NeighborChunkUpRightNameID, _preparedChunks[2]);
            _pointBuilder.SetBuffer(_pointBuilderKernelID, NeighborChunkRightNameID, _preparedChunks[3]);
            _pointBuilder.SetBuffer(_pointBuilderKernelID, NeighborChunkDownRightNameID, _preparedChunks[4]);
            _pointBuilder.SetBuffer(_pointBuilderKernelID, NeighborChunkDownNameID, _preparedChunks[5]);
            _pointBuilder.SetBuffer(_pointBuilderKernelID, NeighborChunkDownLeftNameID, _preparedChunks[6]);
            _pointBuilder.SetBuffer(_pointBuilderKernelID, NeighborChunkLeftNameID, _preparedChunks[7]);
            _pointBuilder.SetBuffer(_pointBuilderKernelID, NeighborChunkUpLeftNameID, _preparedChunks[8]);

            _pointBuilder.SetBuffer(_pointBuilderKernelID, MetadataNameID, _metadata);
            _pointBuilder.SetBuffer(_pointBuilderKernelID, SolidPointsOutNameID, SolidPointsOut);
            _pointBuilder.SetBuffer(_pointBuilderKernelID, TransparentPointsOutNameID, TransparentPointsOut);
            _pointBuilder.SetBuffer(_pointBuilderKernelID, FoliagePointsOutNameID, FoliagePointsOut);

            _pointBuilder.SetInt(PartitionIndexNameID, 0);

            _pointBuilder.Dispatch(_pointBuilderKernelID, 8, 8, 8);

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
                VoxelEngineLogger.Error<PointBuilderHandler>($"Error reading back point counts: {e}");
                return new[] { 0, 0, 0 };
            }
        }

        /// <summary>
        /// Resets the counters for all output point buffers.
        /// </summary>
        private void ResetCounters()
        {
            SolidPointsOut.SetCounterValue(0);
            TransparentPointsOut.SetCounterValue(0);
            FoliagePointsOut.SetCounterValue(0);
        }

        /// <summary>
        /// Prepares all chunks for point building by decompressing them.
        /// </summary>
        /// <param name="data">The partition build request containing chunk buffers.</param>
        private void PrepareChunks(PartitionBuildRequest data)
        {
            GraphicsBuffer[] buffers = data.Buffers;
            for (int i = 0; i < buffers.Length; i++) DispatchPrepChunk(i, buffers[i]);
        }

        /// <summary>
        /// Dispatches a compute shader job to decompress a single chunk.
        /// </summary>
        /// <param name="chunkIndex">The index of the chunk to prepare.</param>
        /// <param name="compressedChunk">The compressed chunk data buffer.</param>
        private void DispatchPrepChunk(int chunkIndex, GraphicsBuffer compressedChunk)
        {
            _pointBuilder.SetInt(VoxelsPerChunkNameID, VoxelsPerChunk);
            _pointBuilder.SetBuffer(_buildPrepKernelID, CompChunkNameID, compressedChunk);
            _pointBuilder.SetBuffer(_buildPrepKernelID, UnCompChunkNameID, _preparedChunks[chunkIndex]);

            int intervalCount = math.max(0, compressedChunk.count - 1);
            int intervalsPerGroup = PrepThreadsX * PrepIntervalsPerThread;
            int groupsX = math.max(1, (intervalCount + intervalsPerGroup - 1) / intervalsPerGroup);
            _pointBuilder.Dispatch(_buildPrepKernelID, groupsX, 1, 1);
        }
    }
}