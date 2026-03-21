using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using static UnityEngine.GraphicsBuffer;

namespace Runtime.Engine.VoxelConfig.Data
{
    /// <summary>
    /// Registry that manages voxel render definitions, texture arrays and name-to-ID mappings.
    /// Can be finalized to build a NativeArray for Burst-compatible meshing data.
    /// </summary>
    public class VoxelRegistry : IDisposable
    {
        private static readonly int TexturesNameID = Shader.PropertyToID("_Textures");
        internal const int TextureSize = 128; // Texture resolution (square)
        private readonly Dictionary<string, ushort> _nameToId = new();
        private readonly Dictionary<ushort, string> _idToName = new();
        private readonly Dictionary<ushort, VoxelRenderDef> _idToVoxel = new(100);
        private readonly Dictionary<ushort, VoxelDefinition> _idToVoxelDefinition = new(100);
        private VoxelEngineRenderGenData _voxelEngineRenderGenData;
        private readonly TexRegistry _solidTexRegistry = new();
        private readonly TexRegistry _transparentTexRegistry = new();
        private readonly TexRegistry _foliageTexRegistry = new();
        private readonly QuadRegistry _quadRegistry = new();
        private readonly List<uint> _quadTexPairs = new();
        private bool _initialized;

        private GraphicsBuffer _voxelRenderDefBuffer;
        private GraphicsBuffer _quadBuffer;
        private GraphicsBuffer _quadTexPairBuffer;

        public GraphicsBuffer VoxelRenderDefBuffer => _voxelRenderDefBuffer;
        public GraphicsBuffer QuadBuffer => _quadBuffer;
        public GraphicsBuffer QuadTexPairBuffer => _quadTexPairBuffer;

        public void Initialize()
        {
            if (_initialized) return;
            _initialized = true;
            Register("std:air", new VoxelRenderDef
            {
                MeshLayer = MeshLayer.Air,
                Collision = false,
            });
            _voxelEngineRenderGenData = new VoxelEngineRenderGenData();
            Texture2D texError = Resources.Load<Texture2D>("Artwork/TexError");
            RegisterTexture(texError, MeshLayer.Solid);
            Texture2D texErrorT = Resources.Load<Texture2D>("Artwork/TexErrorT");
            RegisterTexture(texErrorT, MeshLayer.Transparent);
        }

        /// <summary>
        /// Registers a voxel definition, builds its texture-based <see cref="VoxelRenderDef"/>, 
        /// and assigns a new voxel ID.
        /// </summary>
        /// <param name="packagePrefix">Prefix of the package this definition belongs to.</param>
        /// <param name="definition">Voxel definition asset to register.</param>
        public void Register(string packagePrefix, VoxelDefinition definition)
        {
            VoxelRenderDef type = new()
            {
                MeshLayer = definition.meshLayer,
                AlwaysRenderAllFaces = definition.alwaysRenderAllFaces,
                DepthFadeDistance = (half)definition.depthFadeDistance,
                Glow = (byte)definition.glow,
                Collision = definition.collision,
                Always = RegisterFaces(definition, QuadDrawCondition.Always),
                Right = RegisterFaces(definition, QuadDrawCondition.Right),
                Left = RegisterFaces(definition, QuadDrawCondition.Left),
                Up = RegisterFaces(definition, QuadDrawCondition.Up),
                Down = RegisterFaces(definition, QuadDrawCondition.Down),
                Front = RegisterFaces(definition, QuadDrawCondition.Forward),
                Back = RegisterFaces(definition, QuadDrawCondition.Backward),
            };

            ushort id = Register(packagePrefix + ":" + definition.name, type);
            if (id == 0) return;
            _idToVoxelDefinition.Add(id, definition);
        }

        private ushort Register(string name, VoxelRenderDef renderDef)
        {
            Initialize();
            if (_nameToId.ContainsKey(name))
            {
                Debug.LogWarning($"Voxel with name {name} is already registered.");
                return 0;
            }

            ushort id = (ushort)_idToVoxel.Count;
            _idToVoxel.Add(id, renderDef);
            _nameToId.Add(name, id);
            _idToName.Add(id, name);
            return id;
        }

