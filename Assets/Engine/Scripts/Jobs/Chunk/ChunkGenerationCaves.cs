using System;
using System.Runtime.CompilerServices;
using Engine.Scripts.Utils.Extensions;
using Engine.Scripts.WorldGen.Blending;
using Engine.Scripts.WorldGen.Caves;
using Engine.Scripts.WorldGen.Core;
using Engine.Scripts.WorldGen.Flora;
using Engine.Scripts.WorldGen.Settings;
using Unity.Collections;
using Unity.Mathematics;
using static Engine.Scripts.Utils.VoxelConstants;

namespace Engine.Scripts.Jobs.Chunk{
    /// <summary>
    ///     Cave data of a block and its flora ring (the columns of <see cref="FloraGrid" />, same indexing): the
    ///     carving limits of every column (<see cref="CaveLimits" />), and the ground probes of the flora test. A probe
    ///     column records, for the 32 voxels from its <see cref="ProbeTop" /> down, whether a worm sphere contains
    ///     them; the vegetation pass combines that with the column limits and the halls, which it evaluates itself.
    ///     Ring columns get their height and limits only when a probe needs them. Temp, owned by the job.
    /// </summary>
    internal struct CaveColumnData : IDisposable{
        /// <summary>Voxels below <see cref="ProbeTop" /> a probe column records.</summary>
        public const int ProbeDepth = 32;

        /// <summary>Marks a height, limit or probe that is not known (yet).</summary>
        public const int Unknown = int.MinValue;

        public int Padding;
        public int2 Size;

        /// <summary>Top block y of the column (<see cref="Unknown" /> in the ring until needed).</summary>
        public NativeArray<int> Height;

        public NativeArray<int> CavernTop;
        public NativeArray<int> WormTop;

        /// <summary>Highest y a ground probe of the column tests, <see cref="Unknown" /> for no probe.</summary>
        public NativeArray<int> ProbeTop;

        /// <summary>Bit k: voxel <see cref="ProbeTop" /> − k of the column lies inside a worm sphere.</summary>
        public NativeArray<uint> ProbeWorm;

        /// <summary>Lowest and highest probe y of the block, for the worm pass to skip steps away from all probes.</summary>
        public int2 ProbeRange;

        /// <summary>Highest worm limit among the block's own columns.</summary>
        public int MaxWormTop;

        public bool IsCreated => Height.IsCreated;

        public CaveColumnData(int padding, Allocator allocator){
            Padding = padding;
            Size = new int2(ChunkWidth, ChunkDepth) + 2 * padding;
            int count = Size.x * Size.y;
            Height = Filled(count, allocator);
            CavernTop = Filled(count, allocator);
            WormTop = Filled(count, allocator);
            ProbeTop = Filled(count, allocator);
            ProbeWorm = new NativeArray<uint>(count, allocator);
            ProbeRange = new int2(int.MaxValue, int.MinValue);
            MaxWormTop = int.MinValue;
        }

        private static NativeArray<int> Filled(int count, Allocator allocator){
            NativeArray<int> array = new(count, allocator, NativeArrayOptions.UninitializedMemory);
            for (int i = 0; i < count; i++) array[i] = Unknown;
            return array;
        }

        /// <summary>Element index of block-local column (x, z), which may lie in the ring; as <see cref="FloraGrid.Index" />.</summary>
        public int Index(int x, int z){
            return z + Padding + (x + Padding) * Size.y;
        }

        public bool InGrid(int x, int z){
            return x >= -Padding && x < ChunkWidth + Padding && z >= -Padding && z < ChunkDepth + Padding;
        }

        public void Dispose(){
            if (Height.IsCreated) Height.Dispose();
            if (CavernTop.IsCreated) CavernTop.Dispose();
            if (WormTop.IsCreated) WormTop.Dispose();
            if (ProbeTop.IsCreated) ProbeTop.Dispose();
            if (ProbeWorm.IsCreated) ProbeWorm.Dispose();
        }
    }

