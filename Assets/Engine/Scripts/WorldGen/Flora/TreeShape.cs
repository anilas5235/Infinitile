using Engine.Scripts.VoxelConfig.Data.Generation;
using Engine.Scripts.WorldGen.Core;
using Engine.Scripts.WorldGen.Settings;
using Unity.Collections;
using Unity.Mathematics;

namespace Engine.Scripts.WorldGen.Flora{
    /// <summary>
    ///     Burst mirror of a <see cref="TreeSpecies" />: a range of <c>WorldGenConfig.TreeVariants</c>, addressed by
    ///     <see cref="FloraRuleDef.Tree" />.
    /// </summary>
    public struct TreeSpeciesDef{
        public int VariantStart;
        public int VariantCount;
    }

    /// <summary>
    ///     Burst mirror of a <see cref="TreeVariant" />, sanitised by the config builder: every range has
    ///     0 ≤ x ≤ y, the width is 1 or 2, the branch count is at most <see cref="TreeShape.MaxBranches" />.
    /// </summary>
    public struct TreeVariantDef{
        /// <summary>Upper bound of this variant's interval in the species' cumulative weights; the last one is 1.</summary>
        public float CumulativeEnd;

        public ushort TrunkVoxel;
        public byte TrunkClass;
        public int2 TrunkHeight;
        public int TrunkWidth;

        /// <summary>Largest lean in blocks per 1000 blocks of trunk height.</summary>
        public int LeanPerMille;

        public CrownShape Crown;
        public ushort LeafVoxel;
        public byte LeafClass;
        public ushort AltLeafVoxel;
        public byte AltLeafClass;
        public int2 CrownRadius;
        public int2 CrownTopRadius;

        /// <summary>Crown height range; (0, 0) derives it from the shape.</summary>
        public int2 CrownHeight;

        /// <summary>Lowest crown layer relative to the trunk's top voxel; may be negative.</summary>
        public int CrownBase;

        public int CrownMinBase;
        /// <summary>Chance that a tree gets branches at all, 0…1.</summary>
        public float BranchChance;

        public int2 BranchCount;
        public BranchStyle Branches;
        public int2 BranchLength;

        /// <summary>Branch start band on the trunk, per mille of the trunk's top voxel height.</summary>
        public int2 BranchBand;

        public int2 ClusterRadius;
        public float HoleChance;
        public float BumpChance;
        public float HangChance;
        public float AltLeafChance;
        public int SpacingRadius;

        /// <summary>Largest |x| or |z| of any voxel offset from the anchor column, from the range maxima.</summary>
        public int Reach;
    }

    /// <summary>The rolled values of one tree at its anchor column (<see cref="TreeShape.Roll" />).</summary>
    public struct TreeRoll{
        /// <summary>Index into <c>WorldGenConfig.TreeVariants</c>.</summary>
        public int Variant;

        public int TrunkHeight;
        public int Width;

        /// <summary>Direction of the lean (one of 8) and its offset in blocks at the trunk top.</summary>
        public int2 LeanDirection;

        public int Lean;
        public int CrownRadius;
        public int CrownTopRadius;
        public int CrownHeight;
        public int BranchCount;

        /// <summary>True when the trunk is wide or leans, so its footprint must be flat (<see cref="TreeShape.Footprint" />).</summary>
        public bool NeedsFlatFootprint => Width > 1 || Lean > 0;

        /// <summary>Fallback on uneven ground: one block wide, no lean.</summary>
        public void Narrow(){
            Width = 1;
            Lean = 0;
        }
    }

    /// <summary>
    ///     Builds the voxels of a parametric tree, a pure function of its anchor column, the variant and the seed.
    ///     The chunk job, the whole-world reference of the tests and the species gallery share this code, so they
    ///     agree voxel for voxel. Integer geometry: horizontal positions are measured in doubled coordinates from the
    ///     crown axis, so a width-2 trunk gets a centred crown.
    ///     <list type="bullet">
    ///         <item>Trunk: from y 0 up, leaning by one block at a time; at each step it gains the voxels that keep it face-connected.</item>
    ///         <item>Crown: centred on the trunk's top column; its lowest layer lies <c>CrownBase</c> above the trunk top, its top layer at least one above it, and the trunk reaches its lowest layer.</item>
    ///         <item>Branches: face-connected paths that start next to a trunk voxel.</item>
    ///         <item>
    ///             Crown noise: a 3D hash at each crown voxel's world position drops outer voxels, adds a voxel outward
    ///             or below, or picks the alternative leaf, so two trees agree on every voxel they share.
    ///         </item>
    ///     </list>
    /// </summary>
    public static class TreeShape{
        /// <summary>Upper bound of a variant's branch count.</summary>
        public const int MaxBranches = 8;

