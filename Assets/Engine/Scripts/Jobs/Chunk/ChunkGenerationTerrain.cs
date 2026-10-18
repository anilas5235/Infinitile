using Engine.Scripts.Utils.Extensions;
using Engine.Scripts.VoxelConfig.Data.Generation;
using Engine.Scripts.WorldGen.Blending;
using Engine.Scripts.WorldGen.Core;
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
        ///     (INT-10/11): one <see cref="WorldSampler.SampleColumn" /> call per column, independent of the chunk
        ///     origin, so neighbouring chunks agree on every border column (TST-03). The blocks come from the
        ///     surface-source biome, the dithered pick from the material weights (§9.6, INT-11); a column without an
        ///     active biome (-1) gets air. Both architectures
        ///     (Terrain-first, Biome-first) run through here; the legacy generator was removed in Phase 2 (INT-40).
        /// </summary>
        /// <param name="randomSeed">World seed (informational; the sampler takes the seed from the config).</param>
        /// <param name="config">Generator configuration providing the spec config and voxel IDs.</param>
        /// <param name="chunkWordPos">World-space origin (in voxels) of the chunk.</param>
        /// <param name="chunkColumns">Output array that will receive per-column height, biome and climate data.</param>
        public static void PrepareChunkMaps(int randomSeed, ref GeneratorConfig config, ref int3 chunkWordPos,
            NativeArray<ChunkColumn> chunkColumns){
            // BLD-11: spatial blend methods classify a padded, world-aligned lattice around the chunk once and then
            // convolve; the cache equals the pure per-tap evaluation bit for bit (GEN-02, TST-01). INT-12: Temp,
            // disposed below.
            int2 minColumn = chunkWordPos.xz;
            int2 maxColumn = minColumn + new int2(ChunkWidth - 1, ChunkDepth - 1);
            BlendCache cache = BlendCache.Build(config.WorldGen, minColumn, maxColumn, 1, Allocator.Temp);

            for (int x = 0; x < ChunkWidth; x++)
            for (int z = 0; z < ChunkDepth; z++){
                int i = GetColumnIdx(x, z, ChunkDepth);
                WorldSampler.SampleColumn(chunkWordPos.x + x, chunkWordPos.z + z, config.WorldGen, cache, out ColumnSample s);

                ChunkColumn col = new(){
                    Height = math.clamp(s.ColumnHeight, 0, MaxTerrainHeight),
                    HeightF = s.Height,
                    Biome = (ushort)math.max(0, s.HardBiome),
                    Temperature = s.Fields.T,
                    Humidity = s.Fields.F,
                    SurfaceSource = s.SurfaceSource,
                    FloraSpecies = s.FloraSpecies,
                    FloraSource = s.FloraSource
                };
                if (s.SurfaceSource >= 0 && s.SurfaceSource < config.WorldGen.BiomeCount){
                    Biome.BiomeDef def = config.WorldGen.Biomes[s.SurfaceSource];
                    col.TopBlock = def.topBlock;
                    col.UnderBlock = def.underBlock;
                    col.StoneBlock = def.stoneBlock;
                }

                chunkColumns[i] = col;
            }

            cache.Dispose();
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
