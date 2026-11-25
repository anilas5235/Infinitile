#ifndef VOXEL_SHADER_COMMON_INCLUDED
#define VOXEL_SHADER_COMMON_INCLUDED

#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/UnityInstancing.hlsl"
#include "../VoxelCommon.hlsl"

// ─────────────────────────────────────────────────────────────────────────────
// Vertex data
// ─────────────────────────────────────────────────────────────────────────────

struct VoxelVertexData
{
    float3 positionOS;
    float2 uv;
    uint4 packed;
    float3 normal; // the quad's normal, for the face shade
};

// How far the surface of a liquid sits below the top of its top block (VoxelRenderConstants.LiquidSurfaceDrop).
static const float LIQUID_SURFACE_DROP = 0.1f;

// ─────────────────────────────────────────────────────────────────────────────
// Vertex helpers
// ─────────────────────────────────────────────────────────────────────────────
StructuredBuffer<PointData> _PointData;
// Points are pulled through the per-camera visible page list instead of a per-point index buffer.
// Both buffers belong to the RenderBuffer being drawn; _VisiblePages is written by PageCulling.compute per frame.
StructuredBuffer<uint> _VisiblePages;
StructuredBuffer<uint> _CountsPerPage;

VoxelVertexData fetch_vertex_data(uint vertexID)
{
    // Slot = one point of one visible page; the indirect vertex count is visiblePages * POINTS_PER_PAGE * 6
    uint slot = vertexID / 6;
    uint page = _VisiblePages[slot / POINTS_PER_PAGE];
    uint inPage = slot % POINTS_PER_PAGE;
    uint cornerID = vertexID % 6;

    VoxelVertexData v;
    if (inPage >= _CountsPerPage[page])
    {
        // Unused slot of a partly filled page: all six corners at the same position give a zero-area triangle
        // that the rasteriser drops.
        v.positionOS = 0;
        v.uv = 0;
        v.packed = 0;
        v.normal = 0;
        return v;
    }

    uint index = page * POINTS_PER_PAGE + inPage;

    // Fetch point data directly
    PointData p = _PointData[index];

    v.positionOS = p.position;
    v.packed = p.packed;

    uint quadIndex = get_quad_index(p.packed);
    QuadData quad = _Quad_buffer[quadIndex];
    v.normal = quad.normal;
    float3 corner;

    // Triangle strip corners: two triangles forming a quad
    // Triangle 1: 00-01-02, Triangle 2: 02-01-03
    switch (cornerID)
    {
    case 0:
        corner = quad.position00;
        v.uv = quad.uv00;
        break;
    case 1:
        corner = quad.position01;
        v.uv = quad.uv01;
        break;
    case 2:
        corner = quad.position02;
        v.uv = quad.uv02;
        break;
    case 3:
        corner = quad.position02;
        v.uv = quad.uv02;
        break;
    case 4:
        corner = quad.position01;
        v.uv = quad.uv01;
        break;
    case 5:
        corner = quad.position03;
        v.uv = quad.uv03;
        break;
    default:
        corner = 0;
        v.uv = 0;
        break;
    }

    // The top layer of a liquid: its top corners sit lower, so a full lake does not fill its top block
    if (get_liquid_top(p.packed) && corner.y > 0.5f) corner.y -= LIQUID_SURFACE_DROP;
    v.positionOS += corner;
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

// Globals driven by the DayNightCycle component (VoxelLightingGlobals sets defaults without one).
// Day/night only changes these; the propagated light levels stay as they are.
float _VoxelSkyBrightness;
float4 _VoxelSkyTint;
float _VoxelAmbient;
// Unit direction towards the sun, set by VoxelWorldRenderer from the URP main light (zero without one).
float4 _VoxelSunDirection;
// Strength of the face shade, 0 (every face alike) to 1 (RendererSettings.faceShadeStrength).
float _VoxelFaceShadeStrength;

// Corner levels ul, ur, dr, dl interpolated over the face (as the AO).
float interpolate_corners(const uint4 corners, float2 uv)
{
    float4 c = (float4)corners;
    return lerp(lerp(c.w, c.z, uv.x), lerp(c.x, c.y, uv.x), uv.y);
}

// Brightness of a light level 0-15: each level below 15 loses 15 %.
float light_curve(float level)
{
    return pow(0.85f, 15.0f - level);
}

float3 light_curve(float3 level)
{
    return pow(0.85f, 15.0f - level);
}

// Light = max(skyCurve(sky) * brightness * tint * face shade, blockCurve(RGB)), never below the ambient. The face
// shade darkens only the sky light: a torch lights every side of a block alike.
float3 calc_voxel_light(const uint4 packed, float2 uv, const float shade)
{
    float sky = interpolate_corners(get_sky_light(packed), uv);
    float3 block = float3(
        interpolate_corners(get_red_light(packed), uv),
        interpolate_corners(get_green_light(packed), uv),
        interpolate_corners(get_blue_light(packed), uv));

    float3 sky_light = light_curve(sky) * _VoxelSkyBrightness * _VoxelSkyTint.rgb * shade;
    float3 light = max(sky_light, light_curve(block));
    return max(light, _VoxelAmbient);
}

// Horizon fog, set by VoxelWorldRenderer from the sky material: its horizon and zenith colours, and the
// edge fade x = start, y = 1 / (end - start) in horizontal blocks, z = 1 when on, w = the sky's gradient power.
float4 _VoxelHorizonColor;
float4 _VoxelZenithColor;
float4 _VoxelFogParams;
// Exponential rise of the edge fade: x = steepness k, y = 1 / (exp(k) - 1).
float4 _VoxelFogCurve;

// The sky colour in the view direction, as VoxelSky.shader draws it without the sun, so fully fogged
// terrain disappears into the sky behind it.
float3 horizon_fog_color(const float3 viewDirWS)
{
    const float t = pow(saturate(viewDirWS.y), _VoxelFogParams.w);
    const float3 sky = lerp(_VoxelHorizonColor.rgb, _VoxelZenithColor.rgb, t);
    return sky * max(_VoxelSkyBrightness * _VoxelSkyTint.rgb, _VoxelAmbient);
}

// Blends color into the horizon fog. Only the horizontal distance counts, measured as a square like the drawn
// chunks (the larger of |x| and |z|), so height and the view's pitch never add fog. The fade rises exponentially
// from 0 at its start to 1 at the edge of the drawn chunks.
float3 apply_horizon_fog(const float3 color, const float3 positionWS)
{
    const float3 to_point = positionWS - _WorldSpaceCameraPos;
    const float2 d = abs(to_point.xz);
    const float t = saturate((max(d.x, d.y) - _VoxelFogParams.x) * _VoxelFogParams.y);
    const float fog = (exp(_VoxelFogCurve.x * t) - 1.0f) * _VoxelFogCurve.y * _VoxelFogParams.z;
    return lerp(color, horizon_fog_color(normalize(to_point)), fog);
}

// Sun height (the y of the direction towards it) at which the face shade has turned fully to the sun.
static const float SUN_SHADE_RISE = 0.2f;

// Face shade: the brightness of a face by its direction, so the sides of a block read apart. shade = (top, bottom,
// ±x sides, ±z sides). With the sun below the horizon (or no sun) the fixed values, a slanted quad (a plant's cross
// plane) blending between them by its normal. While the sun is up, by the face's angle to the sun: the bottom
// value facing away from it, the top value facing it (half Lambert), so the sunny side of a block is the brightest
// and the shade moves with the day. The turn takes the sun's rise to SUN_SHADE_RISE. _VoxelFaceShadeStrength pulls
// the result towards 1.
float face_shade(const float4 shade, const float3 normal)
{
    const float3 n = abs(normal);
    const float sides = dot(n.xz, shade.zw) / max(n.x + n.z, 1e-4f);
    const float by_direction = lerp(sides, normal.y >= 0.0f ? shade.x : shade.y, n.y);
    const float by_sun = lerp(shade.y, shade.x, 0.5f * dot(normal, _VoxelSunDirection.xyz) + 0.5f);
    const float shade_value = lerp(by_direction, by_sun, smoothstep(0.0f, SUN_SHADE_RISE, _VoxelSunDirection.y));
    return lerp(1.0f, shade_value, _VoxelFaceShadeStrength);
}

float calc_glow(const float glow_data)
{
    return 1.0f + glow_data / 8.0f;
}

// ── Biome tint (DEC-119, DEC-122) ─────────────────────────────────────────────────────────────────────────────────
// The biome colours around the player by world x/z (BiomeTintMap): slice 0 the water colour, 1 the grass colour,
// 2 the plant colour, 3 the leaf litter colour (DEC-206); one texel per 4 x 4 block columns, repeating; _VoxelBiomeTintParams.x = 1 / map size in blocks.
TEXTURE2D_ARRAY(_VoxelBiomeTint);
SAMPLER(sampler_linear_repeat);
float4 _VoxelBiomeTintParams;

#define BIOME_TINT_WATER 0u

// Linear-light mean of the tinted pixels of every grey tint tile (Docs/Tools/make_tint_tiles.py, Voxel.BiomeTintGreyMean),
// so a tile multiplied by colour / TINT_GREY_MEAN shows the biome colour as its mean.
#define TINT_GREY_MEAN 0.70f
#define TINT_NO_MASK 0xFFFFu

// The biome colour of tint map slice `layer` at a world position: four bilinear taps two blocks apart diagonally, so
// the colour blends over about 16 blocks between biomes.
float3 biome_tint_color(const float3 positionWS, const uint layer)
{
    const float d = 2.0f * _VoxelBiomeTintParams.x;
    const float2 uv = positionWS.xz * _VoxelBiomeTintParams.x;
    float3 sum = SAMPLE_TEXTURE2D_ARRAY_LOD(_VoxelBiomeTint, sampler_linear_repeat, uv + float2(-d, -d), layer, 0).rgb;
    sum += SAMPLE_TEXTURE2D_ARRAY_LOD(_VoxelBiomeTint, sampler_linear_repeat, uv + float2(d, -d), layer, 0).rgb;
    sum += SAMPLE_TEXTURE2D_ARRAY_LOD(_VoxelBiomeTint, sampler_linear_repeat, uv + float2(-d, d), layer, 0).rgb;
    sum += SAMPLE_TEXTURE2D_ARRAY_LOD(_VoxelBiomeTint, sampler_linear_repeat, uv + float2(d, d), layer, 0).rgb;
    return sum * 0.25f;
}

// Biome tint word per texture index of the material's texture array (VoxelRegistry): bits 0-1 the tint map slice
// (0 = not tinted), bit 2 set when tinted through a mask, bits 16-31 the mask's slice in _VoxelTintMasks.
StructuredBuffer<uint> _VoxelTexTint;

// The tinted parts of the side faces (grass band over dirt): alpha 1 = tinted.
TEXTURE2D_ARRAY(_VoxelTintMasks);
SAMPLER(sampler_point_repeat);

// The factor a texture's colour is multiplied by at a vertex (biome colour / TINT_GREY_MEAN; 1 when the texture is
// not tinted) and its mask slice (TINT_NO_MASK = the whole face).
void texture_tint(const uint texIndex, const float3 positionWS, out float3 factor, out uint mask)
{
    const uint word = _VoxelTexTint[texIndex];
    const uint layer = word & 3u;
    factor = layer != 0u ? biome_tint_color(positionWS, layer) * (1.0f / TINT_GREY_MEAN) : 1.0f;
    mask = (word & 4u) != 0u ? word >> 16 : TINT_NO_MASK;
}

// The albedo with the biome tint: multiplied by factor where the mask says so (everywhere without a mask).
float3 apply_texture_tint(const float3 albedo, const float3 factor, const uint mask, const float2 uv)
{
    const float2 dx = ddx(uv), dy = ddy(uv); // outside the branch: the mask can change inside a pixel quad
    float m = 1.0f;
    [branch] if (mask != TINT_NO_MASK)
        m = SAMPLE_TEXTURE2D_ARRAY_GRAD(_VoxelTintMasks, sampler_point_repeat, uv, mask, dx, dy).a;
    return albedo * lerp(1.0f, factor, m);
}

#endif
