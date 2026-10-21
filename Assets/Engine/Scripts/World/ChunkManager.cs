using System;
using System.Collections.Generic;
using Engine.Scripts.Data;
using Engine.Scripts.Jobs.Core;
using Engine.Scripts.Settings;
using Engine.Scripts.ThirdParty.Priority_Queue;
using Engine.Scripts.Utils.Extensions;
using Engine.Scripts.Utils.Logger;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using static Engine.Scripts.Utils.Extensions.VectorConstants;
using static Engine.Scripts.Utils.VoxelConstants;
using static Engine.Scripts.Utils.VoxelUtils;

namespace Engine.Scripts.World{
    /// <summary>
    ///     Manages chunk data in memory. Maintains a limited pool of loaded chunks prioritized by focus position.
    ///     Tracks remesh / recollider flags and provides voxel access.
    /// </summary>
    public class ChunkManager{
        private readonly int _chunkStoreSize;
        private readonly Dictionary<int2, Chunk> _chunks;
        private readonly HashSet<int3> _reCollidePartitions;

        private readonly HashSet<int3> _reMeshPartitions;
        private readonly SimpleFastPriorityQueue<int2, float> _unloadingQueue;
        private readonly Stack<Chunk> _chunkObjectPool; // unloaded chunk objects are reused
        internal Action<Chunk> OnChunkChange;

        internal Action OnRemeshRequested;

        /// <summary>
        ///     Raised before a chunk that is part of the current <see cref="ChunkAccessor" /> is edited or
        ///     disposed. Schedulers whose jobs read the accessor subscribe and complete their running job first.
        /// </summary>
        internal Action AccessorChunkWriting;

        /// <summary>
        ///     Raised after <see cref="UnloadChunk" /> removed a loaded chunk, so the world can tell listeners
        ///     (renderer buffers, pool) that the chunk is gone regardless of who triggered the unload.
        /// </summary>
        internal Action<int2> OnChunkUnloaded;

        /// <summary>
        ///     Raised before a chunk whose voxel data the running light job reads (see
        ///     <see cref="GetLightJobChunks" />) is edited or disposed. The light scheduler completes and applies it.
        /// </summary>
        internal Action LightJobReading;

        /// <summary>Raised after a voxel changed, with its world position and previous id.</summary>
        internal Action<int3, ushort> VoxelEdited;

        /// <summary>Raised when a lit chunk lost its light and must be lit again.</summary>
        internal Action OnLightInvalidated;

        private NativeParallelHashMap<int2, ChunkVoxelData> _accessorMap;
        private NativeParallelHashMap<int2, ChunkVoxelData> _lightJobMap;

        private PriorityUtil.Focus _focus;

        /// <summary>
        ///     Creates a new manager with capacities from <paramref name="settings" />.
        /// </summary>
        internal ChunkManager(VoxelEngineSettings settings){
            _chunkStoreSize = (settings.Chunk.LoadDistance + 2).SquareSize();

            // Sized for every loaded chunk; re-lit chunks flag partitions up to the load ring
            int reMeshCapacity = _chunkStoreSize * PartitionsPerChunk;
            _reMeshPartitions = new HashSet<int3>(reMeshCapacity);
            _reCollidePartitions = new HashSet<int3>(reMeshCapacity);

            _chunks = new Dictionary<int2, Chunk>(_chunkStoreSize);
            _unloadingQueue = new SimpleFastPriorityQueue<int2, float>();
            _unloadingQueue.Reserve(_chunkStoreSize + 1); // queue nodes are pooled up front

            // Every chunk object the store can hold exists from the start, so loading a chunk never
            // allocates a managed object, not even before the store is full for the first time.
            _chunkObjectPool = new Stack<Chunk>(_chunkStoreSize);
            for (int i = 0; i < _chunkStoreSize; i++) _chunkObjectPool.Push(new Chunk());

            _accessorMap = new NativeParallelHashMap<int2, ChunkVoxelData>(
                settings.Scheduler.meshingBatchSize * 6,
                Allocator.Domain
            );
            _lightJobMap = new NativeParallelHashMap<int2, ChunkVoxelData>(
                math.max(1, settings.Scheduler.lightBatchSize) * 9,
                Allocator.Domain
            );
        }

