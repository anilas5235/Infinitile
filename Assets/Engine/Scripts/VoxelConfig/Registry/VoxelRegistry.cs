using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Engine.Scripts.VoxelConfig.Data;
using Engine.Scripts.VoxelConfig.Data.Internal;
using Engine.Scripts.VoxelConfig.Data.Mesh;
using Engine.Scripts.VoxelConfig.Data.Voxel;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using static UnityEngine.GraphicsBuffer;

namespace Engine.Scripts.VoxelConfig.Registry
{
    /// <summary>
    ///     Registry that manages voxel render definitions, texture arrays and name-to-ID mappings.
    ///     Can be finalized to build a NativeArray for Burst-compatible meshing data.
    /// </summary>
    public class VoxelRegistry : IDisposable
    {
        internal const int TextureSize = 128; // Texture resolution (square)
        private static readonly int TexturesNameID = Shader.PropertyToID("_Textures");

        private readonly Registry<VoxelRenderDef> _voxelRenderDefRegistry = new(100);
        private readonly Registry<VoxelDefinition> _voxelDefinitionRegistry = new(100);
        private readonly Registry<string> _nameRegistry = new(100);
        private readonly QuadRegistry _quadRegistry = new(40);
        private readonly TexRegistry _solidTexRegistry = new(200);
        private readonly TexRegistry _transparentTexRegistry = new(100);
        private readonly TexRegistry _foliageTexRegistry = new(50);
        private readonly List<uint> _quadTexPairs = new();

        private bool _initialized;
        private VoxelEngineRenderGenData _voxelEngineRenderGenData;

        public GraphicsBuffer VoxelRenderDefBuffer { get; private set; }

        public GraphicsBuffer QuadBuffer { get; private set; }

        public GraphicsBuffer QuadTexPairBuffer { get; private set; }

        /// <summary>
        ///     Releases Burst-native resources used by the registry.
        /// </summary>
        public void Dispose()
        {
            if (_voxelEngineRenderGenData.VoxelRenderDefs.IsCreated)
                _voxelEngineRenderGenData.VoxelRenderDefs.Dispose();
            VoxelRenderDefBuffer.Dispose();
            QuadBuffer.Dispose();
            QuadTexPairBuffer.Dispose();
        }

        public void Initialize()
        {
            if (_initialized) return;
            _initialized = true;
            Register("std:air", new VoxelRenderDef
            {
                MeshLayer = MeshLayer.Air,
                Collision = false
            });
            _voxelEngineRenderGenData = new VoxelEngineRenderGenData();
            Texture2D texError = Resources.Load<Texture2D>("Artwork/TexError");
            RegisterTexture(texError, MeshLayer.Solid);
            Texture2D texErrorT = Resources.Load<Texture2D>("Artwork/TexErrorT");
            RegisterTexture(texErrorT, MeshLayer.Transparent);
        }

        /// <summary>
        ///     Registers a voxel definition, builds its texture-based <see cref="VoxelRenderDef" />,
        ///     and assigns a new voxel ID.
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
                Back = RegisterFaces(definition, QuadDrawCondition.Backward)
            };

