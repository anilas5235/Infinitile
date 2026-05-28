using Engine.Scripts.Utils.Extensions;
using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;
using static Engine.Scripts.Utils.VoxelConstants;
using Random = Unity.Mathematics.Random;

namespace Engine.Scripts.Jobs.Chunk
{
    /// <summary>
    ///     Provides Burst-compiled helpers to place rare world structures such as pyramids, oases,
    ///     igloos, shipwrecks and mineshafts on or within generated terrain.
    /// </summary>
    internal partial struct ChunkJob
    {
        /// <summary>
        ///     Places biome-dependent structures into the voxel buffer using per-column metadata.
        ///     Structures are rare and are positioned deterministically based on chunk position and seed.
        /// </summary>
        /// <param name="vox">Voxel buffer to modify with structure blocks.</param>
        /// <param name="chunkColumns">Per-column data including biome and terrain height.</param>
        /// <param name="chunkWordPos">World-space origin (in voxels) of the chunk.</param>
        /// <param name="randomSeed">Global seed used to derive the per-chunk random stream.</param>
        /// <param name="config">Generator configuration providing voxel IDs for structure materials.</param>
        [BurstCompile]
        public static void PlaceStructures(ref NativeArray<ushort> vox,
            ref NativeArray<ChunkColumn> chunkColumns, ref int3 chunkWordPos,
            int randomSeed, ref GeneratorConfig config)
        {
            uint seed = (uint)((chunkWordPos.x * 43524) ^ (chunkWordPos.z * 7856) ^ randomSeed ^ 0x85ebca6b);
            Random rng = new(seed == 0 ? 1u : seed);

            for (int x = 1; x < ChunkWidth - 1; x++)
            for (int z = 1; z < ChunkDepth - 1; z++)
            {
                int gi = GetColumnIdx(x, z, ChunkDepth);
                ChunkColumn chunkCol = chunkColumns[gi];
                int gy = chunkCol.Height;
                if (gy <= 0 || gy >= ChunkHeight - 2) continue;

                Biome biome = chunkCol.Biome;
                ushort surface = vox[ChunkSize.Flatten(x, gy, z)];
                if (surface == 0) continue;

                switch (biome)
                {
                    case Biome.Desert:
                        if (rng.NextFloat() < 0.00005f)
                            PlacePyramid(ref vox, x, gy + 1, z, 5, config.SandStoneRed);
                        else if (rng.NextFloat() < 0.000002f) PlaceOasis(ref vox, x, gy, z, ref config, ref rng);

                        break;
                    case Biome.Ice:
                        if (rng.NextFloat() < 0.0007f) PlaceIgloo(ref vox, x, gy + 1, z, ref config);

                        break;
                    case Biome.Ocean:
                        if (surface == config.Water && rng.NextFloat() < 0.00001f)
                            PlaceShipwreck(ref vox, x, config.WaterLevel - 1, z, ref config);

                        break;
                }

                if (biome is Biome.Plains or Biome.Forest or Biome.Mountain or Biome.HighStone &&
                    rng.NextFloat() < 0.0009f)
                    PlaceMineShaft(ref vox, x, gy, z, ref config, rng.NextInt(6, 20));
            }
        }

        private static void PlacePyramid(ref NativeArray<ushort> vox, int cx, int cy, int cz, int radius, ushort block)
        {
            for (int layer = 0; layer <= radius; layer++)
            {
                int r = radius - layer;
                for (int ox = -r; ox <= r; ox++)
                for (int oz = -r; oz <= r; oz++)
                {
                    int x = cx + ox;
                    int y = cy + layer;
                    int z = cz + oz;
                    if (!InChunk(x, y, z)) continue;
                    vox[ChunkSize.Flatten(x, y, z)] = block;
                }
            }
        }

        private static void PlaceOasis(ref NativeArray<ushort> vox, int cx, int cy, int cz,
            ref GeneratorConfig config, ref Random rng)
        {
            ushort water = config.Water;
            ushort grass = config.Grass;
            for (int ox = -1; ox <= 1; ox++)
            for (int oz = -1; oz <= 1; oz++)
            {
                int x = cx + ox;
                int y = cy + 1;
                int z = cz + oz;
                if (!InChunk(x, y, z)) continue;
                vox[ChunkSize.Flatten(x, y, z)] = water;
                int sx0 = x + 1;
                int sx1 = x - 1;
                int sz0 = z + 1;
                int sz1 = z - 1;
                if (InChunk(sx0, y, z) &&
                    vox[ChunkSize.Flatten(sx0, y, z)] == 0)
                    vox[ChunkSize.Flatten(sx0, y, z)] = grass;
                if (InChunk(sx1, y, z) &&
                    vox[ChunkSize.Flatten(sx1, y, z)] == 0)
                    vox[ChunkSize.Flatten(sx1, y, z)] = grass;
                if (InChunk(x, y, sz0) &&
                    vox[ChunkSize.Flatten(x, y, sz0)] == 0)
                    vox[ChunkSize.Flatten(x, y, sz0)] = grass;
                if (InChunk(x, y, sz1) &&
                    vox[ChunkSize.Flatten(x, y, sz1)] == 0)
                    vox[ChunkSize.Flatten(x, y, sz1)] = grass;
            }

            PlacePalm(ref vox, cx - 2, cy + 1, cz, ref rng, ref config);
            PlacePalm(ref vox, cx + 2, cy + 1, cz, ref rng, ref config);
        }

        private static void PlaceIgloo(ref NativeArray<ushort> vox, int cx, int cy, int cz, ref GeneratorConfig config)
        {
            ushort snow = config.Snow;
            for (int ox = -1; ox <= 1; ox++)
            for (int oz = -1; oz <= 1; oz++)
            for (int oy = 0; oy <= 2; oy++)
            {
                int x = cx + ox;
                int y = cy + oy;
                int z = cz + oz;
                if (!InChunk(x, y, z)) continue;
                if ((ox == 0 && oy == 0 && oz == 0) || (oy == 1 && oz == -1) || (oy == 0 && oz == -1)) continue;
                vox[ChunkSize.Flatten(x, y, z)] = snow;
            }
        }

        private static void PlaceShipwreck(ref NativeArray<ushort> vox, int cx, int cy, int cz,
            ref GeneratorConfig config)
        {
            ushort planks = config.Planks;
            for (int ox = -2; ox <= 2; ox++)
            for (int oz = -1; oz <= 1; oz++)
            {
                int x = cx + ox;
                int y = cy + 1 + math.min(math.abs(ox), 1);
                int z = cz + oz;
                if (!InChunk(x, y, z)) continue;
                vox[ChunkSize.Flatten(x, y, z)] = planks;
            }
        }

        private static void PlaceMineShaft(ref NativeArray<ushort> vox, int cx, int groundY, int cz,
            ref GeneratorConfig config, int depth)
        {
            ushort planks = config.Planks;
            for (int y = groundY; y > math.max(1, groundY - depth); y--)
            {
                if (!InChunk(cx, y, cz)) continue;
                vox[ChunkSize.Flatten(cx, y, cz)] = 0;
                if ((groundY - y) % 3 == 0 && InChunk(cx + 1, y, cz))
                    vox[ChunkSize.Flatten(cx + 1, y, cz)] = planks;
                if ((groundY - y) % 5 == 0 && InChunk(cx - 1, y, cz))
                    vox[ChunkSize.Flatten(cx - 1, y, cz)] = planks;
            }

            if (InChunk(cx, groundY + 1, cz))
                vox[ChunkSize.Flatten(cx, groundY + 1, cz)] = 0;
        }
    }
}