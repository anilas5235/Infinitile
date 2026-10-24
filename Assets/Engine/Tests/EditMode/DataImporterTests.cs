using System;
using System.Collections.Generic;
using System.Linq;
using Engine.Scripts.Jobs.Chunk;
using Engine.Scripts.VoxelConfig;
using Engine.Scripts.VoxelConfig.Data.Generation;
using Engine.Scripts.VoxelConfig.Data.Voxel;
using Engine.Scripts.WorldGen.Core;
using Engine.Scripts.WorldGen.Flora;
using Engine.Scripts.WorldGen.Settings;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Engine.Tests.EditMode{
    /// <summary>
    ///     The Standard package loads through the same registry calls DataImporter.Awake makes, every voxel gets a
    ///     unique ID, every biome resolves and CreateConfig() yields usable arrays.
    /// </summary>
    public class DataImporterTests{
        private GameObject _host;
        private DataImporter _importer;
        private bool _registriesLive;
        private VoxelDataPackage[] _packages;

        [SetUp]
        public void SetUp(){
            _host = new GameObject("DataImporterTests");
            _host.SetActive(false); // Awake must not run: the test drives the registries itself
            _importer = _host.AddComponent<DataImporter>();
            _packages = Resources.LoadAll<VoxelDataPackage>("VoxelDataPackages");
            Assert.IsNotEmpty(_packages, "no VoxelDataPackage in Resources/VoxelDataPackages");
        }

        [TearDown]
        public void TearDown(){
            if (_registriesLive) DisposeRegistries();
            Object.DestroyImmediate(_host);
        }

        private void DisposeRegistries(){
            _registriesLive = false;
            _importer.VoxelRegistry.Dispose();
            _importer.BiomeRegistry.Dispose();
            _importer.VoxelStructureRegistry.Dispose();
        }

        /// <summary>Mirrors DataImporter.Awake without the scene dependencies (world, materials).</summary>
        private void LoadPackages(){
            Texture2D texError = Resources.Load<Texture2D>("Artwork/TexError");
            Texture2D texErrorT = Resources.Load<Texture2D>("Artwork/TexErrorT");
            Assert.IsNotNull(texError);
            Assert.IsNotNull(texErrorT);

            _importer.VoxelRegistry.Initialize(texError, texErrorT, texErrorT);
            foreach (VoxelDataPackage package in _packages)
            foreach (Voxel voxel in package.voxel.Where(v => v))
                _importer.VoxelRegistry.Register(package.packagePrefix, voxel);

            _importer.BiomeRegistry.Initialize();
            foreach (VoxelDataPackage package in _packages)
            foreach (Biome biome in package.biomes.Where(b => b))
                _importer.BiomeRegistry.Register(package.packagePrefix, biome);

            _importer.VoxelStructureRegistry.Initialize();
            foreach (VoxelDataPackage package in _packages)
            foreach (VoxelStructure structure in package.structures.Where(s => s))
                _importer.VoxelStructureRegistry.Register(package.packagePrefix, structure);

            _importer.VoxelRegistry.FinalizeRegistry();
            _importer.BiomeRegistry.FinalizeRegistry(_importer.VoxelRegistry);
            _importer.VoxelStructureRegistry.FinalizeRegistry(_importer.VoxelRegistry);
            _registriesLive = true;
        }

        [Test]
        public void StandardPackage_RegistersEveryVoxelWithUniqueIds(){
            LoadPackages();
            List<KeyValuePair<ushort, FixedString32Bytes>> entries = _importer.VoxelRegistry.GetAllEntries();
            CollectionAssert.AllItemsAreUnique(entries.Select(e => e.Key).ToList(), "IDs");
            CollectionAssert.AllItemsAreUnique(entries.Select(e => e.Value.ToString()).ToList(), "names");
            Assert.AreEqual(0, _importer.VoxelRegistry.GetIdOrThrow("std:air"));

            int voxelCount = 0;
            foreach (VoxelDataPackage package in _packages)
            foreach (Voxel voxel in package.voxel.Where(v => v)){
                voxelCount++;
                Assert.IsTrue(_importer.VoxelRegistry.TryGetId(voxel.GetFullName(), out ushort id),
                    $"{voxel.GetFullName()} not registered");
                Assert.IsTrue(_importer.VoxelRegistry.TryGet(id, out Voxel so));
                Assert.AreSame(voxel, so);
                Assert.IsTrue(_importer.VoxelRegistry.VoxelMap.ContainsKey(voxel.GetFullName()));
            }

            Assert.AreEqual(voxelCount + 1, entries.Count, "air + every package voxel");
            Assert.AreEqual(entries.Count, _importer.VoxelRegistry.VoxelMap.Count);
            Assert.AreEqual(entries.Count, _importer.VoxelRegistry.GetVoxelGenData().VoxelRenderDefs.Length);
        }

        [Test]
        public void StandardPackage_EveryBiomeResolvesItsBlocks(){
            LoadPackages();
            int biomeCount = 0;
            foreach (VoxelDataPackage package in _packages)
            foreach (Biome biome in package.biomes.Where(b => b)){
                biomeCount++;
                Assert.IsNotNull(biome.TopBlock, $"{biome.name}: TopBlock");
                Assert.IsNotNull(biome.UnderBlock, $"{biome.name}: UnderBlock");
                Assert.IsNotNull(biome.StoneBlock, $"{biome.name}: StoneBlock");
                Assert.IsTrue(_importer.VoxelRegistry.TryGetId(biome.TopBlock.GetFullName(), out ushort top));
                Assert.IsTrue(_importer.VoxelRegistry.TryGetId(biome.UnderBlock.GetFullName(), out ushort under));
                Assert.IsTrue(_importer.VoxelRegistry.TryGetId(biome.StoneBlock.GetFullName(), out ushort stone));

                string fullName = package.packagePrefix + ":" + biome.name;
                ushort biomeId = _importer.BiomeRegistry.GetIdOrThrow(fullName);
                Biome.BiomeDef def = _importer.BiomeRegistry.BiomeArray[biomeId];
                Assert.AreEqual(top, def.topBlock, $"{fullName}: top");
                Assert.AreEqual(under, def.underBlock, $"{fullName}: under");
                Assert.AreEqual(stone, def.stoneBlock, $"{fullName}: stone");

                // Under water the biome's underwater block replaces the top block; an empty field uses the under block.
                Voxel wet = biome.UnderwaterTopBlock ? biome.UnderwaterTopBlock : biome.UnderBlock;
                Assert.IsTrue(_importer.VoxelRegistry.TryGetId(wet.GetFullName(), out ushort wetId), $"{fullName}: underwater");
                Assert.AreEqual(wetId, def.underwaterTopBlock, $"{fullName}: underwater");
                Assert.AreNotEqual("std:Grass", wet.GetFullName(), $"{fullName}: no grass under water");
                if (fullName == "std:Ocean") Assert.AreEqual("std:Sand", wet.GetFullName(), "Ocean floors stay sand");
            }

            Assert.Greater(biomeCount, 0);
            Assert.AreEqual(biomeCount, _importer.BiomeRegistry.BiomeArray.Length);
        }

        [Test]
        public void CreateConfig_ArraysAreCreated(){
            LoadPackages();
            GeneratorConfig config = _importer.CreateConfig();
            Assert.IsTrue(config.Voxels.IsCreated);
            Assert.IsTrue(config.Voxels.ContainsKey("std:air"));
            Assert.IsTrue(config.Voxels.ContainsKey("std:Water"), "FillTerrain needs std:Water");
            Assert.IsTrue(config.Voxels.ContainsKey("std:Ice"), "FillTerrain needs std:Ice");
        }

        [Test]
        public void CreateConfig_DisposingConfigAndRegistries_DoesNotThrow(){
            // GeneratorConfig.Dispose frees only the world-gen config; the voxel map belongs to the registry,
            // which disposes it in OnDestroy. Ownership must be clear: disposing both in runtime order must not throw.
            LoadPackages();
            WorldGenSettings worldGen = ScriptableObject.CreateInstance<WorldGenSettings>();
            GeneratorConfig config;
            try{
                config = _importer.CreateConfig();
                config.WorldGen = worldGen.ToConfig(TestFixtures.Seed, _importer.VoxelRegistry);
            }
            finally{
                Object.DestroyImmediate(worldGen);
            }

            Exception configError = null;
            Exception registryError = null;
            try{
                config.Dispose();
            }
            catch (Exception e){
                configError = e;
            }

            try{
                DisposeRegistries();
            }
            catch (Exception e){
                registryError = e;
            }

            Assert.IsNull(configError, $"GeneratorConfig.Dispose threw: {configError}");
            Assert.IsNull(registryError, $"registry Dispose threw after config.Dispose: {registryError}");
        }

        private const string ShippedSettingsPath = "Assets/EngineSettings/WorldGenSettings.asset";

        private WorldGenConfig ShippedConfig(out WorldGenSettings settings){
            settings = AssetDatabase.LoadAssetAtPath<WorldGenSettings>(ShippedSettingsPath);
            Assert.IsNotNull(settings, ShippedSettingsPath);
            return settings.ToConfig(TestFixtures.Seed, _importer.VoxelRegistry);
        }

        [Test]
        public void ShippedWorldGenSettings_FloraTablesResolveThroughTheRegistry(){
            // Placeholder flora rules on the Standard biomes: every species has a registry id and its replace class.
            LoadPackages();
            WorldGenConfig cfg = ShippedConfig(out WorldGenSettings settings);
            try{
                Assert.IsTrue(cfg.HasFlora);
                Assert.AreEqual(1, cfg.FloraReach, "Tree: 3×3 leaf box");
                Assert.AreEqual(3, settings.TreeSpacingRadius);
                Assert.AreEqual(4, cfg.FloraPadding, "reach + r_tree");
                for (int i = 0; i < cfg.FloraRules.Length; i++){
                    FloraRuleDef d = cfg.FloraRules[i];
                    Assert.AreNotEqual(0, d.Species);
                    if (d.Structure < 0) Assert.AreNotEqual(0, d.Voxel, $"rule {i}: voxel id");
                }

                for (int i = 0; i < cfg.FloraVoxels.Length; i++) Assert.AreNotEqual(0, cfg.FloraVoxels[i].Voxel, $"structure voxel {i}");

                Assert.AreEqual(FloraVoxelClass.Foliage, cfg.VoxelClassOf(Id("std:GrassF")));
                Assert.AreEqual(FloraVoxelClass.Leaves, cfg.VoxelClassOf(Id("std:Leaves")));
                Assert.AreEqual(FloraVoxelClass.Solid, cfg.VoxelClassOf(Id("std:LogOak")));
                Assert.AreEqual(FloraVoxelClass.Solid, cfg.VoxelClassOf(Id("std:Cactus")));
                Assert.AreEqual(FloraVoxelClass.Solid, cfg.VoxelClassOf(Id("std:Water")), "water is never overwritten");
            }
            finally{
                cfg.Dispose();
            }
        }

        private ushort Id(string fullName){
            Assert.IsTrue(_importer.VoxelRegistry.TryGetId(fullName, out ushort id), fullName);
            return id;
        }

        [Test]
        public void ShippedWorldGenSettings_EveryLandBiomeCarriesFlora(){
            // Flora is visible for all land biomes. A 4096² area at stride 8: every biome with
            // flora rules that supplies the surface somewhere also has planted columns.
            LoadPackages();
            WorldGenConfig cfg = ShippedConfig(out WorldGenSettings settings);
            const int n = 512;
            RegionBuffers buffers = new(){
                SurfaceSource = new NativeArray<int>(n * n, Allocator.TempJob),
                FloraSource = new NativeArray<int>(n * n, Allocator.TempJob)
            };
            try{
                WorldGenRequest request = new(){ Config = cfg, Origin = new int2(-2048, -2048), Size = new int2(n, n), Stride = 8 };
                WorldGenApi.SampleRegion(request, ref buffers).Complete();
                List<Biome> biomes = settings.Biomes.Biomes;
                int[] surface = new int[biomes.Count], planted = new int[biomes.Count];
                for (int i = 0; i < n * n; i++){
                    if (buffers.SurfaceSource[i] >= 0) surface[buffers.SurfaceSource[i]]++;
                    if (buffers.FloraSource[i] >= 0) planted[buffers.FloraSource[i]]++;
                }

                string summary = string.Join(", ", biomes.Select((b, i) => $"{b.name}: surface {surface[i]}, planted {planted[i]}"));
                TestContext.WriteLine(summary);
                int landBiomes = 0;
                for (int b = 0; b < biomes.Count; b++){
                    if (biomes[b].FloraRules.Count == 0){
                        Assert.AreEqual(0, planted[b], biomes[b].name + " has no rules");
                        continue;
                    }

                    if (surface[b] == 0) continue;
                    landBiomes++;
                    Assert.Greater(planted[b], 0, $"{biomes[b].name} has no flora ({summary})");
                }

                Assert.GreaterOrEqual(landBiomes, 5, summary);
            }
            finally{
                buffers.SurfaceSource.Dispose();
                buffers.FloraSource.Dispose();
                cfg.Dispose();
            }
        }
    }
}
