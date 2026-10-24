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
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareOpaqueTexture.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl"

            // ── Material properties ───────────────────────────────────
            CBUFFER_START(UnityPerMaterial)
                float4 _AOColor;
                float _AOIntensity;
                float _AOPower;
                float4 _AOCurve;
            CBUFFER_END

            TEXTURE2D_ARRAY(_Textures);
            SAMPLER(sampler_Textures);

            // Volume fog of see-through liquids, by slot (VoxelRegistry.VolumeFogDef); slot 0 = none.
            struct VolumeFog
            {
                float3 fogColor;
                float density;
                float3 lift;
                float selfIllumination;
                float3 gain;
                float contrast;
                float3 invGamma;
                float saturation;
            };

            StructuredBuffer<VolumeFog> _VoxelVolumeFog;

            struct FragExtraData
            {
                uint texture_index;
                uint ao;
                uint volume_fog;
                float surface_alpha;
                float glow;
            };

            FragExtraData unpack_frag_extra_data(uint4 packed)
            {
                FragExtraData data;
                data.texture_index = get_tex_index(packed);
                data.ao = get_ao(packed);
                data.volume_fog = get_volume_fog(packed);
                data.surface_alpha = get_surface_alpha(packed);
                data.glow = get_glow(packed);
                return data;
            }

            // The underwater colour grading, as URP's LutBuilderLdr applies the liquid's ColorAdjustments and
            // LiftGammaGain overrides: contrast in LogC, lift/gamma/gain, global saturation.
            float3 grade(const VolumeFog fog, float3 color)
            {
                color = LogCToLinear((LinearToLogC(color) - ACEScc_MIDGRAY) * fog.contrast + ACEScc_MIDGRAY);
                color = max(0.0f, color);
                color = color * fog.gain + fog.lift;
                color = sign(color) * pow(abs(color), fog.invGamma);
                const float luma = Luminance(color);
                return luma + fog.saturation * (color - luma);
            }

            // The opaque scene behind the surface as the underwater view shows it (GEN-38): fogged with
            // UnderwaterFog.shader's exp² fog over the distance the view ray travels through the liquid, then
            // colour graded. The sky behind counts as infinitely far.
            float3 through_volume(const VolumeFog fog, const float4 positionSS, const float3 positionWS)
            {
                const float2 uv = positionSS.xy / positionSS.w;
                const float3 scene = SampleSceneColor(uv);

                const float raw_depth = SampleSceneDepth(uv);
                #if UNITY_REVERSED_Z
                const bool sky = raw_depth <= 0.0f;
                #else
                const bool sky = raw_depth >= 1.0f;
                #endif
                const float surface_eye_depth = positionSS.w;
                const float behind = max(LinearEyeDepth(raw_depth, _ZBufferParams) - surface_eye_depth, 0.0f);
                const float d = behind * distance(positionWS, _WorldSpaceCameraPos) / surface_eye_depth * fog.density;

                const float3 light = max(_VoxelSkyBrightness * _VoxelSkyTint.rgb, max(_VoxelAmbient, fog.selfIllumination));
                const float3 fog_color = fog.fogColor * light;
                const float amount = sky ? 1.0f : 1.0f - exp(-d * d);
                return grade(fog, lerp(scene, fog_color, amount));
            }

            // ── Fragment shader ───────────────────────────────────────
            half4 frag(Varyings IN, const bool front : SV_IsFrontFace) : SV_Target
            {
                // --- Texture array sample ---
                FragExtraData extra = unpack_frag_extra_data(IN.packed);
                const float2 uv = IN.uv;
                const float4 albedo = SAMPLE_TEXTURE2D_ARRAY(_Textures, sampler_Textures, uv, extra.texture_index);

                // --- Ambient occlusion ---
                const float4 ao_color = calc_ao_color(_AOColor, albedo, _AOCurve, extra.ao, _AOIntensity, _AOPower, uv);

                // --- Light ---
                const float3 light = calc_voxel_light(IN.packed, uv);

                // --- Glow ---
                const float glow = calc_glow(extra.glow);

                const float3 surface = ao_color.rgb * light * glow;
                const float alpha = albedo.w * extra.surface_alpha;

                // --- Volume fog: seen from outside, the surface lies over the fogged scene behind it. From inside
                // (back faces) the underwater post-process fogs the view instead. ---
                if (extra.volume_fog != 0 && front)
                {
                    const float3 behind = through_volume(_VoxelVolumeFog[extra.volume_fog], IN.positionSS, IN.positionWS);
                    return half4(apply_horizon_fog(lerp(behind, surface, alpha), IN.positionWS), 1);
                }

                // --- Alpha clip ---
                clip(alpha - 0.001f);

                // --- Final colour ---
                return half4(apply_horizon_fog(surface, IN.positionWS), alpha);
            }
            ENDHLSL
        }
    }
}