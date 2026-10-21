using System;
using System.Collections.Generic;
using Engine.Scripts.Data;
using Engine.Scripts.Jobs.Light;
using Engine.Scripts.Utils;
using Engine.Scripts.Utils.Extensions;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using static Engine.Scripts.Utils.VoxelConstants;

namespace Engine.Tests.EditMode{
    /// <summary>
    ///     A small hand-built world for the light tests — chunks -r…r around the origin, voxel ids with
    ///     their own light table (independent of the voxel registry), and helpers to light chunks with the Burst
    ///     <see cref="LightJob" /> and to apply edits with <see cref="LightEditJob" />.
    /// </summary>
    internal sealed partial class LightTestWorld : IDisposable{
        internal const ushort Air = 0;
        internal const ushort Stone = 1;
        internal const ushort Leaves = 2; // attenuation 1
        internal const ushort Water = 3; // attenuation 2
        internal const ushort Glass = 4; // attenuation 0
        internal const ushort RedLamp = 5; // opaque, emits (15, 0, 0)
        internal const ushort Lava = 6; // opaque, emits (15, 9, 3)
        internal const ushort BlueCrystal = 7; // clear, emits (0, 0, 12)
        internal const int VoxelCount = 8;

        internal static readonly ushort[] EditPalette ={ Air, Air, Air, Stone, Stone, Leaves, Water, Glass, RedLamp, Lava, BlueCrystal };

        public NativeParallelHashMap<int2, ChunkVoxelData> Chunks;
        public VoxelLightTable Table;

        internal LightTestWorld(int radius, Func<int3, ushort> generator){
            Table = CreateTable(Allocator.Persistent);
            Chunks = new NativeParallelHashMap<int2, ChunkVoxelData>((2 * radius + 1) * (2 * radius + 1), Allocator.Persistent);
            ushort[] voxels = new ushort[VoxelsPerChunk];

            for (int cx = -radius; cx <= radius; cx++)
            for (int cz = -radius; cz <= radius; cz++){
                int2 chunk = new(cx, cz);
                for (int x = 0; x < ChunkWidth; x++)
                for (int z = 0; z < ChunkDepth; z++)
                for (int y = 0; y < ChunkHeight; y++)
                    voxels[ChunkSize.Flatten(new int3(x, y, z))] =
                        generator(new int3(cx * ChunkWidth + x, y, cz * ChunkDepth + z));

                Chunks.Add(chunk, Compress(voxels));
            }
        }

        internal static VoxelLightTable CreateTable(Allocator allocator){
            VoxelLightTable table = new(VoxelCount, allocator);
            table.Set(Air, 0, 0);
            table.Set(Stone, 15, 0);
            table.Set(Leaves, 1, 0);
            table.Set(Water, 2, 0);
            table.Set(Glass, 0, 0);
            table.Set(RedLamp, 15, VoxelLight.PackRgb(15, 0, 0));
            table.Set(Lava, 15, VoxelLight.PackRgb(15, 9, 3));
            table.Set(BlueCrystal, 0, VoxelLight.PackRgb(0, 0, 12));
            return table;
        }

        private static ChunkVoxelData Compress(ushort[] voxels){
            ChunkVoxelData data = new(256);
            ushort run = voxels[0];
            int count = 0;
            foreach (ushort v in voxels){
                if (v == run){
                    count++;
                    continue;
                }

                data.AddVoxels(run, count);
                run = v;
                count = 1;
            }

            data.AddVoxels(run, count);
            return data;
        }

        internal static int2 ChunkOf(int3 world){
            return new int2(VoxelUtils.FloorDivide(world.x, ChunkWidth), VoxelUtils.FloorDivide(world.z, ChunkDepth));
        }

        internal static int3 LocalOf(int3 world){
            int2 chunk = ChunkOf(world);
            return new int3(world.x - chunk.x * ChunkWidth, world.y, world.z - chunk.y * ChunkDepth);
        }

        internal ushort Get(int3 world){
            return Chunks[ChunkOf(world)].GetVoxel(LocalOf(world));
        }

        /// <summary>Sets a voxel and returns the previous id (the map holds the list by value: write it back).</summary>
        internal ushort Set(int3 world, ushort id){
            int2 chunk = ChunkOf(world);
            ChunkVoxelData data = Chunks[chunk];
            ushort old = data.GetVoxel(LocalOf(world));
            data.SetVoxel(LocalOf(world), id);
            Chunks[chunk] = data;
            return old;
        }

