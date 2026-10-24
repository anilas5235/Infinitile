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
        ///     Horizon fog of the voxel shaders: none up to <paramref name="start" />, rising exponentially with
        ///     <paramref name="steepness" /> to full at <paramref name="end" /> (the larger of the x and z blocks from
        ///     the camera, a square like the drawn chunks), in the sky's colour in the view direction: the gradient from
        ///     <paramref name="horizon" /> to <paramref name="zenith" /> with <paramref name="gradientPower" />. The
        ///     colours are the sky material's (sRGB); the material converts them to linear for the sky, a global colour
        ///     is passed on as it is, so they are converted here or the fog is paler than the sky.
        /// </summary>
        public static void SetHorizonFog(bool enabled, Color horizon, Color zenith, float gradientPower, float start,
            float end, float steepness){
            bool linear = QualitySettings.activeColorSpace == ColorSpace.Linear;
            Shader.SetGlobalColor(VoxelHorizonColorNameID, linear ? horizon.linear : horizon);
            Shader.SetGlobalColor(VoxelZenithColorNameID, linear ? zenith.linear : zenith);
            Shader.SetGlobalVector(VoxelFogParamsNameID, FogParams(enabled, start, end, gradientPower));
            Shader.SetGlobalVector(VoxelFogCurveNameID, FogCurve(steepness));
        }

        /// <summary>
        ///     Shader form of the fog: x = start, y = 1 / (end − start), z = 1 when on, w = the sky's gradient power.
        /// </summary>
        public static Vector4 FogParams(bool enabled, float start, float end, float gradientPower){
            return new Vector4(start, 1f / Mathf.Max(end - start, 1e-3f), enabled ? 1f : 0f, gradientPower);
        }

        /// <summary>
        ///     Shader form of the fog's rise (exp(k t) − 1) / (exp(k) − 1) over t = 0..1: x = k, y = 1 / (exp(k) − 1).
        ///     The steepness is kept above 0, where the curve becomes a straight line.
        /// </summary>
        public static Vector4 FogCurve(float steepness){
            float k = Mathf.Max(steepness, 1e-3f);
            return new Vector4(k, 1f / (Mathf.Exp(k) - 1f), 0f, 0f);
        }

        /// <summary>Unit direction towards the sun for the sky's sun disc; zero hides it.</summary>
        public static void SetSunDirection(Vector3 towardsSun){
            Shader.SetGlobalVector(VoxelSunDirectionNameID, towardsSun);
        }
    }
}
