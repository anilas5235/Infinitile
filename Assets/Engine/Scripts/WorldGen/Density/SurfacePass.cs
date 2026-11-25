using Engine.Scripts.WorldGen.Flora;
using Engine.Scripts.WorldGen.Graph;
using Engine.Scripts.WorldGen.Settings;
using Engine.Scripts.WorldGen.Surface;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;

namespace Engine.Scripts.WorldGen.Density{
    /// <summary>Work of the surface pass (P5), summed over chunks.</summary>
    public struct SurfaceWorkCounters{
        /// <summary>Columns the rules ran on, and columns that kept the fallback fill (no rules for their biome).</summary>
        public long Columns, FallbackColumns;

        /// <summary>Solid voxels decided by the full graph, and by the interior plan.</summary>
        public long FullVoxels, InteriorVoxels;

        /// <summary>Decision nodes visited by the full graph and the interior plan.</summary>
        public long Nodes;

        /// <summary>Interior ranges decided (one graph walk each).</summary>
        public long InteriorRanges;

        /// <summary>Water surface voxels that froze.</summary>
        public long IceVoxels;

        public void Add(in SurfaceWorkCounters o){
            Columns += o.Columns;
            FallbackColumns += o.FallbackColumns;
            FullVoxels += o.FullVoxels;
            InteriorVoxels += o.InteriorVoxels;
            Nodes += o.Nodes;
            InteriorRanges += o.InteriorRanges;
            IceVoxels += o.IceVoxels;
        }

        public override string ToString(){
            return $"surface: columns {Columns} (fallback {FallbackColumns}), voxels full {FullVoxels} / interior {InteriorVoxels}, nodes {Nodes}, " +
                   $"interior ranges {InteriorRanges}, ice {IceVoxels}";
        }
    }

    /// <summary>
    ///     P5 (WorldGen v2 §7, §8): the fluids, the ice and the surface rules of every column, from the terrain flags
    ///     and the surface-source biome (AD-25). Open voxels below the water level are water; the water surface
    ///     (y = water level − 1) of a column open to the sky freezes where T + ice wobble &lt; T_ice and the surface
    ///     biome does not refuse snow. Each solid run is walked from its top: voxels within the biome's stone depth
    ///     bounds of the run's top or bottom walk the full decision graph, the rest take the interior plan's action
    ///     for their y range (§7.6). Both give the same block (W2-SRF tests compare them on every voxel).
    /// </summary>
    public static unsafe class SurfacePass{
        private const int N = DensityLayout.ChunkSize;
        private const int H = DensityLayout.Height;

        /// <summary>Per-column scratch: the lazily computed column tests, values and outputs.</summary>
        private struct Column{
            public int X, Z, WorldX, WorldZ, Biome;
            public float Temperature, Preliminary, DeltaDown;
            public byte* FlagState; // 0 unknown, 1 false, 2 true
            public int* Ints;
            public byte* IntKnown;
            public float* Outputs;
            public byte* OutputKnown;
            public int* RangeAction; // per interior range: −1 unknown, else the action
            public int* RangeLow;
            public int RangeCount;
        }

        /// <summary>One voxel's run context for the leaf tests.</summary>
        private struct Voxel{
            public int Y, DepthAbove, DepthBelow, WaterDepth, RunTop;
            public bool Sky;
        }

