using Engine.Scripts.VoxelConfig.Data.Mesh;
using UnityEngine;
using static Engine.Scripts.Utils.VoxelConstants;

namespace Engine.Scripts.Utils{
    /// <summary>
    ///     Constants for GPU-based voxel rendering pipeline.
    /// </summary>
    public static class VoxelRenderConstants{
        public const int MaxPointsPerPartition = PartitionWidth * PartitionHeight * PartitionDepth * 3;
        public const int RenderBufferSize = PointsPerPage * PagesPerBuffer;
        public const int PointsPerPage = 256;
        public const int PagesPerBuffer = 1024;

        #region Mirrored in Shaders/Urp/VoxelGpuConstants.hlsl (GpuConstantsTests compares them)

        /// <summary>Flat chunk index x * ChunkDepth * ChunkHeight + z * ChunkHeight + y, as shifts.</summary>
        internal const int ChunkIndexShiftX = 13;

        /// <inheritdoc cref="ChunkIndexShiftX" />
        internal const int ChunkIndexShiftZ = 8;

        /// <summary>Elements per interval store page as a shift (<see cref="Render.IntervalPages.PageSize" />).</summary>
        internal const int CompPageShift = 9;

        internal const int CompPageMask = (1 << CompPageShift) - 1;

        internal const int MeshLayerSolid = (int)MeshLayer.Solid;

        /// <summary>Chunks of a partition build: the partition's chunk and its eight neighbours.</summary>
        internal const int PrepChunks = 9;

        /// <summary>
        ///     Voxels per axis of the box BuildPrepBox decompresses: the partition plus a 1-voxel border, all that
        ///     BuildPoints reads. Partitions are cubes.
        /// </summary>
        internal const int PrepBoxSize = PartitionWidth + 2;

        /// <summary>Threads per group of BuildPrepBox, one box column per thread.</summary>
        internal const int PrepBoxThreads = 64;

        /// <summary>Threads per group and axis of BuildPoints; a partition is dispatched as a cube of groups.</summary>
        internal const int BuildPointsThreads = 4;

        internal const int CopyIntervalsThreads = 64;

        /// <summary>Threads per group of CopyPoints: one group per page and mesh layer, one thread per point.</summary>
        internal const int CopyPointsThreads = PointsPerPage;

        internal const int CullThreads = 128;

        #endregion

        public static readonly int TexturesNameID = Shader.PropertyToID("_Textures");
        public static readonly int QuadBufferNameID = Shader.PropertyToID("_Quad_buffer");

        public static readonly int VoxelRenderDefNameID = Shader.PropertyToID("_VoxelRenderDefs");
        public static readonly int VoxelRenderDefCountNameID = Shader.PropertyToID("_VoxelRenderDefsCount");

        public static readonly int VoxelQuadTexPairNameID = Shader.PropertyToID("_VoxelQuadTexPairs");
        public static readonly int VoxelQuadTexPairCountNameID = Shader.PropertyToID("_VoxelQuadTexPairsCount");

        public static readonly int PreparedBoxNameID = Shader.PropertyToID("_PreparedBox");
        public static readonly int PreparedBoxOutNameID = Shader.PropertyToID("_PreparedBoxOut");
        public static readonly int BoxOriginYNameID = Shader.PropertyToID("_BoxOriginY");

        public static readonly int MetadataNameID = Shader.PropertyToID("_Metadata");

        public static readonly int CompChunkNameID = Shader.PropertyToID("_CompChunk");
        public static readonly int CompPagesNameID = Shader.PropertyToID("_CompPages");
        public static readonly int CopySourceNameID = Shader.PropertyToID("_CopySource");
        public static readonly int CopyTargetNameID = Shader.PropertyToID("_CopyTarget");
        public static readonly int CopyCountNameID = Shader.PropertyToID("_CopyCount");
        public static readonly int CopyStrideNameID = Shader.PropertyToID("_CopyStride");

        public static readonly int SolidPointsOutNameID = Shader.PropertyToID("_SolidPointsOut");
        public static readonly int TransparentPointsOutNameID = Shader.PropertyToID("_TransparentPointsOut");
        public static readonly int FoliagePointsOutNameID = Shader.PropertyToID("_FoliagePointsOut");
        public static readonly int PartitionIndexNameID = Shader.PropertyToID("_PartitionIndex");
        public static readonly int PointCapacityNameID = Shader.PropertyToID("_PointCapacity");

        // Lighting globals of the voxel shaders
        public static readonly int VoxelSkyBrightnessNameID = Shader.PropertyToID("_VoxelSkyBrightness");
        public static readonly int VoxelSkyTintNameID = Shader.PropertyToID("_VoxelSkyTint");
        public static readonly int VoxelAmbientNameID = Shader.PropertyToID("_VoxelAmbient");

        // Sky and horizon fog globals (VoxelSky.shader, VoxelShaderCommon.hlsl)
        public static readonly int VoxelHorizonColorNameID = Shader.PropertyToID("_VoxelHorizonColor");
        public static readonly int VoxelZenithColorNameID = Shader.PropertyToID("_VoxelZenithColor");
        public static readonly int VoxelFogParamsNameID = Shader.PropertyToID("_VoxelFogParams");
        public static readonly int VoxelSunDirectionNameID = Shader.PropertyToID("_VoxelSunDirection");

        // Volume fog of see-through liquids (VoxelRegistry, TransparentVoxel.shader)
        public static readonly int VoxelVolumeFogNameID = Shader.PropertyToID("_VoxelVolumeFog");

        // Sky material properties the horizon fog copies, so fogged terrain matches the sky behind it
        public static readonly int SkyHorizonColorNameID = Shader.PropertyToID("_HorizonColor");
        public static readonly int SkyZenithColorNameID = Shader.PropertyToID("_ZenithColor");
        public static readonly int SkyGradientPowerNameID = Shader.PropertyToID("_GradientPower");

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
        public static readonly int ArgsBufferNameID = Shader.PropertyToID("_ArgsBuffer");
        public static readonly int PointsPerPageNameID = Shader.PropertyToID("_PointsPerPage");
        public static readonly int PagesPerBufferNameID = Shader.PropertyToID("_PagesPerBuffer");

        // GPU culling (PageCulling.compute and the vertex pulling in VoxelShaderCommon.hlsl)
        public static readonly int PageBoundsNameID = Shader.PropertyToID("_PageBounds");
        public static readonly int VisiblePagesNameID = Shader.PropertyToID("_VisiblePages");
        public static readonly int FrustumPlanesNameID = Shader.PropertyToID("_FrustumPlanes");
        public static readonly int ShadowExtrusionNameID = Shader.PropertyToID("_ShadowExtrusion");
        public static readonly int CullingEnabledNameID = Shader.PropertyToID("_CullingEnabled");
    }
}
