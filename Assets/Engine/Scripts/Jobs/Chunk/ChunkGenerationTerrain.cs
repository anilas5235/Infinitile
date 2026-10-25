using System.Runtime.CompilerServices;
using Engine.Scripts.Utils.Extensions;
using Engine.Scripts.VoxelConfig.Data.Generation;
using Engine.Scripts.WorldGen.Blending;
using Engine.Scripts.WorldGen.Core;
using Engine.Scripts.WorldGen.Flora;
using Engine.Scripts.WorldGen.Settings;
using Unity.Collections;
using Unity.Mathematics;
using static Engine.Scripts.Utils.VoxelConstants;

namespace Engine.Scripts.Jobs.Chunk{
    /// <summary>
    ///     Provides Burst-compiled helpers to prepare biome-aware terrain metadata and fill voxel buffers
    ///     for a single chunk from the world sampler and the generator configuration.
    /// </summary>
    internal partial struct ChunkJob{
        /// <summary>
        ///     Computes climate values, terrain height and biome information for every column of a chunk: one sampler
        ///     decision per column, independent of the chunk origin, so neighbouring chunks agree on every border
        ///     column. The blocks come from the surface-source biome; a column without an active biome (-1) gets air.
        ///     Both architectures (Terrain-first, Biome-first) run through here. This overload discards the flora grid
        ///     and includes the flora decisions of <see cref="DecideFlora" /> and the snow, strata and ice decisions of
        ///     <see cref="ApplySurfaceCover" />.
        /// </summary>
        /// <param name="config">Generator configuration providing the world-gen config and voxel IDs.</param>
        /// <param name="chunkWordPos">World-space origin (in voxels) of the chunk.</param>
        /// <param name="chunkColumns">Output array that will receive per-column height, biome and climate data.</param>
        public static void PrepareChunkMaps(ref GeneratorConfig config, ref int3 chunkWordPos,
            NativeArray<ChunkColumn> chunkColumns){
            FloraGrid grid = new(config.WorldGen.FloraPadding, Allocator.Temp);
            PrepareChunkMaps(ref config, ref chunkWordPos, chunkColumns, ref grid);
            DecideFlora(ref grid, chunkColumns, chunkWordPos, config.WorldGen);
            ApplySurfaceCover(chunkColumns, chunkWordPos, config.WorldGen, grid.Cache);
            grid.Dispose();
        }

        /// <summary>
        ///     <see cref="PrepareChunkMaps(ref GeneratorConfig,ref int3,NativeArray{ChunkColumn})" /> that also fills
        ///     <paramref name="grid" /> (built with <c>config.WorldGen.FloraPadding</c>) for
        ///     <see cref="PlaceVegetation" />: the core sample (<see cref="WorldSampler.SampleCore" />) of every chunk
        ///     column, plus that of every ring column that may be a multi-block candidate, and their rule rolls. The
        ///     caller runs <see cref="DecideFlora" /> next, which completes every column to
        ///     <see cref="WorldSampler.SampleColumn(int,int,in WorldGenConfig)" />, then <see cref="ApplySurfaceCover" />
        ///     for snow, strata and ice. The grid keeps the blend cache for the flora pass.
        /// </summary>
        public static void PrepareChunkMaps(ref GeneratorConfig config, ref int3 chunkWordPos,
            NativeArray<ChunkColumn> chunkColumns, ref FloraGrid grid){
            ref WorldGenConfig cfg = ref config.WorldGen;
            int padding = grid.Padding;
            // Spatial blend methods classify a padded, world-aligned lattice around the chunk once and then convolve;
            // the cache equals the pure per-tap evaluation bit for bit. It also covers the flora ring, whose few
            // samples would otherwise evaluate every tap directly. Temp, owned by the grid.
            int2 minColumn = chunkWordPos.xz;
            int2 maxColumn = minColumn + new int2(ChunkWidth - 1, ChunkDepth - 1);
            grid.Cache = BlendCache.Build(cfg, minColumn - padding, maxColumn + padding, 1, Allocator.Temp);
            ref BlendCache cache = ref grid.Cache;

            for (int x = 0; x < ChunkWidth; x++)
            for (int z = 0; z < ChunkDepth; z++){
                int i = GetColumnIdx(x, z, ChunkDepth);
                int wx = chunkWordPos.x + x, wz = chunkWordPos.z + z;
                WorldSampler.SampleCore(wx, wz, cfg, cache, out ColumnSample s);

                ChunkColumn col = new(){
                    Height = math.clamp(s.ColumnHeight, 0, MaxTerrainHeight),
                    HeightF = s.Height,
                    Biome = (ushort)math.max(0, s.HardBiome),
                    Temperature = s.Fields.T,
                    Humidity = s.Fields.F,
                    SurfaceSource = s.SurfaceSource
                };
                if (s.SurfaceSource >= 0 && s.SurfaceSource < cfg.BiomeCount){
                    Biome.BiomeDef def = cfg.Biomes[s.SurfaceSource];
                    col.TopBlock = TopBlockOf(def, col.Height, cfg.WaterLevel);
                    col.UnderBlock = def.underBlock;
                    col.StoneBlock = def.stoneBlock;
                    col.StrataBlock = def.strataBlock;
                }

                int g = grid.Index(x, z);
                grid.Rule[g] = FloraPlacement.RollRule(wx, wz, cfg, cache, s, out col.FloraSource);
                grid.Height[g] = col.Height;
                chunkColumns[i] = col;
            }

            if (cfg.HasFlora){
                // Ring columns matter only as multi-block candidates; the pre-filter skips most samples.
                for (int x = -padding; x < ChunkWidth + padding; x++)
                for (int z = -padding; z < ChunkDepth + padding; z++){
                    if (x >= 0 && x < ChunkWidth && z >= 0 && z < ChunkDepth) continue;
                    int g = grid.Index(x, z);
                    int wx = chunkWordPos.x + x, wz = chunkWordPos.z + z;
                    grid.Rule[g] = -1;
                    if (!FloraPlacement.MayBeMultiBlock(wx, wz, cfg)) continue;
                    WorldSampler.SampleCore(wx, wz, cfg, cache, out ColumnSample s);
                    int rule = FloraPlacement.RollRule(wx, wz, cfg, cache, s, out int _);
                    if (!FloraPlacement.IsMultiBlock(rule, cfg)) continue;
                    grid.Rule[g] = rule;
                    grid.Height[g] = math.clamp(s.ColumnHeight, 0, MaxTerrainHeight);
                }
            }
        }