        // Shape roll indices (salt HashSalts.TreeShape(i)).
        private const int RollTrunk = 0;
        private const int RollLean = 1;
        private const int RollLeanDirection = 2;
        private const int RollCrownRadius = 3;
        private const int RollCrownTopRadius = 4;
        private const int RollCrownHeight = 5;
        private const int RollBranchCount = 6;
        private const int RollBranchChance = 7;
        private const int RollBranchBase = 8;
        private const int RollsPerBranch = 4;

        /// <summary>The variant of a tree species at anchor column (x, z): the weighted pick with <see cref="HashSalts.TreeVariant" />.</summary>
        public static int PickVariant(int x, int z, in WorldGenConfig cfg, int species){
            TreeSpeciesDef s = cfg.TreeSpecies[species];
            float u = Hash.Hash01(x, z, cfg.Seed, HashSalts.TreeVariant);
            int last = s.VariantStart + s.VariantCount - 1;
            for (int v = s.VariantStart; v < last; v++)
                if (u < cfg.TreeVariants[v].CumulativeEnd) return v;
            return last;
        }

        /// <summary>Rolls every range of variant <paramref name="variant" /> at anchor column (x, z).</summary>
        public static TreeRoll Roll(int x, int z, uint seed, int variant, in TreeVariantDef v){
            TreeRoll r = new(){
                Variant = variant,
                TrunkHeight = RollRange(x, z, seed, RollTrunk, v.TrunkHeight),
                Width = v.TrunkWidth,
                CrownRadius = RollRange(x, z, seed, RollCrownRadius, v.CrownRadius),
                CrownTopRadius = RollRange(x, z, seed, RollCrownTopRadius, v.CrownTopRadius),
                CrownHeight = RollRange(x, z, seed, RollCrownHeight, v.CrownHeight),
                BranchCount = Hash.Hash01(x, z, seed, HashSalts.TreeShape(RollBranchChance)) < v.BranchChance
                    ? RollRange(x, z, seed, RollBranchCount, v.BranchCount)
                    : 0
            };
            int maxLean = r.TrunkHeight * v.LeanPerMille / 1000;
            r.Lean = RollRange(x, z, seed, RollLean, new int2(0, maxLean));
            r.LeanDirection = Direction8(Hash.Hash32(x, z, seed, HashSalts.TreeShape(RollLeanDirection)));
            return r;
        }

        /// <summary>
        ///     Columns under the trunk, relative to the anchor: the rectangle from the anchor to the leaning top
        ///     (xy = min corner, zw = max corner, inclusive). Its terrain heights must lie within 1 block of each
        ///     other for a wide or leaning trunk.
        /// </summary>
        public static int4 Footprint(in TreeRoll r){
            int2 top = r.LeanDirection * r.Lean;
            int2 min = math.min(top, 0);
            int2 max = math.max(top, 0) + (r.Width - 1);
            return new int4(min, max);
        }

