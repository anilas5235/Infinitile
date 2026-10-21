using System;
using Engine.Scripts.VoxelConfig.Data.Generation;
using Unity.Mathematics;
using UnityEngine;

namespace Engine.Scripts.WorldGen.Flora{
    /// <summary>
    ///     One flora rule of a biome: a species (single voxel, a stack of one voxel, or a multi-block
    ///     structure), its probability per column and placement conditions. The Burst mirror is
    ///     <see cref="FloraRuleDef" />; <see cref="FloraPlacement" /> evaluates it.
    /// </summary>
    [Serializable]
    public class FloraRule{
        [Tooltip("Single-voxel species (grass, flowers, mushrooms, rocks, cactus). Leave empty when SpeciesStructure is set.")]
        public VoxelConfig.Data.Voxel.Voxel SpeciesVoxel;

        [Tooltip("Multi-block species (tree). Leave empty when SpeciesVoxel is set.")]
        public VoxelStructure SpeciesStructure;

        [Tooltip("Probability per column that this species is rolled, in [0,1]. The rules of one biome are cumulative.")]
        [Range(0f, 1f)]
        public float Probability = 0.05f;

        [Tooltip("When true, the plant is suppressed if the column's surface source biome differs from its flora source biome.")]
        public bool RequiresSurfaceFromSameBiome;

        [Tooltip("Minimum height of the top block's upper face above the water surface, in blocks (default 1).")]
        public int MinHeightAboveWater = 1;

        [Tooltip("SpeciesVoxel only: the voxel is stacked this many blocks high at least (e.g. a cactus 1–3 high).")]
        [Min(1)]
        public int MinStackHeight = 1;

        [Tooltip("SpeciesVoxel only: the voxel is stacked at most this many blocks high. Above 1 the species is multi-block (anchor spacing applies).")]
        [Min(1)]
        public int MaxStackHeight = 1;

        /// <summary>True when the rule names exactly one species.</summary>
        public bool IsValid => (SpeciesVoxel != null) ^ (SpeciesStructure != null);

        /// <summary>Structures and stacks higher than one block take part in the anchor spacing.</summary>
        public bool IsMultiBlock => SpeciesStructure != null || math.max(MinStackHeight, MaxStackHeight) > 1;
    }

    /// <summary>
    ///     Burst mirror of a <see cref="FloraRule" />, stored flattened in <c>WorldGenConfig.FloraRules</c> and
    ///     addressed per biome by <c>BiomeDef.floraRuleStart/floraRuleCount</c>. The species roll picks the rule with
    ///     <see cref="CumulativeStart" /> ≤ u &lt; <see cref="CumulativeEnd" />; both bounds are computed once
    ///     by the config builder, so the roll and the multi-block pre-filter compare against the same floats.
    /// </summary>
    public struct FloraRuleDef{
        /// <summary>Species id (1-based index into the config's species table; 0 = none).</summary>
        public ushort Species;

        public float CumulativeStart;
        public float CumulativeEnd;

        /// <summary>1 when the plant needs surface source = flora source.</summary>
        public byte RequiresSurfaceFromSameBiome;

        /// <summary>1 for structures and stacks above one block: anchor spacing applies.</summary>
        public byte MultiBlock;

        public int MinHeightAboveWater;

        /// <summary>Index into <c>WorldGenConfig.FloraStructures</c>, or -1 for a (stacked) voxel species.</summary>
        public int Structure;

        /// <summary>Voxel id of a voxel species; 0 for structures (and without a registry).</summary>
        public ushort Voxel;

        /// <summary>Replace class of <see cref="Voxel" /> (<see cref="FloraVoxelClass" />).</summary>
        public byte VoxelClass;

        public int MinStackHeight;
        public int MaxStackHeight;
    }

    /// <summary>A structure species: a range of <c>WorldGenConfig.FloraVoxels</c> relative to its anchor.</summary>
    public struct FloraStructureDef{
        public int Start;
        public int Count;

        /// <summary>Largest |x| or |z| of any voxel offset: how far the structure reaches from its anchor column.</summary>
        public int Reach;
    }

    /// <summary>
    ///     One voxel of a flattened structure. <see cref="Offset" /> is relative to the first voxel above the anchor's
    ///     top block (the anchor's surface y + 1).
    /// </summary>
    public struct FloraVoxel{
        public int3 Offset;
        public ushort Voxel;

        /// <summary>Replace class (<see cref="FloraVoxelClass" />).</summary>
        public byte Class;
    }

    /// <summary>
    ///     Which voxels flora may overwrite: a flora voxel replaces an existing voxel only if the existing
    ///     voxel's class is lower. Air &lt; foliage (grass, flowers) &lt; leaves (transparent with collision) &lt; everything
    ///     else (terrain, water, logs, cactus). So leaves never cut into terrain, logs push leaves aside, and nothing
    ///     floods water. Overlapping trees are stamped in world order (x, then z), which makes the result the same in
    ///     every chunk that contains the voxel.
    /// </summary>
    public static class FloraVoxelClass{
        public const byte Air = 0;
        public const byte Foliage = 1;
        public const byte Leaves = 2;
        public const byte Solid = 3;

        /// <summary>Class of a voxel from its mesh layer and collision flag.</summary>
        public static byte Of(VoxelConfig.Data.Mesh.MeshLayer layer, bool collision){
            return layer switch{
                VoxelConfig.Data.Mesh.MeshLayer.Air => Air,
                VoxelConfig.Data.Mesh.MeshLayer.Foliage => Foliage,
                VoxelConfig.Data.Mesh.MeshLayer.Transparent when collision => Leaves,
                _ => Solid
            };
        }
    }
}
