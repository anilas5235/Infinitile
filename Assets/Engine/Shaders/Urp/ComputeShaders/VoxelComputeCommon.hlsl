#ifndef VOXEL_COMPUTE_COMMON_INCLUDED
#define VOXEL_COMPUTE_COMMON_INCLUDED

// ─────────────────────────────────────────────────────────────────────────────
// GPU Rendering Constants
// ─────────────────────────────────────────────────────────────────────────────

#define MESH_LAYER_SOLID 0

// ─────────────────────────────────────────────────────────────────────────────
// Metadata Structures
// ─────────────────────────────────────────────────────────────────────────────

struct PartitionMetadata
{
    int3 partitionPS; // partition coords in partition Space 
    int3 partitionWS; // World partition coordinates
};

#endif
