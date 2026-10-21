#ifndef VOXEL_COMMON_INCLUDED
#define VOXEL_COMMON_INCLUDED

// ─────────────────────────────────────────────────────────────────────────────
// Structs
// ─────────────────────────────────────────────────────────────────────────────

struct PointData
{
    float3 position; // Voxel position in partition local space
    // x: quadIndex(u16) | texIndex(u16)
    // y: sky (4 corners × u4) | red (4 corners × u4)      LGT-09
    // z: ao(u8) | depthFade(f16) | glow(u8)
    // w: green (4 corners × u4) | blue (4 corners × u4)  LGT-09
    // Corner order inside each 16-bit channel: up-left, up-right, down-right, down-left (UV space).
    uint4 packed;
};

// ─────────────────────────────────────────────────────────────────────────────
// Quad buffer
// ─────────────────────────────────────────────────────────────────────────────

struct QuadData
{
    float3 position00; // vertex offset from voxel origin
    float3 position01;
    float3 position02;
    float3 position03;
    float3 normal;
    float3 up;
    float3 right;
    float2 uv00;
    float2 uv01;
    float2 uv02;
    float2 uv03;
};

StructuredBuffer<QuadData> _Quad_buffer;

// ─────────────────────────────────────────────────────────────────────────────
// Packed data accessors
// ─────────────────────────────────────────────────────────────────────────────

uint get_quad_index(in uint4 packed)
{
    return packed.x & 0xFFFF;
}

void set_quad_index(inout uint4 packed, uint quad_index)
{
    packed.x = (packed.x & 0xFFFF0000) | (quad_index & 0xFFFF);
}

uint get_tex_index(in uint4 packed)
{
    return packed.x >> 16 & 0xFFFF;
}

void set_tex_index(inout uint4 packed, uint tex_index)
{
    packed.x = (packed.x & 0x0000FFFF) | ((tex_index & 0xFFFF) << 16);
}

// LGT-09: per-corner light, four 4-bit corners per channel (ul, ur, dr, dl). Voxel light is sky|R|G|B with sky in
// bits 12-15 and B in bits 0-3 (VoxelLight on the C# side).
uint4 unpack_corners(uint bits)
{
    return uint4(bits & 0xF, bits >> 4 & 0xF, bits >> 8 & 0xF, bits >> 12 & 0xF);
}

uint pack_corners(in uint4 corners)
{
    return (corners.x & 0xF) | ((corners.y & 0xF) << 4) | ((corners.z & 0xF) << 8) | ((corners.w & 0xF) << 12);
}

uint4 get_sky_light(in uint4 packed)
{
    return unpack_corners(packed.y & 0xFFFF);
}

uint4 get_red_light(in uint4 packed)
{
    return unpack_corners(packed.y >> 16);
}

uint4 get_green_light(in uint4 packed)
{
    return unpack_corners(packed.w & 0xFFFF);
}

uint4 get_blue_light(in uint4 packed)
{
    return unpack_corners(packed.w >> 16);
}

// Sets all four channels from the corners' voxel light values (sky|R|G|B each, corners ul, ur, dr, dl).
void set_corner_light(inout uint4 packed, in uint4 corner_light)
{
    uint4 sky = corner_light >> 12 & 0xF;
    uint4 red = corner_light >> 8 & 0xF;
    uint4 green = corner_light >> 4 & 0xF;
    uint4 blue = corner_light & 0xF;
    packed.y = pack_corners(sky) | (pack_corners(red) << 16);
    packed.w = pack_corners(green) | (pack_corners(blue) << 16);
}

uint get_ao(in uint4 packed)
{
    return packed.z & 0xFF;
}

void set_ao(inout uint4 packed, uint ao)
{
    packed.z = (packed.z & 0xFFFFFF00) | (ao & 0xFF);
}

float get_depth_fade_dist(in uint4 packed)
{
    return f16tof32(packed.z >> 8 & 0xFFFF);
}

void set_depth_fade_dist(inout uint4 packed, in float depth_fade_dist)
{
    packed.z = (packed.z & 0xFF0000FF) | ((f32tof16(depth_fade_dist) & 0xFFFF) << 8); // F-19: bits 8-23
}

float get_glow(uint4 packed)
{
    return (float)(packed.z >> 24 & 0xFF);
}

void set_glow(inout uint4 packed, in float glow)
{
    packed.z = (packed.z & 0x00FFFFFF) | (((uint)glow & 0xFF) << 24);
}

#endif