            ushort id = Register(packagePrefix + ":" + definition.name, type);
            if (id == 0) return;
            _voxelDefinitionRegistry.Register(definition);
        }

        private ushort Register(string name, VoxelRenderDef renderDef)
        {
            Initialize();
            if (_nameRegistry.TryGetId(name, out ushort existingId))
            {
                Debug.LogWarning($"Voxel with name {name} is already registered with ID {existingId}.");
                return existingId;
            }

            ushort id = _nameRegistry.Register(name);
            _voxelRenderDefRegistry.Register(renderDef);
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
        ///     Tries to get an ID for a given name.
        /// </summary>
        /// <param name="name">Registered voxel name.</param>
        /// <param name="id">Resulting voxel ID if found.</param>
        /// <returns><c>true</c> if the name exists; otherwise, <c>false</c>.</returns>
        public bool TryGetId(string name, out ushort id)
        {
            return _nameRegistry.TryGetId(name, out id);
        }

        /// <summary>
        ///     Gets the ID for a given name or throws if it does not exist.
        /// </summary>
        /// <param name="name">Registered voxel name.</param>
        /// <returns>Voxel ID associated with the name.</returns>
        public ushort GetIdOrThrow(string name)
        {
            if (TryGetId(name, out ushort id)) return id;
            throw new KeyNotFoundException($"No voxel found with name {name}");
        }

        /// <summary>
        ///     Tries to get the registered name for a voxel ID.
        /// </summary>
        /// <param name="id">Voxel ID.</param>
        /// <param name="name">Output name if found.</param>
        /// <returns><c>true</c> if the ID exists; otherwise, <c>false</c>.</returns>
        public bool TryGetName(ushort id, out string name)
        {
            return _nameRegistry.TryGet(id, out name);
        }

        /// <summary>
        ///     Tries to get the <see cref="VoxelDefinition" /> associated with the given ID.
        /// </summary>
        /// <param name="id">Voxel ID.</param>
        /// <param name="def">Output voxel definition if found.</param>
        /// <returns><c>true</c> if the ID has an associated definition; otherwise, <c>false</c>.</returns>
        public bool TryGetVoxelDefinition(ushort id, out VoxelDefinition def)
        {
            return _voxelDefinitionRegistry.TryGet(id, out def);
        }

        /// <summary>
        ///     Finalizes the registry by preparing texture arrays and GPU buffers for rendering.
        /// </summary>
        public void FinalizeRegistry()
        {
            PrepareArrays();
            PrepareVoxelGenData();
        }

        private void PrepareVoxelGenData()
        {
            int voxelCount = _voxelRenderDefRegistry.Count;
            if (_voxelEngineRenderGenData.VoxelRenderDefs.IsCreated)
                _voxelEngineRenderGenData.VoxelRenderDefs.Dispose();
            _voxelEngineRenderGenData.VoxelRenderDefs =
                new NativeArray<VoxelRenderDef>(voxelCount, Allocator.Domain);

            VoxelRenderDefBuffer?.Dispose();
            VoxelRenderDefBuffer =
                new GraphicsBuffer(Target.Structured, voxelCount, Marshal.SizeOf<GPUVoxelDef>());
            GPUVoxelDef[] gpuVoxelDefData = new GPUVoxelDef[voxelCount];

            for (int i = 0; i < voxelCount; i++)
            {
                _voxelRenderDefRegistry.TryGet((ushort)i, out VoxelRenderDef def);
                _voxelEngineRenderGenData.VoxelRenderDefs[i] = def;
                gpuVoxelDefData[i] = new GPUVoxelDef(def);
            }

            VoxelRenderDefBuffer.SetData(gpuVoxelDefData);

            QuadBuffer?.Dispose();
            QuadBuffer = new GraphicsBuffer(Target.Structured, _quadRegistry.QuadArray.Length,
                Marshal.SizeOf<QuadDefinition.QuadData>());
            QuadBuffer.SetData(_quadRegistry.QuadArray);

            QuadTexPairBuffer?.Dispose();
            QuadTexPairBuffer = new GraphicsBuffer(Target.Structured, _quadTexPairs.Count, sizeof(uint));
            QuadTexPairBuffer.SetData(_quadTexPairs.ToArray());
        }

        /// <summary>
        ///     Retrieves the data package used for meshing and rendering.
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
        ///     Applies the texture array for a given mesh layer to the specified material (shader property "_Textures").
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
        ///     Returns a list of all registered IDs and their corresponding names.
        /// </summary>
        /// <returns>List of ID/name pairs.</returns>
        public List<KeyValuePair<ushort, string>> GetAllEntries()
        {
            return _nameRegistry.GetAllEntries();
        }

        private struct GPUVoxelDef
        {
            private uint MeshLayer;
            private uint AlwaysRenderAllFaces;
            private half DepthFadeDistance;
            private uint Glow;
            private uint Collision;
            private uint2 shape_quad_indices_alwaysRender;
            private uint2 shape_quad_indices_right;
            private uint2 shape_quad_indices_left;
            private uint2 shape_quad_indices_up;
            private uint2 shape_quad_indices_down;
            private uint2 shape_quad_indices_front;
            private uint2 shape_quad_indices_back;

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
        }
    }
}