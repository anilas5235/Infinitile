#ifndef VOXEL_COMPUTE_COMMON_INCLUDED
#define VOXEL_COMPUTE_COMMON_INCLUDED

// ─────────────────────────────────────────────────────────────────────────────
// GPU Rendering Constants
// ─────────────────────────────────────────────────────────────────────────────

#define MAX_POINTS_PER_PARTITION 98304
#define MESH_LAYER_SOLID 0
#define MESH_LAYER_TRANSPARENT 1
#define MESH_LAYER_AIR 255

// ─────────────────────────────────────────────────────────────────────────────
// Metadata Structures
// ─────────────────────────────────────────────────────────────────────────────

struct PartitionMetadata
{
    int3 partitionPS; // partition coords in partition Space 
    int3 partitionWS; // World partition coordinates
    float3 boundsMin; // AABB min for frustum culling
    float3 boundsMax; // AABB max
};

struct ChunkMetadata
{
    int2 chunkPos; // World chunk XZ coordinates
    float3 boundsMin; // AABB min for coarse culling
    float3 boundsMax; // AABB max
    uint partitionMask; // Bitmask: which of 8 partitions are active (bit Y)
};

#endif