        /// <summary>
        ///     Whether a chunk is flagged for remeshing.
        /// </summary>
        internal bool ShouldReMesh(int3 position){
            return _reMeshPartitions.Contains(position);
        }

        /// <summary>
        ///     Whether a chunk needs collider rebuild.
        /// </summary>
        internal bool ShouldReCollide(int3 position){
            return _reCollidePartitions.Contains(position);
        }

        /// <summary>
        ///     Disposes native resources and all chunk data.
        /// </summary>
        internal void Dispose(){
            _accessorMap.Dispose();
            _lightJobMap.Dispose();
            foreach ((int2 _, Chunk chunk) in _chunks) chunk.Dispose();
        }

        /// <summary>
        ///     Updates focus (player) and priorities in eviction queue.
        /// </summary>
        internal void FocusUpdate(PriorityUtil.Focus focus){
            _focus = focus;
            // Static lambda with explicit state instead of a closure capturing the focus; evict by plain distance
            _unloadingQueue.UpdateAllPriorities(focus, static (f, pos) => PriorityUtil.EvictionPriority(ref pos, ref f));
        }

        /// <summary>
        ///     Adds newly generated chunks, evicts oldest if capacity exceeded.
        /// </summary>
        internal void AddChunks(NativeParallelHashMap<int2, ChunkVoxelData> chunks){
            foreach (KeyValue<int2, ChunkVoxelData> pair in chunks){
                int2 position = pair.Key;

                if (_chunks.ContainsKey(position))
                    throw new InvalidOperationException($"Chunk {position} already exists");

                if (_unloadingQueue.Count >= _chunkStoreSize) RemoveChunkData(_unloadingQueue.Dequeue());

                Chunk chunk;
                if (_chunkObjectPool.Count > 0){
                    chunk = _chunkObjectPool.Pop();
                    chunk.Reinit(position);
                }
                else chunk = new Chunk(position);

                chunk.VoxelData = pair.Value;
                _chunks.Add(position, chunk);
                _unloadingQueue.Enqueue(position, PriorityUtil.EvictionPriority(ref position, ref _focus));
            }
        }

        /// <summary>
        ///     Removes the chunk data (eviction). Persistence hook could be added here.
        /// </summary>
        private void RemoveChunkData(int2 position){
            UnloadChunk(position);
        }

        /// <summary>
        ///     Unloads a chunk's data and raises <see cref="OnChunkUnloaded" />.
        /// </summary>
        /// <returns>True if the chunk was loaded and has been removed.</returns>
        internal bool UnloadChunk(int2 position){
            if (_unloadingQueue.Contains(position)) _unloadingQueue.Remove(position);
            if (!_chunks.TryGetValue(position, out Chunk chunk)) return false;

            CompleteReadersOf(position);
            chunk.Dispose();
            _chunks.Remove(position);
            _chunkObjectPool.Push(chunk);

            for (int pId = 0; pId < PartitionsPerChunk; pId++){
                int3 partitionPos = new(position.x, pId, position.y);
                _reMeshPartitions.Remove(partitionPos);
                _reCollidePartitions.Remove(partitionPos);
            }

            // A lit neighbour's light may hold edits of this chunk that a reload regenerates away.
            for (int x = -1; x <= 1; x++)
            for (int z = -1; z <= 1; z++)
                if (x != 0 || z != 0)
                    InvalidateLight(position + new int2(x, z));

            OnChunkUnloaded?.Invoke(position);
            return true;
        }

        /// <summary>
        ///     Unloads every loaded chunk (each raises <see cref="OnChunkUnloaded" />), e.g. before the world is
        ///     regenerated with another configuration.
        /// </summary>
        internal void UnloadAll(){
            int2[] loaded = new int2[_chunks.Count];
            _chunks.Keys.CopyTo(loaded, 0);
            foreach (int2 position in loaded) UnloadChunk(position);
        }

