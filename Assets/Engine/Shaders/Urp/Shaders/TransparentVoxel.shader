Shader "Custom/TransparentVoxel"
{
    Properties
    {
        _AOColor ("AO Color", Color) = (0, 0, 0, 1)
        _AOCurve("AOCurve", Vector, 4) = (0.75, 0.825, 0.9, 1)
        _AOIntensity("AOIntensity", Range(0, 1)) = 1
        _AOPower("AOPower", Range(1, 10)) = 1
        _FaceShade("Face shade (top, bottom, x sides, z sides)", Vector) = (1, 0.62, 0.8, 0.88)
        [NoScaleOffset] _Textures ("Textures", 2DArray) = "" {}
    }

    // The blended blocks of the transparent layer, from its buffer (the cut-out leaves in it are CutoutVoxel's):
    // - VoxelGlass: glass and other plain see-through blocks, drawn by VoxelGlassFeature after the skybox, before
    //   the transparents, so a liquid surface in front blends over them. No depth write, so a liquid surface
    //   behind the glass is still drawn.
    // - Universal Forward: the voxels with a volume slot (liquids): a tinted, alpha-blended surface with a
    //   world-space pattern scrolling in two directions, coloured by the biome's water colour (world tint map),
    //   with URP's transparents. Marks its pixels in the stencil (LIQUID_STENCIL).
    // - VoxelGlassOver: the glass again, drawn by VoxelGlassFeature after the transparents, only on liquid pixels
    //   and only where the glass lies in front of the liquid surface, so the liquid does not cover it.
    // A pass's other quads collapse to a point and are not rasterised.

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType" = "Transparent"
            "UniversalMaterialType" = "Unlit"
            "Queue" = "Transparent"
        }

        // ─────────────────────────────────────────────────────────────
        // Shared data types used by every pass
        // ─────────────────────────────────────────────────────────────
        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "VoxelShaderCommon.hlsl"
        #include "../VoxelCommon.hlsl"

        // ── Material properties ───────────────────────────────────
        CBUFFER_START(UnityPerMaterial)
            float4 _AOColor;
            float _AOIntensity;
            float _AOPower;
            float4 _AOCurve;
            float4 _FaceShade;
        CBUFFER_END

        struct Varyings
        {
            float4 positionCS : SV_POSITION;
            float2 uv : TEXCOORD0;
            uint4 packed : TEXCOORD1;
            float3 positionWS : TEXCOORD2; // horizon fog
            float4 positionSS : TEXCOORD3;
            float2 shade : TEXCOORD4; // face shade seen from the front, from the back
            nointerpolation float3 normal : TEXCOORD5; // the quad's normal, for the liquid's world-space pattern
        };

        // ── Vertex shader with expansion ─────────────────────────────
        // volume_pass: draw the voxels with a volume slot (liquids), else the plain blended ones (glass)
        Varyings vert_filtered(const uint vertexID, const bool volume_pass)
        {
            VoxelVertexData v = fetch_vertex_data(vertexID);

            Varyings o = (Varyings)0;

            if (get_cutout(v.packed) || (get_volume_fog(v.packed) != 0) != volume_pass) return o;

            o.positionCS = TransformObjectToHClip(v.positionOS);
            o.uv = v.uv;
            o.packed = v.packed;
            o.positionSS = ComputeScreenPos(o.positionCS);
            o.positionWS = TransformObjectToWorld(v.positionOS);
            o.shade = float2(face_shade(_FaceShade, v.normal), face_shade(_FaceShade, -v.normal));
            o.normal = v.normal;
            return o;
        }

        Varyings vert_glass(uint vertexID : SV_VertexID)
        {
            return vert_filtered(vertexID, false);
        }

        Varyings vert_volume(uint vertexID : SV_VertexID)
        {
            return vert_filtered(vertexID, true);
        }

        TEXTURE2D_ARRAY(_Textures);
        SAMPLER(sampler_Textures);

        // The surface colour (texture, AO, light, glow and face shade) and its alpha; front: the side of the quad
        // the camera sees (SV_IsFrontFace), so a face seen from inside the block is shaded like the opposite face.
        float4 surface_color(const Varyings IN, const bool front)
        {
            const float4 albedo = SAMPLE_TEXTURE2D_ARRAY(_Textures, sampler_Textures, IN.uv, get_tex_index(IN.packed));
            const float4 ao_color = calc_ao_color(_AOColor, albedo, _AOCurve, get_ao(IN.packed), _AOIntensity,
                                                  _AOPower, IN.uv);
            const float3 light = calc_voxel_light(IN.packed, IN.uv, front ? IN.shade.x : IN.shade.y);
            const float glow = calc_glow(get_glow(IN.packed));
            return float4(ao_color.rgb * light * glow, albedo.w * get_surface_alpha(IN.packed));
        }

        half4 frag_glass(Varyings IN, const bool front : SV_IsFrontFace) : SV_Target
        {
            const float4 color = surface_color(IN, front);
            clip(color.a - 0.001f);
            return half4(apply_horizon_fog(color.rgb, IN.positionWS), color.a);
        }
        ENDHLSL

        // ═════════════════════════════════════════════════════════════
        // Pass – Glass (VoxelGlassFeature, after the skybox)
        // ═════════════════════════════════════════════════════════════
        Pass
        {
            Name "Voxel Glass"
            Tags
            {
                "LightMode" = "VoxelGlass"
            }

            Cull Off
            ZTest LEqual
            ZWrite Off
            Blend SrcAlpha OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex   vert_glass
            #pragma fragment frag_glass
            ENDHLSL
        }

        // ═════════════════════════════════════════════════════════════
        // Pass – Glass over liquids (VoxelGlassFeature, after the transparents)
        // ═════════════════════════════════════════════════════════════
        Pass
        {
            Name "Voxel Glass Over Liquids"
            Tags
            {
                "LightMode" = "VoxelGlassOver"
            }

            Cull Off
            ZTest LEqual // the liquid wrote its depth: glass behind its surface stays under it
            ZWrite Off
            Blend SrcAlpha OneMinusSrcAlpha
            Stencil
            {
                Ref 64 // LIQUID_STENCIL
                ReadMask 64
                Comp Equal
            }

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex   vert_glass
            #pragma fragment frag_glass
            ENDHLSL
        }

        // ═════════════════════════════════════════════════════════════
        // Pass – Forward / Unlit
        // ═════════════════════════════════════════════════════════════
        Pass
        {
            Name "Universal Forward"
            Tags
            {
                "LightMode" = "UniversalForward"
            }

            Cull Off
            ZTest LEqual
            ZWrite On
            Blend SrcAlpha OneMinusSrcAlpha
            Stencil
            {
                Ref 64 // LIQUID_STENCIL: the pixels a liquid surface covers, for the VoxelGlassOver pass
                WriteMask 64
                Comp Always
                Pass Replace
            }

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex   vert_volume
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl"

            // Surface look of the liquids, by slot (VoxelRegistry.VolumeFogDef); slot 0 = none.
            struct VolumeFog
            {
                float3 tint;
                float biomeTint;
                float depthFade;
                float depthAlpha;
                float flowSpeed;
                float patternStrength;
            };

            StructuredBuffer<VolumeFog> _VoxelVolumeFog;

            // The biome water colour around the player by world x/z (WaterTintMap): one texel per block column,
            // repeating; _VoxelWaterTintParams.x = 1 / its size in texels, z = the mip it is blended from.
            TEXTURE2D(_VoxelWaterTint);
            SAMPLER(sampler_linear_repeat);
            float4 _VoxelWaterTintParams;

            // The biome water colour at a world position: four bilinear taps two blocks apart diagonally from the
            // blend mip (one texel per 4 x 4 columns), so the colour blends over about 16 blocks between biomes.
            float3 biome_water_color(const float3 positionWS)
            {
                const float mip = _VoxelWaterTintParams.z;
                const float d = 2.0f * _VoxelWaterTintParams.x;
                const float2 uv = positionWS.xz * _VoxelWaterTintParams.x;
                float3 sum = SAMPLE_TEXTURE2D_LOD(_VoxelWaterTint, sampler_linear_repeat, uv + float2(-d, -d), mip).rgb;
                sum += SAMPLE_TEXTURE2D_LOD(_VoxelWaterTint, sampler_linear_repeat, uv + float2(d, -d), mip).rgb;
                sum += SAMPLE_TEXTURE2D_LOD(_VoxelWaterTint, sampler_linear_repeat, uv + float2(-d, d), mip).rgb;
                sum += SAMPLE_TEXTURE2D_LOD(_VoxelWaterTint, sampler_linear_repeat, uv + float2(d, d), mip).rgb;
                return sum * 0.25f;
            }

            // Whether a face is a top or bottom face (else a side).
            bool is_horizontal(const float3 normal)
            {
                const float3 n = abs(normal);
                return n.y >= n.x && n.y >= n.z;
            }

            // World-space texture coordinates of a face by its normal: top/bottom by x/z, the sides by their
            // horizontal axis and y (v up), so the pattern runs on across blocks.
            float2 world_uv(const float3 positionWS, const float3 normal)
            {
                if (is_horizontal(normal)) return positionWS.xz;
                return abs(normal.x) >= abs(normal.z) ? positionWS.zy : positionWS.xy;
            }

            // The liquid's texture pattern: two samples in world space scrolling in two directions (on the sides
            // both run down), averaged. Gradients from the unscrolled coordinates, so the mip level stays steady.
            float4 liquid_pattern(const float3 positionWS, const float3 normal, const uint tex, const float speed)
            {
                const float2 uv = world_uv(positionWS, normal);
                const float2 dx = ddx(uv);
                const float2 dy = ddy(uv);
                const float t = _Time.y * speed;
                const bool top = is_horizontal(normal);
                const float2 flow_a = top ? float2(0.8f, 0.35f) : float2(0.0f, 1.0f);
                const float2 flow_b = top ? float2(-0.45f, -0.9f) : float2(0.15f, 0.6f);
                const float4 a = SAMPLE_TEXTURE2D_ARRAY_GRAD(_Textures, sampler_Textures, uv + flow_a * t, tex, dx, dy);
                const float4 b = SAMPLE_TEXTURE2D_ARRAY_GRAD(_Textures, sampler_Textures,
                                                            uv * 0.75f + flow_b * t + 0.37f, tex,
                                                            dx * 0.75f, dy * 0.75f);
                return 0.5f * (a + b);
            }

            // ── Fragment shader ───────────────────────────────────────
            half4 frag(Varyings IN, const bool front : SV_IsFrontFace) : SV_Target
            {
                const uint tex = get_tex_index(IN.packed);
                const VolumeFog liquid = _VoxelVolumeFog[get_volume_fog(IN.packed)];

                // --- Pattern: the scrolling world-space texture around its mean colour ---
                const float4 pattern = liquid_pattern(IN.positionWS, IN.normal, tex, liquid.flowSpeed);
                const float3 mean = SAMPLE_TEXTURE2D_ARRAY_LOD(_Textures, sampler_Textures, float2(0.5f, 0.5f),
                                                               tex, 16).rgb; // the last mip: the texture's mean
                float3 albedo;
                if (liquid.biomeTint > 0.5f)
                {
                    // the biome colour carries the pattern's light and dark as a luminance ratio
                    const float k = Luminance(pattern.rgb) / max(Luminance(mean), 1e-3f);
                    albedo = biome_water_color(IN.positionWS) * (1.0f + (k - 1.0f) * liquid.patternStrength);
                }
                else
                    albedo = mean + (pattern.rgb - mean) * liquid.patternStrength;
                albedo *= liquid.tint;

                // --- AO, light, glow, face shade (as the other voxels, by the quad's own uv) ---
                const float4 ao_color = calc_ao_color(_AOColor, float4(albedo, 1.0f), _AOCurve, get_ao(IN.packed),
                                                      _AOIntensity, _AOPower, IN.uv);
                const float3 light = calc_voxel_light(IN.packed, IN.uv, front ? IN.shade.x : IN.shade.y);
                const float3 color = ao_color.rgb * light * calc_glow(get_glow(IN.packed));
                float alpha = pattern.a * get_surface_alpha(IN.packed);

                // --- Depth tint: seen from outside, the alpha rises with the scene depth behind the surface ---
                if (front && liquid.depthFade > 0.0f)
                {
                    const float2 screen_uv = IN.positionSS.xy / IN.positionSS.w;
                    const float behind = max(LinearEyeDepth(SampleSceneDepth(screen_uv), _ZBufferParams) -
                                             IN.positionSS.w, 0.0f);
                    alpha = lerp(alpha, max(alpha, liquid.depthAlpha), saturate(behind / liquid.depthFade));
                }

                clip(alpha - 0.001f);
                return half4(apply_horizon_fog(color, IN.positionWS), alpha);
            }
            ENDHLSL
        }
    }
}
