#ifndef VOXEL_SHADER_COMMON_INCLUDED
#define VOXEL_SHADER_COMMON_INCLUDED

#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/UnityInstancing.hlsl"
#include "../VoxelCommon.hlsl"

// ─────────────────────────────────────────────────────────────────────────────
// Vertex input structs (for GPU rendering - kept for compatibility)
// ─────────────────────────────────────────────────────────────────────────────

struct Attributes
{
    uint vertexID : SV_VertexID;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct VoxelVertexData
{
    float3 positionOS;
    float2 uv;
    uint4 packed;
};

// ─────────────────────────────────────────────────────────────────────────────
// Vertex helpers
// ─────────────────────────────────────────────────────────────────────────────
StructuredBuffer<PointData> _PointData;
StructuredBuffer<uint> _IndexBuffer;

VoxelVertexData fetch_vertex_data(uint vertexID)
{
    // Calculate point and corner indices
    uint pointID = vertexID / 6;
    uint index = _IndexBuffer[pointID];
    uint cornerID = vertexID % 6;

    // Fetch point data directly
    PointData p = _PointData[index];

    VoxelVertexData v;
    v.positionOS = p.position;
    v.packed = p.packed;

    uint quadIndex = get_quad_index(p.packed);
    QuadData quad = _Quad_buffer[quadIndex];


    // Triangle strip corners: two triangles forming a quad
    // Triangle 1: 00-01-02, Triangle 2: 02-01-03
    switch (cornerID)
    {
    case 0:
        v.positionOS += quad.position00;
        v.uv = quad.uv00;
        break;
    case 1:
        v.positionOS += quad.position01;
        v.uv = quad.uv01;
        break;
    case 2:
        v.positionOS += quad.position02;
        v.uv = quad.uv02;
        break;
    case 3:
        v.positionOS += quad.position02;
        v.uv = quad.uv02;
        break;
    case 4:
        v.positionOS += quad.position01;
        v.uv = quad.uv01;
        break;
    case 5:
        v.positionOS += quad.position03;
        v.uv = quad.uv03;
        break;
    default:
        v.uv = 0;
        break;
    }

    return v;
}


// ─────────────────────────────────────────────────────────────────────────────
// Frag helpers
// ─────────────────────────────────────────────────────────────────────────────

int compute_ao_corner(const int s1, const int s2, const int c)
{
    return s1 == 1 && s2 == 1 ? 0 : 3 - (s1 + s2 + c);
}

float scale_ao(const float4 curve, const int index, const float intensity, const float power)
{
    return pow(abs(curve[index] * intensity), power);
}

float4 calc_ao_color(const float4 ao_color, const float4 albedo, const float4 ao_curve, const int ao_data,
                     const float ao_intensity, const float ao_power, const float2 uv)
{
    // Bits: 0=up (UV.y=1), 1=up-right (UV=1,1), 2=right (UV.x=1), 3=down-right (UV=1,0),
    //       4=down (UV.y=0), 5=down-left (UV=0,0), 6=left (UV.x=0), 7=up-left (UV=0,1)
    int u = ao_data >> 0 & 1;
    int ur = ao_data >> 1 & 1;
    int r = ao_data >> 2 & 1;
    int dr = ao_data >> 3 & 1;
    int d = ao_data >> 4 & 1;
    int dl = ao_data >> 5 & 1;
    int l = ao_data >> 6 & 1;
    int ul = ao_data >> 7 & 1;

    float dlc = scale_ao(ao_curve, compute_ao_corner(l, d, dl), ao_intensity, ao_power);
    float ulc = scale_ao(ao_curve, compute_ao_corner(l, u, ul), ao_intensity, ao_power);
    float drc = scale_ao(ao_curve, compute_ao_corner(r, d, dr), ao_intensity, ao_power);
    float urc = scale_ao(ao_curve, compute_ao_corner(r, u, ur), ao_intensity, ao_power);

    float t = lerp(lerp(dlc, drc, uv.x), lerp(ulc, urc, uv.x), uv.y);
    return lerp(ao_color, albedo, t);
}

float calc_sun_light(const uint4 light_data, float2 uv)
{
    float ul = lerp(0.05f, 1.0f, light_data.x / 15.0f);
    float ur = lerp(0.05f, 1.0f, light_data.y / 15.0f);
    float dr = lerp(0.05f, 1.0f, light_data.z / 15.0f);
    float dl = lerp(0.05f, 1.0f, light_data.w / 15.0f);

    return lerp(lerp(dl, dr, uv.x), lerp(ul, ur, uv.x), uv.y);
}

float calc_glow(const float glow_data)
{
    return 1.0f + glow_data / 8.0f;
}

#endif
