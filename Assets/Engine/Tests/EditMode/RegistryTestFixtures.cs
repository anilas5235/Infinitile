using System.Collections.Generic;
using Engine.Scripts.VoxelConfig.Data.Generation;
using Engine.Scripts.VoxelConfig.Data.Mesh;
using Engine.Scripts.VoxelConfig.Data.Voxel;
using Engine.Scripts.VoxelConfig.Registry;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Engine.Tests.EditMode{
    /// <summary>
    ///     Builds runtime ScriptableObjects and textures for registry tests and destroys them again in TearDown.
    ///     Everything created here lives only in memory; no asset is written to the project.
    /// </summary>
    internal sealed class RegistryTestFixtures{
        public const string Prefix = "std";
        private readonly List<Object> _created = new();

        public Texture2D CreateTexture(string name, Color32 colour){
            Texture2D tex = new(VoxelRegistry.TextureSize, VoxelRegistry.TextureSize, TextureFormat.RGBA32, false){
                name = name
            };
            Color32[] pixels = new Color32[VoxelRegistry.TextureSize * VoxelRegistry.TextureSize];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = colour;
            tex.SetPixels32(pixels);
            tex.Apply();
            _created.Add(tex);
            return tex;
        }

        public QuadDefinition CreateQuad(string name, Vector3 origin){
            QuadDefinition quad = ScriptableObject.CreateInstance<QuadDefinition>();
            quad.name = name;
            quad.position00 = origin;
            quad.position01 = origin + Vector3.up;
            quad.position02 = origin + Vector3.up + Vector3.right;
            quad.position03 = origin + Vector3.right;
            quad.normal = Vector3.back;
            quad.up = Vector3.up;
            quad.right = Vector3.right;
            quad.uv00 = new Vector2(0, 0);
            quad.uv01 = new Vector2(0, 1);
            quad.uv02 = new Vector2(1, 1);
            quad.uv03 = new Vector2(1, 0);
            _created.Add(quad);
            return quad;
        }

        public VoxelShape CreateShape(string name, params (QuadDefinition quad, QuadDrawCondition condition)[] quads){
            VoxelShape shape = ScriptableObject.CreateInstance<VoxelShape>();
            shape.name = name;
            shape.quads = new VoxelQuad[quads.Length];
            for (int i = 0; i < quads.Length; i++)
                shape.quads[i] = new VoxelQuad{ quadDef = quads[i].quad, drawCondition = quads[i].condition };
            _created.Add(shape);
            return shape;
        }

        public VoxelDataPackage CreatePackage(string prefix){
            VoxelDataPackage package = ScriptableObject.CreateInstance<VoxelDataPackage>();
            package.name = prefix + "Package";
            package.packagePrefix = prefix;
            package.voxel = new List<Voxel>();
            package.biomes = new List<Biome>();
            _created.Add(package);
            return package;
        }

        public Voxel CreateVoxel(string name, VoxelDataPackage package, VoxelShape shape, Texture2D all,
            MeshLayer layer = MeshLayer.Solid, bool collision = true){
            Voxel voxel = ScriptableObject.CreateInstance<Voxel>();
            voxel.name = name;
            voxel.package = package;
            voxel.shape = shape;
            voxel.all = all;
            voxel.meshLayer = layer;
            voxel.collision = collision;
            voxel.TextureMode = Voxel.VoxelTexMode.AllSame;
            package.voxel.Add(voxel);
            _created.Add(voxel);
            return voxel;
        }

        public Biome CreateBiome(string name, Voxel top, Voxel under, Voxel stone, float temperature = 0.5f){
            Biome biome = ScriptableObject.CreateInstance<Biome>();
            biome.name = name;
            biome.TopBlock = top;
            biome.UnderBlock = under;
            biome.StoneBlock = stone;
            biome.TargetTemperature = temperature;
            _created.Add(biome);
            return biome;
        }

        public VoxelStructure CreateStructure(string name, Voxel voxel){
            VoxelStructure structure = ScriptableObject.CreateInstance<VoxelStructure>();
            structure.name = name;
            structure.parts = new[]{ new StructurePart{ voxel = voxel, placements = new VoxelPlacement[1] } };
            _created.Add(structure);
            return structure;
        }

        /// <summary>A small, finalised voxel registry: std:air (0), std:Grass (1), std:Dirt (2), std:Stone (3).</summary>
        public VoxelRegistry CreateStandardVoxelRegistry(out VoxelDataPackage package, out Voxel[] voxels){
            package = CreatePackage(Prefix);
            QuadDefinition quad = CreateQuad("Quad", Vector3.zero);
            VoxelShape shape = CreateShape("Cube", (quad, QuadDrawCondition.Always));
            Texture2D tex = CreateTexture("Tex", new Color32(10, 20, 30, 255));
            voxels = new[]{
                CreateVoxel("Grass", package, shape, tex),
                CreateVoxel("Dirt", package, shape, tex),
                CreateVoxel("Stone", package, shape, tex)
            };

            VoxelRegistry registry = new();
            registry.Initialize(
                CreateTexture("ErrSolid", new Color32(255, 0, 255, 255)),
                CreateTexture("ErrTransparent", new Color32(255, 0, 255, 128)),
                CreateTexture("ErrFoliage", new Color32(255, 0, 255, 64)));
            foreach (Voxel voxel in voxels) registry.Register(Prefix, voxel);
            registry.FinalizeRegistry();
            return registry;
        }

        public void DestroyAll(){
            foreach (Object obj in _created)
                if (obj) Object.DestroyImmediate(obj);
            _created.Clear();
        }
    }
}