        public static void Run(in SurfaceRules rules, in WorldGenConfig worldGen, int2 origin, ref DensityChunkBuffers b, NativeArray<ushort> voxels,
            ref SurfaceWorkCounters counters){
            ushort water = worldGen.WaterVoxel, ice = worldGen.IceVoxel, keep = worldGen.DensityStoneVoxel;
            int waterLevel = math.clamp(worldGen.WaterLevel, 0, H);
            ushort* to = (ushort*)voxels.GetUnsafePtr();

            byte* flagState = stackalloc byte[SurfaceRules.MaxColumnFlags];
            int* ints = stackalloc int[SurfaceRules.MaxColumnInts];
            byte* intKnown = stackalloc byte[SurfaceRules.MaxColumnInts];
            float* outputs = stackalloc float[SurfaceRules.MaxOutputs];
            byte* outputKnown = stackalloc byte[SurfaceRules.MaxOutputs];
            int* rangeAction = stackalloc int[SurfaceRules.MaxInteriorThresholds + 1];
            int* rangeLow = stackalloc int[SurfaceRules.MaxInteriorThresholds + 2];

            for (int x = 0; x < N; x++)
            for (int z = 0; z < N; z++){
                int c = x * N + z;
                byte* solid = b.Solid + c * H;
                ushort* column = to + c * H;
                int biome = b.Surface[c];

                if (!rules.HasRulesFor(biome)){
                    for (int y = 0; y < waterLevel; y++) column[y] = solid[y] != 0 ? keep : water;
                    for (int y = waterLevel; y < H; y++) column[y] = solid[y] != 0 ? keep : (ushort)0;
                    counters.FallbackColumns++;
                    continue;
                }

                counters.Columns++;
                BiomeRules br = rules.Biomes[biome];
                Column col = new(){
                    X = x, Z = z, WorldX = origin.x + x, WorldZ = origin.y + z, Biome = biome,
                    FlagState = flagState, Ints = ints, IntKnown = intKnown, Outputs = outputs, OutputKnown = outputKnown,
                    RangeAction = rangeAction, RangeLow = rangeLow, RangeCount = -1
                };
                UnsafeUtility.MemClear(flagState, SurfaceRules.MaxColumnFlags);
                UnsafeUtility.MemClear(intKnown, SurfaceRules.MaxColumnInts);
                UnsafeUtility.MemClear(outputKnown, SurfaceRules.MaxOutputs);
                col.Temperature = rules.TemperatureSlot >= 0 ? Lattice(b.Lattice, rules.TemperatureSlot, x, z) : 0f;
                col.Preliminary = Lattice(b.Lattice, rules.PreliminarySurfaceSlot, x, z);
                col.DeltaDown = Lattice(b.DeltaDown, 0, x, z);

                // Runs from the top: open voxels are water below the water level, air above.
                int nextSolidAbove = H;
                bool sky = true;
                int yy = H - 1;
                while (yy >= 0){
                    if (solid[yy] == 0){
                        column[yy] = yy < waterLevel ? water : (ushort)0;
                        yy--;
                        continue;
                    }

                    int top = yy;
                    while (yy >= 0 && solid[yy] != 0) yy--;
                    int bottom = yy + 1;
                    Voxel v = new(){ RunTop = top, Sky = sky, WaterDepth = math.max(0, math.min(waterLevel, nextSolidAbove) - (top + 1)) };
                    Fill(rules, worldGen, br, ref col, ref b, v, top, bottom, column, keep, waterLevel, ref counters);
                    sky = false;
                    nextSolidAbove = bottom;
                }

                // Ice (§8.2): the water surface of a column open to the sky, where it is cold enough.
                int surface = waterLevel - 1;
                if (rules.Ice != 0 && ice != 0 && surface >= 0 && b.Heightmap[c] < surface && worldGen.Layout.Defs[biome].neverSnows == 0){
                    float wobble = rules.IceWobble >= 0 ? Output(rules, ref col, ref b, rules.IceWobble) : 0f;
                    if (rules.ColdAt(surface, col.Temperature, wobble, rules.IceTemperature) || RiverIceCold(rules, ref col, ref b, surface)){
                        column[surface] = ice;
                        counters.IceVoxels++;
                    }
                }
            }
        }

        /// <summary>Frozen rivers (§8.3, DEC-207): a river column freezes where T is below the river limit, without wobble.</summary>
        private static bool RiverIceCold(in SurfaceRules rules, ref Column col, ref DensityChunkBuffers b, int surface){
            return rules.RiverIce >= 0 && Output(rules, ref col, ref b, rules.RiverIce) >= rules.RiverIceMin &&
                   rules.ColdAt(surface, col.Temperature, 0f, rules.RiverIceTemperature);
        }

