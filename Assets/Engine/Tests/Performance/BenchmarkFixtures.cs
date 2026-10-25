using Engine.Scripts.Jobs.Chunk;
using Engine.Scripts.WorldGen.Settings;
using Engine.Scripts.VoxelConfig.Data.Generation;
using Engine.Scripts.VoxelConfig.Data.Internal;
using Engine.Scripts.VoxelConfig.Data.Mesh;
using Engine.Scripts.VoxelConfig.Data.Voxel;
using Engine.Scripts.WorldGen.Flora;
using System.Collections.Generic;
using Engine.Scripts.Data;
using NUnit.Framework;
using Unity.Collections;
using UnityEditor;
using UnityEngine;

namespace Engine.Tests.Performance{
    /// <summary>Flora of <see cref="BenchmarkFixtures.CreateSpeciesGeneratorConfig" />.</summary>
    internal enum SpeciesFlora{
        /// <summary>The rules of the shipped Forest biome (its tree species, undergrowth, mushrooms and logs).</summary>
        ShippedForest,

        /// <summary>One rule with the shipped variant of the largest reach, so the padding is the largest possible.</summary>
        LargestReach
    }

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
            Dictionary<Voxel, ushort> ids = new();
            System.Func<Voxel, ushort> voxelId = null;
            if (flora) voxelId = v => v ? ids[v] : (ushort)0;
            return CreatePlaceholderGeneratorConfig(architecture, method, allocator, created,
                flora ? set => AddPlaceholderFlora(set, ids, created) : null, voxelId);
        }

        /// <summary>
        ///     The placeholder world with DistanceBlend (the shipped method) and real species from the shipped package:
        ///     the shipped Forest rules or the shipped variant with the largest reach, on every land biome.
        /// </summary>
        /// <param name="ids">
        ///     Voxel id of every species voxel, from <see cref="VoxelTypeCount" /> upwards, for the light and collider
        ///     tables (<see cref="CreateLightTable" />, <see cref="CreateRenderGenData(Dictionary{Voxel,ushort},Allocator)" />).
        /// </param>
        internal static GeneratorConfig CreateSpeciesGeneratorConfig(GenArchitecture architecture, SpeciesFlora flora,
            Allocator allocator, List<Object> created, out Dictionary<Voxel, ushort> ids){
            Dictionary<Voxel, ushort> map = new();
            ushort next = VoxelTypeCount;

            ushort VoxelId(Voxel v){
                if (!v) return 0;
                if (!map.TryGetValue(v, out ushort id)){
                    id = next++;
                    map.Add(v, id);
                }

                return id;
            }

            GeneratorConfig config = CreatePlaceholderGeneratorConfig(architecture, BlendMethod.DistanceBlend, allocator, created,
                set => AddSpeciesFlora(set, flora, map, created), VoxelId);
            ids = map;
            return config;
        }

        private static GeneratorConfig CreatePlaceholderGeneratorConfig(GenArchitecture architecture, BlendMethod method,
            Allocator allocator, List<Object> created, System.Action<BiomeSet> addFlora, System.Func<Voxel, ushort> voxelId){
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

            addFlora?.Invoke(set);
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

        /// <summary>Folders of the shipped species and biomes.</summary>
        private const string ShippedTreesPath = "Assets/Engine/Resources/VoxelDataPackages/Standard/Trees";

        private const string ShippedForestPath = "Assets/Engine/Resources/VoxelDataPackages/Standard/Bioms/Forest.asset";

        /// <summary>Per-column probability of the largest-reach tree: high enough that the spacing radius limits the anchors.</summary>
        private const float LargestReachProbability = .1f;

        /// <summary>
        ///     Gives the five land biomes the shipped Forest rules, or a single rule with the largest-reach variant at
        ///     <see cref="LargestReachProbability" /> and even density. Ocean keeps no flora. The ground blocks are
        ///     in-memory voxels with the fixture ids.
        /// </summary>
        private static void AddSpeciesFlora(BiomeSet set, SpeciesFlora flora, Dictionary<Voxel, ushort> ids, List<Object> created){
            List<FloraRule> rules;
            if (flora == SpeciesFlora.ShippedForest){
                Biome forest = AssetDatabase.LoadAssetAtPath<Biome>(ShippedForestPath);
                Assert.IsNotNull(forest, ShippedForestPath);
                rules = forest.FloraRules;
            }
            else{
                TreeSpecies tree = Create<TreeSpecies>("LargestReach", created);
                tree.Variants.Add(LargestReachVariant());
                rules = new List<FloraRule>{ new(){ SpeciesTree = tree, Probability = LargestReachProbability } };
            }

            Voxel Ground(string name, ushort id){
                Voxel v = Create<Voxel>(name, created);
                v.meshLayer = MeshLayer.Solid;
                v.collision = true;
                ids.Add(v, id);
                return v;
            }

            Voxel grass = Ground("Grass", Grass), dirt = Ground("Dirt", Dirt), stone = Ground("Stone", Stone);
            for (int b = 0; b < set.Biomes.Count; b++){
                Biome biome = set.Biomes[b];
                biome.TopBlock = b == 1 ? dirt : grass;
                biome.UnderBlock = dirt;
                biome.StoneBlock = stone;
                if (b < set.Biomes.Count - 1) biome.FloraRules = new List<FloraRule>(rules);
            }
        }

        /// <summary>The shipped variant with the largest horizontal reach; ties go to the first in path order.</summary>
        internal static TreeVariant LargestReachVariant(){
            TreeVariant best = null;
            int bestReach = -1;
            List<string> paths = new();
            foreach (string guid in AssetDatabase.FindAssets("t:TreeSpecies", new[]{ ShippedTreesPath }))
                paths.Add(AssetDatabase.GUIDToAssetPath(guid));
            paths.Sort(System.StringComparer.Ordinal);
            foreach (string path in paths){
                TreeSpecies species = AssetDatabase.LoadAssetAtPath<TreeSpecies>(path);
                List<TreeVariantDef> defs = new();
                FloraConfigBuilder.AddTree(species, _ => 1, defs, new Dictionary<ushort, byte>(), null, false);
                for (int i = 0; i < defs.Count; i++){
                    if (defs[i].Reach <= bestReach) continue;
                    bestReach = defs[i].Reach;
                    best = species.Variants[i];
                }
            }

            Assert.IsNotNull(best, ShippedTreesPath);
            return best;
        }

        /// <summary>
        ///     Light table of the fixture voxels (solid blocks opaque, water 2) and the species voxels with the
        ///     attenuation and emission of their assets.
        /// </summary>
        internal static VoxelLightTable CreateLightTable(Dictionary<Voxel, ushort> ids, Allocator allocator){
            VoxelLightTable table = new(TableSize(ids), allocator);
            for (int id = 0; id < table.Count; id++) table.Set(id, 0, 0);
            foreach (ushort solid in new[]{ Grass, Dirt, Stone, Ice }) table.Set(solid, (byte)VoxelLightLimits.MaxLevel, 0);
            table.Set(Water, 2, 0);
            foreach (KeyValuePair<Voxel, ushort> entry in ids){
                if (entry.Value < VoxelTypeCount) continue; // fixture ids keep the values above
                Vector3Int e = entry.Key.lightEmission;
                table.Set(entry.Value, (byte)entry.Key.lightAttenuation, VoxelLight.PackRgb(e.x, e.y, e.z));
            }

            return table;
        }

        /// <summary>Render definitions of the fixture voxels and the species voxels, indexed by voxel ID.</summary>
        internal static VoxelEngineRenderGenData CreateRenderGenData(Dictionary<Voxel, ushort> ids, Allocator allocator){
            VoxelEngineRenderGenData data = CreateRenderGenData(allocator);
            NativeArray<Voxel.VoxelDef> defs = new(TableSize(ids), allocator);
            NativeArray<Voxel.VoxelDef>.Copy(data.VoxelRenderDefs, defs, VoxelTypeCount);
            data.VoxelRenderDefs.Dispose();
            foreach (KeyValuePair<Voxel, ushort> entry in ids)
                if (entry.Value >= VoxelTypeCount)
                    defs[entry.Value] = new Voxel.VoxelDef{ Id = entry.Value, MeshLayer = entry.Key.meshLayer, Collision = entry.Key.collision };
            data.VoxelRenderDefs = defs;
            return data;
        }

        private static int TableSize(Dictionary<Voxel, ushort> ids){
            int size = VoxelTypeCount;
            foreach (ushort id in ids.Values) size = Mathf.Max(size, id + 1);
            return size;
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
