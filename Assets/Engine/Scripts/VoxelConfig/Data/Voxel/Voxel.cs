using System;
using System.Collections.Generic;
using System.Linq;
using Engine.Scripts.Utils.Logger;
using Engine.Scripts.VoxelConfig.Data.Mesh;
using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Engine.Scripts.VoxelConfig.Data.Voxel{
    /// <summary>
    ///     ScriptableObject that describes a single voxel type, including textures, mesh layer, collision, and optional
    ///     post-processing data.
    /// </summary>
    [CreateAssetMenu(fileName = "Voxel", menuName = "Infinitile/Voxel/VoxelDefinition")]
    public class Voxel : ScriptableObject{
        /// <summary>
        ///     Defines how textures are assigned to voxel faces.
        /// </summary>
        public enum VoxelTexMode{
            /// <summary>
            ///     One texture is used for all faces.
            /// </summary>
            AllSame,

            /// <summary>
            ///     Separate textures for top and bottom, and one shared texture for all side faces.
            /// </summary>
            TopBottomSides,

            /// <summary>
            ///     All six directions can have unique textures.
            /// </summary>
            SixSidesUnique,

            /// <summary>
            ///     All quads can have unique textures, allowing complex shapes with different textures on each face.
            /// </summary>
            AllUnique
        }

        public VoxelDataPackage package;

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
        ///     Depth (blocks behind the surface) over which a transparent voxel's alpha rises from the surface alpha to
        ///     1, so deep liquid turns opaque; zero or negative disables the depth fade.
        /// </summary>
        [Tooltip("Blocks behind the surface over which the alpha rises from the surface alpha to 1; <= 0 disables it")]
        public float depthFadeDistance = -1f;

        /// <summary>
        ///     Scale of the texture alpha (see-through water); 1 keeps the texture alpha. A transparent voxel with
        ///     post-process fog draws its surface with this alpha over the scene behind it, fogged and colour graded as from inside.
        /// </summary>
        [Tooltip("Scale of the texture alpha at the surface; with post-process fog the scene behind is fogged and graded as seen from inside")]
        [Range(0f, 1f)] public float surfaceAlpha = 1f;

        [Range(0, 255)] public int glow;

        /// <summary>
        ///     Extra light loss per step into this voxel, 0–15 (air and glass 0, leaves 1, water 2;
        ///     15 = opaque, the default for solid blocks). Opaque voxels hold no light unless they emit.
        /// </summary>
        [Tooltip("Extra light loss per step, 0 = clear, 15 = opaque")]
        [Range(0, VoxelLightLimits.MaxLevel)] public int lightAttenuation = VoxelLightLimits.MaxLevel;

        /// <summary>
        ///     Emitted block light per channel (R, G, B), 0–15 each, e.g. lava (15, 9, 3). Independent of
        ///     <see cref="glow" />, which stays a render effect.
        /// </summary>
        [Tooltip("Emitted block light (R, G, B), 0-15 per channel")]
        public Vector3Int lightEmission;

        public bool usePostProcess;

        /// <summary>
        ///     Optional post processing data applied when rendering this voxel.
        /// </summary>
        public VoxelPostProcessData postProcess = new();

        public VoxelShape shape;

        /// <summary>
        ///     How the voxel may be turned when placed. The asset is authored as the unturned orientation: axis Y,
        ///     front +Z (the <see cref="front" /> texture), lower half. Each other orientation is its own voxel id.
        /// </summary>
        [Tooltip("How the voxel may be turned when placed; the asset is authored with axis Y and front +Z")]
        public VoxelFacing facing;

        /// <summary>Adds an upper (upside-down) orientation to every facing, like a slab in the upper half.</summary>
        [Tooltip("Adds an upper (upside-down) orientation to every facing, like a slab in the upper half")]
        public bool topBottom;

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

        /// <summary>
        ///     If true, the player cannot break this voxel. Generation and <c>VoxelWorld.SetVoxel</c> still replace it.
        /// </summary>
        [Tooltip("The player cannot break it; generation and scripts still can")]
        public bool unbreakable;

        public Dictionary<QuadDefinition, Texture2D> allUnique;

        /// <summary>
        ///     Gets or sets the texture mapping mode for this voxel.
        /// </summary>
        public VoxelTexMode TextureMode{
            get => textureMode;
            set => textureMode = value;
        }

        /// <summary>
        ///     Gets the quads and textures that match the given draw condition.
        /// </summary>
        /// <param name="condition">The quad draw condition to filter by.</param>
        /// <returns>A list of quad-definition and texture pairs.</returns>
        public List<(QuadDefinition, Texture2D)> GetQuadsAndTextures(QuadDrawCondition condition){
            List<(QuadDefinition, Texture2D)> result = new();
            foreach (VoxelQuad quad in shape.quads){
                if (quad.drawCondition != condition) continue;
                Texture2D tex = FindTex(quad, condition);
                if (!tex) continue;
                result.Add((quad.quadDef, tex));
            }

            return result;
        }

        /// <summary>
        ///     Gets a representative texture for display purposes based on the current texture mode.
        /// </summary>
        /// <param name="condition">The draw condition used to select a texture.</param>
        /// <returns>The texture used for display, or null if no texture is available.</returns>
        public Texture2D GetDisplayTexture(QuadDrawCondition condition){
            return textureMode switch{
                VoxelTexMode.AllUnique => allUnique.First().Value,
                _ => FindTex(null, condition)
            };
        }

        private void OnValidate(){
            lightEmission = Vector3Int.Max(Vector3Int.zero,
                Vector3Int.Min(lightEmission, Vector3Int.one * VoxelLightLimits.MaxLevel));
        }

        public FixedString32Bytes GetFullName(){
            FixedString32Bytes fullName;
            try{
                fullName = new FixedString32Bytes(package.packagePrefix + ":" + name);
            }
            catch (ArgumentException e){
                VoxelEngineLogger.Error<Voxel>(
                    $"Name '{name}' exceeds the maximum length of {FixedString32Bytes.UTF8MaxLengthInBytes} bytes. Registration skipped.({e.Message})");
                return "Error";
            }

            return fullName;
        }

        /// <summary>
        ///     Resolves the texture for a quad and draw condition based on the current texture mode.
        /// </summary>
        /// <param name="quad">The quad to resolve, or null when the mode does not require a quad.</param>
        /// <param name="condition">The draw condition.</param>
        /// <returns>The resolved texture, or null if no texture is assigned.</returns>
        private Texture2D FindTex(VoxelQuad quad, QuadDrawCondition condition){
            return textureMode switch{
                VoxelTexMode.AllSame => all,
                VoxelTexMode.TopBottomSides => condition switch{
                    QuadDrawCondition.Up => top,
                    QuadDrawCondition.Down => bottom,
                    _ => side
                },
                VoxelTexMode.SixSidesUnique => condition switch{
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

        /// <summary>
        ///     Render definition for a voxel, including texture slots for all faces, mesh layer,
        ///     collision flag and additional rendering information.
        /// </summary>
        [BurstCompile]
        public struct VoxelDef{
            public ushort Id;

            /// <summary>Mesh layer (solid, transparent or air).</summary>
            public MeshLayer MeshLayer;

            /// <summary>Whether all faces should always be rendered, even when hidden by neighbors.</summary>
            public bool AlwaysRenderAllFaces;

            /// <summary>Slot of the voxel's volume fog in the registry's fog buffer; 0 = none (plain alpha blend).</summary>
            public byte VolumeFog;

            /// <summary>Scale of the texture alpha, 0-255 for 0-1.</summary>
            public byte SurfaceAlpha;

            /// <summary> Emissive glow level for the voxel (0-255, where 255 is full brightness).</summary>
            public byte Glow;

            /// <summary>Whether this voxel participates in physics collision.</summary>
            public bool Collision;

            /// <summary>Extra light loss per step into this voxel (0–15, 15 = opaque).</summary>
            public byte LightAttenuation;

            /// <summary>Emitted block light, packed like the voxel light with sky 0 (see VoxelLight).</summary>
            public ushort LightEmission;

            /// <summary>
            ///     The shape closes all six faces of the cube, so a neighbour of the same or a lower mesh layer skips its
            ///     face towards it. Off for partial shapes such as slabs and plants.
            /// </summary>
            public bool HidesNeighbourFaces;

            public uint2 Always;
            public uint2 Right;
            public uint2 Left;
            public uint2 Up;
            public uint2 Down;
            public uint2 Front;
            public uint2 Back;

            public bool IsAir => MeshLayer == MeshLayer.Air;
            public bool IsTransparent => MeshLayer == MeshLayer.Transparent;
            public bool IsSolid => MeshLayer == MeshLayer.Solid;
            public bool IsFoliage => MeshLayer == MeshLayer.Foliage;
        }
    }

    /// <summary>Value range of the voxel light properties.</summary>
    public static class VoxelLightLimits{
        public const int MaxLevel = 15;
    }

    /// <summary>
    ///     Optional per-voxel post-processing parameters such as color grading and fog.
    /// </summary>
    [Serializable]
    public class VoxelPostProcessData{
        /// <summary>
        ///     Color tint (lift/gamma/gain) applied during post processing.
        /// </summary>
        public Color postProcessColor;

        /// <summary>
        ///     Contrast adjustment factor.
        /// </summary>
        public float contrast;

        /// <summary>
        ///     Saturation adjustment factor.
        /// </summary>
        public float saturation;

        /// <summary>
        ///     Enables additional fog for this voxel type.
        /// </summary>
        public bool enableFog;

        /// <summary>
        ///     Fog color when <see cref="enableFog" /> is true; lit by the sky unless the voxel emits light.
        /// </summary>
        public Color fogColor = Color.gray;

        /// <summary>
        ///     Fog density value used when <see cref="enableFog" /> is true.
        /// </summary>
        public float fogDensity = .01f;

        /// <summary>
        ///     Fraction of the fog's light lost per block of depth below the liquid's surface (exponential), so deep
        ///     water gets darker; 0 keeps the fog equally bright at every depth.
        /// </summary>
        [Min(0f)] public float depthDarkening;
    }
}