        private uint2 RegisterFaces(VoxelDefinition definition, QuadDrawCondition condition)
        {
            int baseIndex = _quadTexPairs.Count;
            int texPairsAdded = 0;
            foreach ((QuadDefinition qDef, Texture2D tex) in definition.GetQuadsAndTextures(condition))
            {
                ushort texId = RegisterTexture(tex, definition.meshLayer);
                ushort quadId = _quadRegistry.Register(qDef);
                _quadTexPairs.Add(quadId | ((uint)texId << 16));
                texPairsAdded++;
            }
            return new uint2((uint)baseIndex, (uint)texPairsAdded);
        }

        private ushort RegisterTexture(Texture2D tex, MeshLayer meshLayer)
        {
            return meshLayer switch
            {
                MeshLayer.Solid => _solidTexRegistry.Register(tex),
                MeshLayer.Transparent => _transparentTexRegistry.Register(tex),
                MeshLayer.Foliage => _foliageTexRegistry.Register(tex),
                _ => 0
            };
        }

        /// <summary>
        /// Tries to get an ID for a given name.
        /// </summary>
        /// <param name="name">Registered voxel name.</param>
        /// <param name="id">Resulting voxel ID if found.</param>
        /// <returns><c>true</c> if the name exists; otherwise, <c>false</c>.</returns>
        public bool GetId(string name, out ushort id) => _nameToId.TryGetValue(name, out id);

        /// <summary>
        /// Gets the ID for a given name or throws if it does not exist.
        /// </summary>
        /// <param name="name">Registered voxel name.</param>
        /// <returns>Voxel ID associated with the name.</returns>
        public ushort GetIdOrThrow(string name) => _nameToId[name];

        /// <summary>
        /// Tries to get the registered name for a voxel ID.
        /// </summary>
        /// <param name="id">Voxel ID.</param>
        /// <param name="name">Output name if found.</param>
        /// <returns><c>true</c> if the ID exists; otherwise, <c>false</c>.</returns>
        public bool GetName(ushort id, out string name) => _idToName.TryGetValue(id, out name);

        /// <summary>
        /// Tries to get the <see cref="VoxelDefinition"/> associated with the given ID.
        /// </summary>
        /// <param name="id">Voxel ID.</param>
        /// <param name="def">Output voxel definition if found.</param>
        /// <returns><c>true</c> if the ID has an associated definition; otherwise, <c>false</c>.</returns>
        public bool GetVoxelDefinition(ushort id, out VoxelDefinition def) =>
            _idToVoxelDefinition.TryGetValue(id, out def);

        /// <summary>
        /// Finalizes registration: builds texture arrays and a native array for render definitions.
        /// </summary>
        public void FinalizeRegistry()
        {
            PrepareArrays();
            PrepareVoxelGenData();
        }

        private void PrepareVoxelGenData()
        {
            if (_voxelEngineRenderGenData.VoxelRenderDefs.IsCreated)
                _voxelEngineRenderGenData.VoxelRenderDefs.Dispose();
            _voxelEngineRenderGenData.VoxelRenderDefs =
                new NativeArray<VoxelRenderDef>(_idToVoxel.Count, Allocator.Persistent);

            _voxelRenderDefBuffer?.Dispose();
            _voxelRenderDefBuffer = new GraphicsBuffer(Target.Structured, _idToVoxel.Count, Marshal.SizeOf<GPUVoxelDef>());
            GPUVoxelDef[] gpuVoxelDefData = new GPUVoxelDef[_idToVoxel.Count];

            for (int i = 0; i < _idToVoxel.Count; i++)
            {
                VoxelRenderDef def = _idToVoxel[(ushort)i];
                _voxelEngineRenderGenData.VoxelRenderDefs[i] = def;
                gpuVoxelDefData[i] = new GPUVoxelDef(def);
            }

            _voxelRenderDefBuffer.SetData(gpuVoxelDefData);
            
            _quadBuffer?.Dispose();
            _quadBuffer = new GraphicsBuffer(Target.Structured, _quadRegistry.QuadArray.Length, Marshal.SizeOf<QuadDefinition.QuadData>());
            _quadBuffer.SetData(_quadRegistry.QuadArray);
            
            _quadTexPairBuffer?.Dispose();
            _quadTexPairBuffer = new GraphicsBuffer(Target.Structured, _quadTexPairs.Count, sizeof(uint));
            _quadTexPairBuffer.SetData(_quadTexPairs.ToArray());
        }