    /// <summary>
    ///     The 3D passes after the terrain fill: they carve or replace voxels below the surface and never touch the
    ///     column data, so the surface, the biome and the flora decisions stay those of the uncarved column. Every
    ///     pass reads only world coordinates, the seed and the config, so a voxel is the same in every chunk, slice
    ///     or volume that contains it. Each pass is its own non-inlined method.
    /// </summary>
    internal partial struct ChunkJob{
        /// <summary>
        ///     Carving limits of the block's columns, from their heights and those of the ring one column around the
        ///     block (sampled here).
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void PrepareCaveColumns(ref CaveColumnData caves, NativeArray<ChunkColumn> chunkColumns,
            int3 origin, in WorldGenConfig cfg, in BlendCache cache){
            const int w = ChunkWidth + 2;
            NativeArray<int> heights = new(w * (ChunkDepth + 2), Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            for (int x = -1; x <= ChunkWidth; x++)
            for (int z = -1; z <= ChunkDepth; z++){
                bool inside = x >= 0 && x < ChunkWidth && z >= 0 && z < ChunkDepth;
                heights[(z + 1) * w + x + 1] = inside
                    ? chunkColumns[GetColumnIdx(x, z, ChunkDepth)].Height
                    : CaveColumns.SurfaceY(origin.x + x, origin.z + z, cfg, cache);
            }

            int maxWorm = int.MinValue;
            for (int x = 0; x < ChunkWidth; x++)
            for (int z = 0; z < ChunkDepth; z++){
                int lowest = int.MaxValue;
                bool wet = false;
                for (int dx = -1; dx <= 1; dx++)
                for (int dz = -1; dz <= 1; dz++){
                    int h = heights[(z + dz + 1) * w + x + dx + 1];
                    lowest = math.min(lowest, h);
                    wet |= CaveColumns.Submerged(h, cfg);
                }

                int height = heights[(z + 1) * w + x + 1];
                CaveLimits limits = CaveColumns.Limits(origin.x + x, origin.z + z, height, lowest, wet, cfg, cache);
                int g = caves.Index(x, z);
                caves.Height[g] = height;
                caves.CavernTop[g] = limits.Cavern;
                caves.WormTop[g] = limits.Worm;
                maxWorm = math.max(maxWorm, limits.Worm);
            }

            caves.MaxWormTop = maxWorm;
            heights.Dispose();
        }

        /// <summary>
        ///     Ground probes of the flora test: the top block under every single-block plant of the block, and the
        ///     voxels under the trunk (<see cref="UnderTrunk" />) or the anchor column of every multi-block anchor within
        ///     reach. Runs after <see cref="DecideFlora" />, so the worm pass knows which voxels to record.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void RegisterCaveProbes(ref CaveColumnData caves, in FloraGrid grid,
            NativeArray<ChunkColumn> chunkColumns, int3 origin, in WorldGenConfig cfg){
            if (!cfg.HasFlora) return;
            for (int x = 0; x < ChunkWidth; x++)
            for (int z = 0; z < ChunkDepth; z++){
                ChunkColumn col = chunkColumns[GetColumnIdx(x, z, ChunkDepth)];
                if (col.FloraSpecies == 0 || cfg.FloraRules[grid.Rule[grid.Index(x, z)]].MultiBlock != 0) continue;
                AddProbe(ref caves, x, z, col.Height);
            }

            int reach = cfg.FloraReach;
            for (int x = -reach; x < ChunkWidth + reach; x++)
            for (int z = -reach; z < ChunkDepth + reach; z++){
                int g = grid.Index(x, z);
                if (grid.Anchor[g] == 0) continue;
                AnchorGround(grid, x, z, origin, cfg, out int4 box, out int y);
                for (int fx = x + box.x; fx <= x + box.z; fx++)
                for (int fz = z + box.y; fz <= z + box.w; fz++)
                    AddProbe(ref caves, fx, fz, y);
            }
        }

        private static void AddProbe(ref CaveColumnData caves, int x, int z, int y){
            if (!caves.InGrid(x, z)) return;
            int g = caves.Index(x, z);
            caves.ProbeTop[g] = math.max(caves.ProbeTop[g], y);
            caves.ProbeRange = new int2(math.min(caves.ProbeRange.x, y), math.max(caves.ProbeRange.y, y));
        }

        /// <summary>
        ///     The voxels an anchor stands on: for a tree the columns under its trunk (relative to the anchor) at the
        ///     height under the trunk's lowest voxel, otherwise the anchor's own top block.
        /// </summary>
        private static void AnchorGround(in FloraGrid grid, int x, int z, int3 origin, in WorldGenConfig cfg,
            out int4 box, out int y){
            int g = grid.Index(x, z);
            FloraRuleDef d = cfg.FloraRules[grid.Rule[g]];
            box = int4.zero;
            y = grid.Height[g];
            if (d.Tree < 0) return;
            int wx = origin.x + x, wz = origin.z + z;
            int variant = TreeShape.PickVariant(wx, wz, cfg, d.Tree);
            TreeRoll roll = TreeShape.Roll(wx, wz, cfg.Seed, variant, cfg.TreeVariants[variant]);
            if (grid.Anchor[g] == 2) roll.Narrow();
            box = UnderTrunk(roll);
            y -= roll.Sink;
        }

