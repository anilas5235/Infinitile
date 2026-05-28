using Engine.Scripts.Utils.Extensions;
using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;
using static Engine.Scripts.Utils.VoxelConstants;
using Random = Unity.Mathematics.Random;

namespace Engine.Scripts.Jobs.Chunk
{
    /// <summary>
    ///     Provides Burst-compiled helpers to place biome-dependent vegetation such as trees,
    ///     grass, cacti, crops and mushrooms on top of generated terrain.
    /// </summary>
    internal partial struct ChunkJob
    {
        /// <summary>
        ///     Places vegetation for every suitable surface column in the chunk using biome information
        ///     and a deterministic random stream derived from chunk position and global seed.
        /// </summary>
        /// <param name="vox">Voxel buffer that will receive vegetation blocks.</param>
        /// <param name="chunkColumns">Per-column data including biome, height and climate.</param>
        /// <param name="chunkWordPos">World-space origin (in voxels) of the chunk.</param>
        /// <param name="randomSeed">Global seed used to derive the per-chunk random generator.</param>
        /// <param name="config">Generator configuration providing voxel IDs for vegetation blocks.</param>
        [BurstCompile]
        public static void PlaceVegetation(ref NativeArray<ushort> vox,
            ref NativeArray<ChunkColumn> chunkColumns,
            ref int3 chunkWordPos, int randomSeed, ref GeneratorConfig config)
        {
            uint seed = (uint)((chunkWordPos.x * 73856093) ^ (chunkWordPos.z * 19349663) ^ randomSeed ^ 0x85ebca6b);
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

                TryPlaceBiomeVegetation(ref vox, ref chunkColumns, ref config, surface, x, gy, z, biome,
                    ref rng);
            }
        }

        private static void TryPlaceBiomeVegetation(ref NativeArray<ushort> vox,
            ref NativeArray<ChunkColumn> chunkColumns,
            ref GeneratorConfig config, ushort surface, int x, int gy, int z, Biome biome, ref Random rng)
        {
            if (!IsEarthLike(surface, ref config)) return;

            if (!InChunk(x, gy + 1, z) ||
                vox[ChunkSize.Flatten(x, gy + 1, z)] != 0) return;

            int oneAboveIdx = ChunkSize.Flatten(x, gy + 1, z);

            switch (biome)
            {
                case Biome.Forest:
                    if (surface != config.Grass) break;
                    if (rng.NextFloat() < 0.1f)
                    {
                        int h = rng.NextInt(5, 9);
                        ushort chosenLog = rng.NextInt(0, 100) < 75 ? config.LogOak : config.LogBirch;
                        if (CanPlaceTree(ref vox, x, gy + 1, z, config.LogOak, config.LogBirch))
                            PlaceTree(ref vox, x, gy + 1, z, h, chosenLog, ref rng, ref config);
                    }
                    else if (rng.NextFloat() < 0.12f)
                    {
                        vox[oneAboveIdx] = config.GrassF;
                    }

                    break;
                case Biome.Plains:
                    if (surface != config.Grass) break;
                    if (rng.NextFloat() < 0.015f)
                    {
                        int h = rng.NextInt(6, 10);
                        ushort chosenLog = rng.NextInt(0, 100) < 80 ? config.LogOak : config.LogBirch;
                        if (CanPlaceTree(ref vox, x, gy + 1, z, config.LogOak, config.LogBirch))
                            PlaceTree(ref vox, x, gy + 1, z, h, chosenLog, ref rng, ref config);
                    }
                    else if (rng.NextFloat() < 0.06f)
                    {
                        vox[oneAboveIdx] = config.Flowers;
                    }
                    else if (SurfaceHasNeighbor(ref vox, x, gy, z, config.Water) &&
                             rng.NextFloat() < 0.002f)
                    {
                        SpawnWheatCluster(ref vox, ref chunkColumns, ref config, ref rng, x, z);
                    }
                    else if (rng.NextFloat() < 0.05f)
                    {
                        vox[oneAboveIdx] = config.GrassF;
                    }

                    break;
                case Biome.Jungle:
                    if (surface == config.Grass && rng.NextFloat() < 0.6f)
                    {
                        int h = rng.NextInt(7, 11);
                        if (CanPlaceTree(ref vox, x, gy + 1, z, config.LogOak, config.LogBirch))
                            PlaceTree(ref vox, x, gy + 1, z, h, config.LogOak, ref rng, ref config);
                    }

                    break;
                case Biome.Desert:
                    if (surface == config.Sand && rng.NextFloat() < 0.015f)
                        PlaceColumn(ref vox, x, gy + 1, z, config.Cactus, rng.NextInt(1, 5));
                    else if ((surface == config.Sand || surface == config.Dirt) && rng.NextFloat() < 0.08f)
                        vox[oneAboveIdx] = config.GrassFDry;

                    break;
                case Biome.Swamp:
                    if (rng.NextFloat() < 0.06f)
                    {
                        int m = rng.NextInt(0, 100);
                        vox[oneAboveIdx] = m switch
                        {
                            < 50 => config.MushroomBrown,
                            < 80 => config.MushroomRed,
                            _ => config.MushroomTan
                        };
                    }

                    break;
                case Biome.HighStone:
                case Biome.GreyMountain:
                    if (rng.NextFloat() < 0.02f && surface == config.Dirt) vox[oneAboveIdx] = config.GrassF;

                    break;
                case Biome.RedDesert:
                    if (surface != config.SandRed) break;
                    if (rng.NextFloat() < 0.004f) PlaceColumn(ref vox, x, gy + 1, z, config.Cactus, rng.NextInt(1, 6));

                    break;
                case Biome.Beach:
                    if (rng.NextFloat() < 0.02f) PlacePalm(ref vox, x, gy + 1, z, ref rng, ref config);

                    break;
                case Biome.Tundra:
                    if (surface == config.DirtSnowy && rng.NextFloat() < 0.08f) vox[oneAboveIdx] = config.GrassFDead;

                    break;
            }
        }

