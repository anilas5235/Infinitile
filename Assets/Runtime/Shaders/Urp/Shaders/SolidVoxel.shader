Shader "Custom/VoxelShader"
{
    Properties
    {
        _AOColor ("AO Color", Color) = (0, 0, 0, 1)
        _AOCurve("AOCurve", Vector) = (0.75, 0.825, 0.9, 1)
        _AOIntensity("AOIntensity", Range(0, 1)) = 1
        _AOPower("AOPower", Range(1, 10)) = 1
        [NoScaleOffset] _Textures ("Textures", 2DArray) = "" {}
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType" = "Opaque"
            "UniversalMaterialType" = "Unlit"
            "Queue" = "Geometry"
        }

        // ─────────────────────────────────────────────────────────────
        // Shared data types used by every pass
        // ─────────────────────────────────────────────────────────────
        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "VoxelShaderCommon.hlsl"
        struct Varyings
        {
            float4 positionCS : SV_POSITION;
            float2 uv : TEXCOORD0; // xy = tile UV
            uint4 packed : TEXCOORD1; // (texArrayIndex u16, sunLightLevel u4, 4 bit unused, ao u8)
        };    

        // ── Vertex shader with expansion ─────────────────────────────
        Varyings vert(uint vertexID : SV_VertexID)
        {
           VoxelVertexData v = fetch_vertex_data(vertexID);

            Varyings o;
            o.positionCS = TransformObjectToHClip(v.positionOS);
            o.uv = v.uv;
            o.packed = v.packed;
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

            Cull Back
            ZTest LEqual
            ZWrite On
            Blend One Zero

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex   vert
            #pragma fragment frag

            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

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
            };

            FragExtraData unpack_frag_extra_data(uint4 packed)
            {
                FragExtraData data;
                data.texture_index = get_tex_index(packed);
                data.sun_light = get_sun_light(packed);
                data.artificial_light = get_artificial_light(packed);
                data.ao = get_ao(packed);
                return data;
            }

            // ── Fragment shader ───────────────────────────────────────
            half4 frag(Varyings IN) : SV_Target
            {
                //return half4(1, 0, 1, 1); // Debug magenta to verify shader is running
                // --- Texture array sample ---
                FragExtraData extra = unpack_frag_extra_data(IN.packed);
                float2 uv = IN.uv;
                float4 albedo = SAMPLE_TEXTURE2D_ARRAY(_Textures, sampler_Textures, uv, extra.texture_index);

                // --- Ambient occlusion ---
                float4 ao_color = calc_ao_color(_AOColor, albedo, _AOCurve, extra.ao, _AOIntensity, _AOPower, uv);

                // --- Sun light level ---
                float sun_light = calc_sun_light(extra.sun_light, uv);

                // --- Final colour ---
                return half4(ao_color.rgb * sun_light, 1);
            }
            ENDHLSL
        }

        // ═════════════════════════════════════════════════════════════
        // Pass – Depth Only
        // ═════════════════════════════════════════════════════════════
        Pass
        {
            Name "DepthOnly"
            Tags
            {
                "LightMode" = "DepthOnly"
            }

            Cull Back
            ZTest LEqual
            ZWrite On
            ColorMask R

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex   vert
            #pragma fragment frag_depth

            #pragma multi_compile_instancing

            CBUFFER_START(UnityPerMaterial)
                float4 _AOColor;
                float4 _AOCurve;
                float _AOIntensity;
                float _AOPower;
            CBUFFER_END

            half frag_depth(Varyings IN) : SV_Target
            {
                return 0;
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

            // Unity-supplied selection ID (injected by the editor)
            int _ObjectId;
            int _PassValue;

            half4 frag_sel(Varyings IN) : SV_Target
            {
                // Encode the object/pass ID as required by the SceneSelectionPass protocol.
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

            // Unity encodes the picking ID into this float4 via
            // SceneView / HandleUtility.
            float4 _SelectionID;

            half4 frag_pick(Varyings IN) : SV_Target
            {
                return _SelectionID;
            }
            ENDHLSL
        }
    }
}