        /// <summary>
        ///     Columns under the lowest trunk voxels of <paramref name="roll" />, relative to the anchor, as (min x, min z,
        ///     max x, max z): the whole line of a lying trunk, the base square of a standing one.
        /// </summary>
        private static int4 UnderTrunk(in TreeRoll roll){
            return roll.Lying ? TreeShape.Footprint(roll) : new int4(0, 0, roll.Width - 1, roll.Width - 1);
        }

        /// <summary>
        ///     The cave passes carve voxel (x, y, z) of the block or its ring, a probed voxel (worm spheres are known only
        ///     there): the worm record, the hall noise and the lava pockets, each within the column's limits, which ring
        ///     columns sample on first use. Equals the pure evaluation at that voxel.
        /// </summary>
        private static bool GroundCarved(ref CaveColumnData caves, int x, int y, int z, int3 origin,
            in WorldGenConfig cfg, in BlendCache cache){
            if (!caves.IsCreated || y <= CaveColumns.Floor || !caves.InGrid(x, z)) return false;
            ref readonly CaveConfig c = ref cfg.Caves;
            int g = caves.Index(x, z);
            int wx = origin.x + x, wz = origin.z + z;

            int top = caves.ProbeTop[g];
            bool inWorm = top != CaveColumnData.Unknown && y >= c.WormMinY && y <= top
                          && top - y < CaveColumnData.ProbeDepth && (caves.ProbeWorm[g] & (1u << (top - y))) != 0;
            bool mayHall = y >= c.CavernBandMin && y <= c.CavernBandMax;
            bool mayPocket = c.LavaVoxel != 0 && y >= c.LavaPocketBandMin && y <= c.LavaPocketBandMax;
            if (!inWorm && !mayHall && !mayPocket) return false;

            // Every limit lies at or below the top block, the hall limit (which the pockets keep too) Cover below it.
            if (caves.Height[g] == CaveColumnData.Unknown) caves.Height[g] = CaveColumns.SurfaceY(wx, wz, cfg, cache);
            if (y > caves.Height[g]) return false;
            bool belowCover = (mayHall || mayPocket) && y <= caves.Height[g] - c.Cover;
            if (!inWorm && !belowCover) return false;
            if (caves.WormTop[g] == CaveColumnData.Unknown){
                CaveLimits limits = CaveColumns.Limits(wx, wz, cfg, cache);
                caves.CavernTop[g] = limits.Cavern;
                caves.WormTop[g] = limits.Worm;
            }

            if (inWorm && y <= caves.WormTop[g]) return true;
            if (!belowCover) return false;
            return (mayHall && Caverns.Carves(wx, y, wz, caves.CavernTop[g], cfg))
                   || (mayPocket && LavaPockets.Carves(wx, y, wz, caves.CavernTop[g], cfg));
        }

        /// <summary>
        ///     Ore veins (<see cref="OreVeins" />): rule by rule, walks every vein whose seed lies within the rule's
        ///     longest walk of the block and replaces the rule's host voxel within its y range wherever the walk passes.
        ///     Runs before the carving passes, so the caves expose the ores.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void PlaceOres(NativeArray<ushort> vox, int3 origin, in WorldGenConfig cfg){
            ref readonly CaveConfig c = ref cfg.Caves;
            for (int r = 0; r < c.Ores.Length; r++){
                OreRuleDef def = c.Ores[r];
                int3 lo = new(origin.x, math.max(def.MinY, CaveColumns.Floor + 1), origin.z);
                int3 hi = new(origin.x + ChunkWidth - 1, math.min(def.MaxY, ChunkHeight - 1), origin.z + ChunkDepth - 1);
                if (lo.y > hi.y) continue;
                int reach = def.VeinSize.y - 1;
                int3 first = OreVeins.Cell(lo - reach), last = OreVeins.Cell(hi + reach);
                for (int cx = first.x; cx <= last.x; cx++)
                for (int cy = first.y; cy <= last.y; cy++)
                for (int cz = first.z; cz <= last.z; cz++){
                    int3 cell = new(cx, cy, cz);
                    int count = OreVeins.Count(cell, r, def, cfg);
                    for (int k = 0; k < count; k++){
                        OreVein vein = OreVeins.Begin(cell, r, k, def, cfg);
                        int extent = vein.Size - 1;
                        if (math.any(vein.Seed + extent < lo) || math.any(vein.Seed - extent > hi)) continue;
                        int3 v = vein.Seed;
                        for (int s = 0; s < vein.Size; s++){
                            if (s > 0) v = OreVeins.Next(vein, s, v);
                            if (math.any(v < lo) || math.any(v > hi)) continue;
                            int i = ChunkSize.Flatten(v.x - origin.x, v.y - origin.y, v.z - origin.z);
                            if (vox[i] == def.Host) vox[i] = def.Ore;
                        }
                    }
                }
            }
        }