        /// <summary>The voxels of one solid run: full graph near its top and bottom, the interior plan between.</summary>
        private static void Fill(in SurfaceRules rules, in WorldGenConfig worldGen, in BiomeRules br, ref Column col, ref DensityChunkBuffers b, Voxel v,
            int top, int bottom, ushort* column, ushort keep, int waterLevel, ref SurfaceWorkCounters counters){
            int fullTop = rules.FullEvaluation ? bottom : math.max(bottom, top - br.RAbove);
            int fullBottom = rules.FullEvaluation ? bottom - 1 : math.min(fullTop - 1, bottom + br.RBelow);
            for (int y = top; y >= fullTop; y--){
                SurfaceAction a = rules.Actions[Full(rules, worldGen, br.Root, ref col, ref b, v, y, top, bottom, waterLevel, ref counters)];
                column[y] = Emit(rules, a, ref col, ref b, y, keep);
                // The top's cover (a snow layer) goes on the open voxel above it when that is air.
                if (y == top && a.Cover != 0 && top + 1 < H && column[top + 1] == 0) column[top + 1] = a.Cover;
            }

            for (int y = bottom; y <= fullBottom; y++)
                column[y] = Emit(rules, rules.Actions[Full(rules, worldGen, br.Root, ref col, ref b, v, y, top, bottom, waterLevel, ref counters)], ref col, ref b, y,
                    keep);

            int lo = fullBottom + 1, hi = fullTop - 1;
            if (lo > hi) return;
            counters.InteriorVoxels += hi - lo + 1;
            if (col.RangeCount < 0) PlanRanges(rules, br, ref col, ref b);
            v.DepthAbove = int.MaxValue / 2;
            v.DepthBelow = int.MaxValue / 2;
            for (int r = 0; r < col.RangeCount; r++){
                int from = math.max(lo, col.RangeLow[r]), until = math.min(hi, col.RangeLow[r + 1] - 1);
                if (from > until) continue;
                if (col.RangeAction[r] < 0){
                    v.Y = from;
                    col.RangeAction[r] = Walk(rules, worldGen, br.Interior, ref col, ref b, v, waterLevel, ref counters);
                    counters.InteriorRanges++;
                }

                SurfaceAction a = rules.Actions[col.RangeAction[r]];
                if (a.Kind == RuleActionKind.Bands){
                    for (int y = from; y <= until; y++) column[y] = Emit(rules, a, ref col, ref b, y, keep);
                }
                else{
                    ushort id = a.Kind == RuleActionKind.Block ? a.Block : keep;
                    for (int y = from; y <= until; y++) column[y] = id;
                }
            }
        }

        /// <summary>The column's interior ranges: sorted y thresholds of the interior graph, actions decided lazily.</summary>
        private static void PlanRanges(in SurfaceRules rules, in BiomeRules br, ref Column col, ref DensityChunkBuffers b){
            int count = br.ThresholdCount;
            int* t = stackalloc int[SurfaceRules.MaxInteriorThresholds];
            for (int i = 0; i < count; i++){
                int value = Int(rules, ref col, ref b, rules.Thresholds[br.ThresholdStart + i]);
                int k = i - 1;
                while (k >= 0 && t[k] > value){
                    t[k + 1] = t[k];
                    k--;
                }

                t[k + 1] = value;
            }

            col.RangeLow[0] = int.MinValue / 2;
            for (int i = 0; i < count; i++) col.RangeLow[i + 1] = t[i];
            col.RangeLow[count + 1] = int.MaxValue / 2;
            col.RangeCount = count + 1;
            for (int i = 0; i <= count; i++) col.RangeAction[i] = -1;
        }

