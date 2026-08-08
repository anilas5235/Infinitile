using System;
using System.Collections.Generic;
using Engine.Scripts.Jobs.Chunk;
using Engine.Scripts.Utils;
using Engine.Scripts.Utils.Logger;
using Engine.Scripts.VoxelConfig.Data.Mesh;
using Engine.Scripts.VoxelConfig.Data.Voxel;
using Engine.Scripts.VoxelConfig.Registry;
using Unity.Collections;
using UnityEngine;
using static Engine.Scripts.Utils.VoxelRenderConstants;

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
            SetUpRenderData();
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
            VoxelRegistry.Initialize();
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
        }

        private void SetUpRenderData()
        {
            VoxelRegistry.FinalizeRegistry();

            VoxelRegistry.ApplyToMaterial(voxelSolidMaterial, MeshLayer.Solid);
            VoxelRegistry.ApplyToMaterial(voxelTransparentMaterial, MeshLayer.Transparent);
            VoxelRegistry.ApplyToMaterial(voxelFoliageMaterial, MeshLayer.Foliage);
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
                BrickGrey = VoxelRegistry.GetIdOrThrow("std:BrickGrey"),
                BrickRed = VoxelRegistry.GetIdOrThrow("std:BrickRed"),
                Cactus = VoxelRegistry.GetIdOrThrow("std:Cactus"),
                CottonBlue = VoxelRegistry.GetIdOrThrow("std:CottonBlue"),
                CottonGreen = VoxelRegistry.GetIdOrThrow("std:CottonGreen"),
                CottonRed = VoxelRegistry.GetIdOrThrow("std:CottonRed"),
                CottonTan = VoxelRegistry.GetIdOrThrow("std:CottonTan"),
                Dirt = VoxelRegistry.GetIdOrThrow("std:Dirt"),
                DirtGravel = VoxelRegistry.GetIdOrThrow("std:DirtGravel"),
                DirtSandy = VoxelRegistry.GetIdOrThrow("std:DirtSandy"),
                DirtSnowy = VoxelRegistry.GetIdOrThrow("std:DirtSnowy"),
                Flowers = VoxelRegistry.GetIdOrThrow("std:Flowers"),
                Glass = VoxelRegistry.GetIdOrThrow("std:Glass"),
                Grass = VoxelRegistry.GetIdOrThrow("std:Grass"),
                GrassF = VoxelRegistry.GetIdOrThrow("std:GrassF"),
                GrassFDead = VoxelRegistry.GetIdOrThrow("std:GrassFDead"),
                GrassFDry = VoxelRegistry.GetIdOrThrow("std:GrassFDry"),
                GreystoneRubyOre = VoxelRegistry.GetIdOrThrow("std:GreystoneRubyOre"),
                Ice = VoxelRegistry.GetIdOrThrow("std:Ice"),
                Lava = VoxelRegistry.GetIdOrThrow("std:Lava"),
                Leaves = VoxelRegistry.GetIdOrThrow("std:Leaves"),
                LeavesOrange = VoxelRegistry.GetIdOrThrow("std:LeavesOrange"),
                LogBirch = VoxelRegistry.GetIdOrThrow("std:LogBirch"),
                LogOak = VoxelRegistry.GetIdOrThrow("std:LogOak"),
                MushroomBrown = VoxelRegistry.GetIdOrThrow("std:MushroomBrown"),
                MushroomRed = VoxelRegistry.GetIdOrThrow("std:MushroomRed"),
                MushroomTan = VoxelRegistry.GetIdOrThrow("std:MushroomTan"),
                Oven = VoxelRegistry.GetIdOrThrow("std:Oven"),
                Planks = VoxelRegistry.GetIdOrThrow("std:Planks"),
                PlanksRed = VoxelRegistry.GetIdOrThrow("std:PlanksRed"),
                Rock = VoxelRegistry.GetIdOrThrow("std:Rock"),
                RockMossy = VoxelRegistry.GetIdOrThrow("std:RockMossy"),
                Sand = VoxelRegistry.GetIdOrThrow("std:Sand"),
                SandGrey = VoxelRegistry.GetIdOrThrow("std:SandGrey"),
                SandRed = VoxelRegistry.GetIdOrThrow("std:SandRed"),
                SandStoneRed = VoxelRegistry.GetIdOrThrow("std:SandStoneRed"),
                SandStoneRedElm = VoxelRegistry.GetIdOrThrow("std:SandStoneRedEmeraldOre"),
                SandStoneRedSandy = VoxelRegistry.GetIdOrThrow("std:SandStoneRedSandy"),
                Stone = VoxelRegistry.GetIdOrThrow("std:Stone"),
                StoneCoalOre = VoxelRegistry.GetIdOrThrow("std:StoneCoalOre"),
                StoneDiamondOre = VoxelRegistry.GetIdOrThrow("std:StoneDiamondOre"),
                StoneGoldOre = VoxelRegistry.GetIdOrThrow("std:StoneGoldOre"),
                StoneGrassy = VoxelRegistry.GetIdOrThrow("std:StoneGrassy"),
                StoneGravel = VoxelRegistry.GetIdOrThrow("std:StoneGravel"),
                StoneGrey = VoxelRegistry.GetIdOrThrow("std:StoneGrey"),
                StoneGreySandy = VoxelRegistry.GetIdOrThrow("std:StoneGreySandy"),
                StoneIronBrownOre = VoxelRegistry.GetIdOrThrow("std:StoneIronBrownOre"),
                StoneIronGreenOre = VoxelRegistry.GetIdOrThrow("std:StoneIronGreenOre"),
                StoneSandy = VoxelRegistry.GetIdOrThrow("std:StoneSandy"),
                StoneSilverOre = VoxelRegistry.GetIdOrThrow("std:StoneSilverOre"),
                StoneSnowy = VoxelRegistry.GetIdOrThrow("std:StoneSnowy"),
                Snow = VoxelRegistry.GetIdOrThrow("std:Snow"),
                Water = VoxelRegistry.GetIdOrThrow("std:Water"),
                WheatStage1 = VoxelRegistry.GetIdOrThrow("std:WheatStage1"),
                WheatStage2 = VoxelRegistry.GetIdOrThrow("std:WheatStage2"),
                WheatStage3 = VoxelRegistry.GetIdOrThrow("std:WheatStage3"),
                WheatStage4 = VoxelRegistry.GetIdOrThrow("std:WheatStage4"),
                Workbench = VoxelRegistry.GetIdOrThrow("std:Workbench")
            };

            return config;
        }
    }
}