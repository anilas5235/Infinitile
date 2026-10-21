using System;
using System.Runtime.InteropServices;
using Engine.Scripts.Utils.Logger;
using Engine.Scripts.VoxelConfig;
using Unity.Collections;
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
        private const int PrepBoxVoxels = PrepBoxSize * PrepBoxSize * PrepBoxSize;
        private const int PrepBoxGroups = (PrepBoxSize * PrepBoxSize + PrepBoxThreads - 1) / PrepBoxThreads;
        private readonly int _buildPrepBoxKernelID;

        private readonly GraphicsBuffer _metadata;
        private readonly ComputeShader _pointBuilder;
        private readonly int _pointBuilderKernelID;
        private readonly GraphicsBuffer _preparedBox;

        // page tables of the 2 x 9 interval lists of a build (voxel ids, then light): a header with the start of
        // each list's table, then the tables back to back
        private const int PageTableLists = 2 * PrepChunks;
        private readonly GraphicsBuffer _pageTable;
        private readonly uint[] _pageTableUpload = new uint[PageTableLists * (1 + IntervalPages.MaxPagesPerList)];

        private readonly GraphicsBuffer _readBackCountBuffer;
        private readonly GraphicsBuffer _voxelQuadTexPairBuffer;

        private readonly GraphicsBuffer _voxelRenderDefBuffer;
        private NativeArray<uint> _counts;
        private readonly PartitionMetadata[] _metadataUpload = new PartitionMetadata[1];
        private readonly int[] _pointCounts = new int[3]; // result array reused per handler
        private bool _disposed;
        private bool _readBackPending;
        private AsyncGPUReadbackRequest _readBackRequest; // polled, not awaited, so a build allocates nothing

        /// <summary>
        ///     Initializes a new instance of the PointBuilderHandler class.
        /// </summary>
        /// <param name="pointBuilder">The compute shader for building points.</param>
        /// <param name="voxelRenderDef">The GPU buffer containing voxel render definitions.</param>
        /// <param name="voxelQuadTexPair">The GPU buffer containing voxel quad texture pairs.</param>
        public PointBuilderHandler(ComputeShader pointBuilder, GraphicsBuffer voxelRenderDef,
            GraphicsBuffer voxelQuadTexPair) : this(pointBuilder, voxelRenderDef, voxelQuadTexPair,
            DataImporter.Instance.VoxelRegistry.QuadBuffer){ }

        /// <summary>Creates the handler with an explicit quad buffer instead of the importer's (tests).</summary>
        internal PointBuilderHandler(ComputeShader pointBuilder, GraphicsBuffer voxelRenderDef,
            GraphicsBuffer voxelQuadTexPair, GraphicsBuffer quadBuffer){
            _pointBuilder = pointBuilder;
            _voxelRenderDefBuffer = voxelRenderDef;
            _voxelQuadTexPairBuffer = voxelQuadTexPair;
            _buildPrepBoxKernelID = pointBuilder.FindKernel("BuildPrepBox");
            _pointBuilderKernelID = pointBuilder.FindKernel("BuildPoints");
            _preparedBox = new GraphicsBuffer(Target.Structured, PrepBoxVoxels, sizeof(uint));
            _pageTable = new GraphicsBuffer(Target.Structured, _pageTableUpload.Length, sizeof(uint));

            int vSize = Marshal.SizeOf<Vertex>();
            // counter buffers; the shader skips writes at or beyond MaxPointsPerPartition
            const Target pointTarget = Target.Structured | Target.Counter;
            SolidPointsOut = new GraphicsBuffer(pointTarget, MaxPointsPerPartition, vSize);
            TransparentPointsOut = new GraphicsBuffer(pointTarget, MaxPointsPerPartition, vSize);
            FoliagePointsOut = new GraphicsBuffer(pointTarget, MaxPointsPerPartition, vSize);

            _metadata = new GraphicsBuffer(Target.Structured, 1, Marshal.SizeOf<PartitionMetadata>());
            _readBackCountBuffer = new GraphicsBuffer(Target.Raw, 3, sizeof(uint));
            _counts = new NativeArray<uint>(_readBackCountBuffer.count, Allocator.Domain);
            pointBuilder.SetBuffer(_pointBuilderKernelID, QuadBufferNameID, quadBuffer);
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

            // A read-back into _counts may still be pending; the array cannot be released before it has completed.
            // Every resource is released on its own so one failure does not leak the others.
            if (_readBackPending) _readBackRequest.WaitForCompletion();

            SafeDispose(SolidPointsOut);
            SafeDispose(TransparentPointsOut);
            SafeDispose(FoliagePointsOut);
            SafeDispose(_metadata);
            SafeDispose(_readBackCountBuffer);
            SafeDispose(_pageTable);
            SafeDispose(_preparedBox);

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
        ///     Poll <see cref="IsReadBackDone" /> and then call <see cref="TryCompleteBuild" /> for the counts.
        /// </summary>
        /// <param name="data">The partition build request containing chunk data.</param>
        /// <param name="store">The interval store that holds the request's lists.</param>
        internal void BuildPoints(PartitionBuildRequest data, ChunkIntervalStore store){
            ResetCounters();
            _metadataUpload[0] = data.GetMetadata();
            _metadata.SetData(_metadataUpload);

            PrepareBox(data, store);

            _pointBuilder.SetBuffer(_pointBuilderKernelID, VoxelRenderDefNameID, _voxelRenderDefBuffer);
            _pointBuilder.SetInt(VoxelRenderDefCountNameID, _voxelRenderDefBuffer.count);

            _pointBuilder.SetBuffer(_pointBuilderKernelID, VoxelQuadTexPairNameID, _voxelQuadTexPairBuffer);
            _pointBuilder.SetInt(VoxelQuadTexPairCountNameID, _voxelQuadTexPairBuffer.count);

            _pointBuilder.SetBuffer(_pointBuilderKernelID, PreparedBoxNameID, _preparedBox);

            _pointBuilder.SetBuffer(_pointBuilderKernelID, MetadataNameID, _metadata);
            _pointBuilder.SetBuffer(_pointBuilderKernelID, SolidPointsOutNameID, SolidPointsOut);
            _pointBuilder.SetBuffer(_pointBuilderKernelID, TransparentPointsOutNameID, TransparentPointsOut);
            _pointBuilder.SetBuffer(_pointBuilderKernelID, FoliagePointsOutNameID, FoliagePointsOut);

            _pointBuilder.SetInt(PartitionIndexNameID, 0);
            _pointBuilder.SetInt(PointCapacityNameID, MaxPointsPerPartition);

            _pointBuilder.Dispatch(_pointBuilderKernelID, PartitionWidth / BuildPointsThreads,
                PartitionHeight / BuildPointsThreads, PartitionDepth / BuildPointsThreads);

            CopyCount(SolidPointsOut, _readBackCountBuffer, sizeof(uint) * 0);
            CopyCount(TransparentPointsOut, _readBackCountBuffer, sizeof(uint) * 1);
            CopyCount(FoliagePointsOut, _readBackCountBuffer, sizeof(uint) * 2);

            _readBackRequest = AsyncGPUReadback.RequestIntoNativeArray(ref _counts, _readBackCountBuffer);
            _readBackPending = true;
        }

        /// <summary>
        ///     Finishes the pending build: converts the read-back counters to the solid, transparent and foliage
        ///     point counts.
        /// </summary>
        /// <param name="counts">
        ///     The handler-owned counts array (valid until the next build); all zero when the build failed.
        /// </param>
        /// <returns>
        ///     False when the handler was released while the read-back ran or the read-back failed; the counts then
        ///     say nothing about the partition.
        /// </returns>
        internal bool TryCompleteBuild(out int[] counts){
            counts = _pointCounts;
            if (!_readBackPending) return true;
            _readBackPending = false;

            if (_disposed){ // the handler was released while the read-back ran
                Array.Clear(_pointCounts, 0, _pointCounts.Length);
                return false;
            }

            if (_readBackRequest.hasError){
                VoxelEngineLogger.Error<PointBuilderHandler>("Error reading back point counts: the GPU read-back failed");
                Array.Clear(_pointCounts, 0, _pointCounts.Length);
                return false;
            }

            ToPointCounts(_counts, _pointCounts);
            return true;
        }

        /// <summary>
        ///     Converts the GPU read-back counters (solid, transparent, foliage) to the point counts used for
        ///     buffer allocation; separate so tests can check the read-back clamp.
        /// </summary>
        internal static int[] ToPointCounts(NativeArray<uint> counts){
            int[] results = new int[counts.Length];
            ToPointCounts(counts, results);
            return results;
        }

        /// <summary>Fills a caller-owned array instead of allocating one per read-back.</summary>
        internal static void ToPointCounts(NativeArray<uint> counts, int[] results){
            for (int i = 0; i < counts.Length; i++){
                // The shader skips points beyond the buffer capacity, but the counter keeps growing
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

        /// <summary>The box <see cref="PrepareBox" /> writes (tests).</summary>
        internal GraphicsBuffer PreparedBox => _preparedBox;

        /// <summary>
        ///     Decompresses the partition plus a 1-voxel border, the only voxels BuildPoints reads, from the interval
        ///     lists of its chunk and the eight neighbours in one dispatch: each voxel's uint holds the voxel id in the
        ///     lower 16 bits and its light in the upper 16 bits (spec 20.4).
        /// </summary>
        /// <param name="data">The partition build request containing the chunks' interval lists.</param>
        /// <param name="store">The interval store that holds the lists.</param>
        internal void PrepareBox(PartitionBuildRequest data, ChunkIntervalStore store){
            IntervalRange[] voxelLists = data.VoxelLists;
            IntervalRange[] lightLists = data.LightLists;
            // Header first, then the page tables; only the used part is uploaded.
            int used = PageTableLists;
            for (int i = 0; i < PrepChunks; i++){
                _pageTableUpload[i] = (uint)used;
                used += store.WritePageTable(voxelLists[i], _pageTableUpload, used);
                _pageTableUpload[PrepChunks + i] = (uint)used;
                used += store.WritePageTable(lightLists[i], _pageTableUpload, used);
            }

            _pageTable.SetData(_pageTableUpload, 0, 0, used);
            _pointBuilder.SetBuffer(_buildPrepBoxKernelID, CompChunkNameID, store.Buffer);
            _pointBuilder.SetBuffer(_buildPrepBoxKernelID, CompPagesNameID, _pageTable);
            _pointBuilder.SetBuffer(_buildPrepBoxKernelID, PreparedBoxOutNameID, _preparedBox);
            // also read by BuildPoints to place positions in the box
            _pointBuilder.SetInt(BoxOriginYNameID, data.Partition.y * PartitionHeight - 1);
            _pointBuilder.Dispatch(_buildPrepBoxKernelID, PrepBoxGroups, 1, 1);
        }
    }
}
