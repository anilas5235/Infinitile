using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace Engine.Scripts.Settings{
    /// <summary>
    ///     Renderer configuration options used to control chunk materials, compute shaders, and shadow casting.
    /// </summary>
    [Serializable]
    public class RendererSettings{
        /// <summary>
        ///     The material used to render solid chunk surfaces.
        /// </summary>
        [Header("Material Settings")]
        public Material solidMaterial;

        /// <summary>
        ///     The material used to render the blended blocks of the transparent layer (TransparentVoxel.shader): glass
        ///     in its VoxelGlass pass (VoxelGlassFeature) and liquids with URP's transparents.
        /// </summary>
        public Material transparentMaterial;

        /// <summary>
        ///     The material used to render foliage chunk surfaces (CutoutVoxel.shader, all points).
        /// </summary>
        public Material foliageMaterial;

        /// <summary>
        ///     The material used to render the cut-out blocks of the transparent layer, such as leaves
        ///     (CutoutVoxel.shader, _VoxelPass 3), with the opaque blocks.
        /// </summary>
        public Material leavesMaterial;

        /// <summary>
        ///     The compute shader used to build point data from voxel partitions.
        /// </summary>
        [Header("ComputeShader Settings")]
        public ComputeShader pointBuilder;

        /// <summary>
        ///     The compute shader used to copy built points into render buffers.
        /// </summary>
        public ComputeShader copyPoints;

        /// <summary>
        ///     The compute shader that culls render pages per camera and writes the indirect draw args
        ///     (PageCulling.compute, kernels CullPages and WriteArgs).
        /// </summary>
        public ComputeShader pageCulling;

        /// <summary>
        ///     Skybox material (Engine/VoxelSky) set as the scene's skybox at startup. Its horizon colour is the colour
        ///     of the horizon fog, so the fog meets the sky. Empty keeps the scene's skybox.
        /// </summary>
        [Header("Sky and fog")]
        public Material skyMaterial;

        /// <summary>
        ///     Horizon fog in the voxel shaders: an edge fade over the outer chunk ring of the drawn square, full at
        ///     its edge so the drawn chunks fade into the sky. Nearer terrain has no fog.
        /// </summary>
        public bool horizonFog = true;

        /// <summary>
        ///     How sharply the fog rises over the outer chunk ring, (exp(k t) − 1) / (exp(k) − 1); higher keeps the
        ///     ring clear for longer, near 0 is a straight ramp.
        /// </summary>
        [Min(0.01f)] public float fogSteepness = 3f;

        /// <summary>
        ///     How strongly the face shade darkens the sides of a block (the <c>_FaceShade</c> values of the voxel
        ///     materials and the shade by the sun's angle): 0 lights every face alike, 1 is the full shade. Read every
        ///     frame, so it can be tuned in Play mode.
        /// </summary>
        [Range(0f, 1f)] public float faceShadeStrength = DefaultFaceShadeStrength;

        /// <summary>The shipped <see cref="faceShadeStrength" /> (owner's pick, 2026-11-18).</summary>
        public const float DefaultFaceShadeStrength = 0.65f;

        /// <summary>
        ///     GPU frustum culling of render pages. Off draws every page that holds points, for debugging and
        ///     before/after comparison; the image must be identical.
        /// </summary>
        [Header("Culling")]
        public bool gpuCulling = true;

        /// <summary>
        ///     Whether chunk meshes cast shadows. With shadows on, every buffer is drawn a second time as shadow caster
        ///     with a list of pages whose bounds extruded along the main light reach the view.
        /// </summary>
        [HideInInspector] public ShadowCastingMode shadows = ShadowCastingMode.Off;
    }
}
