using System;
using System.Collections.Generic;
using Engine.Scripts.Data;
using Engine.Scripts.Jobs;
using Engine.Scripts.Render;
using Engine.Scripts.Settings;
using Engine.Scripts.ThirdParty.Priority_Queue;
using Engine.Scripts.Utils;
using Engine.Scripts.Utils.Extensions;
using Engine.Scripts.Utils.Logger;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;
using static Engine.Scripts.Utils.Extensions.VectorConstants;
using static Engine.Scripts.Utils.VoxelConstants;
using static Engine.Scripts.Utils.VoxelUtils;

namespace Engine.Scripts.Components
{
    /// <summary>
    ///     Manages chunk data in memory. Maintains a limited pool of loaded chunks prioritized by focus position.
    ///     Tracks remesh / recollider flags and provides voxel access.
    /// </summary>
    public class ChunkManager
    {
        private readonly Dictionary<int2, Chunk> _chunks;
        private readonly int _chunkStoreSize;
        private readonly SimpleFastPriorityQueue<int2, int> _queue;
        private readonly HashSet<int3> _reCollidePartitions;

        private readonly HashSet<int3> _reMeshPartitions;
        private NativeParallelHashMap<int2, ChunkVoxelData> _accessorMap;

        private int3 _focus;
        private NativeParallelHashMap<int2, ChunkLightData> _lightAccessorMap;

        internal Action OnRemeshRequested;
        internal Action<Chunk> OnChunkChange;
        
        /// <summary>
        ///     Creates a new manager with capacities from <paramref name="settings" />.
        /// </summary>
        internal ChunkManager(VoxelEngineSettings settings)
        {
            _chunkStoreSize = (settings.Chunk.LoadDistance + 2).SquareSize();

            _reMeshPartitions = new HashSet<int3>();
            _reCollidePartitions = new HashSet<int3>();

            _chunks = new Dictionary<int2, Chunk>(_chunkStoreSize);
            _queue = new SimpleFastPriorityQueue<int2, int>();

            _accessorMap = new NativeParallelHashMap<int2, ChunkVoxelData>(
                settings.Scheduler.meshingBatchSize * 6,
                Allocator.Domain
            );
            _lightAccessorMap = new NativeParallelHashMap<int2, ChunkLightData>(
                settings.Scheduler.meshingBatchSize * 6,
                Allocator.Domain
            );
        }

        /// <summary>
        ///     Whether a chunk is flagged for remeshing.
        /// </summary>
        internal bool ShouldReMesh(int3 position) => _reMeshPartitions.Contains(position);

        /// <summary>
        ///     Whether a chunk needs collider rebuild.
        /// </summary>
        internal bool ShouldReCollide(int3 position) => _reCollidePartitions.Contains(position);

        /// <summary>
        ///     Disposes native resources and all chunk data.
        /// </summary>
        internal void Dispose()
        {
            _accessorMap.Dispose();
            _lightAccessorMap.Dispose();
            foreach ((int2 _, Chunk chunk) in _chunks) chunk.Dispose();
        }

        /// <summary>
        ///     Updates focus (player) and priorities in eviction queue.
        /// </summary>
        internal void FocusUpdate(int3 focus)
        {
            _focus = focus;
            _queue.UpdateAllPriorities(pos => -PriorityUtil.DistPriority(ref pos, ref focus));
        }

        /// <summary>
        ///     Adds newly generated chunks, evicts oldest if capacity exceeded.
        /// </summary>
        internal void AddChunks(NativeParallelHashMap<int2, ChunkVoxelData> chunks)
        {
            foreach (KeyValue<int2, ChunkVoxelData> pair in chunks)
            {
                int2 position = pair.Key;

                if (_chunks.ContainsKey(position))
                    throw new InvalidOperationException($"Chunk {position} already exists");

                if (_queue.Count >= _chunkStoreSize) RemoveChunkData(_queue.Dequeue());

                Chunk chunk = new(position) { VoxelData = pair.Value };
                _chunks.Add(position, chunk);
                _queue.Enqueue(position, -(position - _focus.xz).SqrMagnitude());
            }
        }

        /// <summary>
        ///     Removes the chunk data (eviction). Persistence hook could be added here.
        /// </summary>
        private void RemoveChunkData(int2 position)
        {
            UnloadChunk(position);
        }

        internal void UnloadChunk(int2 position)
        {
            if (_queue.Contains(position)) _queue.Remove(position);
            if (!_chunks.TryGetValue(position, out Chunk chunk)) return;

            chunk.Dispose();
            _chunks.Remove(position);

            for (int pId = 0; pId < PartitionsPerChunk; pId++)
            {
                int3 partitionPos = new(position.x, pId, position.y);
                _reMeshPartitions.Remove(partitionPos);
                _reCollidePartitions.Remove(partitionPos);
            }
        }