        /// <summary>
        ///     The flora decisions on the grid of <see cref="PrepareChunkMaps(ref GeneratorConfig,ref int3,NativeArray{ChunkColumn},ref FloraGrid)" />,
        ///     the same as <see cref="FloraPlacement.Decide" />: the spacing radius of every multi-block candidate, the
        ///     anchors within reach of the chunk (<see cref="FloraGrid.Anchor" />, including the flat-footprint test of
        ///     wide, leaning and lying trunks), then species and source of every chunk column. A separate pass: code added
        ///     to <c>PrepareChunkMaps</c> slows the Burst build of its sampling loops.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void DecideFlora(ref FloraGrid grid, NativeArray<ChunkColumn> chunkColumns, int3 chunkWordPos,
            in WorldGenConfig cfg){
            if (cfg.HasFlora){
                int padding = grid.Padding;
                for (int x = -padding; x < ChunkWidth + padding; x++)
                for (int z = -padding; z < ChunkDepth + padding; z++){
                    int g = grid.Index(x, z);
                    int rule = grid.Rule[g];
                    grid.Radius[g] = FloraPlacement.IsMultiBlock(rule, cfg)
                        ? (byte)math.min(FloraPlacement.SpacingRadius(chunkWordPos.x + x, chunkWordPos.z + z, rule, cfg), 255)
                        : (byte)0;
                }

                // Anchors within reach of the chunk; every candidate they may conflict with lies inside the grid.
                int reach = cfg.FloraReach;
                for (int x = -reach; x < ChunkWidth + reach; x++)
                for (int z = -reach; z < ChunkDepth + reach; z++){
                    int g = grid.Index(x, z);
                    if (!IsGridAnchor(grid, x, z, chunkWordPos.xz, cfg)) continue;
                    grid.Anchor[g] = AnchorFit(grid, chunkColumns, x, z, chunkWordPos, cfg);
                }
            }

            // Species and source at single-block plants and anchors, (0, -1) elsewhere, as the sampler reports.
            for (int x = 0; x < ChunkWidth; x++)
            for (int z = 0; z < ChunkDepth; z++){
                int i = GetColumnIdx(x, z, ChunkDepth);
                int g = grid.Index(x, z);
                int rule = grid.Rule[g];
                ChunkColumn col = chunkColumns[i];
                if (rule >= 0 && (!FloraPlacement.IsMultiBlock(rule, cfg) || grid.Anchor[g] != 0))
                    col.FloraSpecies = cfg.FloraRules[rule].Species;
                else{
                    col.FloraSpecies = 0;
                    col.FloraSource = -1;
                }

                chunkColumns[i] = col;
            }
        }

