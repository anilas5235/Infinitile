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
        ///     The material used to render transparent chunk surfaces.
        /// </summary>
        public Material transparentMaterial;

        /// <summary>
        ///     The material used to render foliage chunk surfaces.
        /// </summary>
        public Material foliageMaterial;

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
        ///     Horizon fog in the voxel shaders, full at the edge of the drawn chunks so they fade into the sky.
        /// </summary>
        public bool horizonFog = true;

        /// <summary>Where the horizon fog starts, as a fraction of the draw distance.</summary>
        [Range(0f, 1f)] public float fogStart = 0.6f;

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