        /// <summary>The action of voxel <paramref name="y" /> of the run from the full graph.</summary>
        private static int Full(in SurfaceRules rules, in WorldGenConfig worldGen, int root, ref Column col, ref DensityChunkBuffers b, Voxel v, int y, int top,
            int bottom, int waterLevel, ref SurfaceWorkCounters counters){
            v.Y = y;
            v.DepthAbove = top - y;
            v.DepthBelow = y - bottom;
            counters.FullVoxels++;
            return Walk(rules, worldGen, root, ref col, ref b, v, waterLevel, ref counters);
        }

        private static ushort Emit(in SurfaceRules rules, in SurfaceAction a, ref Column col, ref DensityChunkBuffers b, int y, ushort keep){
            switch (a.Kind){
                case RuleActionKind.Block: return a.Block;
                case RuleActionKind.Bands: {
                    int t = y + (a.Shift >= 0 ? Int(rules, ref col, ref b, a.Shift) : 0);
                    int phase = t % a.Period;
                    if (phase < 0) phase += a.Period;
                    if (phase >= a.Thickness) return a.Block;
                    int band = t / a.Period;
                    if (t % a.Period < 0) band--;
                    return a.PaletteBlock(band);
                }
                default: return keep;
            }
        }

        /// <summary>Walks the graph from <paramref name="node" /> to its action.</summary>
        private static int Walk(in SurfaceRules rules, in WorldGenConfig worldGen, int node, ref Column col, ref DensityChunkBuffers b, in Voxel v, int waterLevel,
            ref SurfaceWorkCounters counters){
            while (node >= 0){
                RuleNode d = rules.Nodes[node];
                counters.Nodes++;
                bool pass;
                switch (d.Kind){
                    case RuleNodeKind.Flag:
                        pass = Flag(rules, worldGen, ref col, ref b, d.A);
                        break;
                    case RuleNodeKind.YAtLeast:
                        pass = v.Y >= Int(rules, ref col, ref b, d.A);
                        break;
                    case RuleNodeKind.DepthAbove:
                        pass = v.DepthAbove <= Int(rules, ref col, ref b, d.A);
                        break;
                    case RuleNodeKind.DepthBelow:
                        pass = v.DepthBelow <= Int(rules, ref col, ref b, d.A);
                        break;
                    case RuleNodeKind.WaterDepth:
                        pass = v.WaterDepth >= d.A && v.WaterDepth <= d.B;
                        break;
                    case RuleNodeKind.SkyOpen:
                        pass = v.Sky;
                        break;
                    default: { // TopAboveWater
                        int above = v.RunTop - (waterLevel - 1);
                        pass = above >= d.A && above < Int(rules, ref col, ref b, d.B);
                        break;
                    }
                }

                node = pass ? d.OnTrue : d.OnFalse;
            }

            return -node - 1;
        }

        private static bool Flag(in SurfaceRules rules, in WorldGenConfig worldGen, ref Column col, ref DensityChunkBuffers b, int i){
            byte state = col.FlagState[i];
            if (state != 0) return state == 2;
            ColumnFlagDef d = rules.Flags[i];
            bool value;
            switch (d.Kind){
                case ColumnFlagKind.Steep:
                    value = Rise(ref b, col.X, col.Z) >= d.A;
                    break;
                case ColumnFlagKind.NearWater:
                    value = NearWater(ref b, col.X, col.Z, (int)d.A);
                    break;
                case ColumnFlagKind.StepFoot:
                    value = StepUp(ref b, col.X, col.Z) >= d.A;
                    break;
                case ColumnFlagKind.Noise: {
                    float o = d.Output >= 0 ? Output(rules, ref col, ref b, d.Output) : 0f;
                    value = o >= d.A && o < d.B;
                    break;
                }
                default: { // Density
                    int noise = math.clamp(d.Output, 0, 3);
                    float f = FloraPlacement.DensityFactor(col.WorldX, col.WorldZ, worldGen.Seed, noise, worldGen.FloraDensityWavelength[noise],
                        worldGen.FloraDensityContrast[noise]);
                    value = f >= d.A && f < d.B;
                    break;
                }
            }

            col.FlagState[i] = value ? (byte)2 : (byte)1;
            return value;
        }

