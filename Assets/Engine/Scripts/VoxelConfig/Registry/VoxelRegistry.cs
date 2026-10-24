using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Engine.Scripts.Data;
using Engine.Scripts.Utils.Logger;
using Engine.Scripts.VoxelConfig.Data.Internal;
using Engine.Scripts.VoxelConfig.Data.Mesh;
using Engine.Scripts.VoxelConfig.Data.Voxel;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using static UnityEngine.GraphicsBuffer;
using static Engine.Scripts.Utils.VoxelRenderConstants;

namespace Engine.Scripts.VoxelConfig.Registry{
    /// <summary>
    ///     Registry that manages voxel render definitions, texture arrays and name-to-ID mappings.
    ///     Can be finalized to build a NativeArray for Burst-compatible meshing data.
    /// </summary>
    public class VoxelRegistry : TopLevelRegistry<Voxel>{
        internal const int TextureSize = 128; // Texture resolution (square)
        private readonly TexRegistry _foliageTexRegistry = new(50);
        private readonly QuadRegistry _quadRegistry = new(40);
        private readonly List<uint> _quadTexPairs = new();
        private readonly TexRegistry _solidTexRegistry = new(200);
        private readonly TexRegistry _transparentTexRegistry = new(100);

        private readonly Registry<Voxel.VoxelDef> _voxelRenderDefRegistry = new(100);
        private readonly List<VolumeFogDef> _volumeFogs = new();

        private VoxelEngineRenderGenData _voxelEngineRenderGenData;
        private VoxelLightTable _lightTable;

        public GraphicsBuffer VoxelRenderDefBuffer{ get; private set; }

        public GraphicsBuffer QuadBuffer{ get; private set; }

        public GraphicsBuffer QuadTexPairBuffer{ get; private set; }

        /// <summary>
        ///     Volume fog of the transparent voxels with post-process fog, by <see cref="Voxel.VoxelDef.VolumeFog" />
        ///     slot (slot 0 unused); the transparent shader reads it as <c>_VoxelVolumeFog</c>.
        /// </summary>
        public GraphicsBuffer VolumeFogBuffer{ get; private set; }
        public NativeHashMap<FixedString32Bytes, Voxel.VoxelDef> VoxelMap{ get; private set; }

        public void Initialize(Texture2D errorTexSolid, Texture2D errorTexTransparent, Texture2D errorTexFoliage){
            InternalInitialize();
            _volumeFogs.Clear();
            _volumeFogs.Add(default); // slot 0 = no volume fog

            RegisterAir();

            RegisterTexture(errorTexSolid, MeshLayer.Solid);
            RegisterTexture(errorTexTransparent, MeshLayer.Transparent);
            RegisterTexture(errorTexFoliage, MeshLayer.Foliage);
        }

        private void RegisterAir(){
            FixedString32Bytes fullName = new("std:air");
            ushort id = NameRegistry.Register(fullName);
            if (id != 0) throw new Exception("RegisterAir: ID " + id + " already exists");
            _voxelRenderDefRegistry.Register(id, new Voxel.VoxelDef{
                Id = id,
                MeshLayer = MeshLayer.Air,
                Collision = false,
                LightAttenuation = 0 // air is clear
            });
            VoxelEngineLogger.Info<VoxelRegistry>($"Registered Voxel '{fullName}' with ID {id}");
        }

