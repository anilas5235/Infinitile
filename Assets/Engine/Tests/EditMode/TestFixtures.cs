using Engine.Scripts.Data;
using Engine.Scripts.Jobs.Chunk;
using Engine.Scripts.Settings;
using Engine.Scripts.Utils;
using Engine.Scripts.VoxelConfig.Data.Generation;
using Engine.Scripts.VoxelConfig.Data.Internal;
using Engine.Scripts.VoxelConfig.Data.Mesh;
using Engine.Scripts.VoxelConfig.Data.Voxel;
using Engine.Scripts.WorldGen.Settings;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Engine.Tests.EditMode{
    /// <summary>
    ///     Shared hand-built inputs for the characterisation tests. Mirrors the performance BenchmarkFixtures so
    ///     no test depends on the Resources-based DataImporter unless it explicitly tests it.
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
            // One Biome-first biome at offset 100 with a small amplitude keeps the surface near y = 100 for the
            // tests that fill terrain here; block ids are patched in because no registry exists in the fixture.
            WorldGenSettings settings = ScriptableObject.CreateInstance<WorldGenSettings>();
            BiomeSet set = ScriptableObject.CreateInstance<BiomeSet>();
            Biome biome = ScriptableObject.CreateInstance<Biome>();
            try{
                settings.Architecture = GenArchitecture.BiomeFirst;
                biome.name = "Plains";
                biome.TargetTemperature = .5f;
                biome.TargetHumidity = .5f;
                biome.TargetContinental = .5f;
                biome.TargetErosion = .5f;
                biome.TargetPV = 0f;
                biome.HeightOffset = 100f;
                biome.HeightAmplitude = 6f;
                biome.OctaveWeights = new[]{ 1f, .6f, .3f, .12f, .05f, .02f };
                biome.Jaggedness = 0f;
                set.Biomes.Add(biome);
                settings.Biomes = set;

                WorldGenConfig worldGen = settings.ToConfig(Seed, null, allocator);
                Biome.BiomeDef def = worldGen.Biomes[0];
                def.topBlock = Grass;
                def.underBlock = Dirt;
                def.stoneBlock = Stone;
                worldGen.Biomes[0] = def;

                NativeHashMap<FixedString32Bytes, Voxel.VoxelDef> voxels = new(VoxelTypeCount, allocator);
                voxels.Add("std:air", new Voxel.VoxelDef{ Id = Air, MeshLayer = MeshLayer.Air });
                voxels.Add("std:Grass", new Voxel.VoxelDef{ Id = Grass, MeshLayer = MeshLayer.Solid, Collision = true });
                voxels.Add("std:Dirt", new Voxel.VoxelDef{ Id = Dirt, MeshLayer = MeshLayer.Solid, Collision = true });
                voxels.Add("std:Stone", new Voxel.VoxelDef{ Id = Stone, MeshLayer = MeshLayer.Solid, Collision = true });
                voxels.Add("std:Water", new Voxel.VoxelDef{ Id = Water, MeshLayer = MeshLayer.Transparent });
                voxels.Add("std:Ice", new Voxel.VoxelDef{ Id = Ice, MeshLayer = MeshLayer.Solid, Collision = true });

                return new GeneratorConfig{
                    Voxels = voxels,
                    WorldGen = worldGen
                };
            }
            finally{
                Object.DestroyImmediate(biome);
                Object.DestroyImmediate(set);
                Object.DestroyImmediate(settings);
            }
        }

        /// <summary>
        ///     Releases a config from <see cref="CreateGeneratorConfig" />. <c>GeneratorConfig.Dispose</c> frees only
        ///     the world-gen config, because at runtime the registry owns the voxel map; here the fixture created it,
        ///     so it frees it too.
        /// </summary>
        internal static void DisposeGeneratorConfig(ref GeneratorConfig config){
            if (config.Voxels.IsCreated) config.Voxels.Dispose();
            config.Dispose();
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
                LoadDistance = drawDistance + 3, // data -> light (3 x 3 data) -> mesh (3 x 3 light)
                UpdateDistance = math.max(drawDistance - 2, 2)
            };
            settings.Scheduler = new SchedulerSettings();
            settings.Renderer = new RendererSettings();
            return settings;
        }
    }
}
