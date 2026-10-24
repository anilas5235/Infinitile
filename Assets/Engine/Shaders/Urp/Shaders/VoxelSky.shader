Shader "Engine/VoxelSky"
{
    Properties
    {
        _HorizonColor ("Horizon (also the horizon fog colour)", Color) = (0.66, 0.78, 0.92, 1)
        _ZenithColor ("Zenith", Color) = (0.24, 0.46, 0.84, 1)
        _GradientPower ("Gradient power", Range(0.1, 4)) = 0.6
        _SunColor ("Sun", Color) = (1, 0.96, 0.85, 1)
        _SunSize ("Sun radius (radians)", Range(0.005, 0.2)) = 0.04
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType" = "Background"
            "Queue" = "Background"
            "PreviewType" = "Skybox"
        }

        Cull Off
        ZWrite Off

        Pass
        {
            HLSLPROGRAM
            #pragma vertex   vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _HorizonColor;
                float4 _ZenithColor;
                float _GradientPower;
                float4 _SunColor;
                float _SunSize;
            CBUFFER_END

            // Voxel lighting globals (VoxelShaderCommon.hlsl) and the sun set by VoxelWorldRenderer
            float _VoxelSkyBrightness;
            float4 _VoxelSkyTint;
            float _VoxelAmbient;
            float4 _VoxelSunDirection;

            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 direction : TEXCOORD0;
            };

            Varyings vert(Attributes v)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                o.direction = v.positionOS.xyz;
                return o;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                const float3 dir = normalize(IN.direction);

                // --- Lit like an open-sky voxel face, so the horizon matches the voxel horizon fog ---
                const float3 light = max(_VoxelSkyBrightness * _VoxelSkyTint.rgb, _VoxelAmbient);

                // --- Gradient; below the horizon the sky stays at the horizon colour ---
                const float t = pow(saturate(dir.y), _GradientPower);
                float3 sky = lerp(_HorizonColor.rgb, _ZenithColor.rgb, t) * light;

                // --- Sun disc (1 - cos(a) is about a^2 / 2), hidden below the horizon ---
                const float r = _SunSize * _SunSize * 0.5f;
                const float disc = 1.0f - smoothstep(r * 0.6f, r, 1.0f - dot(dir, _VoxelSunDirection.xyz));
                sky = lerp(sky, _SunColor.rgb * light, disc * saturate(dir.y * 50.0f));

                return half4(sky, 1);
            }
            ENDHLSL
        }
    }
}
