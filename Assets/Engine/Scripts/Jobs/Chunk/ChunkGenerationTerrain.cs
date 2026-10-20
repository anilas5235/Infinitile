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
    ///     for a single chunk from the spec sampler and the generator configuration.
    /// </summary>
    internal partial struct ChunkJob{
        /// <summary>
        ///     Computes climate values, terrain height and biome information for every column of a chunk
        ///     (INT-10/11): one sampler decision per column, independent of the chunk origin, so neighbouring chunks
        ///     agree on every border column (TST-03). The blocks come from the surface-source biome, the dithered pick
        ///     from the material weights (§9.6, INT-11); a column without an active biome (-1) gets air. Both
        ///     architectures (Terrain-first, Biome-first) run through here; the legacy generator was removed in
        ///     Phase 2 (INT-40). This overload discards the flora grid.
        /// </summary>
        /// <param name="randomSeed">World seed (informational; the sampler takes the seed from the config).</param>
        /// <param name="config">Generator configuration providing the spec config and voxel IDs.</param>
        /// <param name="chunkWordPos">World-space origin (in voxels) of the chunk.</param>
        /// <param name="chunkColumns">Output array that will receive per-column height, biome and climate data.</param>
        public static void PrepareChunkMaps(int randomSeed, ref GeneratorConfig config, ref int3 chunkWordPos,
            NativeArray<ChunkColumn> chunkColumns){
            FloraGrid grid = new(config.WorldGen.FloraPadding, Allocator.Temp);
            PrepareChunkMaps(randomSeed, ref config, ref chunkWordPos, chunkColumns, ref grid);
            grid.Dispose();
        }

        /// <summary>
        ///     <see cref="PrepareChunkMaps(int,ref GeneratorConfig,ref int3,NativeArray{ChunkColumn})" /> that also fills
        ///     <paramref name="grid" /> (built with <c>config.WorldGen.FloraPadding</c>) for
        ///     <see cref="PlaceVegetation" />. Every column equals <see cref="WorldSampler.SampleColumn(int,int,in WorldGenConfig)" />
        ///     (VEG-01, VEG-06): the core sample (<see cref="WorldSampler.SampleCore" />) of every chunk column, plus that
        ///     of every ring column that may be a multi-block candidate, then the rule roll and anchor order of
        ///     <see cref="FloraPlacement.Decide" />, read from the grid instead of re-sampled (VEG-05).
        /// </summary>
        public static void PrepareChunkMaps(int randomSeed, ref GeneratorConfig config, ref int3 chunkWordPos,
            NativeArray<ChunkColumn> chunkColumns, ref FloraGrid grid){
            ref WorldGenConfig cfg = ref config.WorldGen;
            int padding = grid.Padding;
            // BLD-11: spatial blend methods classify a padded, world-aligned lattice around the chunk once and then
            // convolve; the cache equals the pure per-tap evaluation bit for bit (GEN-02, TST-01). It also covers the
            // flora ring (VEG-05), whose few samples would otherwise evaluate every tap directly. INT-12: Temp,
            // disposed below.
            int2 minColumn = chunkWordPos.xz;
            int2 maxColumn = minColumn + new int2(ChunkWidth - 1, ChunkDepth - 1);
            BlendCache cache = BlendCache.Build(cfg, minColumn - padding, maxColumn + padding, 1, Allocator.Temp);

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
                    col.TopBlock = def.topBlock;
                    col.UnderBlock = def.underBlock;
                    col.StoneBlock = def.stoneBlock;
                }

                int g = grid.Index(x, z);
                grid.Rule[g] = FloraPlacement.RollRule(wx, wz, cfg, s, out col.FloraSource);
                grid.Height[g] = col.Height;
                chunkColumns[i] = col;
            }

            if (cfg.HasFlora){
                // VEG-05: ring columns matter only as multi-block candidates; the pre-filter skips most samples.
                for (int x = -padding; x < ChunkWidth + padding; x++)
                for (int z = -padding; z < ChunkDepth + padding; z++){
                    if (x >= 0 && x < ChunkWidth && z >= 0 && z < ChunkDepth) continue;
                    int g = grid.Index(x, z);
                    int wx = chunkWordPos.x + x, wz = chunkWordPos.z + z;
                    grid.Rule[g] = -1;
                    if (!FloraPlacement.MayBeMultiBlock(wx, wz, cfg)) continue;
                    WorldSampler.SampleCore(wx, wz, cfg, cache, out ColumnSample s);
                    int rule = FloraPlacement.RollRule(wx, wz, cfg, s, out int _);
                    if (!FloraPlacement.IsMultiBlock(rule, cfg)) continue;
                    grid.Rule[g] = rule;
                    grid.Height[g] = math.clamp(s.ColumnHeight, 0, MaxTerrainHeight);
                }

                // Anchors within reach of the chunk; their r_tree discs lie inside the grid.
                int reach = cfg.FloraReach;
                int r = cfg.TreeSpacingRadius;
                for (int x = -reach; x < ChunkWidth + reach; x++)
                for (int z = -reach; z < ChunkDepth + reach; z++){
                    int g = grid.Index(x, z);
                    grid.Anchor[g] = IsGridAnchor(grid, x, z, chunkWordPos.xz, r, cfg) ? (byte)1 : (byte)0;
                }
            }

            // VEG-06: species and source at single-block plants and anchors, (0, -1) elsewhere, as the sampler reports.
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

            cache.Dispose();
        }

        /// <summary>
        ///     VEG-05 on the grid: column (x, z) is a multi-block candidate and no candidate in its r_tree disc outranks
        ///     it (<see cref="FloraPlacement.Outranks" />).
        /// </summary>
        private static bool IsGridAnchor(in FloraGrid grid, int x, int z, int2 origin, int r, in WorldGenConfig cfg){
            if (!FloraPlacement.IsMultiBlock(grid.Rule[grid.Index(x, z)], cfg)) return false;
            int2 self = origin + new int2(x, z);
            uint own = FloraPlacement.Priority(self.x, self.y, cfg);
            for (int dx = -r; dx <= r; dx++)
            for (int dz = -r; dz <= r; dz++){
                if (!FloraPlacement.InSpacingDisc(dx, dz, r)) continue;
                if (!FloraPlacement.IsMultiBlock(grid.Rule[grid.Index(x + dx, z + dz)], cfg)) continue;
                int2 other = self + new int2(dx, dz);
                if (FloraPlacement.Outranks(other, FloraPlacement.Priority(other.x, other.y, cfg), self, own)) return false;
            }

            return true;
        }

        /// <summary>
        ///     Fills the voxel buffer for a chunk with terrain blocks based on the prepared column data
        ///     and configuration values.
        /// </summary>
        /// <param name="vox">Voxel buffer to write to (one entry per voxel).</param>
        /// <param name="waterLevel">Global water level used to place water or surface blocks.</param>
        /// <param name="chunkColumns">Per-column terrain metadata produced by <see cref="PrepareChunkMaps" />.</param>
        /// <param name="config">Generator configuration providing voxel IDs for stone, dirt, grass, etc.</param>
        public static void FillTerrain(NativeArray<ushort> vox, NativeArray<ChunkColumn> chunkColumns,
            ref GeneratorConfig config){
            const ushort air = 0;
            ushort waterBlock = config.Voxels["std:Water"].Id;
            ushort ice = config.Voxels["std:Ice"].Id;
            int waterLevel = config.WaterLevel;
            // GEN-14 / D-06: the ice override is a climate override that the settings can switch off.
            bool iceOverride = config.WorldGen.EnableClimateOverrides;

            for (int x = 0; x < ChunkWidth; x++)
            for (int z = 0; z < ChunkDepth; z++){
                int i = GetColumnIdx(x, z, ChunkDepth);

                ChunkColumn col = chunkColumns[i];

                ushort st = col.StoneBlock;
                ushort under = col.UnderBlock;
                ushort top = col.TopBlock;


                int gy = col.Height;

                for (int y = 0; y < ChunkHeight; y++){
                    ushort v;
                    if (y < gy - 4) v = st;
                    else if (y < gy) v = under;
                    // GEN-14: the top block sits at h_int even under water, so Ocean floors are sand (GEN-25).
                    else if (y == gy) v = top;
                    else v = y < waterLevel ? waterBlock : air;

                    // F-07: the water surface is the topmost water voxel at waterLevel - 1 (y == waterLevel is air).
                    if (iceOverride && y == waterLevel - 1 && v == waterBlock && col.Temperature < .2f) v = ice;

                    vox[ChunkSize.Flatten(x, y, z)] = v;
                }
            }
        }
    }
}