        private static void SpawnWheatCluster(ref NativeArray<ushort> vox,
            ref NativeArray<ChunkColumn> chunkColumns,
            ref GeneratorConfig config, ref Random rng, int x, int z)
        {
            int cluster = rng.NextInt(1, 4);
            for (int wx = -cluster; wx <= cluster; wx++)
            for (int wz = -cluster; wz <= cluster; wz++)
            {
                int tx = x + wx;
                int tz = z + wz;
                if (tx < 0 || tx >= ChunkWidth || tz < 0 || tz >= ChunkDepth) continue;
                int tgi = GetColumnIdx(tx, tz, ChunkDepth);
                int tgy = chunkColumns[tgi].Height;
                if (tgy <= 0 || tgy >= ChunkHeight - 1) continue;
                int belowIdx = ChunkSize.Flatten(tx, tgy, tz);
                ushort below = vox[belowIdx];
                if (!IsEarthLike(below, ref config)) continue;
                int tIdx = ChunkSize.Flatten(tx, tgy + 1, tz);
                if (vox[tIdx] == 0)
                {
                    int stage = rng.NextInt(1, 5);
                    ushort id = stage switch
                    {
                        1 => config.WheatStage1,
                        2 => config.WheatStage2,
                        3 => config.WheatStage3,
                        _ => config.WheatStage4
                    };
                    vox[tIdx] = id;
                }
            }
        }

        private static bool SurfaceHasNeighbor(ref NativeArray<ushort> vox, int cx, int cy, int cz, ushort waterId)
        {
            int neighbors = 0;
            int3 pos = new(cx - 1, cy, cz);
            if (InChunk(ref pos))
            {
                ushort voxel = vox[ChunkSize.Flatten(pos)];
                if (voxel != 0 && voxel != waterId)
                    neighbors++;
            }

            pos = new int3(cx + 1, cy, cz);
            if (InChunk(ref pos))
            {
                ushort voxel = vox[ChunkSize.Flatten(pos)];
                if (voxel != 0 && voxel != waterId)
                    neighbors++;
            }

            pos = new int3(cx, cy, cz + 1);
            if (InChunk(ref pos))
            {
                ushort voxel = vox[ChunkSize.Flatten(pos)];
                if (voxel != 0 && voxel != waterId)
                    neighbors++;
            }

            pos = new int3(cx, cy, cz - 1);
            if (InChunk(ref pos))
            {
                ushort voxel = vox[ChunkSize.Flatten(pos)];
                if (voxel != 0 && voxel != waterId)
                    neighbors++;
            }

            return neighbors > 0;
        }

        private static bool IsEarthLike(ushort id, ref GeneratorConfig config)
        {
            if (id == 0) return false;
            return id == config.Grass ||
                   id == config.Dirt ||
                   id == config.DirtSnowy ||
                   id == config.Sand ||
                   id == config.SandGrey ||
                   id == config.SandRed ||
                   id == config.StoneSandy;
        }

