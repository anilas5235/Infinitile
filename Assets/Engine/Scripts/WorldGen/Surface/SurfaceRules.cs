using System;
using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Mathematics;

namespace Engine.Scripts.WorldGen.Surface{
    /// <summary>Leaf tests of the compiled decision graph (<see cref="RuleNode.Kind" />).</summary>
    public static class RuleNodeKind{
        /// <summary>Column flag A (<see cref="ColumnFlagDef" />), computed once per column.</summary>
        public const byte Flag = 0;

        /// <summary>y ≥ column int A.</summary>
        public const byte YAtLeast = 1;

        /// <summary>Depth below the run's top ≤ column int A.</summary>
        public const byte DepthAbove = 2;

        /// <summary>Height above the run's bottom ≤ column int A.</summary>
        public const byte DepthBelow = 3;

        /// <summary>A ≤ water depth above the run ≤ B.</summary>
        public const byte WaterDepth = 4;

        /// <summary>The run is the column's top run.</summary>
        public const byte SkyOpen = 5;

        /// <summary>A ≤ run top − (water level − 1) &lt; column int B.</summary>
        public const byte TopAboveWater = 6;

        /// <summary>Kinds that depend on the run rather than on y or the column: not allowed in the interior plan.</summary>
        public static bool IsRunLevel(byte kind){
            return kind == WaterDepth || kind == SkyOpen || kind == TopAboveWater;
        }
    }

    /// <summary>
    ///     One test of the decision graph. A target ≥ 0 is a node, a negative one the action −target − 1 (§7.5: the
    ///     rule lists of a biome compile into one graph of leaf tests, every condition evaluated at most once).
    /// </summary>
    public struct RuleNode{
        public byte Kind;
        public int A, B;
        public int OnTrue, OnFalse;
    }

    public static class RuleActionKind{
        /// <summary>No rule matched: the generator's stone (<c>DensityStoneVoxel</c>).</summary>
        public const byte Keep = 0;

        public const byte Block = 1;

        /// <summary>Strata bands over <see cref="SurfaceAction.Block" />.</summary>
        public const byte Bands = 2;
    }

    public struct SurfaceAction{
        public byte Kind;

        /// <summary>The block (bands: the block between the bands).</summary>
        public ushort Block;

        /// <summary>Block actions: the voxel placed on the open voxel above a run's top (a snow layer), 0 for none.</summary>
        public ushort Cover;

        public int Period, Thickness;

        /// <summary>Column int of the band shift, −1 for none.</summary>
        public int Shift;

        public ushort P0, P1, P2, P3;
        public int PaletteCount;

        public readonly ushort PaletteBlock(int band){
            if (PaletteCount <= 1) return P0;
            int i = band % PaletteCount;
            if (i < 0) i += PaletteCount;
            return i switch{
                0 => P0,
                1 => P1,
                2 => P2,
                _ => P3
            };
        }
    }

    public static class ColumnFlagKind{
        /// <summary>The heightmap rises at least A across the column (x or z).</summary>
        public const byte Steep = 0;

        /// <summary>A wet probe within A blocks.</summary>
        public const byte NearWater = 1;

        /// <summary>A ≤ output[Output] &lt; B.</summary>
        public const byte Noise = 2;

        /// <summary>A ≤ flora density factor [Output] &lt; B.</summary>
        public const byte Density = 3;

        /// <summary>A direct neighbour column inside the chunk lies at least A higher (the foot of a step).</summary>
        public const byte StepFoot = 4;
    }

    public struct ColumnFlagDef{
        public byte Kind;
        public float A, B;
        public int Output;
    }

    public static class ColumnIntKind{
        public const byte Const = 0;

        /// <summary>A + floor(output[Output]).</summary>
        public const byte DepthNoise = 1;

        /// <summary>floor(A + output[Output]) (no output: floor(A)).</summary>
        public const byte YAbove = 2;

        /// <summary>
        ///     floor(preliminary_surface − Δdown − A) when B is 1 (A = the biome's R_above, so the layers under a surface at
        ///     its lowest possible y stay whole), else floor(preliminary_surface − A).
        /// </summary>
        public const byte AbovePreliminary = 3;

        /// <summary>The lowest y where T − L · max(0, y − lapseFrom) + output[Output] &lt; A (256 for none).</summary>
        public const byte Cold = 4;

        /// <summary>ceil(max(treeLineFloor, lapseFrom + (T − T_tree) / L) + output[Output]).</summary>
        public const byte TreeLine = 5;

        /// <summary>round(A · output[Output]): v1's beach band.</summary>
        public const byte BeachBound = 6;
    }

    public struct ColumnIntDef{
        public byte Kind;
        public float A, B;
        public int Output;
    }

    /// <summary>A 2D graph output the rules read: from the column outputs (per-column) or interpolated from the lattice.</summary>
    public struct RuleOutput{
        public int Column, Lattice;
    }

    /// <summary>A biome's compiled rules: the roots of its full and interior graphs and its interior plan.</summary>
    public struct BiomeRules{
        /// <summary>Full graph root (a node, or −action − 1); <see cref="HasRules" /> false: the biome keeps the fallback fill.</summary>
        public int Root;

        public int Interior;

        /// <summary>Largest stone_depth_above / stone_depth_below bound of any rule, −1 for none (§7.6).</summary>
        public int RAbove, RBelow;

        /// <summary>Column ints of the interior graph's y tests (at most <see cref="SurfaceRules.MaxInteriorThresholds" />).</summary>
        public int ThresholdStart, ThresholdCount;

        public byte HasRules;
    }