        private static int Int(in SurfaceRules rules, ref Column col, ref DensityChunkBuffers b, int i){
            if (col.IntKnown[i] != 0) return col.Ints[i];
            ColumnIntDef d = rules.Ints[i];
            float o = d.Output >= 0 ? Output(rules, ref col, ref b, d.Output) : 0f;
            int value;
            switch (d.Kind){
                case ColumnIntKind.Const:
                    value = (int)d.A;
                    break;
                case ColumnIntKind.DepthNoise:
                    value = (int)d.A + (int)math.floor(o);
                    break;
                case ColumnIntKind.YAbove:
                    value = (int)math.floor(d.A + o);
                    break;
                case ColumnIntKind.AbovePreliminary:
                    value = (int)math.floor(col.Preliminary - (d.B != 0f ? col.DeltaDown + d.A : d.A));
                    break;
                case ColumnIntKind.Cold:
                    value = rules.ColdThreshold(col.Temperature, o, d.A);
                    break;
                case ColumnIntKind.TreeLine:
                    value = rules.TreeLine(col.Temperature, o);
                    break;
                default: // BeachBound
                    value = (int)math.round(d.A * o);
                    break;
            }

            col.Ints[i] = value;
            col.IntKnown[i] = 1;
            return value;
        }

        private static float Output(in SurfaceRules rules, ref Column col, ref DensityChunkBuffers b, int i){
            if (col.OutputKnown[i] != 0) return col.Outputs[i];
            RuleOutput o = rules.Outputs[i];
            float value = o.Column >= 0
                ? b.ColumnValues[o.Column * DensityLayout.Columns + col.Z * N + col.X]
                : Lattice(b.Lattice, o.Lattice, col.X, col.Z);
            col.Outputs[i] = value;
            col.OutputKnown[i] = 1;
            return value;
        }

        /// <summary>
        ///     The climate of chunk column (x, z) as the rules see it, for tests and reports: T, the snow line (the lowest
        ///     y where the snow test holds, 256 for none), the tree line (y at and above it is above the tree line), the
        ///     heightmap's rise and whether the water surface is cold enough to freeze.
        /// </summary>
        internal static void ColumnClimate(in SurfaceRules rules, ref DensityChunkBuffers b, int x, int z, out float temperature, out int snowLine,
            out int treeLine, out int rise, out bool iceCold){
            temperature = rules.TemperatureSlot >= 0 ? Lattice(b.Lattice, rules.TemperatureSlot, x, z) : 0f;
            float snow = WobbleAt(rules, ref b, rules.SnowWobble, x, z);
            float tree = WobbleAt(rules, ref b, rules.TreeLineWobble, x, z);
            float ice = WobbleAt(rules, ref b, rules.IceWobble, x, z);
            snowLine = rules.ColdThreshold(temperature, snow, rules.SnowTemperature);
            treeLine = rules.TreeLine(temperature, tree);
            rise = Rise(ref b, x, z);
            iceCold = rules.ColdAt(DensityLayout.WetnessY, temperature, ice, rules.IceTemperature) ||
                      rules.RiverIce >= 0 && WobbleAt(rules, ref b, rules.RiverIce, x, z) >= rules.RiverIceMin &&
                      rules.ColdAt(DensityLayout.WetnessY, temperature, 0f, rules.RiverIceTemperature);
        }

        private static float WobbleAt(in SurfaceRules rules, ref DensityChunkBuffers b, int output, int x, int z){
            if (output < 0) return 0f;
            RuleOutput o = rules.Outputs[output];
            return o.Column >= 0 ? b.ColumnValues[o.Column * DensityLayout.Columns + z * N + x] : Lattice(b.Lattice, o.Lattice, x, z);
        }