        /// <summary>
        ///     Halls (<see cref="Caverns" />): the hall and pillar noises on a lattice over the columns whose mask is open,
        ///     interpolated at every voxel of the cavern band up to the column's limit.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void CarveCaverns(NativeArray<ushort> vox, in CaveColumnData caves, int3 origin, in WorldGenConfig cfg){
            ref readonly CaveConfig c = ref cfg.Caves;
            int yLow = math.max(c.CavernBandMin, CaveColumns.Floor + 1);
            NativeArray<float> raise = new(ChunkWidth * ChunkDepth, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            int2 lo = new(int.MaxValue), hi = new(int.MinValue);
            int yHigh = int.MinValue;
            for (int x = 0; x < ChunkWidth; x++)
            for (int z = 0; z < ChunkDepth; z++){
                int i = GetColumnIdx(x, z, ChunkDepth);
                raise[i] = -1f;
                int top = math.min(caves.CavernTop[caves.Index(x, z)], c.CavernBandMax);
                if (top < yLow) continue;
                float mask = CaveMasks.Cavern(origin.x + x, origin.z + z, cfg);
                if (!Caverns.MaskOpen(mask, c)) continue;
                raise[i] = Caverns.MaskRaise(mask, c);
                lo = math.min(lo, new int2(x, z));
                hi = math.max(hi, new int2(x, z));
                yHigh = math.max(yHigh, top);
            }

            if (yHigh < yLow){
                raise.Dispose();
                return;
            }

            // Lattice points around the open columns: world-aligned, so every block reads the same corners.
            const int step = Caverns.LatticeStep;
            int3 first = new(Caverns.LatticeFloor(origin.x + lo.x), Caverns.LatticeFloor(yLow), Caverns.LatticeFloor(origin.z + lo.y));
            int3 last = new(Caverns.LatticeFloor(origin.x + hi.x), Caverns.LatticeFloor(yHigh), Caverns.LatticeFloor(origin.z + hi.y));
            int3 n = (last - first) / step + 2;
            NativeArray<float2> lattice = new(n.x * n.y * n.z, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            for (int ix = 0; ix < n.x; ix++)
            for (int iy = 0; iy < n.y; iy++)
            for (int iz = 0; iz < n.z; iz++){
                int3 p = first + new int3(ix, iy, iz) * step;
                lattice[(ix * n.y + iy) * n.z + iz] = new float2(Caverns.HallNoise(p.x, p.y, p.z, cfg),
                    Caverns.PillarNoise(p.x, p.y, p.z, cfg));
            }

            for (int x = lo.x; x <= hi.x; x++)
            for (int z = lo.y; z <= hi.y; z++){
                float r = raise[GetColumnIdx(x, z, ChunkDepth)];
                if (r < 0f) continue;
                int wx = origin.x + x, wz = origin.z + z;
                int ix = (Caverns.LatticeFloor(wx) - first.x) / step, iz = (Caverns.LatticeFloor(wz) - first.z) / step;
                float fx = Caverns.LatticeFraction(wx), fz = Caverns.LatticeFraction(wz);
                int top = math.min(caves.CavernTop[caves.Index(x, z)], c.CavernBandMax);
                for (int y = yLow; y <= top; y++){
                    int iy = (Caverns.LatticeFloor(y) - first.y) / step;
                    float2 c000 = lattice[(ix * n.y + iy) * n.z + iz];
                    float2 c100 = lattice[((ix + 1) * n.y + iy) * n.z + iz];
                    float2 c001 = lattice[(ix * n.y + iy) * n.z + iz + 1];
                    float2 c101 = lattice[((ix + 1) * n.y + iy) * n.z + iz + 1];
                    float2 c010 = lattice[(ix * n.y + iy + 1) * n.z + iz];
                    float2 c110 = lattice[((ix + 1) * n.y + iy + 1) * n.z + iz];
                    float2 c011 = lattice[(ix * n.y + iy + 1) * n.z + iz + 1];
                    float2 c111 = lattice[((ix + 1) * n.y + iy + 1) * n.z + iz + 1];
                    float3 f = new(fx, Caverns.LatticeFraction(y), fz);
                    float2 noises = new(
                        Caverns.Trilinear(c000.x, c100.x, c001.x, c101.x, c010.x, c110.x, c011.x, c111.x, f),
                        Caverns.Trilinear(c000.y, c100.y, c001.y, c101.y, c010.y, c110.y, c011.y, c111.y, f));
                    if (Caverns.Carves(y, r, noises, c)) vox[ChunkSize.Flatten(x, y, z)] = 0;
                }
            }

            lattice.Dispose();
            raise.Dispose();
        }

        /// <summary>
        ///     Worm tunnels (<see cref="Worms" />): walks every worm whose start lies within <c>WormPadding</c> of the
        ///     block or its ring, with its branch, and carves each step's sphere into the block below the column limits.
        ///     Steps near a ground probe of the ring are recorded for the flora test. Nothing is buffered: the walk
        ///     carves as it goes, so no cap can drop a worm. The anchors of the cells one beyond the range are
        ///     computed once, since every worm steers at starts of its neighbour cells.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void CarveWorms(NativeArray<ushort> vox, ref CaveColumnData caves, int3 origin, in WorldGenConfig cfg,
            in BlendCache cache){
            ref readonly CaveConfig c = ref cfg.Caves;
            int padding = c.WormPadding + caves.Padding;
            int2 min = origin.xz - padding, max = origin.xz + new int2(ChunkWidth - 1, ChunkDepth - 1) + padding;
            int2 firstCell = Worms.Cell(min, cfg), lastCell = Worms.Cell(max, cfg);
            WormAnchors anchors = WormAnchors.Create(firstCell - 1, lastCell + 1, cfg, cache, Allocator.Temp);
            for (int cx = firstCell.x; cx <= lastCell.x; cx++)
            for (int cz = firstCell.y; cz <= lastCell.y; cz++)
            for (int k = 0; k < Worms.MaxPerCell; k++){
                int2 cell = new(cx, cz);
                if (!Worms.Present(cell, k, cfg)) continue;
                int2 start = Worms.StartColumn(cell, k, cfg);
                if (math.any(start < min) || math.any(start > max)) continue;
                if (!Worms.Begin(cell, k, cfg, cache, anchors, out Worm worm)) continue;
                Worm branch = default;
                bool branched = false;
                do{
                    CarveStep(vox, ref caves, worm, origin, c);
                    if (worm.Step == worm.BranchStep) branched = Worms.Branch(worm, c, out branch);
                } while (Worms.Next(ref worm, c));

                if (!branched) continue;
                do{
                    CarveStep(vox, ref caves, branch, origin, c);
                } while (Worms.Next(ref branch, c));
            }

            anchors.Dispose();
        }

