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
    // - VoxelGlass: glass and other plain see-through blocks, drawn by VoxelGlassFeature after the skybox and before
    //   URP copies the opaque colour, so a liquid surface in front shows them in the scene behind it.
    // - Universal Forward: the voxels with a volume slot (liquids), with URP's transparents.
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
            float shade : TEXCOORD4; // face shade
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
            o.shade = face_shade(_FaceShade, v.normal);
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

        // The surface colour (texture, AO, light, glow and face shade) and its alpha.
        float4 surface_color(const Varyings IN)
        {
            const float4 albedo = SAMPLE_TEXTURE2D_ARRAY(_Textures, sampler_Textures, IN.uv, get_tex_index(IN.packed));
            const float4 ao_color = calc_ao_color(_AOColor, albedo, _AOCurve, get_ao(IN.packed), _AOIntensity,
                                                  _AOPower, IN.uv);
            const float3 light = calc_voxel_light(IN.packed, IN.uv, IN.shade);
            const float glow = calc_glow(get_glow(IN.packed));
            return float4(ao_color.rgb * light * glow, albedo.w * get_surface_alpha(IN.packed));
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
            ZWrite On
            Blend SrcAlpha OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex   vert_glass
            #pragma fragment frag_glass

            half4 frag_glass(Varyings IN) : SV_Target
            {
                const float4 color = surface_color(IN);
                clip(color.a - 0.001f);
                return half4(apply_horizon_fog(color.rgb, IN.positionWS), color.a);
            }
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

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex   vert_volume
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareOpaqueTexture.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl"

            // Volume of see-through liquids, by slot (VoxelRegistry.VolumeFogDef); slot 0 = none.
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
                float depthFade;
                float depthDarkening;
                float fogged;
                float pad;
            };

            StructuredBuffer<VolumeFog> _VoxelVolumeFog;

            // Cosine of water's critical angle (refractive index 1.33): from inside, light from above only comes
            // through the surface closer than this to its normal (Snell's window).
            static const float CriticalCos = 0.66f;

            // The opaque scene behind a surface pixel: its colour, how far behind the surface it lies (eye depth)
            // and whether it is the sky.
            struct Behind
            {
                float3 color;
                float depth;
                bool sky;
            };

            Behind sample_behind(const float4 positionSS)
            {
                const float2 uv = positionSS.xy / positionSS.w;
                const float raw_depth = SampleSceneDepth(uv);

                Behind behind;
                behind.color = SampleSceneColor(uv);
                #if UNITY_REVERSED_Z
                behind.sky = raw_depth <= 0.0f;
                #else
                behind.sky = raw_depth >= 1.0f;
                #endif
                behind.depth = max(LinearEyeDepth(raw_depth, _ZBufferParams) - positionSS.w, 0.0f);
                return behind;
            }

            // Depth fade: 0 at the surface, 1 where the scene lies the volume's depth fade distance behind it.
            float depth_fade(const VolumeFog fog, const float behind_depth)
            {
                return fog.depthFade > 0.0f ? saturate(behind_depth / fog.depthFade) : 0.0f;
            }

            // Light of the fog, as UnderwaterFog.shader: lit like an open-sky voxel, never below the ambient or
            // the liquid's own light.
            float3 fog_light(const VolumeFog fog)
            {
                return max(_VoxelSkyBrightness * _VoxelSkyTint.rgb, max(_VoxelAmbient, fog.selfIllumination));
            }

            // Depth darkening: the fog gets darker with depth below the surface (exponential).
            float darkening(const VolumeFog fog, const float depth)
            {
                return exp(-fog.depthDarkening * max(depth, 0.0f));
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

            // The opaque scene behind the surface as the underwater view shows it before the grading:
            // fogged with UnderwaterFog.shader's exp² fog over the distance the view ray travels through the
            // liquid, darkened with depth as there. The depth fade hides it at least as much, so deep liquid turns
            // into its fog. The sky behind counts as infinitely far.
            float3 through_volume(const VolumeFog fog, const Behind behind, const float4 positionSS,
                                  const float3 positionWS)
            {
                const float3 view = positionWS - _WorldSpaceCameraPos;
                const float view_dist = length(view);
                const float ray = behind.depth * view_dist / positionSS.w; // eye depth to distance along the ray
                const float d = ray * fog.density;

                // as dark as the liquid where the fog has mostly built up: the hit, or one fog length down the ray
                const float reach = min(behind.sky ? 1e6f : ray, 1.0f / max(fog.density, 1e-4f));
                const float3 fog_color = fog.fogColor * fog_light(fog) * darkening(fog, -view.y / view_dist * reach);

                const float amount = behind.sky ? 1.0f : max(1.0f - exp(-d * d), depth_fade(fog, behind.depth));
                return lerp(behind.color, fog_color, amount);
            }

            // ── Fragment shader ───────────────────────────────────────
            half4 frag(Varyings IN, const bool front : SV_IsFrontFace) : SV_Target
            {
                // --- Surface: texture, AO, light, glow, face shade ---
                const float4 surface = surface_color(IN);
                float3 color = surface.rgb;
                float alpha = surface.a;

                const uint volume_fog = get_volume_fog(IN.packed);
                if (volume_fog != 0)
                {
                    const VolumeFog fog = _VoxelVolumeFog[volume_fog];
                    const bool fogged = fog.fogged > 0.5f;
                    if (front)
                    {
                        // --- Seen from outside: with fog the surface lies over the fogged scene, both colour graded as
                        // from inside; without, the depth fade raises the alpha to 1 ---
                        const Behind behind = sample_behind(IN.positionSS);
                        if (fogged)
                        {
                            const float3 scene = through_volume(fog, behind, IN.positionSS, IN.positionWS);
                            return half4(apply_horizon_fog(grade(fog, lerp(scene, color, alpha)), IN.positionWS), 1);
                        }
                        alpha = lerp(alpha, 1.0f, depth_fade(fog, behind.depth));
                    }
                    else if (fogged)
                    {
                        // --- Seen from inside (the underwater post-process fogs the way here): outside Snell's
                        // window the surface reflects the liquid below, which shows as its fog ---
                        const float3 view = normalize(IN.positionWS - _WorldSpaceCameraPos);
                        const float3 normal = normalize(cross(ddy(IN.positionWS), ddx(IN.positionWS)));
                        const float window = smoothstep(CriticalCos - 0.04f, CriticalCos + 0.04f,
                                                        abs(dot(view, normal)));
                        const float3 reflected = fog.fogColor * fog_light(fog) *
                            darkening(fog, abs(view.y) / max(fog.density, 1e-4f));
                        color = lerp(reflected, color, window);
                        alpha = lerp(1.0f, alpha, window);
                    }
                }

                // --- Alpha clip ---
                clip(alpha - 0.001f);

                // --- Final colour ---
                return half4(apply_horizon_fog(color, IN.positionWS), alpha);
            }
            ENDHLSL
        }
    }
}
