using UnityEngine;
using static Engine.Scripts.Utils.VoxelConstants;

namespace Engine.Scripts.Utils
{
    /// <summary>
    ///     Constants for GPU-based voxel rendering pipeline.
    /// </summary>
    public static class VoxelRenderConstants
    {
        public const int MaxPointsPerPartition = PartitionWidth * PartitionHeight * PartitionDepth * 3;
        public const int MaxDirtyUploadsPerFrame = 8;

        public const int RenderBufferSize = PointsPerPage * PagesPerBuffer;
        public const int PointsPerPage = 256;
        public const int PagesPerBuffer = 1024;

        public static readonly int QuadBufferNameID = Shader.PropertyToID("_Quad_buffer");

        public static readonly int VoxelRenderDefNameID = Shader.PropertyToID("_VoxelRenderDefs");
        public static readonly int VoxelRenderDefCountNameID = Shader.PropertyToID("_VoxelRenderDefsCount");

        public static readonly int VoxelQuadTexPairNameID = Shader.PropertyToID("_VoxelQuadTexPairs");
        public static readonly int VoxelQuadTexPairCountNameID = Shader.PropertyToID("_VoxelQuadTexPairsCount");

        public static readonly int MainChunkNameID = Shader.PropertyToID("_MainChunk");
        public static readonly int NeighborChunkUpNameID = Shader.PropertyToID("_NeighborChunk0");
        public static readonly int NeighborChunkUpRightNameID = Shader.PropertyToID("_NeighborChunk1");
        public static readonly int NeighborChunkRightNameID = Shader.PropertyToID("_NeighborChunk2");
        public static readonly int NeighborChunkDownRightNameID = Shader.PropertyToID("_NeighborChunk3");
        public static readonly int NeighborChunkDownNameID = Shader.PropertyToID("_NeighborChunk4");
        public static readonly int NeighborChunkDownLeftNameID = Shader.PropertyToID("_NeighborChunk5");
        public static readonly int NeighborChunkLeftNameID = Shader.PropertyToID("_NeighborChunk6");
        public static readonly int NeighborChunkUpLeftNameID = Shader.PropertyToID("_NeighborChunk7");

        public static readonly int MetadataNameID = Shader.PropertyToID("_Metadata");

        public static readonly int CompChunkNameID = Shader.PropertyToID("_CompChunk");
        public static readonly int UnCompChunkNameID = Shader.PropertyToID("_UnCompChunk");
        public static readonly int PrepChunkIndexNameID = Shader.PropertyToID("_PrepChunkIndex");

        public static readonly int VoxelsPerChunkNameID = Shader.PropertyToID("_VoxelsPerChunk");
        public static readonly int ChunkSizeNameID = Shader.PropertyToID("_ChunkSize");
        public static readonly int PartitionSizeNameID = Shader.PropertyToID("_PartitionSize");

        public static readonly int SolidPointsOutNameID = Shader.PropertyToID("_SolidPointsOut");
        public static readonly int TransparentPointsOutNameID = Shader.PropertyToID("_TransparentPointsOut");
        public static readonly int FoliagePointsOutNameID = Shader.PropertyToID("_FoliagePointsOut");
        public static readonly int PartitionIndexNameID = Shader.PropertyToID("_PartitionIndex");

        public static readonly int SolidPointsInNameID = Shader.PropertyToID("_SolidPointsIn");
        public static readonly int SolidPointsCopyOutNameID = Shader.PropertyToID("_SolidPointsCopyOut");
        public static readonly int SolidPagesNameID = Shader.PropertyToID("_SolidPages");

        public static readonly int TransparentPointsInNameID = Shader.PropertyToID("_TransparentPointsIn");
        public static readonly int TransparentPointsCopyOutNameID = Shader.PropertyToID("_TransparentPointsCopyOut");
        public static readonly int TransparentPagesNameID = Shader.PropertyToID("_TransparentPages");

        public static readonly int FoliagePointsInNameID = Shader.PropertyToID("_FoliagePointsIn");
        public static readonly int FoliagePointsCopyOutNameID = Shader.PropertyToID("_FoliagePointsCopyOut");
        public static readonly int FoliagePagesNameID = Shader.PropertyToID("_FoliagePages");

        public static readonly int PageCountsNameID = Shader.PropertyToID("_PageCounts");
        public static readonly int CountsPerPageNameID = Shader.PropertyToID("_CountsPerPage");

        public static readonly int PointDataNameID = Shader.PropertyToID("_PointData");
        public static readonly int IndexBufferNameID = Shader.PropertyToID("_IndexBuffer");
        public static readonly int ArgsBufferNameID = Shader.PropertyToID("_ArgsBuffer");
        public static readonly int TotalPointCountNameID = Shader.PropertyToID("_TotalPointCount");
        public static readonly int PointsPerPageNameID = Shader.PropertyToID("_PointsPerPage");
        public static readonly int PagesPerBufferNameID = Shader.PropertyToID("_PagesPerBuffer");
    }
}