        protected override void SubRegister(ushort id, FixedString32Bytes fullName, Voxel so){
            Voxel.VoxelDef type = new(){
                Id = id,
                MeshLayer = so.meshLayer,
                AlwaysRenderAllFaces = so.alwaysRenderAllFaces,
                VolumeFog = RegisterVolumeFog(so),
                SurfaceAlpha = (byte)math.round(math.saturate(so.surfaceAlpha) * 255f),
                Glow = (byte)so.glow,
                Collision = so.collision,
                LightAttenuation = (byte)math.clamp(so.lightAttenuation, 0, VoxelLightLimits.MaxLevel),
                LightEmission = VoxelLight.PackRgb(
                    math.clamp(so.lightEmission.x, 0, VoxelLightLimits.MaxLevel),
                    math.clamp(so.lightEmission.y, 0, VoxelLightLimits.MaxLevel),
                    math.clamp(so.lightEmission.z, 0, VoxelLightLimits.MaxLevel)),
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

        private uint2 RegisterFaces(Voxel definition, QuadDrawCondition condition){
            int baseIndex = _quadTexPairs.Count;
            int texPairsAdded = 0;
            foreach ((QuadDefinition qDef, Texture2D tex) in definition.GetQuadsAndTextures(condition)){
                ushort texId = RegisterTexture(tex, definition.meshLayer);
                ushort quadId = _quadRegistry.Register(qDef);
                _quadTexPairs.Add(quadId | ((uint)texId << 16));
                texPairsAdded++;
            }

            return new uint2((uint)baseIndex, (uint)texPairsAdded);
        }

        private ushort RegisterTexture(Texture2D tex, MeshLayer meshLayer){
            return meshLayer switch{
                MeshLayer.Solid => _solidTexRegistry.Register(tex),
                MeshLayer.Transparent => _transparentTexRegistry.Register(tex),
                MeshLayer.Foliage => _foliageTexRegistry.Register(tex),
                _ => 0
            };
        }

        /// <summary>
        ///     Finalizes the registry by preparing texture arrays and GPU buffers for rendering.
        /// </summary>
        public void FinalizeRegistry(){
            InternalFinalize();
            PrepareArrays();
            PrepareRenderData();
            PrepareGeneratiionData();
        }

        private void PrepareArrays(){
            _solidTexRegistry.PrepareArray();
            _transparentTexRegistry.PrepareArray();
            _foliageTexRegistry.PrepareArray();
            _quadRegistry.PrepareArray();
        }

        private void PrepareGeneratiionData(){
            if (VoxelMap.IsCreated) VoxelMap.Clear();
            else{
                VoxelMap = new NativeHashMap<FixedString32Bytes, Voxel.VoxelDef>(_voxelRenderDefRegistry.Count,
                    Allocator.Domain);
            }

            for (ushort i = 0; i < _voxelRenderDefRegistry.Count; i++){
                if (_voxelRenderDefRegistry.TryGet(i, out Voxel.VoxelDef def) &&
                    NameRegistry.TryGet(i, out FixedString32Bytes name)){
                    if (!VoxelMap.TryAdd(name, def))
                        VoxelEngineLogger.Warn<VoxelRegistry>($"Failed to add voxel ID {def.Id} to VoxelMap.");
                }
                else
                    VoxelEngineLogger.Warn<VoxelRegistry>($"Voxel ID {i} not found in _voxelRenderDefRegistry.");
            }
        }

        private void PrepareRenderData(){
            int voxelCount = _voxelRenderDefRegistry.Count;
            if (_voxelEngineRenderGenData.VoxelRenderDefs.IsCreated)
                _voxelEngineRenderGenData.VoxelRenderDefs.Dispose();
            _voxelEngineRenderGenData.VoxelRenderDefs =
                new NativeArray<Voxel.VoxelDef>(voxelCount, Allocator.Domain);

            VoxelRenderDefBuffer?.Dispose();
            VoxelRenderDefBuffer =
                new GraphicsBuffer(Target.Structured, voxelCount, Marshal.SizeOf<GPUVoxelDef>());
            GPUVoxelDef[] gpuVoxelDefData = new GPUVoxelDef[voxelCount];

            _lightTable.Dispose();
            _lightTable = new VoxelLightTable(voxelCount, Allocator.Domain);

            for (int i = 0; i < voxelCount; i++){
                bool registered = _voxelRenderDefRegistry.TryGet((ushort)i, out Voxel.VoxelDef def);
                _voxelEngineRenderGenData.VoxelRenderDefs[i] = def;
                // An unregistered id blocks light like a solid block
                byte attenuation = registered ? def.LightAttenuation : (byte)VoxelLightLimits.MaxLevel;
                gpuVoxelDefData[i] = new GPUVoxelDef(def, attenuation);
                _lightTable.Set(i, attenuation, registered ? def.LightEmission : (ushort)0);
            }

            VoxelRenderDefBuffer.SetData(gpuVoxelDefData);

            QuadBuffer?.Dispose();
            QuadBuffer = new GraphicsBuffer(Target.Structured, _quadRegistry.QuadArray.Length,
                Marshal.SizeOf<QuadDefinition.QuadDef>());
            QuadBuffer.SetData(_quadRegistry.QuadArray);

            QuadTexPairBuffer?.Dispose();
            QuadTexPairBuffer = new GraphicsBuffer(Target.Structured, _quadTexPairs.Count, sizeof(uint));
            QuadTexPairBuffer.SetData(_quadTexPairs.ToArray());

            VolumeFogBuffer?.Dispose();
            VolumeFogBuffer =
                new GraphicsBuffer(Target.Structured, _volumeFogs.Count, Marshal.SizeOf<VolumeFogDef>());
            VolumeFogBuffer.SetData(_volumeFogs);
            Shader.SetGlobalBuffer(VoxelVolumeFogNameID, VolumeFogBuffer);
        }

        /// <summary>
        ///     Gives a transparent voxel with post-process fog or a depth fade a volume slot. With fog, the scene seen
        ///     through its surface gets the underwater fog and colour grading; the grading values are prepared
        ///     as URP prepares them for the underwater view (VoxelPostProcessHandler). 0 for every other voxel.
        /// </summary>
        private byte RegisterVolumeFog(Voxel so){
            if (so.meshLayer != MeshLayer.Transparent) return 0;
            bool fogged = so.usePostProcess && so.postProcess.enableFog;
            if (!fogged && so.depthFadeDistance <= 0f) return 0;
            if (_volumeFogs.Count > byte.MaxValue){
                VoxelEngineLogger.Warn<VoxelRegistry>($"No volume fog slot left for '{so.name}'");
                return 0;
            }

            VoxelPostProcessData post = so.postProcess;
            Vector3Int emission = so.lightEmission;
            Vector4 color = post.postProcessColor;
            (Vector4 lift, Vector4 invGamma, Vector4 gain) = ColorUtils.PrepareLiftGammaGain(color, color, color);
            _volumeFogs.Add(new VolumeFogDef{
                FogColor = new float3(post.fogColor.r, post.fogColor.g, post.fogColor.b),
                Density = post.fogDensity,
                Lift = ((float4)lift).xyz,
                SelfIllumination = math.cmax(new float3(emission.x, emission.y, emission.z)) / VoxelLightLimits.MaxLevel,
                Gain = ((float4)gain).xyz,
                Contrast = post.contrast / 100f + 1f,
                InvGamma = ((float4)invGamma).xyz,
                Saturation = post.saturation / 100f + 1f,
                DepthFade = math.max(so.depthFadeDistance, 0f),
                DepthDarkening = fogged ? math.max(post.depthDarkening, 0f) : 0f,
                Fogged = fogged ? 1f : 0f
            });
            return (byte)(_volumeFogs.Count - 1);
        }

        /// <summary>
        ///     Retrieves the data package used for meshing and rendering.
        /// </summary>
        /// <returns>Voxel engine render generation data structure.</returns>
        public VoxelEngineRenderGenData GetVoxelGenData(){
            return _voxelEngineRenderGenData;
        }

        /// <summary>
        ///     Light attenuation and emission per voxel id, read by the light jobs.
        /// </summary>
        public VoxelLightTable GetLightTable(){
            return _lightTable;
        }

        private Texture2DArray GetTextureArray(MeshLayer meshLayer){
            return meshLayer switch{
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
        public void ApplyToMaterial(Material material, MeshLayer solid){
            if (!Finalized)
                throw new InvalidOperationException("VoxelRegistry must be finalized before applying to materials.");

            if (material){
                Texture2DArray texArray = GetTextureArray(solid);
                if (texArray)
                    material.SetTexture(TexturesNameID, texArray);
                else
                    VoxelEngineLogger.Error<VoxelRegistry>("Texture array is null, cannot assign to material.");

                material.SetBuffer(QuadBufferNameID, QuadBuffer);
            }
            else
                VoxelEngineLogger.Error<VoxelRegistry>("Voxel material is null, cannot assign texture array.");
        }

        /// <summary>
        ///     Returns a list of all registered IDs and their corresponding names.
        /// </summary>
        /// <returns>List of ID/name pairs.</returns>
        public List<KeyValuePair<ushort, FixedString32Bytes>> GetAllEntries(){
            return NameRegistry.GetAllEntries();
        }

        /// <summary>
        ///     Releases what was created; an import that threw part way leaves the rest unset.
        /// </summary>
        public override void Dispose(){
            if (_voxelEngineRenderGenData.VoxelRenderDefs.IsCreated)
                _voxelEngineRenderGenData.VoxelRenderDefs.Dispose();
            _lightTable.Dispose();
            VoxelRenderDefBuffer?.Dispose();
            QuadBuffer?.Dispose();
            QuadTexPairBuffer?.Dispose();
            VolumeFogBuffer?.Dispose();
            if (VoxelMap.IsCreated) VoxelMap.Dispose();
            _solidTexRegistry.DestroyArray();
            _transparentTexRegistry.DestroyArray();
            _foliageTexRegistry.DestroyArray();
        }

        /// <summary>One <c>_VoxelVolumeFog</c> entry, as <c>VolumeFog</c> in TransparentVoxel.shader.</summary>
        private struct VolumeFogDef{
            public float3 FogColor; // lit like the underwater fog
            public float Density; // per block, exp² as UnderwaterFog.shader
            public float3 Lift; // lift/gamma/gain of the underwater view, as ColorUtils.PrepareLiftGammaGain
            public float SelfIllumination; // the fog's light floor, max emission / 15
            public float3 Gain;
            public float Contrast; // ColorAdjustments contrast as a factor, contrast / 100 + 1
            public float3 InvGamma;
            public float Saturation; // ColorAdjustments saturation as a factor, saturation / 100 + 1
            public float DepthFade; // blocks over which the alpha rises to 1, 0 = off
            public float DepthDarkening; // fraction of the fog's light lost per block below the surface (exp)
            public float Fogged; // 1 = the scene behind is fogged and graded, 0 = depth fade only
            public float Pad; // keeps the stride a multiple of 16 bytes
        }

        //TODO: Compacting?
        private struct GPUVoxelDef{
            private uint MeshLayer;
            private uint AlwaysRenderAllFaces;
            private uint VolumeFog; // slot in _VoxelVolumeFog, 0 = none
            private uint Glow;
            private uint Collision;
            private uint2 shape_quad_indices_alwaysRender;
            private uint2 shape_quad_indices_right;
            private uint2 shape_quad_indices_left;
            private uint2 shape_quad_indices_up;
            private uint2 shape_quad_indices_down;
            private uint2 shape_quad_indices_front;
            private uint2 shape_quad_indices_back;
            private uint LightAttenuation; // the smooth light skips opaque (15) voxels
            private uint SurfaceAlpha; // unorm8

            public GPUVoxelDef(Voxel.VoxelDef def, byte lightAttenuation){
                MeshLayer = (uint)def.MeshLayer;
                AlwaysRenderAllFaces = def.AlwaysRenderAllFaces ? 1u : 0u;
                VolumeFog = def.VolumeFog;
                Glow = def.Glow;
                Collision = def.Collision ? 1u : 0u;
                shape_quad_indices_alwaysRender = def.Always;
                shape_quad_indices_right = def.Right;
                shape_quad_indices_left = def.Left;
                shape_quad_indices_up = def.Up;
                shape_quad_indices_down = def.Down;
                shape_quad_indices_front = def.Front;
                shape_quad_indices_back = def.Back;
                LightAttenuation = lightAttenuation;
                SurfaceAlpha = def.SurfaceAlpha;
            }
        }
    }
}
