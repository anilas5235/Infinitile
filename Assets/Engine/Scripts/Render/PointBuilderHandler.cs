using System;
using System.Runtime.InteropServices;
using Engine.Scripts.Utils.Logger;
using Engine.Scripts.VoxelConfig;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using static Engine.Scripts.Utils.VoxelRenderConstants;
using static Engine.Scripts.Utils.VoxelConstants;
using static UnityEngine.GraphicsBuffer;

namespace Engine.Scripts.Render{
    /// <summary>
    ///     Handles the building of point geometry from voxel data using compute shaders.
    ///     Manages point preparation, building, and provides GPU buffers for solid, transparent, and foliage points.
    /// </summary>
    public class PointBuilderHandler : IDisposable{
        private const int PrepChunkCount = 9;
        private const int PrepIntervalsPerThread = 8;
        private const int PrepThreadsX = 64;
        private readonly int _buildPrepKernelID;
        private readonly int _buildPrepLightKernelID; // LGT-08

        private readonly GraphicsBuffer _metadata;
        private readonly ComputeShader _pointBuilder;
        private readonly int _pointBuilderKernelID;
        private readonly GraphicsBuffer[] _preparedChunks;

        private readonly GraphicsBuffer _readBackCountBuffer;
        private readonly GraphicsBuffer _voxelQuadTexPairBuffer;

        private readonly GraphicsBuffer _voxelRenderDefBuffer;
        private NativeArray<uint> _counts;
        private readonly PartitionMetadata[] _metadataUpload = new PartitionMetadata[1]; // PERF-04
        // PERF-04: SetInts takes a params array; these constants are passed as pre-built arrays
        private static readonly int[] ChunkSizeInts = { ChunkSize.x, ChunkSize.y, ChunkSize.z };
        private static readonly int[] PartitionSizeInts = { PartitionSize.x, PartitionSize.y, PartitionSize.z };
        private readonly int[] _pointCounts = new int[3]; // PERF-04: result array reused per handler
        private bool _disposed;
        private bool _readBackPending;
        private AsyncGPUReadbackRequest _readBackRequest; // PERF-04: polled; the awaited version allocated per build

        /// <summary>
        ///     Initializes a new instance of the PointBuilderHandler class.
        /// </summary>
        /// <param name="pointBuilder">The compute shader for building points.</param>
        /// <param name="voxelRenderDef">The GPU buffer containing voxel render definitions.</param>
        /// <param name="voxelQuadTexPair">The GPU buffer containing voxel quad texture pairs.</param>
        public PointBuilderHandler(ComputeShader pointBuilder, GraphicsBuffer voxelRenderDef,
            GraphicsBuffer voxelQuadTexPair){
            _pointBuilder = pointBuilder;
            _voxelRenderDefBuffer = voxelRenderDef;
            _voxelQuadTexPairBuffer = voxelQuadTexPair;
            _buildPrepKernelID = pointBuilder.FindKernel("BuildPrep");
            _buildPrepLightKernelID = pointBuilder.FindKernel("BuildPrepLight");
            _pointBuilderKernelID = pointBuilder.FindKernel("BuildPoints");
            _preparedChunks = new GraphicsBuffer[PrepChunkCount];
            for (int i = 0; i < _preparedChunks.Length; i++)
                _preparedChunks[i] = new GraphicsBuffer(Target.Structured, VoxelsPerChunk, sizeof(uint));

            int vSize = Marshal.SizeOf<Vertex>();
            // F-03: counter buffers; the shader skips writes at or beyond MaxPointsPerPartition
            const Target pointTarget = Target.Structured | Target.Counter;
            SolidPointsOut = new GraphicsBuffer(pointTarget, MaxPointsPerPartition, vSize);
            TransparentPointsOut = new GraphicsBuffer(pointTarget, MaxPointsPerPartition, vSize);
            FoliagePointsOut = new GraphicsBuffer(pointTarget, MaxPointsPerPartition, vSize);

            _metadata = new GraphicsBuffer(Target.Structured, 1, Marshal.SizeOf<PartitionMetadata>());
            _readBackCountBuffer = new GraphicsBuffer(Target.Raw, 3, sizeof(uint));
            _counts = new NativeArray<uint>(_readBackCountBuffer.count, Allocator.Domain);
            pointBuilder.SetBuffer(_pointBuilderKernelID, QuadBufferNameID,
                DataImporter.Instance.VoxelRegistry.QuadBuffer);
        }

