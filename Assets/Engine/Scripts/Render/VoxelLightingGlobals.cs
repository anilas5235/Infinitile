using UnityEngine;
using static Engine.Scripts.Utils.VoxelRenderConstants;

namespace Engine.Scripts.Render{
    /// <summary>
    ///     The shader globals of the voxel lighting. The <see cref="DayNightCycle" /> drives them every frame;
    ///     without one the renderer sets the defaults (full daylight), so a scene without the component is lit.
    /// </summary>
    public static class VoxelLightingGlobals{
        public const float DefaultSkyBrightness = 1f;
        public const float DefaultAmbient = 0.04f;

        public static void Set(float skyBrightness, Color skyTint, float ambient){
            Shader.SetGlobalFloat(VoxelSkyBrightnessNameID, skyBrightness);
            Shader.SetGlobalColor(VoxelSkyTintNameID, skyTint);
            Shader.SetGlobalFloat(VoxelAmbientNameID, ambient);
        }

        public static void SetDefaults(){
            Set(DefaultSkyBrightness, Color.white, DefaultAmbient);
        }

        /// <summary>
        ///     Horizon fog of the voxel shaders: none up to <paramref name="start" />, full at <paramref name="end" />
        ///     (horizontal blocks from the camera), in the sky's colour in the view direction: the gradient from
        ///     <paramref name="horizon" /> to <paramref name="zenith" /> with <paramref name="gradientPower" />.
        /// </summary>
        public static void SetHorizonFog(bool enabled, Color horizon, Color zenith, float gradientPower, float start,
            float end){
            Shader.SetGlobalColor(VoxelHorizonColorNameID, horizon);
            Shader.SetGlobalColor(VoxelZenithColorNameID, zenith);
            Shader.SetGlobalVector(VoxelFogParamsNameID, FogParams(enabled, start, end, gradientPower));
        }

        /// <summary>
        ///     Shader form of the fog: x = start, y = 1 / (end − start), z = 1 when on, w = the sky's gradient power.
        /// </summary>
        public static Vector4 FogParams(bool enabled, float start, float end, float gradientPower){
            return new Vector4(start, 1f / Mathf.Max(end - start, 1e-3f), enabled ? 1f : 0f, gradientPower);
        }

        /// <summary>Unit direction towards the sun for the sky's sun disc; zero hides it.</summary>
        public static void SetSunDirection(Vector3 towardsSun){
            Shader.SetGlobalVector(VoxelSunDirectionNameID, towardsSun);
        }
    }
}