    /// <summary>
    ///     The compiled surface rules (WorldGen v2 §7, §8) as the chunk job reads them: one decision graph per biome
    ///     over shared nodes, the actions, the per-column values the leaves read, and the climate of snow, ice and the
    ///     tree line. Built by <see cref="SurfaceRuleCompiler" />; owned by the <c>WorldGenConfig</c> it is attached to.
    /// </summary>
    public struct SurfaceRules : IDisposable{
        public const int MaxColumnFlags = 64, MaxColumnInts = 64, MaxOutputs = 32, MaxInteriorThresholds = 3;

        /// <summary>Largest stone depth bound (§7.6: a cavern's cover is at least this thick, so rules never see through it).</summary>
        public const int MaxDepthBound = 8;

        [ReadOnly] public NativeArray<BiomeRules> Biomes;
        [ReadOnly] public NativeArray<RuleNode> Nodes;
        [ReadOnly] public NativeArray<SurfaceAction> Actions;
        [ReadOnly] public NativeArray<ColumnFlagDef> Flags;
        [ReadOnly] public NativeArray<ColumnIntDef> Ints;
        [ReadOnly] public NativeArray<RuleOutput> Outputs;
        [ReadOnly] public NativeArray<int> Thresholds;

        /// <summary>Lattice slots of T and of preliminary_surface (−1 when the graph lacks T: it reads 0).</summary>
        public int TemperatureSlot, PreliminarySurfaceSlot;

        public float Lapse, LapseFrom, SnowTemperature, IceTemperature, TreeTemperature, TreeLineFloor;

        /// <summary>Rule outputs of the ice, snow and tree line wobbles (−1 none).</summary>
        public int IceWobble, SnowWobble, TreeLineWobble;

        /// <summary>Frozen rivers (DEC-207): the rule output of the river weight (−1: rivers freeze like the sea), its minimum and T limit.</summary>
        public int RiverIce;

        public float RiverIceMin, RiverIceTemperature;

        /// <summary>1: ice forms where it is cold enough (some biome may snow).</summary>
        public byte Ice;

        /// <summary>For tests: evaluate the full graph on every voxel instead of the interior plan.</summary>
        [MarshalAs(UnmanagedType.U1)] public bool FullEvaluation;

        public readonly bool IsCreated => Biomes.IsCreated;

        /// <summary>Whether rules are attached: the fill runs them only then (an <see cref="Empty" /> set has none).</summary>
        public readonly bool HasRules => Biomes.IsCreated && Biomes.Length > 0;

        /// <summary>
        ///     A set without rules: every container is created (zero length), so the jobs that carry a config without
        ///     rules see valid containers; <see cref="HasRules" /> is false.
        /// </summary>
        public static SurfaceRules Empty(Allocator allocator){
            return new SurfaceRules{
                Biomes = new NativeArray<BiomeRules>(0, allocator), Nodes = new NativeArray<RuleNode>(0, allocator), Actions = new NativeArray<SurfaceAction>(0, allocator),
                Flags = new NativeArray<ColumnFlagDef>(0, allocator), Ints = new NativeArray<ColumnIntDef>(0, allocator), Outputs = new NativeArray<RuleOutput>(0, allocator),
                Thresholds = new NativeArray<int>(0, allocator), TemperatureSlot = -1, PreliminarySurfaceSlot = -1, IceWobble = -1, SnowWobble = -1, TreeLineWobble = -1, RiverIce = -1
            };
        }

        public readonly bool HasRulesFor(int biome){
            return Biomes.IsCreated && biome >= 0 && biome < Biomes.Length && Biomes[biome].HasRules != 0;
        }

        /// <summary>
        ///     Whether y is cold: T − L · max(0, y − lapseFrom) + wobble &lt; <paramref name="limit" />. The one formula
        ///     of every temperature test; thresholds are searched with it, so they agree with it exactly.
        /// </summary>
        public readonly bool ColdAt(int y, float temperature, float wobble, float limit){
            return temperature - Lapse * math.max(0f, y - LapseFrom) + wobble < limit;
        }

        /// <summary>The lowest y in 0 … 256 that is cold (<see cref="ColdAt" />); 256 when none is.</summary>
        public readonly int ColdThreshold(float temperature, float wobble, float limit){
            if (ColdAt(0, temperature, wobble, limit)) return 0;
            if (!(Lapse > 0f)) return 256;
            float guess = LapseFrom + (temperature + wobble - limit) / Lapse;
            int y = (int)math.clamp(math.floor(guess) + 1f, 0f, 256f);
            // The formula is exact up to rounding; step to the first cold y with the test itself.
            while (y > 0 && ColdAt(y - 1, temperature, wobble, limit)) y--;
            while (y < 256 && !ColdAt(y, temperature, wobble, limit)) y++;
            return y;
        }

        /// <summary>
        ///     The tree line: voxels at y ≥ this lie above it (§8.5). The floor applies after the wobble, so the line
        ///     never lies below it (W2-CLM-04, DEC-139).
        /// </summary>
        public readonly int TreeLine(float temperature, float wobble){
            float y = Lapse > 0f ? LapseFrom + (temperature - TreeTemperature) / Lapse : TreeLineFloor;
            return (int)math.ceil(math.max(TreeLineFloor, y + wobble));
        }

        public void Dispose(){
            if (Biomes.IsCreated) Biomes.Dispose();
            if (Nodes.IsCreated) Nodes.Dispose();
            if (Actions.IsCreated) Actions.Dispose();
            if (Flags.IsCreated) Flags.Dispose();
            if (Ints.IsCreated) Ints.Dispose();
            if (Outputs.IsCreated) Outputs.Dispose();
            if (Thresholds.IsCreated) Thresholds.Dispose();
        }
    }
}