        /// <summary>
        ///     Appends the voxels of the tree to <paramref name="output" />, offsets relative to
        ///     <paramref name="anchorBase" />: the anchor column at its top block + 1, in world coordinates (the crown
        ///     noise hashes world positions). The list may hold duplicates; stamping resolves them by voxel class.
        /// </summary>
        public static void Build(int3 anchorBase, in TreeRoll r, in TreeVariantDef v, uint seed, ref NativeList<FloraVoxel> output){
            // Vertical layout: the crown's lowest layer lies CrownBase above the trunk top, its top layer at least one
            // above the trunk top and not below CrownMinBase; the trunk reaches the crown.
            int trunkTop = r.TrunkHeight - 1;
            int crownTop = 0, crownBottom = 0, fullBottom = 0, height = 0;
            bool hasCrown = v.Crown != CrownShape.None;
            if (hasCrown){
                fullBottom = trunkTop + v.CrownBase;
                height = r.CrownHeight;
                if (height <= 0){
                    if (v.Crown is CrownShape.Round or CrownShape.Ellipsoid) height = 2 * r.CrownRadius + 1;
                    else{
                        // Cone and Tiered: from CrownMinBase up to one layer above the trunk top.
                        fullBottom = v.CrownMinBase;
                        height = math.max(trunkTop + 2 - fullBottom, 1);
                    }
                }

                crownTop = math.max(fullBottom + height - 1, math.max(trunkTop + 1, v.CrownMinBase));
                fullBottom = crownTop - height + 1;
                crownBottom = math.max(fullBottom, v.CrownMinBase);
                if (r.TrunkHeight > 0) trunkTop = math.max(trunkTop, crownBottom - 1);
            }

            // Trunk, leaning over its rolled height and straight above it.
            int rolledTop = math.max(r.TrunkHeight - 1, 1);
            int2 previous = int2.zero;
            for (int y = 0; y <= trunkTop; y++){
                int2 column = r.LeanDirection * RoundDiv(r.Lean * math.min(y, rolledTop), rolledTop);
                if (y > 0 && math.any(column != previous)){
                    // One step sideways at the layer below: x first, then z, so every voxel touches the last by a face.
                    int2 step = column - previous;
                    if (step.x != 0) TrunkBlock(previous + new int2(step.x, 0), y - 1, r.Width, v, ref output);
                    if (step.y != 0) TrunkBlock(column, y - 1, r.Width, v, ref output);
                }

                TrunkBlock(column, y, r.Width, v, ref output);
                previous = column;
            }

            int2 topColumn = r.TrunkHeight > 0 ? previous : int2.zero;

            if (hasCrown){
                CrownFrame frame = new(){
                    Shape = v.Crown,
                    Centre2 = 2 * topColumn + (r.Width - 1),
                    Top = crownTop,
                    Bottom = crownBottom,
                    FullBottom = fullBottom,
                    Height = height,
                    Radius = r.CrownRadius,
                    TopRadius = r.CrownTopRadius,
                    WidthTerm = 2 * (r.Width - 1)
                };
                AddCrown(anchorBase, frame, r.Width, v, seed, ref output);
                // The leaves right above the trunk top are never dropped, so the trunk always touches its crown.
                if (r.TrunkHeight > 0)
                    for (int i = 0; i < r.Width; i++)
                    for (int j = 0; j < r.Width; j++)
                        AddLeaf(anchorBase, new int3(topColumn.x + i, trunkTop + 1, topColumn.y + j), v, seed, ref output);
            }

            // Branches.
            for (int b = 0; b < r.BranchCount && r.TrunkHeight > 0; b++){
                int salt = RollBranchBase + b * RollsPerBranch;
                int2 band = trunkTop * v.BranchBand / 1000;
                int startY = RollRange(anchorBase.x, anchorBase.z, seed, salt, band);
                uint dirHash = Hash.Hash32(anchorBase.x, anchorBase.z, seed, HashSalts.TreeShape(salt + 1));
                int2 dir = v.Branches == BranchStyle.Arm ? Direction4(dirHash) : Direction8(dirHash);
                int length = RollRange(anchorBase.x, anchorBase.z, seed, salt + 2, v.BranchLength);
                int cluster = RollRange(anchorBase.x, anchorBase.z, seed, salt + 3, v.ClusterRadius);
                // Both styles rise one layer per length step, so the tip ends in the crown's lowest layer or higher.
                if (hasCrown) startY = math.max(startY, crownBottom - length);

                // Start on the trunk voxel that faces the branch direction.
                int2 column = r.LeanDirection * RoundDiv(r.Lean * math.min(startY, rolledTop), rolledTop);
                int3 p = new(column.x + (dir.x > 0 ? r.Width - 1 : 0), startY, column.y + (dir.y > 0 ? r.Width - 1 : 0));
                if (v.Branches == BranchStyle.Arm){
                    p.xz += dir;
                    Add(ref output, p, v.TrunkVoxel, v.TrunkClass);
                    for (int k = 0; k < length; k++){
                        p.y++;
                        Add(ref output, p, v.TrunkVoxel, v.TrunkClass);
                    }
                }
                else{
                    for (int k = 0; k < length; k++){
                        if (dir.x != 0){
                            p.x += dir.x;
                            Add(ref output, p, v.TrunkVoxel, v.TrunkClass);
                        }

                        if (dir.y != 0){
                            p.z += dir.y;
                            Add(ref output, p, v.TrunkVoxel, v.TrunkClass);
                        }

                        p.y++;
                        Add(ref output, p, v.TrunkVoxel, v.TrunkClass);
                    }
                }

                if (cluster > 0){
                    CrownFrame tip = new(){
                        Shape = CrownShape.Round,
                        Centre2 = 2 * p.xz,
                        Top = p.y + cluster,
                        Bottom = p.y - cluster,
                        FullBottom = p.y - cluster,
                        Height = 2 * cluster + 1,
                        Radius = cluster
                    };
                    AddCrown(anchorBase, tip, 1, v, seed, ref output);
                }
            }
        }

