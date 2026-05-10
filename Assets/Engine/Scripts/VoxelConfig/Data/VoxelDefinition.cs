using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Engine.Scripts.VoxelConfig.Data
{
    /// <summary>
    /// ScriptableObject that describes a single voxel type, including textures, mesh layer, collision, and optional post-processing data.
    /// </summary>
    [CreateAssetMenu(fileName = "VoxelDefinition", menuName = "Voxel/Voxel Data")]
    public class VoxelDefinition : ScriptableObject
    {
        /// <summary>
        ///     Defines how textures are assigned to voxel faces.
        /// </summary>
        public enum VoxelTexMode
        {
            /// <summary>
            /// One texture is used for all faces.
            /// </summary>
            AllSame,

            /// <summary>
            /// Separate textures for top and bottom, and one shared texture for all side faces.
            /// </summary>
            TopBottomSides,

            /// <summary>
            /// All six directions can have unique textures.
            /// </summary>
            SixSidesUnique,

            /// <summary>
            /// All quads can have unique textures, allowing complex shapes with different textures on each face.
            /// </summary>
            AllUnique
        }

        [SerializeField] private VoxelTexMode textureMode = VoxelTexMode.AllSame;

        /// <summary>
        ///     Mesh layer used when rendering this voxel instance.
        /// </summary>
        public MeshLayer meshLayer;

        /// <summary>
        ///     If true, all faces are always rendered even when hidden by neighbors.
        /// </summary>
        public bool alwaysRenderAllFaces;

        /// <summary>
        ///     Distance at which transparent voxels start fading; negative value disables depth fading.
        /// </summary>
        public float depthFadeDistance = -1f;

        [Range(0, 255)] public int glow;

        public bool usePostProcess;

        /// <summary>
        ///     Optional post processing data applied when rendering this voxel.
        /// </summary>
        public VoxelPostProcessData postProcess = new();

        public VoxelShape shape;

        /// <summary>Texture used for the top face.</summary>
        public Texture2D top;

        /// <summary>Texture used for the bottom face.</summary>
        public Texture2D bottom;

        /// <summary>Texture used for the forward (+Z) face.</summary>
        public Texture2D front;

        /// <summary>Texture used for the backward (-Z) face.</summary>
        public Texture2D back;

        /// <summary>Texture used for the right (+X) face.</summary>
        public Texture2D right;

        /// <summary>Texture used for the left (-X) face.</summary>
        public Texture2D left;

        /// <summary>Texture used for side faces when using <see cref="VoxelTexMode.TopBottomSides" />.</summary>
        public Texture2D side;

        /// <summary>Single texture used for all faces when using <see cref="VoxelTexMode.AllSame" />.</summary>
        public Texture2D all;

        /// <summary>
        ///     If true, this voxel participates in physics collisions.
        /// </summary>
        public bool collision = true;

        public Dictionary<QuadDefinition, Texture2D> allUnique;


        /// <summary>
        /// Gets or sets the texture mapping mode for this voxel.
        /// </summary>
        public VoxelTexMode TextureMode
        {
            get => textureMode;
            set => textureMode = value;
        }


        /// <summary>
        /// Gets the quads and textures that match the given draw condition.
        /// </summary>
        /// <param name="condition">The quad draw condition to filter by.</param>
        /// <returns>A list of quad-definition and texture pairs.</returns>
        public List<(QuadDefinition, Texture2D)> GetQuadsAndTextures(QuadDrawCondition condition)
        {
            List<(QuadDefinition, Texture2D)> result = new();
            foreach (VoxelQuad quad in shape.quads)
            {
                if (quad.drawCondition != condition) continue;
                Texture2D tex = FindTex(quad, condition);
                if (!tex) continue;
                result.Add((quad.quadDef, tex));
            }

            return result;
        }

        /// <summary>
        /// Gets a representative texture for display purposes based on the current texture mode.
        /// </summary>
        /// <param name="condition">The draw condition used to select a texture.</param>
        /// <returns>The texture used for display, or null if no texture is available.</returns>
        public Texture2D GetDisplayTexture(QuadDrawCondition condition)
        {
            return textureMode switch
            {
                VoxelTexMode.AllUnique => allUnique.First().Value,
                _ => FindTex(null, condition)
            };
        }

        /// <summary>
        /// Resolves the texture for a quad and draw condition based on the current texture mode.
        /// </summary>
        /// <param name="quad">The quad to resolve, or null when the mode does not require a quad.</param>
        /// <param name="condition">The draw condition.</param>
        /// <returns>The resolved texture, or null if no texture is assigned.</returns>
        private Texture2D FindTex(VoxelQuad quad, QuadDrawCondition condition)
        {
            return textureMode switch
            {
                VoxelTexMode.AllSame => all,
                VoxelTexMode.TopBottomSides => condition switch
                {
                    QuadDrawCondition.Up => top,
                    QuadDrawCondition.Down => bottom,
                    _ => side
                },
                VoxelTexMode.SixSidesUnique => condition switch
                {
                    QuadDrawCondition.Up => top,
                    QuadDrawCondition.Down => bottom,
                    QuadDrawCondition.Forward => front,
                    QuadDrawCondition.Backward => back,
                    QuadDrawCondition.Left => left,
                    QuadDrawCondition.Right => right,
                    _ => null
                },
                VoxelTexMode.AllUnique => allUnique != null && allUnique.TryGetValue(quad.quadDef, out Texture2D tex)
                    ? tex
                    : null,
                _ => null
            };
        }
    }

    /// <summary>
    /// Optional per-voxel post-processing parameters such as color grading and fog.
    /// </summary>
    [Serializable]
    public class VoxelPostProcessData
    {
        /// <summary>
        /// Color tint applied during post processing.
        /// </summary>
        public Color postProcessColor;

        /// <summary>
        /// Contrast adjustment factor.
        /// </summary>
        public float contrast;

        /// <summary>
        /// Saturation adjustment factor.
        /// </summary>
        public float saturation;

        /// <summary>
        /// Enables additional fog for this voxel type.
        /// </summary>
        public bool enableFog;

        /// <summary>
        /// Fog density value used when <see cref="enableFog" /> is true.
        /// </summary>
        public float fogDensity = .01f;
    }
}