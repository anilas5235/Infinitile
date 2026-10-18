using Engine.Scripts.Jobs.Chunk;
using Engine.Scripts.WorldGen.Settings;
using Engine.Scripts.VoxelConfig.Data.Generation;
using Engine.Scripts.VoxelConfig.Data.Internal;
using Engine.Scripts.VoxelConfig.Data.Mesh;
using Engine.Scripts.VoxelConfig.Data.Voxel;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;

namespace Engine.Tests.Performance{
    /// <summary>
    ///     PERF-02: hand-built generator/render inputs so the benchmarks run without the Resources-based
    ///     DataImporter. Voxel IDs are fixed; the world-gen config comes from WorldGenSettings built in code,
    ///     exactly like the engine does (WorldGenSettings.ToConfig).
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
            // Phase 2 (INT-40): the only generation input is a WorldGenSettings. One Biome-first biome at offset
            // 100 with a small amplitude keeps the surface near y = 100 for the tests that fill terrain here;
            // block ids are patched in because no registry exists in the fixture.
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
                    WaterLevel = settings.WaterLevel,
                    GlobalSeed = Seed,
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
        ///     PERF-02 "per architecture": the same generator config on the Terrain-first sampler (INT-10). The
        ///     WorldGenSettings, the six section 19.2 placeholder biomes and the section 19.1 placeholder splines are
        ///     built in code (no Resources); block ids are patched in because no registry exists here. The created
        ///     ScriptableObjects are returned so the caller can destroy them.
        /// </summary>
        internal static GeneratorConfig CreateTerrainFirstGeneratorConfig(Allocator allocator, List<Object> created){
            return CreateSpecGeneratorConfig(GenArchitecture.TerrainFirst, BlendMethod.None, allocator, created);
        }

        /// <summary>Same world as <see cref="CreateTerrainFirstGeneratorConfig" /> on the Biome-first sampler (Phase 2).</summary>
        internal static GeneratorConfig CreateBiomeFirstGeneratorConfig(Allocator allocator, List<Object> created){
            return CreateSpecGeneratorConfig(GenArchitecture.BiomeFirst, BlendMethod.None, allocator, created);
        }

        /// <summary>
        ///     The same world with blend method <paramref name="method" /> on the material stage and, in Biome-first, on
        ///     the height stage too (PERF-02 "per architecture x method", Phase 3); strengths are the spec defaults.
        /// </summary>
        internal static GeneratorConfig CreateSpecGeneratorConfig(GenArchitecture architecture, BlendMethod method,
            Allocator allocator, List<Object> created){
            GeneratorConfig config = CreateGeneratorConfig(allocator);
            config.WorldGen.Dispose(); // replaced by the six-biome world below

            WorldGenSettings settings = Create<WorldGenSettings>("WG", created);
            settings.Architecture = architecture;
            settings.MaterialMethod = method;
            settings.HeightMethod = architecture == GenArchitecture.BiomeFirst ? method : BlendMethod.None;
            settings.Splines = Create<TerrainSplineSet>("Splines", created);
            settings.Splines.ResetToPlaceholders();
            BiomeSet set = Create<BiomeSet>("Set", created);
            set.Biomes.Add(PlaceholderBiome("Plains", .55f, .45f, .45f, .70f, -0.2f, 136, 6, new[]{ 1f, .6f, .3f, .12f, .05f, .02f }, 0, created));
            set.Biomes.Add(PlaceholderBiome("Desert", .90f, .10f, .45f, .60f, 0f, 134, 8, new[]{ 1f, .8f, .2f, .05f, .02f, .01f }, 0, created));
            set.Biomes.Add(PlaceholderBiome("Mountain", .35f, .40f, .80f, .15f, 0.6f, 165, 22, new[]{ .7f, .7f, .6f, .45f, .3f, .15f }, 35, created));
            set.Biomes.Add(PlaceholderBiome("Snow", .15f, .50f, .70f, .35f, 0.3f, 158, 14, new[]{ .8f, .6f, .45f, .3f, .15f, .08f }, 15, created));
            set.Biomes.Add(PlaceholderBiome("Forest", .50f, .70f, .50f, .55f, 0f, 140, 9, new[]{ 1f, .6f, .35f, .15f, .06f, .03f }, 0, created));
            set.Biomes.Add(PlaceholderBiome("Ocean", .50f, .80f, .08f, .50f, 0f, 100, 6, new[]{ 1f, .5f, .2f, .05f, .02f, .01f }, 0, created));
            settings.Biomes = set;

            config.WorldGen = settings.ToConfig(Seed, null, allocator);
            for (int b = 0; b < config.WorldGen.BiomeCount; b++){
                Biome.BiomeDef def = config.WorldGen.Biomes[b];
                def.topBlock = b == 1 ? Dirt : Grass;
                def.underBlock = Dirt;
                def.stoneBlock = Stone;
                config.WorldGen.Biomes[b] = def;
            }

            return config;
        }

        private static T Create<T>(string name, List<Object> created) where T : ScriptableObject{
            T so = ScriptableObject.CreateInstance<T>();
            so.name = name;
            created.Add(so);
            return so;
        }

        private static Biome PlaceholderBiome(string name, float t, float f, float c, float e, float pv, float offset,
            float amp, float[] weights, float j, List<Object> created){
            Biome b = Create<Biome>(name, created);
            b.TargetTemperature = t;
            b.TargetHumidity = f;
            b.TargetContinental = c;
            b.TargetErosion = e;
            b.TargetPV = pv;
            b.HeightOffset = offset;
            b.HeightAmplitude = amp;
            b.OctaveWeights = weights;
            b.Jaggedness = j;
            return b;
        }

        /// <summary>
        ///     Releases a config from <see cref="CreateGeneratorConfig" />. Since D-05, <c>GeneratorConfig.Dispose</c>
        ///     frees only the world-gen config, because at runtime the registry owns the voxel map; here the fixture
        ///     created it, so it frees it too.
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
    }
}