        /// <summary>A crown or a branch cluster: its shape, horizontal axis (doubled) and layers.</summary>
        private struct CrownFrame{
            public CrownShape Shape;

            /// <summary>Crown axis in doubled block coordinates (2x + width − 1).</summary>
            public int2 Centre2;

            public int Top;
            public int Bottom;

            /// <summary>Lowest layer of the uncut shape; layers below <see cref="Bottom" /> are cut.</summary>
            public int FullBottom;

            public int Height;
            public int Radius;
            public int TopRadius;

            /// <summary>2 for a width-2 trunk: the four voxels around the axis belong to every layer.</summary>
            public int WidthTerm;
        }

        private static void AddCrown(int3 anchorBase, in CrownFrame f, int width, in TreeVariantDef v, uint seed,
            ref NativeList<FloraVoxel> output){
            int extent = math.max(f.Radius, f.TopRadius) + 1;
            int2 min = (f.Centre2 >> 1) - extent;
            int2 max = (f.Centre2 >> 1) + extent + (width - 1);
            for (int x = min.x; x <= max.x; x++)
            for (int z = min.y; z <= max.y; z++)
            for (int y = f.Bottom; y <= f.Top; y++){
                if (!Inside(f, x, y, z)) continue;
                // Outward faces: +x, −x, +z, −z, +y (bumps), −y (lower edge).
                int outside = 0;
                if (!Inside(f, x + 1, y, z)) outside |= 1;
                if (!Inside(f, x - 1, y, z)) outside |= 2;
                if (!Inside(f, x, y, z + 1)) outside |= 4;
                if (!Inside(f, x, y, z - 1)) outside |= 8;
                if (!Inside(f, x, y + 1, z)) outside |= 16;
                if (!Inside(f, x, y - 1, z)) outside |= 32;
                int3 p = new(x, y, z);
                if (outside == 0){
                    AddLeaf(anchorBase, p, v, seed, ref output);
                    continue;
                }

                int3 world = anchorBase + p;
                uint h = Hash.Hash32(world.x, world.y, world.z, seed, HashSalts.Crown);
                float u = (h >> 8) * (1f / 16777216f);
                if (u < v.HoleChance) continue;
                AddLeaf(anchorBase, p, v, seed, ref output);
                float bumpEnd = v.HoleChance + v.BumpChance;
                if (u < bumpEnd){
                    int faces = outside & 31;
                    int count = math.countbits(faces);
                    if (count == 0) continue;
                    int pick = (int)((h & 0xFFu) % (uint)count);
                    int face = 0;
                    for (int bit = 0; bit < 5; bit++){
                        if ((faces & (1 << bit)) == 0) continue;
                        if (pick-- == 0){
                            face = bit;
                            break;
                        }
                    }

                    AddLeaf(anchorBase, p + FaceOffset(face), v, seed, ref output);
                }
                else if (u < bumpEnd + v.HangChance && (outside & 32) != 0){
                    AddLeaf(anchorBase, p - new int3(0, 1, 0), v, seed, ref output);
                }
            }
        }

