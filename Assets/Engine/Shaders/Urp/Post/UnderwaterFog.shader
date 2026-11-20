Shader "Hidden/Engine/UnderwaterFog"
{
    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
        }

        ZWrite Off
        ZTest Always
        Cull Off
        Blend Off

        Pass
        {
            Name "UnderwaterFog"

            HLSLPROGRAM
            #pragma vertex   Vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            TEXTURE2D_X_FLOAT(_UnderwaterFogDepth);
            float4 _UnderwaterFogColor;
            float _UnderwaterFogDensity;
            float _UnderwaterFogEmission;
            float4 _UnderwaterFogSurface; // x = height of the liquid's surface, y = depth darkening per block, z = scene tint

            // Voxel lighting globals (VoxelShaderCommon.hlsl)
            float _VoxelSkyBrightness;
            float4 _VoxelSkyTint;
            float _VoxelAmbient;

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                const float2 uv = input.texcoord;
                const half4 scene = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv);

                // --- Distance from the camera to the nearest surface; the sky is infinitely far ---
                const float raw_depth = SAMPLE_TEXTURE2D_X(_UnderwaterFogDepth, sampler_PointClamp, uv).r;
                #if UNITY_REVERSED_Z
                const float depth = raw_depth;
                const bool sky = depth <= 0.0;
                #else
                const float depth = lerp(UNITY_NEAR_CLIP_VALUE, 1, raw_depth);
                const bool sky = depth >= 1.0;
                #endif
                const float3 positionWS = ComputeWorldSpacePosition(uv, depth, UNITY_MATRIX_I_VP);
                const float3 to_point = positionWS - _WorldSpaceCameraPos;
                const float dist = length(to_point);
                const float d = dist * _UnderwaterFogDensity;

                // --- Depth darkening: the fog is as dark as the liquid where it has mostly built up, at the hit
                // or one fog length (1 / density) down the ray, whichever is nearer ---
                const float reach = min(sky ? 1e6 : dist, 1.0 / max(_UnderwaterFogDensity, 1e-4));
                const float y = _WorldSpaceCameraPos.y + to_point.y / max(dist, 1e-4) * reach;
                const float darken = exp(-_UnderwaterFogSurface.y * max(_UnderwaterFogSurface.x - y, 0.0));

                // --- Fog colour lit like an open-sky voxel, never below the ambient or the liquid's own light ---
                const float3 light = max(_VoxelSkyBrightness * _VoxelSkyTint.rgb, max(_VoxelAmbient, _UnderwaterFogEmission));
                const float3 fog_color = _UnderwaterFogColor.rgb * light * darken;

                // --- Exp² fog ---
                const float fog = sky ? 1.0 : 1.0 - exp(-d * d);
                // --- Scene tint: what is seen through the liquid takes on the fog's hue (its colour at full
                // brightness) and the depth darkening, so near blocks look blue too, not only the far ones ---
                const float3 hue = _UnderwaterFogColor.rgb /
                                   max(max(_UnderwaterFogColor.r, _UnderwaterFogColor.g), max(_UnderwaterFogColor.b, 1e-3));
                const float tint = _UnderwaterFogSurface.z;
                const float3 seen = scene.rgb * lerp(1.0, hue, tint) * lerp(1.0, darken, tint);
                return half4(lerp(seen, fog_color, fog), scene.a);
            }
            ENDHLSL
        }
    }
}