        /// <summary>A lattice value at chunk column (x, z), bilinear from its cell's corners (<see cref="GraphInterpolation.Bilinear" />'s formula).</summary>
        internal static float Lattice(float* lattice, int slot, int x, int z){
            const int size = DensityLayout.LatticeSize;
            int px = x + DensityLayout.Padding, pz = z + DensityLayout.Padding;
            int i = px >> 2, j = pz >> 2;
            float tx = (px & 3) * 0.25f, tz = (pz & 3) * 0.25f;
            float* l = lattice + slot * size * size;
            float v00 = l[j * size + i], v10 = l[j * size + i + 1], v01 = l[(j + 1) * size + i], v11 = l[(j + 1) * size + i + 1];
            return GraphInterpolation.Lerp(GraphInterpolation.Lerp(v00, v10, tx), GraphInterpolation.Lerp(v01, v11, tx), tz);
        }

        /// <summary>
        ///     The heightmap's rise across the column, per two columns: the larger of |h(x + 2) − h(x − 2)| / 2 and
        ///     |h(z + 2) − h(z − 2)| / 2, rounded; at the chunk edge the footprint shrinks and the difference is scaled to two
        ///     columns. The four-column footprint lets neighbouring columns agree, so steep faces do not flip between rules from
        ///     column to column (DEC-181). A column without terrain is never steep.
        /// </summary>
        internal static int Rise(ref DensityChunkBuffers b, int x, int z){
            short* h = b.Heightmap;
            int self = h[x * N + z];
            if (self < 0) return 0;
            return math.max(Across(h, x, z, RiseReach, 0), Across(h, x, z, 0, RiseReach));
        }

        /// <summary>
        ///     How much higher the highest direct neighbour column (x ± 1, z ± 1) lies, 0 when none is higher. Only columns
        ///     inside the chunk count, so a step across the chunk border is not seen (DEC-205). A column without terrain has 0.
        /// </summary>
        internal static int StepUp(ref DensityChunkBuffers b, int x, int z){
            short* h = b.Heightmap;
            int self = h[x * N + z];
            if (self < 0) return 0;
            int top = self;
            if (x > 0) top = math.max(top, h[(x - 1) * N + z]);
            if (x < N - 1) top = math.max(top, h[(x + 1) * N + z]);
            if (z > 0) top = math.max(top, h[x * N + z - 1]);
            if (z < N - 1) top = math.max(top, h[x * N + z + 1]);
            return top - self;
        }

        /// <summary>Half the footprint of <see cref="Rise" />, in columns.</summary>
        private const int RiseReach = 2;

        private static int Across(short* h, int x, int z, int dx, int dz){
            int x0 = math.max(0, x - dx), z0 = math.max(0, z - dz), x1 = math.min(N - 1, x + dx), z1 = math.min(N - 1, z + dz);
            int a = h[x0 * N + z0], c = h[x1 * N + z1];
            if (a < 0 || c < 0) return 0;
            int span = (x1 - x0) + (z1 - z0);
            int rise = math.abs(c - a);
            return (rise * 4 + span) / (2 * span);
        }

        /// <summary>
        ///     A wet probe within <paramref name="r" /> blocks (Euclidean): probes are the columns at multiples of 4 of the
        ///     padded biome map, wet when terrain lies below the top water layer (P2's wet flag, v1's probes).
        /// </summary>
        internal static bool NearWater(ref DensityChunkBuffers b, int x, int z, int r){
            const int span = DensityLayout.BiomeSpan, pad = DensityLayout.BiomePadding;
            int px = x + pad, pz = z + pad;
            int x0 = math.max(0, px - r), x1 = math.min(span - 1, px + r), z0 = math.max(0, pz - r), z1 = math.min(span - 1, pz + r);
            for (int qz = (z0 + 3) & ~3; qz <= z1; qz += 4)
            for (int qx = (x0 + 3) & ~3; qx <= x1; qx += 4){
                int dx = qx - px, dz = qz - pz;
                if (dx * dx + dz * dz > r * r) continue;
                if (b.Wet[qz * span + qx] != 0) return true;
            }

            return false;
        }
    }
}