        /// <summary>The shape test of a crown voxel at offset (x, y, z), in doubled horizontal coordinates.</summary>
        private static bool Inside(in CrownFrame f, int x, int y, int z){
            if (y < f.Bottom || y > f.Top) return false;
            int dx = 2 * x - f.Centre2.x, dz = 2 * z - f.Centre2.y;
            int horizontal = dx * dx + dz * dz;
            if (f.Shape is CrownShape.Round or CrownShape.Ellipsoid){
                // (dx² + dz²) / Hd + dy² / Vd ≤ 1 with Hd = 4(r² + r) and Vd = (h − 1)² + 2(h − 1): a sphere of
                // radius √(r² + r) for h = 2r + 1, which gives rounder small crowns than radius r.
                int hd = 4 * (f.Radius * f.Radius + f.Radius) + f.WidthTerm;
                int span = f.Height - 1;
                int vd = span * span + 2 * span;
                if (vd == 0) return horizontal <= hd;
                int dy = 2 * y - (f.FullBottom + f.Top);
                return (long)horizontal * vd + (long)dy * dy * hd <= (long)hd * vd;
            }

            // Cone and Tiered: a disc per layer, its radius interpolated from the bottom to the top.
            int layer = y - f.FullBottom;
            int span2 = f.Height - 1;
            int r = span2 == 0 ? f.Radius : RoundDiv(f.Radius * (span2 - layer) + f.TopRadius * layer, span2);
            if (f.Shape == CrownShape.Tiered && (f.Top - y) % 2 == 1) r = math.max(r - 1, 0);
            return horizontal <= 4 * (r * r + r) + f.WidthTerm;
        }

        private static int3 FaceOffset(int face){
            return face switch{
                0 => new int3(1, 0, 0),
                1 => new int3(-1, 0, 0),
                2 => new int3(0, 0, 1),
                3 => new int3(0, 0, -1),
                _ => new int3(0, 1, 0)
            };
        }

        private static void AddLeaf(int3 anchorBase, int3 p, in TreeVariantDef v, uint seed, ref NativeList<FloraVoxel> output){
            int3 world = anchorBase + p;
            bool alt = v.AltLeafChance > 0f && Hash.Hash01(world.x, world.y, world.z, seed, HashSalts.CrownLeafMix) < v.AltLeafChance;
            if (alt) Add(ref output, p, v.AltLeafVoxel, v.AltLeafClass);
            else Add(ref output, p, v.LeafVoxel, v.LeafClass);
        }

        private static void TrunkBlock(int2 column, int y, int width, in TreeVariantDef v, ref NativeList<FloraVoxel> output){
            for (int i = 0; i < width; i++)
            for (int j = 0; j < width; j++)
                Add(ref output, new int3(column.x + i, y, column.y + j), v.TrunkVoxel, v.TrunkClass);
        }

        private static void Add(ref NativeList<FloraVoxel> output, int3 offset, ushort voxel, byte cls){
            output.Add(new FloraVoxel{ Offset = offset, Voxel = voxel, Class = cls });
        }

        /// <summary>A value of <paramref name="range" /> rolled with shape salt <paramref name="i" />.</summary>
        private static int RollRange(int x, int z, uint seed, int i, int2 range){
            int span = math.max(range.y - range.x, 0) + 1;
            return range.x + (int)(Hash.Hash32(x, z, seed, HashSalts.TreeShape(i)) % (uint)span);
        }

        private static int2 Direction8(uint h){
            return (h % 8u) switch{
                0 => new int2(1, 0),
                1 => new int2(1, 1),
                2 => new int2(0, 1),
                3 => new int2(-1, 1),
                4 => new int2(-1, 0),
                5 => new int2(-1, -1),
                6 => new int2(0, -1),
                _ => new int2(1, -1)
            };
        }

        private static int2 Direction4(uint h){
            return (h % 4u) switch{
                0 => new int2(1, 0),
                1 => new int2(0, 1),
                2 => new int2(-1, 0),
                _ => new int2(0, -1)
            };
        }

        /// <summary>round(a / b) for b &gt; 0, halves away from zero, in integers.</summary>
        private static int RoundDiv(int a, int b){
            return a >= 0 ? (2 * a + b) / (2 * b) : -((-2 * a + b) / (2 * b));
        }
    }
}
