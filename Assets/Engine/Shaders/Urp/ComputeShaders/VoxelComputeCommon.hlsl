#ifndef VOXEL_COMPUTE_COMMON_INCLUDED
#define VOXEL_COMPUTE_COMMON_INCLUDED

// ─────────────────────────────────────────────────────────────────────────────
// Metadata Structures
// ─────────────────────────────────────────────────────────────────────────────

struct PartitionMetadata
{
    int3 partitionPS; // partition coords in partition Space 
    int3 partitionWS; // World partition coordinates
};

#endif