        /// <summary>
        ///     Gets the GPU buffer containing solid (opaque) points.
        /// </summary>
        public GraphicsBuffer SolidPointsOut{ get; }

        /// <summary>
        ///     Gets the GPU buffer containing transparent points.
        /// </summary>
        public GraphicsBuffer TransparentPointsOut{ get; }

        /// <summary>
        ///     Gets the GPU buffer containing foliage points.
        /// </summary>
        public GraphicsBuffer FoliagePointsOut{ get; }

        /// <summary>
        ///     Releases all GPU resources held by this handler.
        /// </summary>
        public void Dispose(){
            if (_disposed) return;
            _disposed = true;

            // F-17: a read-back into _counts may still be pending; the array cannot be released before it has
            // completed, and every resource is released on its own so one failure does not leak the others.
            if (_readBackPending) _readBackRequest.WaitForCompletion();

            SafeDispose(SolidPointsOut);
            SafeDispose(TransparentPointsOut);
            SafeDispose(FoliagePointsOut);
            SafeDispose(_metadata);
            SafeDispose(_readBackCountBuffer);
            foreach (GraphicsBuffer chunkBuffer in _preparedChunks) SafeDispose(chunkBuffer);

            try{
                if (_counts.IsCreated) _counts.Dispose();
            }
            catch (Exception e){
                VoxelEngineLogger.Error<PointBuilderHandler>($"Failed to release the read-back array: {e}");
            }
        }

        private static void SafeDispose(GraphicsBuffer buffer){
            try{
                buffer?.Dispose();
            }
            catch (Exception e){
                VoxelEngineLogger.Error<PointBuilderHandler>($"Failed to release a GPU buffer: {e}");
            }
        }

        /// <summary>
        ///     True once the read-back of the last <see cref="BuildPoints" /> call has finished (or none is pending).
        /// </summary>
        internal bool IsReadBackDone => !_readBackPending || _readBackRequest.done;

        /// <summary>
        ///     Dispatches the point build for the given partition and starts the read-back of the point counts.
        ///     Poll <see cref="IsReadBackDone" /> and then call <see cref="CompleteBuild" /> for the counts.
        /// </summary>
        /// <param name="data">The partition build request containing chunk data.</param>
        internal void BuildPoints(PartitionBuildRequest data){
            ResetCounters();
            _metadataUpload[0] = data.GetMetadata();
            _metadata.SetData(_metadataUpload);

            PrepareChunks(data);

            _pointBuilder.SetBuffer(_pointBuilderKernelID, VoxelRenderDefNameID, _voxelRenderDefBuffer);
            _pointBuilder.SetInt(VoxelRenderDefCountNameID, _voxelRenderDefBuffer.count);

            _pointBuilder.SetBuffer(_pointBuilderKernelID, VoxelQuadTexPairNameID, _voxelQuadTexPairBuffer);
            _pointBuilder.SetInt(VoxelQuadTexPairCountNameID, _voxelQuadTexPairBuffer.count);

            _pointBuilder.SetInt(VoxelsPerChunkNameID, VoxelsPerChunk);
            _pointBuilder.SetInts(ChunkSizeNameID, ChunkSizeInts);
            _pointBuilder.SetInts(PartitionSizeNameID, PartitionSizeInts);

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
            _pointBuilder.SetInt(PointCapacityNameID, MaxPointsPerPartition); // F-03

            _pointBuilder.Dispatch(_pointBuilderKernelID, 8, 8, 8);

            CopyCount(SolidPointsOut, _readBackCountBuffer, sizeof(uint) * 0);
            CopyCount(TransparentPointsOut, _readBackCountBuffer, sizeof(uint) * 1);
            CopyCount(FoliagePointsOut, _readBackCountBuffer, sizeof(uint) * 2);

            _readBackRequest = AsyncGPUReadback.RequestIntoNativeArray(ref _counts, _readBackCountBuffer);
            _readBackPending = true;
        }