        private static void CarveStep(NativeArray<ushort> vox, ref CaveColumnData caves, in Worm worm, int3 origin,
            in CaveConfig c){
            float r = worm.Radius;
            int3 lo = (int3)math.floor(worm.Position - r) - origin;
            int3 hi = (int3)math.floor(worm.Position + r) - origin;
            int yFloor = math.max(c.WormMinY, CaveColumns.Floor + 1);

            // The block's voxels below each column's worm limit.
            if (hi.x >= 0 && lo.x < ChunkWidth && hi.z >= 0 && lo.z < ChunkDepth && hi.y >= yFloor && lo.y <= caves.MaxWormTop){
                int3 from = math.max(lo, new int3(0, yFloor, 0));
                int3 to = math.min(hi, new int3(ChunkWidth - 1, ChunkHeight - 1, ChunkDepth - 1));
                for (int x = from.x; x <= to.x; x++)
                for (int z = from.z; z <= to.z; z++){
                    int top = math.min(to.y, caves.WormTop[caves.Index(x, z)]);
                    for (int y = from.y; y <= top; y++)
                        if (Worms.Contains(worm, origin + new int3(x, y, z)))
                            vox[ChunkSize.Flatten(x, y, z)] = 0;
                }
            }

            // Ground probes of the block and its ring, whatever the limits; the flora test applies them.
            int2 range = caves.ProbeRange;
            if (range.x > range.y || hi.y < range.x - (CaveColumnData.ProbeDepth - 1) || lo.y > range.y) return;
            int p = caves.Padding;
            int2 probeFrom = math.max(lo.xz, -p);
            int2 probeTo = math.min(hi.xz, new int2(ChunkWidth - 1, ChunkDepth - 1) + p);
            for (int x = probeFrom.x; x <= probeTo.x; x++)
            for (int z = probeFrom.y; z <= probeTo.y; z++){
                int g = caves.Index(x, z);
                int top = caves.ProbeTop[g];
                if (top == CaveColumnData.Unknown) continue;
                uint bits = caves.ProbeWorm[g];
                int yTo = math.min(hi.y + origin.y, top);
                for (int y = math.max(lo.y + origin.y, top - (CaveColumnData.ProbeDepth - 1)); y <= yTo; y++)
                    if (Worms.Contains(worm, new int3(origin.x + x, y, origin.z + z)))
                        bits |= 1u << (top - y);
                caves.ProbeWorm[g] = bits;
            }
        }

