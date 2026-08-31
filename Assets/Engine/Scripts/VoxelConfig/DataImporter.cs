using System;
using System.Collections.Generic;
using Engine.Scripts.Jobs.Chunk;
using Engine.Scripts.Utils;
using Engine.Scripts.Utils.Logger;
using Engine.Scripts.VoxelConfig.Data.Generation;
using Engine.Scripts.VoxelConfig.Data.Mesh;
using Engine.Scripts.VoxelConfig.Data.Voxel;
using Engine.Scripts.VoxelConfig.Registry;
using Engine.Scripts.World;
using UnityEngine;
using Biome = Engine.Scripts.VoxelConfig.Data.Generation.Biome;
using FixedString32Bytes = Unity.Collections.FixedString32Bytes;

namespace Engine.Scripts.VoxelConfig
{
    /// <summary>
    ///     Loads all <see cref="VoxelDataPackage" /> assets from Resources, registers their definitions in the
    ///     <see cref="VoxelRegistry" />, and updates materials with texture atlas and mesh layer information.
    ///     The singleton lifecycle controls the registry lifetime.
    /// </summary>
    [DefaultExecutionOrder(-1000)]
    public class DataImporter : Singleton<DataImporter>
    {
        public VoxelWorld world;

        /// <summary>
        ///     Material used for opaque voxel rendering (solid mesh layer).
        /// </summary>
        public Material voxelSolidMaterial;

        /// <summary>
        ///     Material used for transparent / alpha voxel rendering (transparent mesh layer).
        /// </summary>
        public Material voxelTransparentMaterial;

        /// <summary>
        /// Material used for foliage voxel rendering (foliage mesh layer).
        /// </summary>
        public Material voxelFoliageMaterial;

        /// <summary>
        ///     Registry containing all registered <see cref="Voxel" /> instances.
        /// </summary>
        public VoxelRegistry VoxelRegistry { get; } = new();

        public BiomeRegistry BiomeRegistry { get; } = new();

        public VoxelStructureRegistry VoxelStructureRegistry { get; } = new();

        private Dictionary<FixedString32Bytes, VoxelDataPackage> _voxelPackages;

        /// <summary>
        ///     Loads packages, registers voxels and updates materials when the importer is created.
        /// </summary>
        protected override void Awake()
        {
            base.Awake();
            _voxelPackages = FindPackages();
            LoadVoxels();
            LoadBioms();
            LoadVoxelStructures();
            SetUpRenderData();
            SetUpGenerationData();
            world.gameObject.SetActive(true);
        }

        private Dictionary<FixedString32Bytes, VoxelDataPackage> FindPackages()
        {
            Dictionary<FixedString32Bytes, VoxelDataPackage> packages = new();

            VoxelDataPackage[] voxelDataPackages = Resources.LoadAll<VoxelDataPackage>("VoxelDataPackages");

            if (voxelDataPackages == null || voxelDataPackages.Length == 0)
            {
                throw new InvalidOperationException("No VoxelDataPackage found in Resources/VoxelDataPackages.");
            }

            foreach (VoxelDataPackage package in voxelDataPackages)
            {
                FixedString32Bytes prefix = package.packagePrefix;
                if (prefix.IsEmpty)
                {
                    VoxelEngineLogger.Warn<DataImporter>("VoxelDataPackage prefix is empty. Package will be ignored.");
                    continue;
                }

                packages.Add(prefix, package);
            }

            VoxelEngineLogger.Info<DataImporter>($"Found {packages.Count} valid packages.");

            return packages;
        }

        private void LoadVoxels()
        {
            Texture2D texError = Resources.Load<Texture2D>("Artwork/TexError");
            Texture2D texErrorT = Resources.Load<Texture2D>("Artwork/TexErrorT");
            VoxelRegistry.Initialize(texError, texErrorT, texErrorT);

            foreach ((FixedString32Bytes prefix, VoxelDataPackage package) in _voxelPackages)
            {
                foreach (Voxel definition in package.voxel)
                {
                    if (!definition)
                    {
                        VoxelEngineLogger.Warn<DataImporter>("Found null VoxelDefinition in package: " + prefix);
                        continue;
                    }

                    VoxelRegistry.Register(prefix, definition);
                }
            }
        }

        private void LoadBioms()
        {
            BiomeRegistry.Initialize();

            foreach ((FixedString32Bytes prefix, VoxelDataPackage package) in _voxelPackages)
            {
                foreach (Biome biome in package.biomes)
                {
                    if (!biome)
                    {
                        VoxelEngineLogger.Warn<DataImporter>("Found null BiomeDefinition in package: " + prefix);
                        continue;
                    }

                    BiomeRegistry.Register(prefix, biome);
                }
            }
        }

        private void LoadVoxelStructures()
        {
            VoxelStructureRegistry.Initialize();

            foreach ((FixedString32Bytes prefix, VoxelDataPackage package) in _voxelPackages)
            {
                foreach (VoxelStructure structure in package.structures)
                {
                    if (!structure)
                    {
                        VoxelEngineLogger.Warn<DataImporter>(
                            "Found null VoxelStructureDefinition in package: " + prefix);
                        continue;
                    }

                    VoxelStructureRegistry.Register(prefix, structure);
                }
            }
        }

        private void SetUpRenderData()
        {
            VoxelRegistry.FinalizeRegistry();

            VoxelRegistry.ApplyToMaterial(voxelSolidMaterial, MeshLayer.Solid);
            VoxelRegistry.ApplyToMaterial(voxelTransparentMaterial, MeshLayer.Transparent);
            VoxelRegistry.ApplyToMaterial(voxelFoliageMaterial, MeshLayer.Foliage);
        }

        private void SetUpGenerationData()
        {
            BiomeRegistry.FinalizeRegistry(VoxelRegistry);
            VoxelStructureRegistry.FinalizeRegistry(VoxelRegistry);
        }

        /// <summary>
        ///     Disposes the registry when the importer is destroyed.
        /// </summary>
        protected override void OnDestroy()
        {
            base.OnDestroy();
            VoxelRegistry.Dispose();
            BiomeRegistry.Dispose();
            VoxelStructureRegistry.Dispose();
        }

        /// <summary>
        ///     Creates a <see cref="GeneratorConfig" /> with resolved voxel IDs for procedural world generation.
        /// </summary>
        /// <returns>Filled generator configuration structure.</returns>
        public GeneratorConfig CreateConfig()
        {
            GeneratorConfig config = new()
            {
                BiomeDefs = BiomeRegistry.BiomeArray,
                Voxels = VoxelRegistry.VoxelMap,
            };

            return config;
        }
    }
}