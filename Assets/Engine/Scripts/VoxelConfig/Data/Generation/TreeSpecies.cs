using System;
using System.Collections.Generic;
using UnityEngine;

namespace Engine.Scripts.VoxelConfig.Data.Generation{
    /// <summary>
    ///     A parametric tree kind: one or more weighted variants, each a trunk, a crown, branches and crown noise
    ///     with value ranges. A flora rule names it as its <c>SpeciesTree</c>; every value of a tree is rolled from
    ///     its anchor column, so a tree comes out the same in every chunk that holds part of it. Shapes are built
    ///     by <c>TreeShape</c>; offsets are relative to the first voxel above the anchor's top block (y 0 sits on
    ///     the ground).
    /// </summary>
    [CreateAssetMenu(fileName = "TreeSpecies", menuName = "Infinitile/Generation/TreeSpecies")]
    public class TreeSpecies : ScriptableObject{
        [Tooltip("Variants of this species; the anchor column picks one by weight.")]
        public List<TreeVariant> Variants = new();
    }

    /// <summary>Crown shape of a tree variant.</summary>
    public enum CrownShape : byte{
        /// <summary>No crown (bare trunks, dead trees).</summary>
        None,

        /// <summary>A sphere of the rolled radius; with a height range, an ellipsoid of that height.</summary>
        Round,

        /// <summary>An ellipsoid of the rolled radius and height (a sphere without a height range).</summary>
        Ellipsoid,

        /// <summary>Discs from the radius at the bottom to the top radius at the top layer.</summary>
        Cone,

        /// <summary>A cone whose every second layer, counted from the top, is one block narrower.</summary>
        Tiered
    }

    /// <summary>How a branch leaves the trunk.</summary>
    public enum BranchStyle : byte{
        /// <summary>Up and out: one block out and one up per step, in one of 8 directions (oaks, dead trees).</summary>
        Diagonal,

        /// <summary>One block out in one of 4 directions, then straight up (cactus arms); without a crown the tip stays below the trunk top.</summary>
        Arm
    }

    /// <summary>An inclusive integer range; a value is rolled uniformly from [Min, Max].</summary>
    [Serializable]
    public struct IntRange{
        public int Min;
        public int Max;

        public IntRange(int min, int max){
            Min = min;
            Max = max;
        }
    }

    /// <summary>One variant of a <see cref="TreeSpecies" />. All ranges are inclusive.</summary>
    [Serializable]
    public class TreeVariant{
        [Tooltip("Name shown in the species gallery.")]
        public string Name = "Normal";

        [Tooltip("Relative weight of this variant in the species' variant pick.")]
        [Min(0f)]
        public float Weight = 1f;

        [Header("Trunk")] [Tooltip("Voxel of the trunk and the branches.")]
        public Voxel.Voxel TrunkVoxel;

        [Tooltip("Trunk height in blocks; 0 for no trunk (bushes, boulders). The trunk is extended when the crown sits above it.")]
        public IntRange TrunkHeight = new(4, 6);

        [Tooltip("Trunk width, 1 or 2 blocks. A width-2 trunk needs ground that is flat within 1 block, otherwise it is built 1 wide.")]
        [Range(1, 2)]
        public int TrunkWidth = 1;

        [Tooltip("Largest lean in blocks per block of trunk height. A leaning trunk needs ground that is flat within 1 block under it, otherwise it stands straight.")]
        [Range(0f, .25f)]
        public float MaxLean;

        [Tooltip("A lying trunk (fallen logs): Trunk Height is the length of a line along x or z from the anchor; crown, branches, width and lean are not used. It needs ground of the anchor's height under its whole length, otherwise it is 2 long, otherwise left out.")]
        public bool Lying;

        [Header("Crown")] public CrownShape CrownShape = CrownShape.Round;

        [Tooltip("Main leaf voxel of the crown and the branch clusters.")]
        public Voxel.Voxel LeafVoxel;

        [Tooltip("Alternative leaf voxel, used with Alt Leaf Chance per voxel.")]
        public Voxel.Voxel AltLeafVoxel;

        [Tooltip("Crown radius in blocks (Cone and Tiered: at the bottom layer).")]
        public IntRange CrownRadius = new(2, 3);

        [Tooltip("Cone and Tiered: radius at the top layer.")]
        public IntRange CrownTopRadius = new(0, 1);

        [Tooltip("Crown height in layers. 0–0 derives it: 2r + 1 for Round and Ellipsoid; Cone and Tiered from Crown Min Base up to one layer above the trunk.")]
        public IntRange CrownHeight = new(0, 0);

        [Tooltip("Lowest crown layer in blocks above the trunk's top voxel (0 = level with it, negative = lower). The crown always ends at least one layer above the trunk. Not used by Cone and Tiered with a derived height.")]
        public int CrownBase;

        [Tooltip("Lowest crown layer in blocks above the ground (0 = the first voxel above the top block); layers below are cut.")]
        public int CrownMinBase;

        [Header("Branches")] [Tooltip("Chance that a tree gets branches at all; Branch Count is rolled only then.")] [Range(0f, 1f)]
        public float BranchChance = 1f;

        public IntRange BranchCount = new(0, 0);
        public BranchStyle BranchStyle = BranchStyle.Diagonal;

        [Tooltip("Diagonal: steps up and out. Arm: blocks up after the first block out, cut on a crownless trunk so the tip stays below the trunk top.")]
        public IntRange BranchLength = new(1, 2);

        [Tooltip("Lowest start of a branch as a fraction of the trunk height. With a crown, a branch starts at least so high that its tip reaches the crown.")]
        [Range(0f, 1f)]
        public float BranchBandMin = .5f;

        [Tooltip("Highest start of a branch as a fraction of the trunk height.")]
        [Range(0f, 1f)]
        public float BranchBandMax = .9f;

        [Tooltip("Radius of the leaf cluster at a branch tip; 0–0 for none.")]
        public IntRange BranchLeafRadius = new(0, 0);

        [Header("Crown noise")] [Tooltip("Chance that an outer crown voxel is left out.")] [Range(0f, 1f)]
        public float HoleChance = .2f;

        [Tooltip("Chance that an outer crown voxel gets one more voxel outward.")]
        [Range(0f, 1f)]
        public float BumpChance = .1f;

        [Tooltip("Chance that a voxel on the crown's lower edge gets one more voxel below it.")]
        [Range(0f, 1f)]
        public float HangChance = .05f;

        [Tooltip("Chance per crown voxel of the alternative leaf voxel.")]
        [Range(0f, 1f)]
        public float AltLeafChance;

        [Header("Placement")] [Tooltip("Blocks the whole shape is sunk into the ground (boulders); sunk voxels never replace terrain.")]
        public IntRange Sink = new(0, 0);

        [Tooltip("No other tree or structure anchor lies within this many blocks of this variant's anchor.")] [Min(0)]
        public int SpacingRadius = 3;
    }
}