        /// <summary>
        ///     Lava (<see cref="LavaPockets" />): in the columns inside the lava mask, carves the pockets of the low band up
        ///     to the column's hall limit, lava at or below <c>LavaLevel</c> and air above, and fills every voxel the halls
        ///     and worms carved at or below <c>LavaLevel</c> with lava.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void FillLava(NativeArray<ushort> vox, in CaveColumnData caves, int3 origin, in WorldGenConfig cfg){
            ref readonly CaveConfig c = ref cfg.Caves;
            ushort lava = c.LavaVoxel;
            if (lava == 0) return;
            for (int x = 0; x < ChunkWidth; x++)
            for (int z = 0; z < ChunkDepth; z++){
                int wx = origin.x + x, wz = origin.z + z;
                float mask = CaveMasks.Lava(wx, wz, cfg);
                if (!LavaPockets.MaskOpen(mask, c)) continue;
                int g = caves.Index(x, z);
                float raise = LavaPockets.MaskRaise(mask, c);
                int2 band = LavaPockets.Band(caves.CavernTop[g], c);
                for (int y = band.x; y <= band.y; y++)
                    if (LavaPockets.Noise(wx, y, wz, cfg) > LavaPockets.Threshold(y, raise, c))
                        vox[ChunkSize.Flatten(x, y, z)] = y <= c.LavaLevel ? lava : (ushort)0;

                // The terrain fill leaves no air at or below the top block, so air there is carved.
                int top = math.min(math.min(c.LavaLevel, caves.Height[g]), ChunkHeight - 1);
                for (int y = CaveColumns.Floor + 1; y <= top; y++){
                    int i = ChunkSize.Flatten(x, y, z);
                    if (vox[i] == 0) vox[i] = lava;
                }
            }
        }

        /// <summary>
        ///     Bedrock floor: all of y 0, and y 1 and y 2 where a 3D hash falls below their share, so the floor is
        ///     ragged rather than flat (like the bedrock layer of Minecraft's terrain generator). Runs last among the
        ///     cave passes, so nothing carves it.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void ApplyBottomLayer(NativeArray<ushort> vox, int3 origin, in WorldGenConfig cfg){
            ref readonly CaveConfig caves = ref cfg.Caves;
            ushort bedrock = caves.BedrockVoxel;
            if (!caves.Enabled || bedrock == 0) return;

            for (int x = 0; x < ChunkWidth; x++)
            for (int z = 0; z < ChunkDepth; z++){
                int wx = origin.x + x, wz = origin.z + z;
                vox[ChunkSize.Flatten(x, 0, z)] = bedrock;
                if (Hash.Hash01(wx, 1, wz, cfg.Seed, HashSalts.BedrockLayer1) < caves.BedrockChance1)
                    vox[ChunkSize.Flatten(x, 1, z)] = bedrock;
                if (Hash.Hash01(wx, 2, wz, cfg.Seed, HashSalts.BedrockLayer2) < caves.BedrockChance2)
                    vox[ChunkSize.Flatten(x, 2, z)] = bedrock;
            }
        }
    }
}