        private static bool CanPlaceTree(ref NativeArray<ushort> vox, int cx, int cy, int cz, ushort logA, ushort logB)
        {
            for (int ox = -1; ox <= 1; ox++)
            for (int oz = -1; oz <= 1; oz++)
            {
                if (ox == 0 && oz == 0) continue;
                int tx = cx + ox;
                int tz = cz + oz;

                int3 pos = new(tx, cy, tz);
                if (!InChunk(ref pos)) continue;
                ushort v = vox[ChunkSize.Flatten(tx, cy, tz)];
                if (v == logA || v == logB) return false;
            }

            return true;
        }

        private static void PlaceTree(ref NativeArray<ushort> vox, int x, int y, int z, int height, ushort idLog,
            ref Random rng, ref GeneratorConfig config)
        {
            ushort idLeaves = config.Leaves;
            if (idLog == config.LogBirch) idLeaves = config.LeavesOrange;

            for (int i = 0; i < height && y + i < ChunkHeight - 1; i++)
            {
                if (!InChunk(x, y + i, z)) break;
                vox[ChunkSize.Flatten(x, y + i, z)] = idLog;
            }

            const int radius = 2;
            int top = y + height - 1;
            for (int ox = -radius; ox <= radius; ox++)
            for (int oz = -radius; oz <= radius; oz++)
            for (int oy = -1; oy <= 1; oy++)
            {
                float dist = math.length(new float3(ox, oy, oz));
                if (dist <= radius + 0.2f)
                {
                    int yy = top + oy;
                    int tx = x + ox;
                    int tz = z + oz;
                    if (!InChunk(tx, yy, tz)) continue;
                    vox[ChunkSize.Flatten(tx, yy, tz)] = idLeaves;
                }
            }

            if (idLog != config.LogBirch) return;

            for (int ox = -2; ox <= 2; ox++)
            for (int oz = -2; oz <= 2; oz++)
            {
                if (!(rng.NextFloat() < 0.12f)) continue;

                int bx = x + ox;
                int bz = z + oz;
                int by = y - 1;
                if (!InChunk(bx, by, bz)) continue;
                int groundIdx = ChunkSize.Flatten(bx, by, bz);
                int idx = ChunkSize.Flatten(bx, by + 1, bz);

                if (!InChunk(bx, by + 1, bz) || vox[idx] != 0 ||
                    !IsEarthLike(vox[groundIdx], ref config)) continue;

                int m = rng.NextInt(0, 100);
                vox[idx] = m < 50 ? config.MushroomBrown : m < 80 ? config.MushroomRed : config.MushroomTan;
            }
        }

        private static void PlaceColumn(ref NativeArray<ushort> vox, int x, int y, int z, int blockId,
            int count)
        {
            for (int i = 0; i < count && y + i < ChunkHeight - 1; i++)
            {
                if (!InChunk(x, y + i, z)) break;
                vox[ChunkSize.Flatten(x, y + i, z)] = (ushort)blockId;
            }
        }

        /// <summary>
        ///     Places a simple palm-like tree structure at the given position using the provided random stream.
        /// </summary>
        /// <param name="vox">Voxel buffer to modify in place.</param>
        /// <param name="cx">X coordinate of the trunk base in chunk space.</param>
        /// <param name="cy">Y coordinate of the trunk base in chunk space.</param>
        /// <param name="cz">Z coordinate of the trunk base in chunk space.</param>
        /// <param name="rng">Random generator used to vary palm height and leaf placement.</param>
        /// <param name="config">Generator configuration providing log and leaf voxel IDs.</param>
        private static void PlacePalm(ref NativeArray<ushort> vox, int cx, int cy, int cz, ref Random rng,
            ref GeneratorConfig config)
        {
            ushort log = config.LogOak;
            ushort leaves = config.Leaves;
            int h = rng.NextInt(3, 6);
            for (int i = 0; i < h; i++)
            {
                int y = cy + i;
                if (!InChunk(cx, y, cz)) break;
                vox[ChunkSize.Flatten(cx, y, cz)] = log;
            }

            int top = cy + h - 1;
            for (int ox = -2; ox <= 2; ox++)
            for (int oz = -2; oz <= 2; oz++)
            {
                if (math.abs(ox) + math.abs(oz) > 3) continue;
                int x = cx + ox;
                int y = top + rng.NextInt(0, 2);
                int z = cz + oz;
                if (!InChunk(x, y, z)) continue;
                vox[ChunkSize.Flatten(x, y, z)] = leaves;
            }
        }
    }
}