        /// <summary>
        ///     Completes any job that still reads <paramref name="chunkPos" /> through the current accessor
        ///     before the chunk's data is written or disposed.
        /// </summary>
        internal void CompleteReadersOf(int2 chunkPos){
            if (_accessorMap.ContainsKey(chunkPos)) AccessorChunkWriting?.Invoke();
            if (_lightJobMap.ContainsKey(chunkPos)) LightJobReading?.Invoke();
        }

        /// <summary>
        ///     Voxel data of the 3 × 3 neighbourhoods of the chunks to light, for <c>LightJob</c>.
        /// </summary>
        internal NativeParallelHashMap<int2, ChunkVoxelData>.ReadOnly GetLightJobChunks(HashSet<int2> positions){
            _lightJobMap.Clear();
            int needed = positions.Count * 9;
            if (_lightJobMap.Capacity < needed) _lightJobMap.Capacity = needed;
            foreach (int2 position in positions)
                for (int x = -1; x <= 1; x++)
                for (int z = -1; z <= 1; z++){
                    int2 pos = position + new int2(x, z);
                    if (!_chunks.TryGetValue(pos, out Chunk chunk))
                        throw new InvalidOperationException($"Chunk {pos} has not been generated");
                    _lightJobMap.TryAdd(pos, chunk.VoxelData);
                }

            return _lightJobMap.AsReadOnly();
        }

        /// <summary>
        ///     Hands the result of a light job to its chunk. A chunk that was lit before (and lost its light,
        ///     see <see cref="InvalidateLight" />) flags its partitions and its neighbours' for remeshing, because
        ///     faces sample the light across chunk borders. Returns false (and releases the light) if the chunk is
        ///     no longer loaded or already lit.
        /// </summary>
        internal bool ApplyLight(int2 position, ChunkLightData light, out Chunk chunk){
            if (!_chunks.TryGetValue(position, out chunk) || chunk.LightReady){
                light.Dispose();
                return false;
            }

            chunk.LightData.Dispose();
            chunk.LightData = light;
            chunk.LightReady = true;

            if (chunk.WasLit){
                chunk.WasLit = false;
                for (int x = -1; x <= 1; x++)
                for (int z = -1; z <= 1; z++)
                for (int y = 0; y < PartitionsPerChunk; y++)
                    FlagReMesh(new int3(position.x + x, y, position.y + z));
            }

            return true;
        }

        /// <summary>
        ///     Marks a lit chunk as unlit so the light job lights it again (its meshes stay until then).
        /// </summary>
        internal void InvalidateLight(int2 position){
            if (!_chunks.TryGetValue(position, out Chunk chunk) || !chunk.LightReady) return;
            chunk.LightReady = false;
            chunk.WasLit = true;
            OnLightInvalidated?.Invoke();
        }

        /// <summary>
        ///     Flags a partition of a loaded chunk for remeshing (light around it changed).
        /// </summary>
        internal void FlagReMesh(int3 partition){
            if (!_chunks.ContainsKey(partition.xz) || !_reMeshPartitions.Add(partition)) return;
            OnRemeshRequested?.Invoke();
        }

        /// <summary>
        ///     Builds a <see cref="ChunkAccessor" /> that includes a 3x3 neighborhood for given positions.
        /// </summary>
        internal ChunkAccessor GetAccessor(HashSet<int3> positions){
            _accessorMap.Clear();
            foreach (int3 position in positions){
                for (int x = -1; x <= 1; x++)
                for (int z = -1; z <= 1; z++){
                    int2 pos = position.xz + new int2(x, z);
                    if (!_chunks.TryGetValue(pos, out Chunk chunk))
                        throw new InvalidOperationException($"Chunk {pos} has not been generated");
                    if (!_accessorMap.ContainsKey(pos)) _accessorMap.Add(pos, chunk.VoxelData);
                }
            }

            return new ChunkAccessor(_accessorMap.AsReadOnly());
        }

