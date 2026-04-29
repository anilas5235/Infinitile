using System;
using Engine.Scripts.Utils.Collections;
using Engine.Scripts.Utils.Extensions;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using static Engine.Scripts.Utils.VoxelConstants;

namespace Engine.Scripts.Data
{
    /// <summary>
    ///     In-memory representation of a chunk with compressed voxel data (run-length style).
    ///     Tracks a dirty flag for mesh rebuild decisions and provides simple read/write API.
    /// </summary>
    [BurstCompile]
    public class Chunk : IDisposable
    {
        /// <summary>
        ///     World-space position (chunk origin coordinate).
        /// </summary>
        public int2 Position { get; }

        public ChunkVoxelData VoxelData;

        public ChunkLightData LightData;

        /// <summary>
        ///     Constructs a new chunk with position and size; initializes data structure.
        /// </summary>
        public Chunk(int2 position)
        {
            Position = position;
            VoxelData = default;
            LightData = new ChunkLightData(128);
        }

        public void Dispose()
        {
            VoxelData.Dispose();
            LightData.Dispose();
        }
    }

    public struct PartitionLightData : IDisposable
    {
        private UnsafeIntervalList<byte> _data;
        public bool IsAllZero => _data.CompressedLength == 1 && _data.Get(0) == 0;
        public bool IsAllMax => _data.CompressedLength == 1 && _data.Get(0) == MaxLightLevel;

        public PartitionLightData(int initCapacity)
        {
            _data = new UnsafeIntervalList<byte>(initCapacity, Allocator.Domain);
            _data.AddInterval(0, VoxelsPerPartition);
        }

        /// <summary>
        ///     Adds a run of identical light values (during initialization / generation).
        /// </summary>
        public void AddLight(byte lightValue, int count)
        {
            _data.AddInterval(lightValue, count);
        }

        /// <summary>
        ///     Sets light value by int3 position. Marks dirty on change.
        /// </summary>
        public bool SetLight(int3 pos, byte lightValue)
        {
            bool result = _data.Set(GetIndex(pos), lightValue);
            return result;
        }

        internal void SetAllLights(byte lightValue)
        {
            _data.Clear();
            _data.AddInterval(lightValue, VoxelsPerPartition);
        }

        /// <summary>
        ///     Reads light value at int3 position.
        /// </summary>
        public byte GetLight(int3 pos)
        {
            return _data.Get(GetIndex(pos));
        }

        /// <summary>
        ///     Disposes native resources.
        /// </summary>
        public void Dispose()
        {
            _data.Dispose();
        }

        private static int GetIndex(in int3 pos)
        {
            return PartitionSize.Flatten(pos);
        }
    }

    [BurstCompile]
    public struct ChunkLightData : IDisposable
    {
        private UnsafeList<PartitionLightData> _data;

        public ChunkLightData(int initCapacity)
        {
            _data = new UnsafeList<PartitionLightData>(PartitionsPerChunk, Allocator.Domain);
            for (int i = 0; i < PartitionsPerChunk; i++) _data.Add(new PartitionLightData(initCapacity));
        }

        /// <summary>
        ///     Reads light value at int3 position.
        /// </summary>
        public byte GetLight(int partitionIndex, int3 pos)
        {
            return _data[partitionIndex].GetLight(pos);
        }

        /// <summary>
        ///     Disposes native resources.
        /// </summary>
        public void Dispose()
        {
            for (int i = 0; i < _data.Length; i++) _data[i].Dispose();
            _data.Dispose();
        }

        public bool TryGetPartitionLight(int index, out PartitionLightData lightData)
        {
            if (index < 0 || index >= _data.Length)
            {
                lightData = default;
                return false;
            }

            lightData = _data[index];
            return true;
        }
    }

    [BurstCompile]
    public struct ChunkVoxelData : IDisposable
    {
        internal UnsafeIntervalList<ushort> Data;
        
        public UnsafeIntervalList<ushort> GetData() => Data;

        public ChunkVoxelData(int initCapacity)
        {
            Data = new UnsafeIntervalList<ushort>(initCapacity, Allocator.Domain);
        }

        /// <summary>
        ///     Adds a run of identical voxels (during initialization / generation).
        /// </summary>
        public void AddVoxels(ushort voxelId, int count)
        {
            Data.AddInterval(voxelId, count);
        }

        /// <summary>
        ///     Sets voxel by int3 position. Marks dirty on change.
        /// </summary>
        public bool SetVoxel(int3 pos, ushort voxelId)
        {
            bool result = Data.Set(GetIndex(pos), voxelId);
            return result;
        }

        /// <summary>
        ///     Reads voxel at int3 position.
        /// </summary>
        public ushort GetVoxel(int3 pos)
        {
            return Data.Get(GetIndex(pos));
        }

        /// <summary>
        ///     Disposes native resources.
        /// </summary>
        public void Dispose()
        {
            Data.Dispose();
        }

        private static int GetIndex(in int3 pos)
        {
            return ChunkSize.Flatten(pos);
        }
    }
}