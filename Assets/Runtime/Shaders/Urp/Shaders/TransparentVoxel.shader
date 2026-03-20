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

        StructuredBuffer<PointData> _PointData;
        StructuredBuffer<uint> _VisiblePartitions;
        StructuredBuffer<uint> _PageStates;
        uint _PointsPerPage;

        static const uint MAX_PAGE_STATES = 512u;

        struct Varyings
        {
            float4 positionCS : SV_POSITION;
            float2 uv : TEXCOORD0; // xy = tile UV
            uint4 packed : TEXCOORD1; // x: (texArrayIndex u16, sunLightLevel u4, 4 bit unused, ao u8)
            // y:half16 , 16 bit unused
            // z and w unused
            float4 positionSS : TEXCOORD3;
        };

        uint resolve_physical_point_id(uint logicalPointID)
        {
            if (_PointsPerPage == 0u)
            {
                return 0u;
            }

            uint remaining = logicalPointID;
            [loop]
            for (uint pageIndex = 0u; pageIndex < MAX_PAGE_STATES; pageIndex++)
            {
                uint pageCount = _PageStates[pageIndex];
                if (pageCount == 0u)
                {
                    break;
                }

                if (remaining < pageCount)
                {
                    return pageIndex * _PointsPerPage + remaining;
                }

                remaining -= pageCount;
            }

            return 0u;
        }


        // ── Vertex shader with expansion ─────────────────────────────
        Varyings vert(uint vertexID : SV_VertexID)
        {
            // Calculate point and corner indices
            uint pointID = vertexID / 6;
            uint cornerID = vertexID % 6;
            uint physicalPointID = resolve_physical_point_id(pointID);
            
            // Fetch point data
            PointData p = _PointData[physicalPointID];
            
            uint quadIndex = get_quad_index(p.packed);
            QuadData quad = quad_buffer[quadIndex];
            
            // Triangle strip corners: two triangles forming a quad
            // Triangle 1: 00-01-02, Triangle 2: 02-01-03
            float3 corners[6] = {
                quad.position00, quad.position01, quad.position02,
                quad.position02, quad.position01, quad.position03
            };
            float2 uvs[6] = {
                quad.uv00, quad.uv01, quad.uv02,
                quad.uv02, quad.uv01, quad.uv03
            };
            
            Varyings o;
            float4 worldPos = float4(p.position + corners[cornerID], 1.0);
            o.positionCS = TransformObjectToHClip(worldPos.xyz);
            o.uv = uvs[cornerID];
            o.packed = p.packed;
            o.positionSS = ComputeScreenPos(o.positionCS);
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

            #pragma multi_compile_instancing

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
                uint4 sun_light;
                uint4 artificial_light;
                uint ao;
                float depth_fade_dist;
                float glow;
            };

            FragExtraData unpack_frag_extra_data(uint4 packed)
            {
                FragExtraData data;
                data.texture_index = get_tex_index(packed);
                data.sun_light = get_sun_light(packed);
                data.artificial_light = get_artificial_light(packed);
                data.ao = get_ao(packed);
                data.depth_fade_dist = get_depth_fade_dist(packed);
                data.glow = get_glow(packed);
                return data;
            }

            float depth_fade(const float4 positionSS, const float dist, const float texAlpha)
            {
                if (dist <= 0.1f) return texAlpha;

                float2 ndc = positionSS.xy / positionSS.w;

                float raw_depth = SampleSceneDepth(ndc);
                float scene_eye_depth = LinearEyeDepth(raw_depth, _ZBufferParams);

                float inter = saturate((scene_eye_depth - positionSS.w) / dist);
                return lerp(texAlpha, 1, inter);
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

                // --- Sun light level ---
                const float sun_light = calc_sun_light(extra.sun_light, uv);

                // --- Depth fade ---
                const float alpha = depth_fade(IN.positionSS, extra.depth_fade_dist, albedo.w);

                // --- Glow ---
                const float glow = calc_glow(extra.glow);

                // --- Final colour ---
                return half4(ao_color.rgb * sun_light * glow, alpha);
            }
            ENDHLSL
        }

        // ═════════════════════════════════════════════════════════════
        // Pass – Scene Selection (editor highlight / outline)
        // ═════════════════════════════════════════════════════════════
        Pass
        {
            Name "SceneSelectionPass"
            Tags
            {
                "LightMode" = "SceneSelectionPass"
            }

            Cull Off
            ZTest LEqual
            ZWrite On
            ColorMask RGBA

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex   vert
            #pragma fragment frag_sel

            #pragma multi_compile_instancing

            CBUFFER_START(UnityPerMaterial)
                float4 _AOColor;
                float4 _AOCurve;
                float _AOIntensity;
                float _AOPower;
            CBUFFER_END

            int _ObjectId;
            int _PassValue;

            half4 frag_sel(Varyings IN) : SV_Target
            {
                return half4(_ObjectId, _PassValue, 1, 1);
            }
            ENDHLSL
        }

        // ═════════════════════════════════════════════════════════════
        // Pass – Scene Picking (editor GPU picking)
        // ═════════════════════════════════════════════════════════════
        Pass
        {
            Name "ScenePickingPass"
            Tags
            {
                "LightMode" = "Picking"
            }

            Cull Off
            ZTest LEqual
            ZWrite On
            ColorMask RGBA

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex   vert
            #pragma fragment frag_pick

            #pragma multi_compile_instancing

            CBUFFER_START(UnityPerMaterial)
                float4 _AOColor;
                float4 _AOCurve;
                float _AOIntensity;
                float _AOPower;
            CBUFFER_END

            float4 _SelectionID;

            half4 frag_pick(Varyings IN) : SV_Target
            {
                return _SelectionID;
            }
            ENDHLSL
        }
    }
}