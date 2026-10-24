Shader "Custom/TransparentVoxel"
{
    Properties
    {
        _AOColor ("AO Color", Color) = (0, 0, 0, 1)
        _AOCurve("AOCurve", Vector, 4) = (0.75, 0.825, 0.9, 1)
        _AOIntensity("AOIntensity", Range(0, 1)) = 1
        _AOPower("AOPower", Range(1, 10)) = 1
        [NoScaleOffset] _Textures ("Textures", 2DArray) = "" {}
    }

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

        struct Varyings
        {
            float4 positionCS : SV_POSITION;
            float2 uv : TEXCOORD0;
            uint4 packed : TEXCOORD1;
            float3 positionWS : TEXCOORD2; // horizon fog
            float4 positionSS : TEXCOORD3;
        };


        // ── Vertex shader with expansion ─────────────────────────────
        Varyings vert(uint vertexID : SV_VertexID)
        {
            VoxelVertexData v = fetch_vertex_data(vertexID);

            Varyings o;
            o.positionCS = TransformObjectToHClip(v.positionOS);
            o.uv = v.uv;
            o.packed = v.packed;
            o.positionSS = ComputeScreenPos(o.positionCS);
            o.positionWS = TransformObjectToWorld(v.positionOS);
            return o;
        }
        ENDHLSL

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

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex   vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            // ── Material properties ───────────────────────────────────
            CBUFFER_START(UnityPerMaterial)
                float4 _AOColor;
                float _AOIntensity;
                float _AOPower;
                float4 _AOCurve;
            CBUFFER_END

            TEXTURE2D_ARRAY(_Textures);
            SAMPLER(sampler_Textures);

            struct FragExtraData
            {
                uint texture_index;
                uint ao;
                float depth_fade_dist;
                float surface_alpha;
                float glow;
            };

            FragExtraData unpack_frag_extra_data(uint4 packed)
            {
                FragExtraData data;
                data.texture_index = get_tex_index(packed);
                data.ao = get_ao(packed);
                data.depth_fade_dist = get_depth_fade_dist(packed);
                data.surface_alpha = get_surface_alpha(packed);
                data.glow = get_glow(packed);
                return data;
            }

            // Alpha rises from the surface alpha to 1 over dist blocks of scene behind the surface (eye depth). The
            // rise is squared, so floors a few blocks deep stay visible while deep water still turns opaque.
            float depth_fade(const float4 positionSS, const float dist, const float surfaceAlpha)
            {
                if (dist <= 0.0f) return surfaceAlpha;

                float2 ndc = positionSS.xy / positionSS.w;

                float raw_depth = SampleSceneDepth(ndc);
                float scene_eye_depth = LinearEyeDepth(raw_depth, _ZBufferParams);

                float inter = saturate((scene_eye_depth - positionSS.w) / dist);
                return lerp(surfaceAlpha, 1, inter * inter);
            }

            // ── Fragment shader ───────────────────────────────────────
            half4 frag(Varyings IN) : SV_Target
            {
                // --- Texture array sample ---
                FragExtraData extra = unpack_frag_extra_data(IN.packed);
                const float2 uv = IN.uv;
                const float4 albedo = SAMPLE_TEXTURE2D_ARRAY(_Textures, sampler_Textures, uv, extra.texture_index);

                // --- Ambient occlusion ---
                const float4 ao_color = calc_ao_color(_AOColor, albedo, _AOCurve, extra.ao, _AOIntensity, _AOPower, uv);

                // --- Light ---
                const float3 light = calc_voxel_light(IN.packed, uv);

                // --- Depth fade ---
                const float alpha = depth_fade(IN.positionSS, extra.depth_fade_dist, albedo.w * extra.surface_alpha);

                // --- Glow ---
                const float glow = calc_glow(extra.glow);

                // --- Alpha clip ---
                clip(alpha - 0.001f);

                // --- Final colour ---
                return half4(apply_horizon_fog(ao_color.rgb * light * glow, IN.positionWS), alpha);
            }
            ENDHLSL
        }
    }
}