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
    ///     In-memory representation of a chunk with run-length compressed voxel and light data.
    /// </summary>
    [BurstCompile]
    public class Chunk : IDisposable{
        public ChunkLightData LightData;

        public ChunkVoxelData VoxelData;

        /// <summary>
        ///     True once <see cref="LightData" /> holds the result of a light job (or later incremental
        ///     updates). Meshing waits until the chunk and its 8 neighbours are lit.
        /// </summary>
        public bool LightReady{ get; internal set; }

        /// <summary>True if the chunk was lit before and its light was invalidated (re-light needs a remesh).</summary>
        internal bool WasLit{ get; set; }

        /// <summary>
        ///     Constructs a new chunk with position and size; initializes data structure.
        /// </summary>
        public Chunk(int2 position){
            Position = position;
            VoxelData = default;
            LightData = new ChunkLightData(128);
        }

        /// <summary>An empty object for the manager's chunk pool; <see cref="Reinit" /> runs before use.</summary>
        internal Chunk(){
            VoxelData = default;
            LightData = default;
        }

        /// <summary>
        ///     World-space position (chunk origin coordinate).
        /// </summary>
        public int2 Position{ get; private set; }

        /// <summary>
        ///     Re-initialises a disposed chunk object the manager keeps for reuse, so loading a chunk does
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
    ///     Allocator of the voxel and light lists of a chunk.
    /// </summary>
    internal static class ChunkLists{
        /// <summary>
        ///     <c>Persistent</c>, not <c>Domain</c>: the chunk and light jobs create and grow these lists on worker
        ///     threads, and Unity's bookkeeping for <c>Allocator.Domain</c> (<c>DomainUnloadAutoFree</c>) is not safe
        ///     against that. Its maintenance pass on the main thread edits the tracking lists without a lock while
        ///     workers push to them, so a pointer the engine has freed can stay tracked, and the next domain reload
        ///     frees it a second time: the Editor crashed there on a script reload after Play mode. Persistent
        ///     memory is not tracked. It is also not freed at a domain reload, so every list needs an owner that
        ///     disposes it: its <see cref="Chunk" />, or the scheduler whose result map still holds it.
        /// </summary>
        internal const Allocator Allocator = Unity.Collections.Allocator.Persistent;
    }

    /// <summary>
    ///     Light of a whole chunk, one 16-bit value per voxel (sky(4) | R(4) | G(4) | B(4), see
    ///     <see cref="VoxelLight" />), stored as an interval list in the same Flatten order as
    ///     <see cref="ChunkVoxelData" /> so the GPU upload reuses the voxel-data path. A new chunk is dark until
    ///     its light job has run.
    /// </summary>
    [BurstCompile]
    public struct ChunkLightData : IDisposable{
        internal UnsafeIntervalList<ushort> Data;

        public bool IsCreated => Data.Length > 0;

        /// <summary>Creates a dark chunk (all light 0).</summary>
        public ChunkLightData(int initCapacity){
            Data = new UnsafeIntervalList<ushort>(initCapacity, ChunkLists.Allocator);
            Data.AddInterval(0, VoxelsPerChunk);
        }

        /// <summary>Creates an empty list that the caller fills with <see cref="AddLights" /> (light job output).</summary>
        internal static ChunkLightData CreateEmpty(int initCapacity){
            return new ChunkLightData{ Data = new UnsafeIntervalList<ushort>(initCapacity, ChunkLists.Allocator) };
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
        private const uint AllPartitions = (1u << PartitionsPerChunk) - 1;

        internal UnsafeIntervalList<ushort> Data;

        /// <summary>
        ///     Bit p is set when partition p of the chunk (its layers p x 32 to p x 32 + 31) may hold a voxel that
        ///     is not air. A partition whose bit is clear holds only air: it has no geometry and no collider, so
        ///     the mesh stage does not build it. The bit is set when a voxel is added or placed and never cleared;
        ///     a partition that edits emptied again is still built (to an empty mesh).
        /// </summary>
        internal uint OccupiedPartitions;

        /// <summary>
        ///     Hard biome of every column (index into the active biome set), ChunkWidth x ChunkDepth bytes in column
        ///     order (<c>z + x * ChunkDepth</c>), written by the chunk job; the water's biome colour reads it
        ///     (<c>BiomeTintMap</c>, underwater fog). Not created for data that did not come from the generator.
        /// </summary>
        internal UnsafeList<byte> Biomes;

        /// <summary>True when <see cref="Biomes" /> holds the column biomes.</summary>
        public bool HasBiomes => Biomes.IsCreated && Biomes.Length == ChunkWidth * ChunkDepth;

        /// <summary>Hard biome of the chunk-local column (<paramref name="x" />, <paramref name="z" />); 0 without biomes.</summary>
        public byte GetBiome(int x, int z){
            return HasBiomes ? Biomes[z + x * ChunkDepth] : (byte)0;
        }

        /// <summary>Allocates <see cref="Biomes" /> (all 0); the chunk job fills it.</summary>
        internal void CreateBiomes(){
            if (Biomes.IsCreated) return;
            Biomes = new UnsafeList<byte>(ChunkWidth * ChunkDepth, ChunkLists.Allocator);
            Biomes.Resize(ChunkWidth * ChunkDepth, NativeArrayOptions.ClearMemory);
        }

        /// <summary>
        ///     Surface-source biome of every column (DistanceBlend's pick, WorldGen v2 §12), same order as
        ///     <see cref="Biomes" />: the grass and plant tints read it, so the colour changes on the same wavy line as the
        ///     material (DEC-194). Only the density generator writes it; without it the tints read <see cref="Biomes" />.
        /// </summary>
        internal UnsafeList<byte> SurfaceBiomes;

        /// <summary>True when <see cref="SurfaceBiomes" /> holds the column surface biomes.</summary>
        public bool HasSurfaceBiomes => SurfaceBiomes.IsCreated && SurfaceBiomes.Length == ChunkWidth * ChunkDepth;

        /// <summary>Surface-source biome of the chunk-local column, or its hard biome when the chunk has none.</summary>
        public byte GetSurfaceBiome(int x, int z){
            return HasSurfaceBiomes ? SurfaceBiomes[z + x * ChunkDepth] : GetBiome(x, z);
        }

        /// <summary>Allocates <see cref="SurfaceBiomes" /> (all 0); the chunk job fills it.</summary>
        internal void CreateSurfaceBiomes(){
            if (SurfaceBiomes.IsCreated) return;
            SurfaceBiomes = new UnsafeList<byte>(ChunkWidth * ChunkDepth, ChunkLists.Allocator);
            SurfaceBiomes.Resize(ChunkWidth * ChunkDepth, NativeArrayOptions.ClearMemory);
        }

        /// <summary>True when partition <paramref name="partitionY" /> may hold a voxel that is not air.</summary>
        public bool PartitionHasVoxels(int partitionY){
            return (uint)partitionY < PartitionsPerChunk && (OccupiedPartitions & (1u << partitionY)) != 0;
        }

        public UnsafeIntervalList<ushort> GetData(){
            return Data;
        }

        public ChunkVoxelData(int initCapacity){
            Data = new UnsafeIntervalList<ushort>(initCapacity, ChunkLists.Allocator);
            OccupiedPartitions = 0;
            Biomes = default;
            SurfaceBiomes = default;
        }

        /// <summary>
        ///     Adds a run of identical voxels (during initialization / generation).
        /// </summary>
        public void AddVoxels(ushort voxelId, int count){
            if (voxelId != 0 && count > 0) MarkOccupied(Data.Length, count);
            Data.AddInterval(voxelId, count);
        }

        /// <summary>
        ///     Sets the bits of the partitions a run of <paramref name="count" /> voxels from flat index
        ///     <paramref name="start" /> touches. The height is the innermost axis of the flat order, so a run is a
        ///     piece of one column, or it runs over the top of a column into the next ones.
        /// </summary>
        private void MarkOccupied(int start, int count){
            if (count >= ChunkHeight){
                OccupiedPartitions = AllPartitions;
                return;
            }

            int end = start + count - 1;
            int first = start % ChunkHeight / PartitionHeight;
            int last = end % ChunkHeight / PartitionHeight;
            uint fromFirst = AllPartitions & ~((1u << first) - 1);
            uint upToLast = (1u << (last + 1)) - 1;
            // In one column the run covers first..last; over a column border it covers first..top and bottom..last
            OccupiedPartitions |= start / ChunkHeight == end / ChunkHeight ? fromFirst & upToLast : fromFirst | upToLast;
        }

        /// <summary>
        ///     Sets voxel by int3 position; returns true if it changed.
        /// </summary>
        public bool SetVoxel(int3 pos, ushort voxelId){
            bool result = Data.Set(GetIndex(pos), voxelId);
            if (voxelId != 0) OccupiedPartitions |= 1u << (pos.y / PartitionHeight);
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
            if (Biomes.IsCreated) Biomes.Dispose();
            if (SurfaceBiomes.IsCreated) SurfaceBiomes.Dispose();
        }

        private static int GetIndex(in int3 pos){
            return ChunkSize.Flatten(pos);
        }
    }
}