        /// <summary>
        ///     Called when a partition is handed to the mesh job: the job meshes the data as it is now, so the remesh
        ///     flag is consumed here. An edit while the job runs flags the partition again and it is meshed once more;
        ///     removing the flag when the batch completes would drop that edit.
        /// </summary>
        internal void ReMeshStarted(int3 position){
            _reMeshPartitions.Remove(position);
        }

        /// <summary>
        ///     Callback after a partition's new mesh was applied: flag it for collider rebuild.
        /// </summary>
        internal void ReMeshedPartition(int3 position){
            _reCollidePartitions.Add(position);
        }

        /// <summary>
        ///     Callback after collider bake completes: remove flag.
        /// </summary>
        internal void ReCollidedPartition(int3 position){
            if (!_reCollidePartitions.Contains(position)) return;
            _reCollidePartitions.Remove(position);
        }

        /// <summary>
        ///     Flags all neighbor partitions (including own) for remeshing.
        /// </summary>
        /// <param name="blockPosition">World block position that changed.</param>
        private void ReMeshPartitions(int3 blockPosition){
            int3 pCoords = GetPartitionCoords(blockPosition);
            _reMeshPartitions.Add(pCoords);
            int3 localPos = GetPartitionLocalVoxelCoords(pCoords, blockPosition);
            switch (localPos.x % PartitionWidth){
                case MinPartitionPosXYZ:
                    _reMeshPartitions.Add(pCoords + Int3Left);
                    break;
                case MaxXPartitionPos:
                    _reMeshPartitions.Add(pCoords + Int3Right);
                    break;
            }

            switch (localPos.z % PartitionDepth){
                case MinPartitionPosXYZ:
                    _reMeshPartitions.Add(pCoords + Int3Backward);
                    break;
                case MaxZPartitionPos:
                    _reMeshPartitions.Add(pCoords + Int3Forward);
                    break;
            }

            switch (localPos.y % PartitionHeight){
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
        internal ushort GetVoxel(int3 position){
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
        internal bool SetVoxel(ushort voxelId, int3 position, bool remesh = true){
            int2 chunkPos = GetChunkCoords(position);
            int3 blockPos = GetLocalVoxelCoords(position);
            // Reject Y outside the chunk; the flat index would otherwise wrap into a neighbouring column
            if (blockPos.y < 0 || blockPos.y >= ChunkHeight) return false;
            if (!_chunks.TryGetValue(chunkPos, out Chunk chunk)){
                VoxelEngineLogger.Warn<ChunkManager>($"Chunk : {chunkPos} not loaded");
                return false;
            }

            // A partition that is already flagged for remeshing accepts edits too: every job that reads the chunk
            // is completed before the write, and the partition is simply meshed once more.
            CompleteReadersOf(chunkPos);
            ushort oldVoxel = chunk.VoxelData.GetVoxel(blockPos);
            bool result = chunk.VoxelData.SetVoxel(blockPos, voxelId);
            _chunks[chunkPos] = chunk;
            if (result) VoxelEdited?.Invoke(position, oldVoxel); // light follows every edit
            if (remesh && result){
                ReMeshPartitions(position);
                OnChunkChange?.Invoke(chunk);
            }

            return result;
        }

        /// <summary>
        ///     Number of currently loaded chunks.
        /// </summary>
        public int ChunkCount(){
            return _chunks.Count;
        }

        /// <summary>
        ///     Checks if a chunk is loaded.
        /// </summary>
        public bool IsChunkLoaded(int2 position){
            return _chunks.ContainsKey(position);
        }

        /// <summary>
        ///     Attempts to get a chunk by its position.
        /// </summary>
        internal bool TryGetChunk(int2 position, out Chunk chunk){
            return _chunks.TryGetValue(position, out chunk);
        }

        #endregion
    }
}
