using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace Engine.Scripts.Settings
{
    /// <summary>
    ///     Renderer-related options for chunk presentation.
    /// </summary>
    [Serializable]
    public class RendererSettings
    {
        [Header("Material Settings")]
        public Material solidMaterial;
        
        public Material transparentMaterial;
        
        public Material foliageMaterial;
        
        [Header("ComputeShader Settings")]
        public ComputeShader pointBuilder;
        
        public ComputeShader copyPoints;
        
        public ComputeShader rebuildBuffers;
        
        /// <summary>
        ///     Whether chunk meshes cast shadows.
        /// </summary>
        [HideInInspector]
        public ShadowCastingMode shadows = ShadowCastingMode.Off;
    }
}