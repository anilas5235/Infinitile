using System.Collections.Generic;
using System.Runtime.InteropServices;
using Runtime.Engine.Jobs.Chunk;
using Runtime.Engine.Utils;
using Runtime.Engine.Utils.Extensions;
using Unity.Mathematics;
using UnityEngine;
using static Runtime.Engine.Utils.VoxelRenderConstants;

namespace Runtime.Engine.VoxelConfig.Data
{
    /// <summary>
    /// Loads all <see cref="VoxelDataPackage"/> assets from Resources, registers their definitions in the
    /// <see cref="VoxelRegistry"/>, and updates materials with texture atlas and mesh layer information.
    /// The singleton lifecycle controls the registry lifetime.
    /// </summary>
    [DefaultExecutionOrder(-1000)]
    public class VoxelDataImporter : Singleton<VoxelDataImporter>
    {
        /// <summary>
        /// Material used for opaque voxel rendering (solid mesh layer).
        /// </summary>
        public Material voxelSolidMaterial;

        /// <summary>
        /// Material used for transparent / alpha voxel rendering (transparent mesh layer).
        /// </summary>
        public Material voxelTransparentMaterial;

        public Material voxelFoliageMaterial;

        /// <summary>
        /// Registry containing all registered <see cref="VoxelDefinition"/> instances.
        /// </summary>
        public VoxelRegistry VoxelRegistry { get; } = new();

        /// <summary>
        /// Loads packages, registers voxels and updates materials when the importer is created.
        /// </summary>
        protected override void Awake()
        {
            base.Awake();
            VoxelRegistry.Initialize();
            VoxelDataPackage[] voxelDataPackages = Resources.LoadAll<VoxelDataPackage>("VoxelDataPackages");
            if (voxelDataPackages == null || voxelDataPackages.Length == 0)
            {
                Debug.LogError("No VoxelDataPackage found in Resources/VoxelDataPackages. Please create a package.");
                return;
            }

            foreach (VoxelDataPackage package in voxelDataPackages) RegisterPackage(package);

            UpdateMaterials();

            voxelSolidMaterial.SetBuffer(QuadBufferID, VoxelRegistry.QuadBuffer);
            voxelTransparentMaterial.SetBuffer(QuadBufferID, VoxelRegistry.QuadBuffer);
            voxelFoliageMaterial.SetBuffer(QuadBufferID, VoxelRegistry.QuadBuffer);
        }

        /// <summary>
        /// Applies registry texture data to the configured materials.
        /// </summary>
        private void UpdateMaterials()
        {
            VoxelRegistry.ApplyToMaterial(voxelSolidMaterial, MeshLayer.Solid);
            VoxelRegistry.ApplyToMaterial(voxelTransparentMaterial, MeshLayer.Transparent);
            VoxelRegistry.ApplyToMaterial(voxelFoliageMaterial, MeshLayer.Foliage);
        }

        /// <summary>
        /// Registers all definitions contained in a package and finalizes the registry (sorting/atlas build).
        /// </summary>
        /// <param name="package">Voxel data package whose definitions should be registered.</param>
        private void RegisterPackage(VoxelDataPackage package)
        {
            string prefix = package.packagePrefix;
            if (string.IsNullOrEmpty(prefix))
            {
                Debug.LogWarning("VoxelDataPackage prefix is empty. Using default 'UserPackage'.");
                prefix = "UserPackage";
            }

            foreach (VoxelDefinition definition in package.voxelTextures)
            {
                if (!definition)
                {
                    Debug.LogWarning("Found null VoxelDefinition in package: " + package.name);
                    continue;
                }

                VoxelRegistry.Register(prefix, definition);
            }

            VoxelRegistry.FinalizeRegistry();
        }

        /// <summary>
        /// Disposes the registry when the importer is destroyed.
        /// </summary>
        protected override void OnDestroy()
        {
            base.OnDestroy();
            VoxelRegistry.Dispose();
        }

        /// <summary>
        /// Creates a <see cref="GeneratorConfig"/> with resolved voxel IDs for procedural world generation.
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
                Workbench = VoxelRegistry.GetIdOrThrow("std:Workbench"),
            };

            return config;
        }
    }
}