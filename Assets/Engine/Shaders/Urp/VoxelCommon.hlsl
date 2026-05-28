#ifndef VOXEL_COMMON_INCLUDED
#define VOXEL_COMMON_INCLUDED

// ─────────────────────────────────────────────────────────────────────────────
// Structs
// ─────────────────────────────────────────────────────────────────────────────

struct PointData
{
    float3 position; // Voxel position in partition local space
    uint4 packed; // quadIndex(u16) | texIndex(u16), lights(4×u4 each), ao(u8) | depthFade(f16) | glow(u8), unused
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

uint4 get_sun_light(in uint4 packed)
{
    return uint4(packed.y & 0xF, packed.y >> 4 & 0xF, packed.y >> 8 & 0xF, packed.y >> 12 & 0xF);
}

void set_sun_light(inout uint4 packed, in uint4 sun_light)
{
    packed.y = (packed.y & 0xFFFF0000) | (sun_light.x & 0xF) | ((sun_light.y & 0xF) << 4) |
        ((sun_light.z & 0xF) << 8) | ((sun_light.w & 0xF) << 12);
}

uint4 get_artificial_light(in uint4 packed)
{
    return uint4(packed.y >> 16 & 0xF, packed.y >> 20 & 0xF, packed.y >> 24 & 0xF, packed.y >> 28 & 0xF);
}

void set_artificial_light(inout uint4 packed, in uint4 artificial_light)
{
    packed.y = (packed.y & 0x0000FFFF) | ((artificial_light.x & 0xF) << 16) |
        ((artificial_light.y & 0xF) << 20) | ((artificial_light.z & 0xF) << 24) |
        ((artificial_light.w & 0xF) << 28);
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
    packed.z = (packed.z & 0xFFFF00FF) | (f32tof16(depth_fade_dist) << 8);
}

float get_glow(uint4 packed)
{
    return (float)(packed.z >> 24 & 0xFF);
}

void set_glow(inout uint4 packed, in float glow)
{
    packed.z = (packed.z & 0x00FFFFFF) | ((uint)(glow) << 24);
}

#endif