        /// <summary>Lights the given chunks with the Burst light job. The caller disposes the values.</summary>
        internal NativeParallelHashMap<int2, ChunkLightData> LightChunks(IEnumerable<int2> chunks){
            NativeList<int2> jobs = new(Allocator.TempJob);
            foreach (int2 c in chunks) jobs.Add(c);
            NativeParallelHashMap<int2, ChunkLightData> results = new(jobs.Length, Allocator.Persistent);
            new LightJob{
                Jobs = jobs,
                Chunks = Chunks.AsReadOnly(),
                Table = Table,
                Results = results.AsParallelWriter()
            }.Schedule(jobs.Length, 1).Complete();
            jobs.Dispose();
            return results;
        }

        internal ChunkLightData LightChunk(int2 chunk){
            NativeParallelHashMap<int2, ChunkLightData> map = LightChunks(new[]{ chunk });
            ChunkLightData light = map[chunk];
            map.Dispose();
            return light;
        }

        internal static ushort LightAt(in ChunkLightData light, int3 world){
            return light.GetLight(LocalOf(world));
        }

        /// <summary>Rolling hills 100…140 with water below 112, the default random test terrain.</summary>
        internal static Func<int3, ushort> RandomTerrain(int seed){
            System.Random random = new(seed);
            float ox = random.Next(0, 10000);
            float oz = random.Next(0, 10000);
            Dictionary<int3, ushort> features = new();

            // Trees (leaf blobs on a stone trunk), caves, lamps, lava pools, glass pillars, floating slabs.
            for (int i = 0; i < 160; i++){
                int x = random.Next(-80, 80);
                int z = random.Next(-80, 80);
                int h = Height(x, z, ox, oz);
                int kind = random.Next(0, 6);
                switch (kind){
                    case 0:
                        for (int y = h + 1; y <= h + 4; y++) features[new int3(x, y, z)] = Stone;
                        for (int dx = -2; dx <= 2; dx++)
                        for (int dz = -2; dz <= 2; dz++)
                        for (int dy = 0; dy <= 2; dy++)
                            features[new int3(x + dx, h + 5 + dy, z + dz)] = Leaves;
                        break;
                    case 1:
                        int cy = random.Next(h - 20, h + 2);
                        int r = random.Next(2, 5);
                        for (int dx = -r; dx <= r; dx++)
                        for (int dz = -r; dz <= r; dz++)
                        for (int dy = -r; dy <= r; dy++)
                            if (dx * dx + dy * dy + dz * dz <= r * r) features[new int3(x + dx, cy + dy, z + dz)] = Air;
                        if (random.Next(0, 2) == 0) features[new int3(x, cy, z)] = random.Next(0, 2) == 0 ? BlueCrystal : RedLamp;
                        break;
                    case 2:
                        features[new int3(x, h + 1, z)] = RedLamp;
                        break;
                    case 3:
                        for (int dx = -1; dx <= 1; dx++)
                        for (int dz = -1; dz <= 1; dz++)
                            features[new int3(x + dx, h, z + dz)] = Lava;
                        break;
                    case 4:
                        for (int y = h + 1; y <= h + 6; y++) features[new int3(x, y, z)] = Glass;
                        break;
                    default:
                        int sy = h + random.Next(3, 10);
                        for (int dx = -3; dx <= 3; dx++)
                        for (int dz = -3; dz <= 3; dz++)
                            features[new int3(x + dx, sy, z + dz)] = dx == 0 ? Water : Stone;
                        break;
                }
            }

            return p => {
                if (features.TryGetValue(p, out ushort f)) return f;
                int h = Height(p.x, p.z, ox, oz);
                if (p.y <= h) return Stone;
                return p.y <= 112 ? Water : Air;
            };
        }

        private static int Height(int x, int z, float ox, float oz){
            return 100 + (int)(Mathf.PerlinNoise((x + ox) * 0.05f, (z + oz) * 0.05f) * 40f);
        }

        public void Dispose(){
            foreach (KeyValue<int2, ChunkVoxelData> kv in Chunks) kv.Value.Dispose();
            Chunks.Dispose();
            Table.Dispose();
        }
    }
}
