using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Engine.Scripts.Utils.Logger;
using Engine.Scripts.VoxelConfig.Data.Internal;
using Engine.Scripts.VoxelConfig.Data.Mesh;
using Engine.Scripts.VoxelConfig.Data.Voxel;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using static UnityEngine.GraphicsBuffer;
using static Engine.Scripts.Utils.VoxelRenderConstants;

namespace Engine.Scripts.VoxelConfig.Registry
{
    /// <summary>
    ///     Registry that manages voxel render definitions, texture arrays and name-to-ID mappings.
    ///     Can be finalized to build a NativeArray for Burst-compatible meshing data.
    /// </summary>
    public class VoxelRegistry : TopLevelRegistry<Voxel>
    {
        internal const int TextureSize = 128; // Texture resolution (square)

        private readonly Registry<Voxel.VoxelDef> _voxelRenderDefRegistry = new(100);
        private readonly QuadRegistry _quadRegistry = new(40);
        private readonly TexRegistry _solidTexRegistry = new(200);
        private readonly TexRegistry _transparentTexRegistry = new(100);
        private readonly TexRegistry _foliageTexRegistry = new(50);
        private readonly List<uint> _quadTexPairs = new();

        private VoxelEngineRenderGenData _voxelEngineRenderGenData;

        public GraphicsBuffer VoxelRenderDefBuffer { get; private set; }

        public GraphicsBuffer QuadBuffer { get; private set; }

        public GraphicsBuffer QuadTexPairBuffer { get; private set; }
        public NativeHashMap<FixedString32Bytes, Voxel.VoxelDef> VoxelMap { get; private set; }

        public void Initialize(Texture2D errorTexSolid, Texture2D errorTexTransparent, Texture2D errorTexFoliage)
        {
            InternalInitialize();

            RegisterAir();

            RegisterTexture(errorTexSolid, MeshLayer.Solid);
            RegisterTexture(errorTexTransparent, MeshLayer.Transparent);
            RegisterTexture(errorTexFoliage, MeshLayer.Foliage);
        }

        private void RegisterAir()
        {
            FixedString32Bytes fullName = new("std:air");
            ushort id = NameRegistry.Register(fullName);
            if (id != 0) throw new Exception("RegisterAir: ID " + id + " already exists");
            _voxelRenderDefRegistry.Register(id, new Voxel.VoxelDef
            {
                Id = id,
                MeshLayer = MeshLayer.Air,
                Collision = false
            });
            VoxelEngineLogger.Info<VoxelRegistry>($"Registered Voxel '{fullName}' with ID {id}");
        }

        protected override void SubRegister(ushort id, FixedString32Bytes fullName, Voxel so)
        {
            Voxel.VoxelDef type = new()
            {
                Id = id,
                MeshLayer = so.meshLayer,
                AlwaysRenderAllFaces = so.alwaysRenderAllFaces,
                DepthFadeDistance = (half)so.depthFadeDistance,
                Glow = (byte)so.glow,
                Collision = so.collision,
                Always = RegisterFaces(so, QuadDrawCondition.Always),
                Right = RegisterFaces(so, QuadDrawCondition.Right),
                Left = RegisterFaces(so, QuadDrawCondition.Left),
                Up = RegisterFaces(so, QuadDrawCondition.Up),
                Down = RegisterFaces(so, QuadDrawCondition.Down),
                Front = RegisterFaces(so, QuadDrawCondition.Forward),
                Back = RegisterFaces(so, QuadDrawCondition.Backward)
            };

            _voxelRenderDefRegistry.Register(id, type);
        }

        private uint2 RegisterFaces(Voxel definition, QuadDrawCondition condition)
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
        ///     Finalizes the registry by preparing texture arrays and GPU buffers for rendering.
        /// </summary>
        public void FinalizeRegistry()
        {
            InternalFinalize();
            PrepareArrays();
            PrepareRenderData();
            PrepareGeneratiionData();
        }

