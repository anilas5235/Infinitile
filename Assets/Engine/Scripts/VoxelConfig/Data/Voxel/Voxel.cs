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

        /// <summary>The kind of block, for the inventory's category chips and its sort (set on every voxel).</summary>
        [Tooltip("The kind of block: the inventory's category chips and its sort by kind")]
        public VoxelCategory category;

        /// <summary>
        ///     If true, all faces are always rendered even when hidden by neighbors.
        /// </summary>
        public bool alwaysRenderAllFaces;

        /// <summary>
        ///     Draws the voxel with a hard alpha edge (each pixel either drawn or not) in the cut-out pass with the
        ///     opaque blocks, instead of blending it. Only for the transparent layer; two such full cubes share one face.
        /// </summary>
        [Tooltip("Hard alpha edge, drawn with the opaque blocks (leaves); transparent layer only")]
        public bool alphaCutout;

        /// <summary>
        ///     Depth (blocks of scene behind the surface) over which a liquid's alpha rises from the surface alpha to
        ///     <see cref="VoxelLiquidSurface.depthAlpha" />, so deep liquid reads darker; zero or negative disables it.
        /// </summary>
        [Tooltip("Blocks behind the surface over which the alpha rises to the liquid's depth alpha; <= 0 disables it")]
        public float depthFadeDistance = -1f;

        /// <summary>Scale of the texture alpha (see-through water); 1 keeps the texture alpha.</summary>
        [Tooltip("Scale of the texture alpha at the surface")]
        [Range(0f, 1f)] public float surfaceAlpha = 1f;

        /// <summary>
        ///     Look of a liquid surface (tint, biome tint, scrolling pattern, depth alpha); only read for liquids of
        ///     the transparent layer.
        /// </summary>
        public VoxelLiquidSurface liquidSurface = new();

        /// <summary>
        ///     Colours the voxel by the biome's grass or plant colour (DEC-122): its textures are grey and the shader
        ///     multiplies the blended biome colour onto them. The top face is tinted whole, the bottom face of a
        ///     top/bottom/sides or six-sided voxel not at all, and every other face through <see cref="biomeTintMask" />
        ///     when set, else whole.
        /// </summary>
        [Tooltip("Colour by the biome's grass or plant colour; the textures are grey")]
        public VoxelBiomeTint biomeTint;

        /// <summary>
        ///     The tinted part of the side faces (grass band over dirt): the alpha says where the side is tinted, the
        ///     colour is the side's grey there, so a block icon draws it tinted over the side. Null tints the side
        ///     whole.
        /// </summary>
        [Tooltip("Side overlay: alpha = tinted part of the side faces (the grass band); empty tints the whole side")]
        public Texture2D biomeTintMask;

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

        /// <summary>
        ///     Colour an icon multiplies onto the display textures: for a liquid its surface tint, with biome tint onto
        ///     the default water colour (the texture is only a grey pattern); white for every other voxel.
        /// </summary>
        public Color DisplayTint{
            get{
                if (biomeTint != VoxelBiomeTint.None) return BiomeTintFactor(biomeTint);
                if (category != VoxelCategory.Liquids) return Color.white;
                VoxelLiquidSurface surface = liquidSurface ?? new VoxelLiquidSurface();
                Color tint = surface.biomeTint ? Generation.Biome.DefaultWaterColor * surface.tint : surface.tint;
                tint.a = 1f;
                return tint;
            }
        }

        /// <summary>
        ///     Colour an icon multiplies onto the side texture: white when <see cref="biomeTintMask" /> carries the
        ///     tinted part (drawn over the side with <see cref="DisplayTint" />), else <see cref="DisplayTint" />.
        /// </summary>
        public Color DisplaySideTint => biomeTint != VoxelBiomeTint.None && biomeTintMask ? Color.white : DisplayTint;

        /// <summary>
        ///     The factor a grey tint tile is multiplied by to show the default biome colour of <paramref name="tint" />
        ///     (as the shader does: linear colour / <see cref="BiomeTintGreyMean" />), gamma-encoded for UI tints.
        /// </summary>
        public static Color BiomeTintFactor(VoxelBiomeTint tint){
            Color color = tint switch{
                VoxelBiomeTint.Foliage => Generation.Biome.DefaultFoliageColor,
                VoxelBiomeTint.Litter => Generation.Biome.DefaultLitterColor,
                _ => Generation.Biome.DefaultGrassColor
            };
            Color linear = color.linear / BiomeTintGreyMean;
            linear.a = 1f;
            return linear.gamma;
        }

        /// <summary>
        ///     Linear-light mean of the tinted pixels of every grey tint tile (Docs/Tools/make_tint_tiles.py), so the
        ///     biome colour is the mean colour the tinted part shows; TINT_GREY_MEAN in VoxelShaderCommon.hlsl.
        /// </summary>
        public const float BiomeTintGreyMean = 0.70f;

        /// <summary>
        ///     Whether the faces of <paramref name="condition" /> take the biome tint, and whether through
        ///     <see cref="biomeTintMask" /> (see <see cref="biomeTint" />).
        /// </summary>
        public (bool tinted, bool masked) BiomeTintOf(QuadDrawCondition condition){
            if (biomeTint == VoxelBiomeTint.None) return (false, false);
            bool perFace = textureMode is VoxelTexMode.TopBottomSides or VoxelTexMode.SixSidesUnique;
            if (perFace && condition == QuadDrawCondition.Up) return (true, false);
            if (perFace && condition == QuadDrawCondition.Down) return (false, false);
            return (true, biomeTintMask);
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

            /// <summary>Drawn with a hard alpha edge in the cut-out pass (leaves); transparent layer only.</summary>
            public bool AlphaCutout;

            /// <summary>A liquid (category Liquids): the surface of its top layer sits a little below the block's top.</summary>
            public bool Liquid;

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

    /// <summary>
    ///     Which biome colour a voxel takes (DEC-122); the value is the layer of the biome tint map
    ///     (<c>BiomeTintMap</c>, layer 0 is the water colour).
    /// </summary>
    public enum VoxelBiomeTint : byte{
        /// <summary>The texture's own colour.</summary>
        None = 0,

        /// <summary>The biome's grass colour (grass block tops and side bands).</summary>
        Grass = 1,

        /// <summary>The biome's plant colour (grass tufts, tall grass, ferns, reeds).</summary>
        Foliage = 2,

        /// <summary>The biome's leaf litter colour (DEC-206).</summary>
        Litter = 3
    }

    /// <summary>Value range of the voxel light properties.</summary>
    public static class VoxelLightLimits{
        public const int MaxLevel = 15;
    }

    /// <summary>
    ///     Optional per-voxel underwater view: the coloured fog drawn while the camera is inside the voxel.
    /// </summary>
    [Serializable]
    public class VoxelPostProcessData{
        /// <summary>
        ///     Enables the underwater fog for this voxel type.
        /// </summary>
        public bool enableFog;

        /// <summary>
        ///     Fog color when <see cref="enableFog" /> is true; lit by the sky unless the voxel emits light. A
        ///     biome-tinted liquid (<see cref="VoxelLiquidSurface.biomeTint" />) takes the biome's water fog colour instead.
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

        /// <summary>
        ///     How much the scene seen through the fog takes on the fog's hue and depth darkening (0 = untouched, 1 =
        ///     fully), so near blocks look blue under water as well, not only the far ones.
        /// </summary>
        [Range(0f, 1f)] public float sceneTint;
    }

    /// <summary>
    ///     Surface look of a liquid: a tinted, alpha-blended surface with a world-space pattern that scrolls in two
    ///     directions (fragment shader only), optionally tinted by the biome's water colour.
    /// </summary>
    [Serializable]
    public class VoxelLiquidSurface{
        /// <summary>Colour multiplied onto the texture; with <see cref="biomeTint" /> it multiplies the biome colour.</summary>
        [Tooltip("Colour multiplied onto the surface (and onto the biome colour with biome tint)")]
        public Color tint = Color.white;

        /// <summary>Colours the surface by the biome's water colour (soft ~4-block border between biomes).</summary>
        [Tooltip("Colour by the biome's water colour; the underwater fog takes the biome's water fog colour")]
        public bool biomeTint;

        /// <summary>Scroll speed of the surface pattern in blocks per second; 0 = still.</summary>
        [Tooltip("Pattern scroll speed in blocks per second, 0 = still")]
        [Min(0f)] public float flowSpeed = 0.25f;

        /// <summary>How much of the texture's light/dark pattern shows, 0 = flat colour, 1 = full pattern.</summary>
        [Tooltip("Strength of the texture's light/dark pattern, 0 = flat colour")]
        [Range(0f, 1f)] public float patternStrength = 1f;

        /// <summary>
        ///     Alpha the surface reaches over <see cref="Voxel.depthFadeDistance" /> blocks of scene behind it
        ///     (deep water reads darker); never below the surface alpha.
        /// </summary>
        [Tooltip("Alpha reached over depthFadeDistance blocks of scene behind the surface")]
        [Range(0f, 1f)] public float depthAlpha = 0.9f;
    }
}