        /// <summary>
        ///     Anchor test on the grid: column (x, z) is a multi-block candidate and no candidate it conflicts with
        ///     (<see cref="FloraPlacement.Conflict" />) outranks it (<see cref="FloraPlacement.Outranks" />).
        /// </summary>
        private static bool IsGridAnchor(in FloraGrid grid, int x, int z, int2 origin, in WorldGenConfig cfg){
            int g = grid.Index(x, z);
            if (!FloraPlacement.IsMultiBlock(grid.Rule[g], cfg)) return false;
            int own = grid.Radius[g];
            int r = cfg.FloraSpacingMax;
            int2 self = origin + new int2(x, z);
            uint priority = FloraPlacement.Priority(self.x, self.y, cfg);
            for (int dx = -r; dx <= r; dx++)
            for (int dz = -r; dz <= r; dz++){
                if (!FloraPlacement.InSpacingDisc(dx, dz, r)) continue;
                int n = grid.Index(x + dx, z + dz);
                if (!FloraPlacement.IsMultiBlock(grid.Rule[n], cfg)) continue;
                if (!FloraPlacement.Conflict(dx * dx + dz * dz, own, grid.Radius[n])) continue;
                int2 other = self + new int2(dx, dz);
                if (FloraPlacement.Outranks(other, FloraPlacement.Priority(other.x, other.y, cfg), self, priority)) return false;
            }

            return true;
        }

        /// <summary>
        ///     <see cref="FloraGrid.Anchor" /> code of the anchor at (x, z): 2 when it is a tree whose wide or leaning
        ///     trunk stands on ground that is not flat within 1 block (<see cref="TreeShape.Footprint" />), so it is
        ///     built 1 wide and straight; for a lying trunk its <see cref="FloraPlacement.LyingFit" />; 1 otherwise.
        ///     The heights are the sampler's, so every chunk decides the same.
        /// </summary>
        private static byte AnchorFit(in FloraGrid grid, NativeArray<ChunkColumn> chunkColumns, int x, int z, int3 chunkWordPos,
            in WorldGenConfig cfg){
            int rule = grid.Rule[grid.Index(x, z)];
            int variant = FloraPlacement.Variant(chunkWordPos.x + x, chunkWordPos.z + z, rule, cfg);
            if (variant < 0) return 1;
            TreeRoll roll = TreeShape.Roll(chunkWordPos.x + x, chunkWordPos.z + z, cfg.Seed, variant, cfg.TreeVariants[variant]);
            if (!roll.NeedsFlatFootprint) return 1;
            if (roll.Lying) return (byte)FloraPlacement.LyingFit(chunkWordPos.x + x, chunkWordPos.z + z, roll, cfg, grid.Cache);
            int4 box = TreeShape.Footprint(roll);
            int min = int.MaxValue, max = int.MinValue;
            for (int fx = x + box.x; fx <= x + box.z; fx++)
            for (int fz = z + box.y; fz <= z + box.w; fz++){
                int h;
                if (fx >= 0 && fx < ChunkWidth && fz >= 0 && fz < ChunkDepth)
                    h = chunkColumns[GetColumnIdx(fx, fz, ChunkDepth)].Height;
                else{
                    WorldSampler.SampleCore(chunkWordPos.x + fx, chunkWordPos.z + fz, cfg, grid.Cache, out ColumnSample s);
                    h = math.clamp(s.ColumnHeight, 0, MaxTerrainHeight);
                }

                min = math.min(min, h);
                max = math.max(max, h);
            }

            return max - min <= 1 ? (byte)1 : (byte)2;
        }