        /// <summary>
        ///     Finishes the pending build: converts the read-back counters to the solid, transparent and foliage
        ///     point counts. Zero counts are returned when the handler was released while the read-back ran (F-17)
        ///     or the read-back failed.
        /// </summary>
        /// <returns>The handler-owned counts array (valid until the next build).</returns>
        internal int[] CompleteBuild(){
            if (!_readBackPending) return _pointCounts;
            _readBackPending = false;

            if (_disposed){ // F-17: the handler was released while the read-back ran
                Array.Clear(_pointCounts, 0, _pointCounts.Length);
                return _pointCounts;
            }

            if (_readBackRequest.hasError){
                VoxelEngineLogger.Error<PointBuilderHandler>("Error reading back point counts: the GPU read-back failed");
                Array.Clear(_pointCounts, 0, _pointCounts.Length);
                return _pointCounts;
            }

            ToPointCounts(_counts, _pointCounts);
            return _pointCounts;
        }

        /// <summary>
        ///     Converts the GPU read-back counters (solid, transparent, foliage) to the point counts used for
        ///     buffer allocation. Extracted so TST-E17 can characterise the read-back clamp (F-03).
        /// </summary>
        internal static int[] ToPointCounts(NativeArray<uint> counts){
            int[] results = new int[counts.Length];
            ToPointCounts(counts, results);
            return results;
        }

        /// <summary>PERF-04: fills a caller-owned array instead of allocating one per read-back.</summary>
        internal static void ToPointCounts(NativeArray<uint> counts, int[] results){
            for (int i = 0; i < counts.Length; i++){
                // F-03: the shader skips points beyond the buffer capacity, but the counter keeps growing
                // (alwaysRenderAllFaces can emit up to 6 faces per voxel). Never request more than the buffer holds.
                uint count = counts[i];
                if (count > MaxPointsPerPartition){
                    VoxelEngineLogger.Warn<PointBuilderHandler>(
                        $"Point buffer {i} overflowed: {count} points, clamped to {MaxPointsPerPartition}");
                    count = MaxPointsPerPartition;
                }

                results[i] = (int)count;
            }
        }

        /// <summary>
        ///     Resets the counters for all output point buffers.
        /// </summary>
        private void ResetCounters(){
            SolidPointsOut.SetCounterValue(0);
            TransparentPointsOut.SetCounterValue(0);
            FoliagePointsOut.SetCounterValue(0);
        }

        /// <summary>
        ///     Prepares all chunks for point building by decompressing them: the voxel ids into the lower 16 bits
        ///     of each voxel's uint, then the light into the upper 16 bits (LGT-08).
        /// </summary>
        /// <param name="data">The partition build request containing chunk buffers.</param>
        private void PrepareChunks(PartitionBuildRequest data){
            _pointBuilder.SetInt(VoxelsPerChunkNameID, VoxelsPerChunk);
            GraphicsBuffer[] buffers = data.Buffers;
            GraphicsBuffer[] lightBuffers = data.LightBuffers;
            for (int i = 0; i < buffers.Length; i++){
                DispatchPrepChunk(_buildPrepKernelID, i, buffers[i]);
                DispatchPrepChunk(_buildPrepLightKernelID, i, lightBuffers[i]); // runs after the ids are written
            }
        }

        /// <summary>
        ///     Dispatches a compute shader job to decompress a single chunk's interval list.
        /// </summary>
        /// <param name="kernel">BuildPrep (voxel ids) or BuildPrepLight (light, LGT-08).</param>
        /// <param name="chunkIndex">The index of the chunk to prepare.</param>
        /// <param name="compressedChunk">The compressed chunk data buffer.</param>
        private void DispatchPrepChunk(int kernel, int chunkIndex, GraphicsBuffer compressedChunk){
            _pointBuilder.SetBuffer(kernel, CompChunkNameID, compressedChunk);
            _pointBuilder.SetBuffer(kernel, UnCompChunkNameID, _preparedChunks[chunkIndex]);

            int intervalCount = math.max(0, compressedChunk.count - 1);
            int intervalsPerGroup = PrepThreadsX * PrepIntervalsPerThread;
            int groupsX = math.max(1, (intervalCount + intervalsPerGroup - 1) / intervalsPerGroup);
            _pointBuilder.Dispatch(kernel, groupsX, 1, 1);
        }
    }
}