        struct GPUVoxelDef
        {
            uint MeshLayer;
            uint AlwaysRenderAllFaces;
            half DepthFadeDistance;
            uint Glow;
            uint Collision;
            uint2 shape_quad_indices_alwaysRender;
            uint2 shape_quad_indices_right;
            uint2 shape_quad_indices_left;
            uint2 shape_quad_indices_up;
            uint2 shape_quad_indices_down;
            uint2 shape_quad_indices_front;
            uint2 shape_quad_indices_back;

            public GPUVoxelDef(VoxelRenderDef def)
            {
                MeshLayer = (uint)def.MeshLayer;
                AlwaysRenderAllFaces = def.AlwaysRenderAllFaces ? 1u : 0u;
                DepthFadeDistance = def.DepthFadeDistance;
                Glow = def.Glow;
                Collision = def.Collision ? 1u : 0u;
                shape_quad_indices_alwaysRender = def.Always;
                shape_quad_indices_right = def.Right;
                shape_quad_indices_left = def.Left;
                shape_quad_indices_up = def.Up;
                shape_quad_indices_down = def.Down;
                shape_quad_indices_front = def.Front;
                shape_quad_indices_back = def.Back;
            }
        };

        /// <summary>
        /// Retrieves the data package used for meshing and rendering.
        /// </summary>
        /// <returns>Voxel engine render generation data structure.</returns>
        public VoxelEngineRenderGenData GetVoxelGenData()
        {
            return _voxelEngineRenderGenData;
        }

        private void PrepareArrays()
        {
            _solidTexRegistry.PrepareArray();
            _transparentTexRegistry.PrepareArray();
            _foliageTexRegistry.PrepareArray();
            _quadRegistry.PrepareArray();
        }

        private Texture2DArray GetTextureArray(MeshLayer meshLayer)
        {
            return meshLayer switch
            {
                MeshLayer.Solid => _solidTexRegistry.TextureArray,
                MeshLayer.Transparent => _transparentTexRegistry.TextureArray,
                MeshLayer.Foliage => _foliageTexRegistry.TextureArray,
                _ => null
            };
        }

        /// <summary>
        /// Releases Burst-native resources used by the registry.
        /// </summary>
        public void Dispose()
        {
            if (_voxelEngineRenderGenData.VoxelRenderDefs.IsCreated)
                _voxelEngineRenderGenData.VoxelRenderDefs.Dispose();
            _voxelRenderDefBuffer.Dispose();
            _quadBuffer.Dispose();
            _quadTexPairBuffer.Dispose();
        }

        /// <summary>
        /// Applies the texture array for a given mesh layer to the specified material (shader property "_Textures").
        /// </summary>
        /// <param name="material">Material to assign the texture array to.</param>
        /// <param name="solid">Mesh layer whose texture array should be used.</param>
        public void ApplyToMaterial(Material material, MeshLayer solid)
        {
            if (material)
            {
                Texture2DArray texArray = GetTextureArray(solid);
                if (texArray)
                    material.SetTexture(TexturesNameID, texArray);
                else
                    Debug.LogWarning("Texture array is null, cannot assign to material.");
            }
            else
            {
                Debug.LogWarning("Voxel material is null, cannot assign texture array.");
            }
        }

        /// <summary>
        /// Returns a list of all registered IDs and their corresponding names.
        /// </summary>
        /// <returns>List of ID/name pairs.</returns>
        public List<KeyValuePair<ushort, string>> GetAllEntries()
        {
            return _idToName.ToList();
        }
    }
}