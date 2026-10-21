using Engine.Scripts.Jobs.Chunk;
using Engine.Scripts.WorldGen.Settings;
using Engine.Scripts.VoxelConfig.Data.Generation;
using Engine.Scripts.VoxelConfig.Data.Internal;
using Engine.Scripts.VoxelConfig.Data.Mesh;
using Engine.Scripts.VoxelConfig.Data.Voxel;
using Engine.Scripts.WorldGen.Flora;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;

namespace Engine.Tests.Performance{
    /// <summary>
    ///     Hand-built generator/render inputs so the benchmarks run without the Resources-based
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
        ///     The placeholder world on the Terrain-first sampler. The WorldGenSettings, the six placeholder biomes and
        ///     the placeholder splines are built in code (no Resources); block ids are patched in because no registry
        ///     exists here. The created ScriptableObjects are returned so the caller can destroy them.
        /// </summary>
        internal static GeneratorConfig CreateTerrainFirstGeneratorConfig(Allocator allocator, List<Object> created){
            return CreatePlaceholderGeneratorConfig(GenArchitecture.TerrainFirst, BlendMethod.None, allocator, created);
        }

        /// <summary>Same world as <see cref="CreateTerrainFirstGeneratorConfig" /> on the Biome-first sampler.</summary>
        internal static GeneratorConfig CreateBiomeFirstGeneratorConfig(Allocator allocator, List<Object> created){
            return CreatePlaceholderGeneratorConfig(GenArchitecture.BiomeFirst, BlendMethod.None, allocator, created);
        }

        /// <summary>
        ///     The same world with blend method <paramref name="method" /> on the material stage and, in Biome-first, on
        ///     the height stage too; strengths are the defaults.
        /// </summary>
        /// <param name="flora">
        ///     Give the biomes the placeholder flora rules (and solid top blocks, so plants can stand on
        ///     them); the species are in-memory voxels and a tree like the shipped one, with ids from 6 upwards.
        /// </param>
        internal static GeneratorConfig CreatePlaceholderGeneratorConfig(GenArchitecture architecture, BlendMethod method,
            Allocator allocator, List<Object> created, bool flora = false){
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

            Dictionary<Voxel, ushort> ids = new();
            if (flora) AddPlaceholderFlora(set, ids, created);
            System.Func<Voxel, ushort> voxelId = null;
            if (flora) voxelId = v => v ? ids[v] : (ushort)0;
            config.WorldGen = settings.ToConfigWithVoxelIds(Seed, voxelId, allocator);
            for (int b = 0; b < config.WorldGen.BiomeCount; b++){
                Biome.BiomeDef def = config.WorldGen.Biomes[b];
                def.topBlock = b == 1 ? Dirt : Grass;
                def.underBlock = Dirt;
                def.stoneBlock = Stone;
                config.WorldGen.Biomes[b] = def;
            }

            return config;
        }

        /// <summary>
        ///     The placeholder flora rules of the six placeholder biomes (same order as in
        ///     <see cref="CreatePlaceholderGeneratorConfig" />), with the shipped tree: log 0…4, filled 3×3×3 leaf box at y 6.
        /// </summary>
        private static void AddPlaceholderFlora(BiomeSet set, Dictionary<Voxel, ushort> ids, List<Object> created){
            Voxel V(string name, ushort id, MeshLayer layer, bool collision){
                Voxel v = Create<Voxel>(name, created);
                v.meshLayer = layer;
                v.collision = collision;
                ids.Add(v, id);
                return v;
            }

            Voxel grass = V("Grass", Grass, MeshLayer.Solid, true), dirt = V("Dirt", Dirt, MeshLayer.Solid, true);
            Voxel stone = V("Stone", Stone, MeshLayer.Solid, true);
            Voxel grassF = V("GrassF", 6, MeshLayer.Foliage, false), flowers = V("Flowers", 7, MeshLayer.Foliage, false);
            Voxel dead = V("GrassFDead", 8, MeshLayer.Foliage, false), dry = V("GrassFDry", 9, MeshLayer.Foliage, false);
            Voxel rock = V("Rock", 10, MeshLayer.Foliage, false), mossy = V("RockMossy", 11, MeshLayer.Foliage, false);
            Voxel brown = V("MushroomBrown", 12, MeshLayer.Foliage, false), red = V("MushroomRed", 13, MeshLayer.Foliage, false);
            Voxel cactus = V("Cactus", 14, MeshLayer.Solid, true), log = V("LogOak", 15, MeshLayer.Solid, true);
            Voxel leaves = V("Leaves", 16, MeshLayer.Transparent, true);

            VoxelStructure tree = Create<VoxelStructure>("Tree", created);
            tree.parts = new[]{
                new StructurePart{
                    voxel = log, placements = new[]{
                        new VoxelPlacement{ shape = PlacementShape.Line, origin = Vector3Int.zero, end = new Vector3Int(0, 4, 0) }
                    }
                },
                new StructurePart{
                    voxel = leaves, placements = new[]{
                        new VoxelPlacement{ shape = PlacementShape.Box, origin = new Vector3Int(0, 6, 0), size = new Vector3Int(3, 3, 3), filled = true }
                    }
                }
            };

            FloraRule R(Voxel v, float p, int maxStack = 1){
                return new FloraRule{ SpeciesVoxel = v, Probability = p, MaxStackHeight = maxStack };
            }

            FloraRule T(float p){
                return new FloraRule{ SpeciesStructure = tree, Probability = p };
            }

            FloraRule[][] rules ={
                new[]{ R(grassF, .30f), R(flowers, .05f), T(.005f) }, // Plains
                new[]{ R(dead, .03f), R(cactus, .01f, 3) }, // Desert
                new[]{ R(dry, .05f), R(rock, .02f), R(mossy, .01f) }, // Mountain
                new[]{ R(dead, .01f), R(rock, .01f) }, // Snow
                new[]{ T(.04f), R(grassF, .20f), R(brown, .01f), R(red, .01f) }, // Forest
                new FloraRule[0] // Ocean
            };
            for (int b = 0; b < set.Biomes.Count; b++){
                Biome biome = set.Biomes[b];
                biome.TopBlock = b == 1 ? dirt : grass;
                biome.UnderBlock = dirt;
                biome.StoneBlock = stone;
                biome.FloraRules = new List<FloraRule>(rules[b]);
            }
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
    }
}