        /// <summary>
        ///     Beach and snow top blocks, strata shift and frozen water surface of every chunk column, from the sampled
        ///     temperature, height and surface biome. Called after <see cref="PrepareChunkMaps(ref GeneratorConfig,ref int3,NativeArray{ChunkColumn},ref FloraGrid)" />
        ///     rather than from it: inside it, even as a separate loop, this code made Burst compile the sampling slower
        ///     (Terrain-first DistanceBlend about a third slower).
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void ApplySurfaceCover(NativeArray<ChunkColumn> chunkColumns, int3 chunkWordPos,
            in WorldGenConfig cfg, in BlendCache cache){
            for (int x = 0; x < ChunkWidth; x++)
            for (int z = 0; z < ChunkDepth; z++){
                int i = GetColumnIdx(x, z, ChunkDepth);
                int wx = chunkWordPos.x + x, wz = chunkWordPos.z + z;
                ChunkColumn col = chunkColumns[i];
                // Snow line and ice edge of the column share one evaluation of the freeze noise.
                float freezeNoise = cfg.EnableClimateOverrides ? SurfaceCover.FreezeNoise(wx, wz, cfg) : 0f;
                if (col.SurfaceSource >= 0 && col.SurfaceSource < cfg.BiomeCount){
                    Biome.BiomeDef def = cfg.Biomes[col.SurfaceSource];
                    // Snow covers a beach like any dry column; the beach block stays under it.
                    if (def.beachBlock != 0 && SurfaceCover.IsBeach(wx, wz, col.Height, def, cfg, cache)){
                        col.TopBlock = def.beachBlock;
                        col.BeachBlock = def.beachBlock;
                    }

                    if (SurfaceCover.IsSnowCovered(col.Temperature, col.Height, freezeNoise, def, cfg))
                        col.TopBlock = def.snowTopBlock;
                    if (def.strataBlock != 0) col.StrataShift = SurfaceCover.StrataShift(wx, wz, cfg);
                }

                col.Frozen = SurfaceCover.WaterFreezes(col.Temperature, freezeNoise, cfg) ? (byte)1 : (byte)0;
                chunkColumns[i] = col;
            }
        }

        /// <summary>
        ///     Top block of a column whose top block sits at <paramref name="height" />: the biome's underwater block when
        ///     the voxel above it is water, otherwise its top block. Water fills y &lt; <paramref name="waterLevel" />, so a
        ///     top block at <c>waterLevel - 1</c> touches the surface and stays dry.
        /// </summary>
        public static ushort TopBlockOf(in Biome.BiomeDef def, int height, int waterLevel){
            bool submerged = height + 1 < waterLevel;
            return submerged && def.underwaterTopBlock != 0 ? def.underwaterTopBlock : def.topBlock;
        }

        /// <summary>
        ///     Fills the voxel buffer for a chunk with terrain blocks based on the prepared column data
        ///     and configuration values.
        /// </summary>
        /// <param name="vox">Voxel buffer to write to (one entry per voxel).</param>
        /// <param name="chunkColumns">Per-column terrain metadata produced by <see cref="PrepareChunkMaps" />.</param>
        /// <param name="config">Generator configuration providing voxel IDs for stone, dirt, grass, etc.</param>
        public static void FillTerrain(NativeArray<ushort> vox, NativeArray<ChunkColumn> chunkColumns,
            ref GeneratorConfig config){
            const ushort air = 0;
            ushort waterBlock = config.Voxels["std:Water"].Id;
            ushort ice = config.Voxels["std:Ice"].Id;
            int waterLevel = config.WorldGen.WaterLevel;
            int strataPeriod = config.WorldGen.StrataPeriod;
            int strataThickness = config.WorldGen.StrataThickness;

            for (int x = 0; x < ChunkWidth; x++)
            for (int z = 0; z < ChunkDepth; z++){
                int i = GetColumnIdx(x, z, ChunkDepth);

                ChunkColumn col = chunkColumns[i];

                ushort st = col.StoneBlock;
                ushort under = col.UnderBlock;
                ushort top = col.TopBlock;
                // A beach replaces the two blocks under the top as well.
                ushort upperUnder = col.BeachBlock != 0 ? col.BeachBlock : under;

                int gy = col.Height;
                bool strata = col.StrataBlock != 0 && strataPeriod > 0 && strataThickness > 0;

                for (int y = 0; y < ChunkHeight; y++){
                    ushort v;
                    if (y < gy - 4) v = st;
                    else if (y < gy) v = y >= gy - 2 ? upperUnder : under;
                    // The column's top block sits at h_int; under water it is already the biome's underwater block.
                    else if (y == gy) v = top;
                    else v = y < waterLevel ? waterBlock : air;

                    // Strata bands replace the stone block wherever it lies, top and under included.
                    if (strata && v == st && SurfaceCover.InStrataBand(y, col.StrataShift, strataPeriod, strataThickness))
                        v = col.StrataBlock;

                    vox[ChunkSize.Flatten(x, y, z)] = v;
                }

                // The water surface is the topmost water voxel at waterLevel - 1 (y == waterLevel is air); it is water
                // exactly when the top block lies below it.
                int surface = waterLevel - 1;
                if (col.Frozen != 0 && surface > gy && surface < ChunkHeight) vox[ChunkSize.Flatten(x, surface, z)] = ice;
            }
        }
    }
}
