using Engine.Scripts.Jobs.Chunk;
using Engine.Scripts.Settings;
using Engine.Scripts.VoxelConfig.Data.Generation;
using Engine.Scripts.VoxelConfig.Data.Internal;
using Engine.Scripts.VoxelConfig.Data.Mesh;
using Engine.Scripts.VoxelConfig.Data.Voxel;
using Unity.Collections;
using UnityEngine;

namespace Engine.Tests.Performance{
    /// <summary>
    ///     PERF-02: hand-built generator/render inputs so the benchmarks run without the Resources-based
    ///     DataImporter. Voxel IDs are fixed; the noise parameters come from a default NoiseSettings asset,
    ///     built exactly like the engine does (NoiseSettings.ToNoiseParameters).
    /// </summary>
    internal static class BenchmarkFixtures{
        internal const ushort Air = 0;
        internal const ushort Grass = 1;
        internal const ushort Dirt = 2;
        internal const ushort Stone = 3;
        internal const ushort Water = 4;
        internal const ushort Ice = 5;
        internal const int VoxelTypeCount = 6;

        internal const int Seed = 1337;

        internal static GeneratorConfig CreateGeneratorConfig(Allocator allocator){
            NoiseSettings noise = ScriptableObject.CreateInstance<NoiseSettings>();
            try{
                NativeArray<Biome.BiomeDef> biomes = new(2, allocator);
                biomes[0] = new Biome.BiomeDef{
                    topBlock = Grass, underBlock = Dirt, stoneBlock = Stone,
                    targetHumidity = 0.5f, targetTemperature = 0.5f, targetContinental = 0.5f, targetElevation = 0.5f
                };
                biomes[1] = new Biome.BiomeDef{
                    topBlock = Dirt, underBlock = Dirt, stoneBlock = Stone,
                    targetHumidity = 0.3f, targetTemperature = 0.1f, targetContinental = 0.7f, targetElevation = 0.7f
                };

                NativeHashMap<FixedString32Bytes, Voxel.VoxelDef> voxels = new(VoxelTypeCount, allocator);
                voxels.Add("std:air", new Voxel.VoxelDef{ Id = Air, MeshLayer = MeshLayer.Air });
                voxels.Add("std:Grass", new Voxel.VoxelDef{ Id = Grass, MeshLayer = MeshLayer.Solid, Collision = true });
                voxels.Add("std:Dirt", new Voxel.VoxelDef{ Id = Dirt, MeshLayer = MeshLayer.Solid, Collision = true });
                voxels.Add("std:Stone", new Voxel.VoxelDef{ Id = Stone, MeshLayer = MeshLayer.Solid, Collision = true });
                voxels.Add("std:Water", new Voxel.VoxelDef{ Id = Water, MeshLayer = MeshLayer.Transparent });
                voxels.Add("std:Ice", new Voxel.VoxelDef{ Id = Ice, MeshLayer = MeshLayer.Solid, Collision = true });

                return new GeneratorConfig{
                    WaterLevel = noise.WaterLevel,
                    GlobalSeed = Seed,
                    BiomeDefs = biomes,
                    Voxels = voxels,
                    NoiseParams = noise.ToNoiseParameters(Seed)
                };
            }
            finally{
                Object.DestroyImmediate(noise);
            }
        }

        /// <summary>Render definitions indexed by voxel ID; only the collision flag matters for the collider mesher.</summary>
        internal static VoxelEngineRenderGenData CreateRenderGenData(Allocator allocator){
            NativeArray<Voxel.VoxelDef> defs = new(VoxelTypeCount, allocator);
            defs[Air] = new Voxel.VoxelDef{ Id = Air, MeshLayer = MeshLayer.Air };
            defs[Grass] = new Voxel.VoxelDef{ Id = Grass, MeshLayer = MeshLayer.Solid, Collision = true };
            defs[Dirt] = new Voxel.VoxelDef{ Id = Dirt, MeshLayer = MeshLayer.Solid, Collision = true };
            defs[Stone] = new Voxel.VoxelDef{ Id = Stone, MeshLayer = MeshLayer.Solid, Collision = true };
            defs[Water] = new Voxel.VoxelDef{ Id = Water, MeshLayer = MeshLayer.Transparent };
            defs[Ice] = new Voxel.VoxelDef{ Id = Ice, MeshLayer = MeshLayer.Solid, Collision = true };
            return new VoxelEngineRenderGenData{ VoxelRenderDefs = defs };
        }
    }
}
