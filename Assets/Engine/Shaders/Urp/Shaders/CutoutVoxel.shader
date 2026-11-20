Shader "Custom/CutoutVoxel"
{
    Properties
    {
        _AOColor ("AO Color", Color) = (0, 0, 0, 1)
        _AOCurve("AOCurve", Vector) = (0.75, 0.825, 0.9, 1)
        _AOIntensity("AOIntensity", Range(0, 1)) = 1
        _AOPower("AOPower", Range(1, 10)) = 1
        _FaceShade("Face shade (top, bottom, x sides, z sides)", Vector) = (1, 0.62, 0.8, 0.88)
        _Cutoff("Alpha cut-off", Range(0, 1)) = 0.5
        [NoScaleOffset] _Textures ("Textures", 2DArray) = "" {}
        [HideInInspector] _VoxelPass ("Pass filter (0 all, 3 cut-out points only)", Float) = 0
    }

    // Blocks with a hard alpha edge: leaves (from the transparent layer, _VoxelPass 3) and the foliage layer
    // (_VoxelPass 0). Each pixel is either drawn or not, so nothing needs sorting; drawn with the opaque blocks
    // (queue AlphaTest), it is in the depth and colour copies the water surface reads.
    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType" = "TransparentCutout"
            "UniversalMaterialType" = "Unlit"
            "Queue" = "AlphaTest"
        }

        // ─────────────────────────────────────────────────────────────
        // Shared data types used by every pass
        // ─────────────────────────────────────────────────────────────
        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "VoxelShaderCommon.hlsl"

        // ── Material properties ───────────────────────────────────
        // Declared once for every pass: the SRP Batcher needs the same UnityPerMaterial layout in all of them.
        CBUFFER_START(UnityPerMaterial)
            float4 _AOColor;
            float _AOIntensity;
            float _AOPower;
            float4 _AOCurve;
            float4 _FaceShade;
            float _Cutoff;
            float _VoxelPass;
        CBUFFER_END

        TEXTURE2D_ARRAY(_Textures);
        SAMPLER(sampler_Textures);

        struct Varyings
        {
            float4 positionCS : SV_POSITION;
            float2 uv : TEXCOORD0; // xy = tile UV
            uint4 packed : TEXCOORD1; // PointData.packed, layout in VoxelCommon.hlsl
            float3 positionWS : TEXCOORD2; // horizon fog
            float2 shade : TEXCOORD3; // face shade seen from the front, from the back
            float3 tint : TEXCOORD4; // biome tint factor, 1 = none (texture_tint)
            nointerpolation uint tintMask : TEXCOORD5; // mask slice, TINT_NO_MASK = the whole face
        };

        // ── Vertex shader with expansion ─────────────────────────────
        Varyings vert(uint vertexID : SV_VertexID)
        {
            VoxelVertexData v = fetch_vertex_data(vertexID);

            Varyings o = (Varyings)0;

            // The transparent layer's buffer also holds glass and liquids, which TransparentVoxel draws; their quads
            // collapse to a point here and are not rasterised.
            if (_VoxelPass > 2.5f && !get_cutout(v.packed)) return o;

            o.positionCS = TransformObjectToHClip(v.positionOS);
            o.uv = v.uv;
            o.packed = v.packed;
            o.positionWS = TransformObjectToWorld(v.positionOS);
            // Faces are drawn from both sides (inside a crown the shared faces are seen from behind)
            o.shade = float2(face_shade(_FaceShade, v.normal), face_shade(_FaceShade, -v.normal));
            o.tint = 1.0f;
            o.tintMask = TINT_NO_MASK;
            return o;
        }

        // The forward pass's vertex shader: also the biome tint of the plant's texture (DEC-120); the depth pass
        // leaves it out. The leaves pass (_VoxelPass 3) reads untinted words.
        Varyings vert_tinted(uint vertexID : SV_VertexID)
        {
            Varyings o = vert(vertexID);
            texture_tint(get_tex_index(o.packed), o.positionWS, o.tint, o.tintMask);
            return o;
        }

        // The texture sample; pixels below the cut-off are discarded.
        float4 sample_cutout(const Varyings IN)
        {
            const float4 albedo = SAMPLE_TEXTURE2D_ARRAY(_Textures, sampler_Textures, IN.uv, get_tex_index(IN.packed));
            clip(albedo.a * get_surface_alpha(IN.packed) - _Cutoff);
            return albedo;
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
            Blend One Zero

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex   vert_tinted
            #pragma fragment frag

            // ── Fragment shader ───────────────────────────────────────
            half4 frag(Varyings IN, const bool front : SV_IsFrontFace) : SV_Target
            {
                float4 albedo = sample_cutout(IN);
                albedo.rgb = apply_texture_tint(albedo.rgb, IN.tint, IN.tintMask, IN.uv);

                // --- Ambient occlusion ---
                const float4 ao_color = calc_ao_color(_AOColor, albedo, _AOCurve, get_ao(IN.packed), _AOIntensity,
                                                      _AOPower, IN.uv);

                // --- Light, glow and face shade ---
                const float3 light = calc_voxel_light(IN.packed, IN.uv, front ? IN.shade.x : IN.shade.y);
                const float glow = calc_glow(get_glow(IN.packed));

                // --- Final colour ---
                return half4(apply_horizon_fog(ao_color.rgb * light * glow, IN.positionWS), 1);
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

            Cull Off
            ZTest LEqual
            ZWrite On
            ColorMask R

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex   vert
            #pragma fragment frag_depth

            half frag_depth(Varyings IN) : SV_Target
            {
                sample_cutout(IN);
                return 0;
            }
            ENDHLSL
        }
    }
}
