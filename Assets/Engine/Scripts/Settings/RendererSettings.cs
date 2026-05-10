using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace Engine.Scripts.Settings
{
    /// <summary>
    /// Renderer configuration options used to control chunk materials, compute shaders, and shadow casting.
    /// </summary>
    [Serializable]
    public class RendererSettings
    {
        [Header("Material Settings")]
        /// <summary>
        /// The material used to render solid chunk surfaces.
        /// </summary>
        public Material solidMaterial;
        
        /// <summary>
        /// The material used to render transparent chunk surfaces.
        /// </summary>
        public Material transparentMaterial;
        
        /// <summary>
        /// The material used to render foliage chunk surfaces.
        /// </summary>
        public Material foliageMaterial;
        
        [Header("ComputeShader Settings")]
        /// <summary>
        /// The compute shader used to build point data from voxel partitions.
        /// </summary>
        public ComputeShader pointBuilder;
        
        /// <summary>
        /// The compute shader used to copy built points into render buffers.
        /// </summary>
        public ComputeShader copyPoints;
        
        /// <summary>
        /// The compute shader used to rebuild indirect draw buffers.
        /// </summary>
        public ComputeShader rebuildBuffers;
        
        /// <summary>
        ///     Whether chunk meshes cast shadows.
        /// </summary>
        [HideInInspector]
        public ShadowCastingMode shadows = ShadowCastingMode.Off;
    }
}