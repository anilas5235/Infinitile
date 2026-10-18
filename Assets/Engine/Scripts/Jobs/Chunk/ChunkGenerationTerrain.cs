using Engine.Scripts.Utils.Extensions;
using Engine.Scripts.VoxelConfig.Data.Generation;
using Engine.Scripts.WorldGen.Core;
using Engine.Scripts.WorldGen.Settings;
using Unity.Collections;
using Unity.Mathematics;
using static Engine.Scripts.Jobs.Chunk.NoiseCalculator;
using static Engine.Scripts.Utils.VoxelConstants;

namespace Engine.Scripts.Jobs.Chunk{
    /// <summary>
    ///     Provides Burst-compiled helpers to prepare biome-aware terrain metadata and fill voxel buffers
    ///     for a single chunk based on noise, configuration and climate.
    /// </summary>
    internal partial struct ChunkJob{
        private const float BiomeScale = 0.0012f;

        /// <summary>
        ///     Computes climate values, terrain height and biome information for every column of a chunk.
        ///     With a spec architecture (Terrain-first or Biome-first, INT-10) every column comes from
        ///     <see cref="WorldSampler.SampleColumn" />; otherwise the legacy noise generator runs (INT-40).
        /// </summary>
        /// <param name="randomSeed">Random seed used to offset noise sampling for deterministic variation.</param>
        /// <param name="config">Generator configuration providing water level, voxel IDs and the spec config.</param>
        /// <param name="chunkWordPos">World-space origin (in voxels) of the chunk.</param>
        /// <param name="chunkColumns">Output array that will receive per-column height, biome and climate data.</param>
        public static void PrepareChunkMaps(int randomSeed, ref GeneratorConfig config, ref int3 chunkWordPos,
            NativeArray<ChunkColumn> chunkColumns){
            if (UsesSpecSampler(ref config)){
                PrepareChunkMapsSpec(ref config, ref chunkWordPos, chunkColumns);
                return;
            }

            for (int x = 0; x < ChunkWidth; x++)
            for (int z = 0; z < ChunkDepth; z++){
                int i = GetColumnIdx(x, z, ChunkDepth);
                float2 worldPos = new(chunkWordPos.x + x, chunkWordPos.z + z);

                WorldNoiseOutput worldNoise = WorldNoise(worldPos, ref config.NoiseParams);

                int height = math.clamp(
                    MinTerrainHeight + (int)(worldNoise.Elevation * TerrainHeightRange),
                    0, MaxTerrainHeight);

                BiomeCalculator.BiomSectionInput input = worldNoise.BiomSectionInput();
                ChunkColumn col = new(){
                    Height = height,
                    HeightF = height,
                    Biome = BiomeCalculator.SelectBiome(ref input, ref config),
                    Temperature = worldNoise.Temperature,
                    Humidity = worldNoise.Humidity,
                    FloraSource = -1
                };
                col.SurfaceSource = col.Biome;
                uint seed = (uint)((chunkWordPos.x + x) ^ (chunkWordPos.z + z) ^ randomSeed ^ 0x85ebca6b);
                Random rng = new(seed == 0 ? 1u : seed);
                SelectSurfaceMaterials(ref config, ref col, ref rng);
                chunkColumns[i] = col;
            }
        }

        /// <summary>
        ///     INT-40: the spec sampler runs when a constructed <see cref="WorldGenConfig" /> selects a spec
        ///     architecture; <see cref="GenArchitecture.Legacy" /> (and the empty config) keeps the old generator.
        /// </summary>
        private static bool UsesSpecSampler(ref GeneratorConfig config){
            return config.WorldGen.IsCreated && config.WorldGen.Architecture != GenArchitecture.Legacy;
        }

        /// <summary>
        ///     INT-10/11: one <see cref="WorldSampler.SampleColumn" /> call per column, independent of the chunk
        ///     origin, so neighbouring chunks agree on every border column (TST-03). The blocks come from the
        ///     surface-source biome (§9.6); a column without an active biome (-1) gets air.
        /// </summary>
        private static void PrepareChunkMapsSpec(ref GeneratorConfig config, ref int3 chunkWordPos,
            NativeArray<ChunkColumn> chunkColumns){
            for (int x = 0; x < ChunkWidth; x++)
            for (int z = 0; z < ChunkDepth; z++){
                int i = GetColumnIdx(x, z, ChunkDepth);
                WorldSampler.SampleColumn(chunkWordPos.x + x, chunkWordPos.z + z, config.WorldGen, out ColumnSample s);

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
            // GEN-14: the ice override is a climate override; the spec config can switch it off (legacy: always on).
            bool iceOverride = !config.WorldGen.IsCreated || config.WorldGen.EnableClimateOverrides;

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
                    else if (y == gy) v = gy < waterLevel ? waterBlock : top;
                    else v = y < waterLevel ? waterBlock : air;

                    // F-07: the water surface is the topmost water voxel at waterLevel - 1 (y == waterLevel is air).
                    if (iceOverride && y == waterLevel - 1 && v == waterBlock && col.Temperature < .2f) v = ice;

                    vox[ChunkSize.Flatten(x, y, z)] = v;
                }
            }
        }

        private static void SelectSurfaceMaterials(ref GeneratorConfig config, ref ChunkColumn col, ref Random rng){
            Biome.BiomeDef biomDef = config.BiomeDefs[col.Biome];

            col.TopBlock = biomDef.topBlock;
            col.UnderBlock = biomDef.underBlock;
            col.StoneBlock = biomDef.stoneBlock;
        }
    }
}