        private void PrepareArrays()
        {
            _solidTexRegistry.PrepareArray();
            _transparentTexRegistry.PrepareArray();
            _foliageTexRegistry.PrepareArray();
            _quadRegistry.PrepareArray();
        }

        private void PrepareGeneratiionData()
        {
            if (VoxelMap.IsCreated) VoxelMap.Clear();
            else
                VoxelMap = new NativeHashMap<FixedString32Bytes, Voxel.VoxelDef>(_voxelRenderDefRegistry.Count,
                    Allocator.Domain);

            for (ushort i = 0; i < _voxelRenderDefRegistry.Count; i++)
            {
                if (_voxelRenderDefRegistry.TryGet(i, out Voxel.VoxelDef def) &&
                    NameRegistry.TryGet(i, out FixedString32Bytes name))
                {
                    if (!VoxelMap.TryAdd(name, def))
                    {
                        VoxelEngineLogger.Warn<VoxelRegistry>($"Failed to add voxel ID {def.Id} to VoxelMap.");
                    }
                }
                else
                {
                    VoxelEngineLogger.Warn<VoxelRegistry>($"Voxel ID {i} not found in _voxelRenderDefRegistry.");
                }
            }
        }

        private void PrepareRenderData()
        {
            int voxelCount = _voxelRenderDefRegistry.Count;
            if (_voxelEngineRenderGenData.VoxelRenderDefs.IsCreated)
                _voxelEngineRenderGenData.VoxelRenderDefs.Dispose();
            _voxelEngineRenderGenData.VoxelRenderDefs =
                new NativeArray<Voxel.VoxelDef>(voxelCount, Allocator.Domain);

            VoxelRenderDefBuffer?.Dispose();
            VoxelRenderDefBuffer =
                new GraphicsBuffer(Target.Structured, voxelCount, Marshal.SizeOf<GPUVoxelDef>());
            GPUVoxelDef[] gpuVoxelDefData = new GPUVoxelDef[voxelCount];

            for (int i = 0; i < voxelCount; i++)
            {
                _voxelRenderDefRegistry.TryGet((ushort)i, out Voxel.VoxelDef def);
                _voxelEngineRenderGenData.VoxelRenderDefs[i] = def;
                gpuVoxelDefData[i] = new GPUVoxelDef(def);
            }

            VoxelRenderDefBuffer.SetData(gpuVoxelDefData);

            QuadBuffer?.Dispose();
            QuadBuffer = new GraphicsBuffer(Target.Structured, _quadRegistry.QuadArray.Length,
                Marshal.SizeOf<QuadDefinition.QuadDef>());
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
            if (!Finalized)
                throw new InvalidOperationException("VoxelRegistry must be finalized before applying to materials.");

            if (material)
            {
                Texture2DArray texArray = GetTextureArray(solid);
                if (texArray)
                    material.SetTexture(TexturesNameID, texArray);
                else
                    VoxelEngineLogger.Error<VoxelRegistry>("Texture array is null, cannot assign to material.");

                material.SetBuffer(QuadBufferNameID, QuadBuffer);
            }
            else
            {
                VoxelEngineLogger.Error<VoxelRegistry>("Voxel material is null, cannot assign texture array.");
            }
        }

        /// <summary>
        ///     Returns a list of all registered IDs and their corresponding names.
        /// </summary>
        /// <returns>List of ID/name pairs.</returns>
        public List<KeyValuePair<ushort, FixedString32Bytes>> GetAllEntries()
        {
            return NameRegistry.GetAllEntries();
        }

        public override void Dispose()
        {
            if (_voxelEngineRenderGenData.VoxelRenderDefs.IsCreated)
                _voxelEngineRenderGenData.VoxelRenderDefs.Dispose();
            VoxelRenderDefBuffer.Dispose();
            QuadBuffer.Dispose();
            QuadTexPairBuffer.Dispose();
            VoxelMap.Dispose();
        }

        //TODO: Compacting?
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

            public GPUVoxelDef(Voxel.VoxelDef def)
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