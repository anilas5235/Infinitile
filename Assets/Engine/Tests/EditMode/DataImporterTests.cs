using System;
using System.Collections.Generic;
using System.Linq;
using Engine.Scripts.Jobs.Chunk;
using Engine.Scripts.Settings;
using Engine.Scripts.VoxelConfig;
using Engine.Scripts.VoxelConfig.Data.Generation;
using Engine.Scripts.VoxelConfig.Data.Voxel;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Engine.Tests.EditMode{
    /// <summary>
    ///     TST-E11: the Standard package loads through the same registry calls DataImporter.Awake makes,
    ///     every voxel gets a unique ID, every biome resolves and CreateConfig() yields usable arrays (D-05).
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
            }

            Assert.Greater(biomeCount, 0);
            Assert.AreEqual(biomeCount, _importer.BiomeRegistry.BiomeArray.Length);
        }

        [Test]
        public void CreateConfig_ArraysAreCreated(){
            LoadPackages();
            GeneratorConfig config = _importer.CreateConfig();
            Assert.IsTrue(config.BiomeDefs.IsCreated);
            Assert.IsTrue(config.Voxels.IsCreated);
            Assert.Greater(config.BiomeDefs.Length, 0);
            Assert.IsTrue(config.Voxels.ContainsKey("std:air"));
            Assert.IsTrue(config.Voxels.ContainsKey("std:Water"), "FillTerrain needs std:Water");
            Assert.IsTrue(config.Voxels.ContainsKey("std:Ice"), "FillTerrain needs std:Ice");
        }

        [Category("KnownBug")] // D-05
        [Test]
        public void CreateConfig_DisposingConfigAndRegistries_DoesNotThrow(){
            // D-05: GeneratorConfig.Dispose disposes BiomeDefs/Voxels, which the registries own and dispose
            // again in OnDestroy. Ownership must be clear: disposing both in runtime order must not throw.
            LoadPackages();
            NoiseSettings noise = ScriptableObject.CreateInstance<NoiseSettings>();
            GeneratorConfig config;
            try{
                config = _importer.CreateConfig();
                config.WaterLevel = noise.WaterLevel;
                config.GlobalSeed = TestFixtures.Seed;
                config.NoiseParams = noise.ToNoiseParameters(TestFixtures.Seed);
            }
            finally{
                Object.DestroyImmediate(noise);
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
    }
}
