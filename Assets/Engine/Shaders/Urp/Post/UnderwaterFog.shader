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
                const float d = distance(positionWS, _WorldSpaceCameraPos) * _UnderwaterFogDensity;

                // --- Fog colour lit like an open-sky voxel, never below the ambient or the liquid's own light ---
                const float3 light = max(_VoxelSkyBrightness * _VoxelSkyTint.rgb, max(_VoxelAmbient, _UnderwaterFogEmission));
                const float3 fog_color = _UnderwaterFogColor.rgb * light;

                // --- Exp² fog ---
                const float fog = sky ? 1.0 : 1.0 - exp(-d * d);
                return half4(lerp(scene.rgb, fog_color, fog), scene.a);
            }
            ENDHLSL
        }
    }
}
