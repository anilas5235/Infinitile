#ifndef VOXEL_GPU_CONSTANTS_INCLUDED
#define VOXEL_GPU_CONSTANTS_INCLUDED

// ─────────────────────────────────────────────────────────────────────────────
// Compile-time constants shared with the C# side. Each define mirrors the constant of the same meaning in
// Engine.Scripts.Utils.VoxelRenderConstants (named in the comment); GpuConstantsTests parses this file and
// fails when a value drifts. Values only, no expressions: the test reads them literally.
// ─────────────────────────────────────────────────────────────────────────────

// Points per render page (PointsPerPage)
#define POINTS_PER_PAGE 256u

// Flat chunk index x * 32 * 256 + z * 256 + y (ChunkIndexShiftX, ChunkIndexShiftZ)
#define CHUNK_INDEX_SHIFT_X 13
#define CHUNK_INDEX_SHIFT_Z 8

// Interval store pages of 512 elements (CompPageShift, CompPageMask)
#define COMP_PAGE_SHIFT 9
#define COMP_PAGE_MASK 511

// MeshLayer.Solid (MeshLayerSolid)
#define MESH_LAYER_SOLID 0

// Partition build: the chunks of a build in PartitionBuildRequest order (PrepChunks), and the decompressed box,
// the partition plus a 1-voxel border, BOX_SIZE voxels along each axis (PrepBoxSize)
#define PREP_CHUNKS 9
#define BOX_SIZE 34

// Thread group sizes; the C# side sizes its dispatches with them
#define PREP_BOX_THREADS 64 // BuildPrepBox, one box column per thread (PrepBoxThreads)
#define BUILD_POINTS_THREADS 4 // BuildPoints, per axis (BuildPointsThreads)
#define COPY_INTERVALS_THREADS 64 // CopyIntervals (CopyIntervalsThreads)
#define COPY_POINTS_THREADS 256 // CopyPoints, one point per thread, equals POINTS_PER_PAGE (CopyPointsThreads)
#define CULL_THREADS 128 // CullPages (CullThreads)

#endif
