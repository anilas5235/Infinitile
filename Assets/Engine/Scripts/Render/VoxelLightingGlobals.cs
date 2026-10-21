using UnityEngine;
using static Engine.Scripts.Utils.VoxelRenderConstants;

namespace Engine.Scripts.Render{
    /// <summary>
    ///     LGT-10: the shader globals of the voxel lighting. The <see cref="DayNightCycle" /> drives them every frame;
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
    }
}
