using Engine.Scripts.Data;
using Engine.Scripts.Jobs.Chunk;
using Engine.Scripts.Settings;
using Engine.Scripts.Utils;
using Engine.Scripts.VoxelConfig.Data.Generation;
using Engine.Scripts.VoxelConfig.Data.Internal;
using Engine.Scripts.VoxelConfig.Data.Mesh;
using Engine.Scripts.VoxelConfig.Data.Voxel;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Engine.Tests.EditMode{
    /// <summary>
    ///     Shared hand-built inputs for the Phase S characterisation tests (TST-E01…E20). Mirrors the
    ///     performance BenchmarkFixtures so no test depends on the Resources-based DataImporter unless it
    ///     explicitly tests it (TST-E11).
    /// </summary>
    internal static class TestFixtures{
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
                NativeArray<Biome.BiomeDef> biomes = new(1, allocator);
                biomes[0] = new Biome.BiomeDef{
                    topBlock = Grass, underBlock = Dirt, stoneBlock = Stone,
                    targetHumidity = 0.5f, targetTemperature = 0.5f, targetContinental = 0.5f, targetElevation = 0.5f
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

        /// <summary>A chunk whose every voxel is <paramref name="voxelId" /> (one interval node).</summary>
        internal static ChunkVoxelData CreateUniformChunk(ushort voxelId){
            ChunkVoxelData data = new(16);
            data.AddVoxels(voxelId, VoxelConstants.VoxelsPerChunk);
            return data;
        }

        /// <summary>
        ///     Engine settings the way VoxelWorld.ConfigureSettings derives them from a draw distance; no prefab,
        ///     no materials. Enough for ChunkManager and the job state handlers.
        /// </summary>
        internal static VoxelEngineSettings CreateSettings(int drawDistance){
            VoxelEngineSettings settings = ScriptableObject.CreateInstance<VoxelEngineSettings>();
            settings.Seed = Seed;
            settings.Chunk = new ChunkSettings{
                DrawDistance = drawDistance,
                LoadDistance = drawDistance + 2,
                UpdateDistance = math.max(drawDistance - 2, 2)
            };
            settings.Scheduler = new SchedulerSettings();
            settings.Renderer = new RendererSettings();
            return settings;
        }
    }
}