        /// <summary>
        ///     Builds a <see cref="ChunkAccessor" /> that includes a 3x3 neighborhood for given positions.
        /// </summary>
        internal ChunkAccessor GetAccessor(List<int3> positions)
        {
            _accessorMap.Clear();
            _lightAccessorMap.Clear();
            foreach (int3 position in positions)
                for (int x = -1; x <= 1; x++)
                for (int z = -1; z <= 1; z++)
                {
                    int2 pos = position.xz + new int2(x, z);
                    if (!_chunks.TryGetValue(pos, out Chunk chunk))
                        throw new InvalidOperationException($"Chunk {pos} has not been generated");
                    if (!_accessorMap.ContainsKey(pos)) _accessorMap.Add(pos, chunk.VoxelData);
                    if (!_lightAccessorMap.ContainsKey(pos)) _lightAccessorMap.Add(pos, chunk.LightData);
                }

            return new ChunkAccessor(_accessorMap.AsReadOnly(), _lightAccessorMap.AsReadOnly());
        }

        /// <summary>
        ///     Callback after chunk remesh completes: remove flag and flag for collider rebuild.
        /// </summary>
        internal void ReMeshedPartition(int3 position)
        {
            if (!_reMeshPartitions.Contains(position)) return;
            _reMeshPartitions.Remove(position);
            _reCollidePartitions.Add(position);
        }

        /// <summary>
        ///     Callback after collider bake completes: remove flag.
        /// </summary>
        internal void ReCollidedPartition(int3 position)
        {
            if (!_reCollidePartitions.Contains(position)) return;
            _reCollidePartitions.Remove(position);
        }

        /// <summary>
        ///     Flags all neighbor partitions (including own) for remeshing.
        /// </summary>
        /// <param name="blockPosition">World block position that changed.</param>
        private void ReMeshPartitions(int3 blockPosition)
        {
            int3 pCoords = GetPartitionCoords(blockPosition);
            _reMeshPartitions.Add(pCoords);
            int3 localPos = GetPartitionLocalVoxelCoords(pCoords, blockPosition);
            switch (localPos.x % PartitionWidth)
            {
                case MinPartitionPosXYZ:
                    _reMeshPartitions.Add(pCoords + Int3Left);
                    break;
                case MaxXPartitionPos:
                    _reMeshPartitions.Add(pCoords + Int3Right);
                    break;
            }

            switch (localPos.z % PartitionDepth)
            {
                case MinPartitionPosXYZ:
                    _reMeshPartitions.Add(pCoords + Int3Backward);
                    break;
                case MaxZPartitionPos:
                    _reMeshPartitions.Add(pCoords + Int3Forward);
                    break;
            }

            switch (localPos.y % PartitionHeight)
            {
                case MinPartitionPosXYZ:
                    _reMeshPartitions.Add(pCoords + Int3Down);
                    break;
                case MaxYPartitionPos:
                    _reMeshPartitions.Add(pCoords + Int3Up);
                    break;
            }

            OnRemeshRequested?.Invoke();
        }

        #region API

        /// <summary>
        ///     Reads a voxel at world position. Returns 0 if chunk or Y out of range.
        /// </summary>
        internal ushort GetVoxel(Vector3Int position)
        {
            int2 chunkPos = GetChunkCoords(position);
            int3 blockPos = GetLocalVoxelCoords(position);
            if (blockPos.y < 0 || blockPos.y >= ChunkSize.y) return 0;
            if (_chunks.TryGetValue(chunkPos, out Chunk chunk)) return chunk.VoxelData.GetVoxel(blockPos);
            VoxelEngineLogger.Warn<ChunkManager>($"Chunk : {chunkPos} not loaded");
            return 0;
        }

        /// <summary>
        ///     Sets a voxel at world position. Optionally triggers remesh for affected neighbor chunks.
        /// </summary>
        /// <param name="voxelId">Voxel ID/type.</param>
        /// <param name="position">World position.</param>
        /// <param name="remesh">Whether to flag for remeshing.</param>
        /// <returns>True if voxel actually changed.</returns>
        internal bool SetVoxel(ushort voxelId, Vector3Int position, bool remesh = true)
        {
            int2 chunkPos = GetChunkCoords(position);
            int3 blockPos = GetLocalVoxelCoords(position);
            if (!_chunks.TryGetValue(chunkPos, out Chunk chunk))
            {
                VoxelEngineLogger.Warn<ChunkManager>($"Chunk : {chunkPos} not loaded");
                return false;
            }

            int3 pCoords = GetPartitionCoords(position);

            if (_reMeshPartitions.Contains(pCoords)) return false;

            bool result = chunk.VoxelData.SetVoxel(blockPos, voxelId);
            _chunks[chunkPos] = chunk;
            if (remesh && result)
            {
                ReMeshPartitions(position.Int3());
                OnChunkChange?.Invoke(chunk);
            }
            return result;
        }

        /// <summary>
        ///     Number of currently loaded chunks.
        /// </summary>
        public int ChunkCount()
        {
            return _chunks.Count;
        }

        /// <summary>
        ///     Checks if a chunk is loaded.
        /// </summary>
        public bool IsChunkLoaded(int2 position)
        {
            return _chunks.ContainsKey(position);
        }

        /// <summary>
        ///     Attempts to get a chunk by its position.
        /// </summary>
        internal bool TryGetChunk(int2 position, out Chunk chunk)
        {
            return _chunks.TryGetValue(position, out chunk);
        }

        #endregion
    }
}