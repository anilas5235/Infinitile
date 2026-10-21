using System;
using Engine.Scripts.Utils.Collections;
using Engine.Scripts.Utils.Extensions;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using static Engine.Scripts.Utils.VoxelConstants;

namespace Engine.Scripts.Data{
    /// <summary>
    ///     In-memory representation of a chunk with compressed voxel data (run-length style).
    ///     Tracks a dirty flag for mesh rebuild decisions and provides simple read/write API.
    /// </summary>
    [BurstCompile]
    public class Chunk : IDisposable{
        public ChunkLightData LightData;

        public ChunkVoxelData VoxelData;

        /// <summary>
        ///     LGT-06: true once <see cref="LightData" /> holds the result of a light job (or later incremental
        ///     updates). Meshing waits until the chunk and its 8 neighbours are lit.
        /// </summary>
        public bool LightReady{ get; internal set; }

        /// <summary>LGT-06: true if the chunk was lit before and its light was invalidated (re-light needs a remesh).</summary>
        internal bool WasLit{ get; set; }

        /// <summary>
        ///     Constructs a new chunk with position and size; initializes data structure.
        /// </summary>
        public Chunk(int2 position){
            Position = position;
            VoxelData = default;
            LightData = new ChunkLightData(128);
        }

        /// <summary>PERF-04: an empty object for the manager's chunk pool; <see cref="Reinit" /> runs before use.</summary>
        internal Chunk(){
            VoxelData = default;
            LightData = default;
        }

        /// <summary>
        ///     World-space position (chunk origin coordinate).
        /// </summary>
        public int2 Position{ get; private set; }

        /// <summary>
        ///     PERF-04: re-initialises a disposed chunk object the manager keeps for reuse, so loading a chunk does
        ///     not allocate a managed object. The caller assigns <see cref="VoxelData" />.
        /// </summary>
        internal void Reinit(int2 position){
            Position = position;
            VoxelData = default;
            LightData = new ChunkLightData(128);
            LightReady = false;
            WasLit = false;
        }

        public void Dispose(){
            VoxelData.Dispose();
            LightData.Dispose();
        }
    }

    /// <summary>
    ///     LGT-01: light of a whole chunk, one 16-bit value per voxel (sky(4) | R(4) | G(4) | B(4), see
    ///     <see cref="VoxelLight" />), stored as an interval list in the same Flatten order as
    ///     <see cref="ChunkVoxelData" /> so the GPU upload reuses the voxel-data path (LGT-08). Replaces the former
    ///     per-partition byte lists. A new chunk is dark until its light job has run (LGT-06).
    /// </summary>
    [BurstCompile]
    public struct ChunkLightData : IDisposable{
        internal UnsafeIntervalList<ushort> Data;

        public bool IsCreated => Data.Length > 0;

        /// <summary>Creates a dark chunk (all light 0).</summary>
        public ChunkLightData(int initCapacity){
            Data = new UnsafeIntervalList<ushort>(initCapacity, Allocator.Domain);
            Data.AddInterval(0, VoxelsPerChunk);
        }

        /// <summary>Creates an empty list that the caller fills with <see cref="AddLights" /> (light job output).</summary>
        internal static ChunkLightData CreateEmpty(int initCapacity){
            return new ChunkLightData{ Data = new UnsafeIntervalList<ushort>(initCapacity, Allocator.Domain) };
        }

        public UnsafeIntervalList<ushort> GetData(){
            return Data;
        }

        /// <summary>
        ///     Adds a run of identical light values (during the light job).
        /// </summary>
        public void AddLights(ushort light, int count){
            Data.AddInterval(light, count);
        }

        /// <summary>
        ///     Sets the light of a chunk-local voxel; returns true if it changed.
        /// </summary>
        public bool SetLight(int3 pos, ushort light){
            return Data.Set(ChunkSize.Flatten(pos), light);
        }

        /// <summary>
        ///     Reads the light of a chunk-local voxel.
        /// </summary>
        public ushort GetLight(int3 pos){
            return Data.Get(ChunkSize.Flatten(pos));
        }

        /// <summary>
        ///     Disposes native resources.
        /// </summary>
        public void Dispose(){
            Data.Dispose();
        }
    }

    [BurstCompile]
    public struct ChunkVoxelData : IDisposable{
        internal UnsafeIntervalList<ushort> Data;

        public UnsafeIntervalList<ushort> GetData(){
            return Data;
        }

        public ChunkVoxelData(int initCapacity){
            Data = new UnsafeIntervalList<ushort>(initCapacity, Allocator.Domain);
        }

        /// <summary>
        ///     Adds a run of identical voxels (during initialization / generation).
        /// </summary>
        public void AddVoxels(ushort voxelId, int count){
            Data.AddInterval(voxelId, count);
        }

        /// <summary>
        ///     Sets voxel by int3 position. Marks dirty on change.
        /// </summary>
        public bool SetVoxel(int3 pos, ushort voxelId){
            bool result = Data.Set(GetIndex(pos), voxelId);
            return result;
        }

        /// <summary>
        ///     Reads voxel at int3 position.
        /// </summary>
        public ushort GetVoxel(int3 pos){
            return Data.Get(GetIndex(pos));
        }

        /// <summary>
        ///     Disposes native resources.
        /// </summary>
        public void Dispose(){
            Data.Dispose();
        }

        private static int GetIndex(in int3 pos){
            return ChunkSize.Flatten(pos);
        }
    }
}
