using System;
using System.Collections.Generic;
using Engine.Scripts.Behaviour;
using Engine.Scripts.Settings;
using Engine.Scripts.Utils;
using Engine.Scripts.Utils.Extensions;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Pool;
using static Engine.Scripts.Jobs.Core.PriorityUtil;
using static Engine.Scripts.Utils.VoxelConstants;
using Object = UnityEngine.Object;

namespace Engine.Scripts.World{
    /// <summary>
    ///     Pool for <see cref="ChunkBehaviour" /> instances (render + collider). Handles activation & reclamation.
    ///     Evicts the farthest from the focus first (<see cref="EvictionQueue" />).
    /// </summary>
    internal class ChunkPool{
        private readonly Dictionary<int2, ChunkBehaviour> _chunkMap;
        private readonly int _chunkPoolSize;
        private readonly EvictionQueue _chunkQueue;
        private readonly HashSet<int3> _colliderSet;
        private readonly Dictionary<int3, ChunkPartition> _meshMap;
        private readonly int _partitionPoolSize;
        private readonly EvictionQueue _partitionQueue;
        private readonly ObjectPool<ChunkBehaviour> _pool;
        internal Action<int2> OnChunkEvicted;
        internal Action<int3> OnPartitionEvicted;

        /// <summary>
        ///     Raised before a partition's collider mesh is cleared or rewritten. A collider bake job may still be
        ///     reading that mesh, so listeners finish it first.
        /// </summary>
        internal Action PartitionMeshWriting;
        private bool _emitEvictionEvents = true;

        private Focus _focus;

        /// <summary>
        ///     Constructs a pool based on draw/update distances.
        /// </summary>
        internal ChunkPool(Transform transform, VoxelEngineSettings settings){
            _chunkPoolSize = (settings.Chunk.ActiveDrawDistance + 2).SquareSize();
            _chunkMap = new Dictionary<int2, ChunkBehaviour>(_chunkPoolSize);

            _partitionPoolSize = settings.Chunk.ActiveDrawDistance.SquareSize() * PartitionsPerChunk;
            _meshMap = new Dictionary<int3, ChunkPartition>(_partitionPoolSize);

            // An entry per active partition (cleared on reclaim), so the set never rehashes during flight
            _colliderSet = new HashSet<int3>(_partitionPoolSize);

            // The queues hold at most one entry over the pool size before a reclaim
            _chunkQueue = new EvictionQueue(_chunkPoolSize + 1, true);
            _partitionQueue = new EvictionQueue(_partitionPoolSize + 1, false);

            _pool = new ObjectPool<ChunkBehaviour>(
                () => {
                    GameObject go = Object.Instantiate(settings.Chunk.ChunkPrefab, transform);
                    go.name = "Chunk"; // named once; renaming per claim would build a string every claim
                    go.SetActive(false);
                    ChunkBehaviour chunkBehaviour = go.GetComponent<ChunkBehaviour>();
                    chunkBehaviour.Init();
                    return chunkBehaviour;
                },
                chunkBehaviour => chunkBehaviour.gameObject.SetActive(true),
                chunkBehaviour => chunkBehaviour.gameObject.SetActive(false),
                chunkBehaviour => {
                    if (chunkBehaviour) UnityEngine.Rendering.CoreUtils.Destroy(chunkBehaviour.gameObject); // DestroyImmediate in edit mode
                },
                false, _chunkPoolSize, _chunkPoolSize
            );

            // Instantiate the whole pool now; claiming a chunk during flight must not Instantiate.
            ChunkBehaviour[] warmUp = new ChunkBehaviour[_chunkPoolSize];
            for (int i = 0; i < warmUp.Length; i++) warmUp[i] = _pool.Get();
            for (int i = 0; i < warmUp.Length; i++) _pool.Release(warmUp[i]);
        }

        internal bool IsPartitionActive(int3 pos){
            return _meshMap.ContainsKey(pos);
        }

        /// <summary>
        ///     Returns whether the collider of a partition is ready: baked, or known to be empty.
        /// </summary>
        internal bool IsCollidable(int3 pos){
            return _colliderSet.Contains(pos);
        }

        /// <summary>
        ///     Updates focus and queue priorities.
        /// </summary>
        internal void FocusUpdate(Focus focus){
            FocusUpdateChunks(focus);
            FocusUpdatePartitions(focus);
        }

        /// <summary>
        ///     The first half of <see cref="FocusUpdate" />: sets the focus and reprioritises the chunk queue. The
        ///     scheduler runs the two halves as separate items of the frame's budget.
        /// </summary>
        internal void FocusUpdateChunks(Focus focus){
            _focus = focus;
            _chunkQueue.UpdateFocus(focus);
        }

        /// <summary>The second half of <see cref="FocusUpdate" />: reprioritises the partition queue.</summary>
        internal void FocusUpdatePartitions(Focus focus){
            _focus = focus;
            _partitionQueue.UpdateFocus(focus);
        }

        private float PriorityCalc(int3 position){
            return EvictionPriority(ref position, ref _focus);
        }

        /// <summary>
        ///     Returns whether a chunk is active (rendered).
        /// </summary>
        internal bool IsChunkActive(int2 pos){
            return _chunkMap.ContainsKey(pos);
        }

        /// <summary>
        ///     Returns existing instance or claims a new one.
        /// </summary>
        private ChunkBehaviour GetOrClaimChunk(int2 position){
            return IsChunkActive(position) ? _chunkMap[position] : ClaimChunk(position);
        }

        /// <summary>
        ///     Claims a new instance; evicts oldest if capacity reached.
        /// </summary>
        private ChunkBehaviour ClaimChunk(int2 position){
            if (_chunkMap.ContainsKey(position))
                throw new InvalidOperationException($"Chunk ({position}) already active");
            if (_chunkQueue.Count >= _chunkPoolSize){
                int2 reclaim = _chunkQueue.DequeueColumn();
                ReclaimChunk(reclaim);
            }

            ChunkBehaviour behaviour;
            using (VoxelProfilerMarkers.PoolActivateChunk.Auto()){
                behaviour = _pool.Get();
                behaviour.ActivePartitions = 0;
                behaviour.CollidablePartitions = 0;
                behaviour.Transform.position = new float3(position.x * ChunkWidth, 0, position.y * ChunkDepth);
            }

            _chunkMap.Add(position, behaviour);
            _chunkQueue.Enqueue(position);
            return behaviour;
        }

        internal float GetPartitionPrioThreshold(){
            return _partitionQueue.Count < _partitionPoolSize
                ? float.MinValue
                : PriorityCalc(_partitionQueue.First);
        }

        internal ChunkPartition GetOrClaimPartition(int3 position){
            return IsPartitionActive(position) ? GetPartition(position) : ClaimPartition(position);
        }

        public ChunkPartition GetPartition(int3 pos){
            return _meshMap[pos];
        }

        private ChunkPartition ClaimPartition(int3 position){
            if (_meshMap.ContainsKey(position))
                throw new InvalidOperationException($"Partition ({position}) already active");

            using ProfilerMarker.AutoScope _ = VoxelProfilerMarkers.PoolClaimPartition.Auto();
            ChunkBehaviour behaviour = GetOrClaimChunk(position.xz);
            ChunkPartition partition = behaviour.GetPartition(position.y);

            // A partition that is already clear is not written: a running bake need not finish first (DEC-232)
            if (!partition.IsClear) PartitionMeshWriting?.Invoke();
            partition.Clear();
            _meshMap.Add(position, partition);
            behaviour.ActivePartitions |= 1u << position.y;
            _partitionQueue.Enqueue(position);

            if (_partitionQueue.Count <= _partitionPoolSize) return partition;

            int3 reclaim = _partitionQueue.Dequeue();
            ReclaimPartition(reclaim);
            return partition;
        }

        /// <summary>
        ///     Reclaims a pooled chunk with its partitions, e.g. after the chunk manager unloaded its data: the
        ///     partitions would otherwise keep their mesh and never be meshed again once the chunk is reloaded.
        /// </summary>
        /// <returns>True if the chunk was active.</returns>
        internal bool TryReclaimChunk(int2 position){
            if (!IsChunkActive(position)) return false;
            _chunkQueue.TryRemove(position);
            ReclaimChunk(position);
            return true;
        }

        private void ReclaimChunk(int2 reclaim){
            using ProfilerMarker.AutoScope _ = VoxelProfilerMarkers.PoolReclaimChunk.Auto();
            ChunkBehaviour reclaimBehaviour = _chunkMap[reclaim];
            PartitionMeshWriting?.Invoke(); // ClearData clears collider meshes a running bake may read
            reclaimBehaviour.ClearData();
            using (VoxelProfilerMarkers.PoolDeactivateChunk.Auto()) _pool.Release(reclaimBehaviour);
            _chunkMap.Remove(reclaim);

            for (int pId = 0; pId < PartitionsPerChunk; pId++){
                int3 partitionPos = new(reclaim.x, pId, reclaim.y);
                ReclaimPartition(partitionPos);
            }

            if (_emitEvictionEvents) OnChunkEvicted?.Invoke(reclaim);
        }

        private void ReclaimPartition(int3 partitionPos){
            if (_meshMap.TryGetValue(partitionPos, out ChunkPartition reclaimPartition)){
                if (!reclaimPartition.IsClear) PartitionMeshWriting?.Invoke();
                reclaimPartition.Clear();
                _meshMap.Remove(partitionPos);
            }

            _partitionQueue.TryRemove(partitionPos);
            _colliderSet.Remove(partitionPos);
            // A reclaimed chunk is out of the map already; its masks are reset when it is claimed again
            if (_chunkMap.TryGetValue(partitionPos.xz, out ChunkBehaviour behaviour)){
                uint keep = ~(1u << partitionPos.y);
                behaviour.ActivePartitions &= keep;
                behaviour.CollidablePartitions &= keep;
            }

            if (_emitEvictionEvents) OnPartitionEvicted?.Invoke(partitionPos);
        }

        /// <summary>
        ///     Tells listeners that collider meshes of active partitions are about to be rewritten.
        /// </summary>
        internal void NotifyPartitionMeshWriting(){
            PartitionMeshWriting?.Invoke();
        }

        /// <summary>
        ///     Callback after collider bake: mark the partition collidable.
        /// </summary>
        internal void ColliderBaked(int3 position){
            _colliderSet.Add(position);
            // The mask holds only active partitions, a subset of the set: a set bit always means collidable
            if (_chunkMap.TryGetValue(position.xz, out ChunkBehaviour behaviour) &&
                (behaviour.ActivePartitions & (1u << position.y)) != 0)
                behaviour.CollidablePartitions |= 1u << position.y;
        }

        /// <summary>
        ///     Bit p: partition p of the column is active (<see cref="IsPartitionActive" />); 0 when the chunk is
        ///     not claimed. One lookup for the whole column (DEC-232).
        /// </summary>
        internal uint ActivePartitionMask(int2 column){
            return _chunkMap.TryGetValue(column, out ChunkBehaviour behaviour) ? behaviour.ActivePartitions : 0;
        }

        /// <summary>
        ///     Bit p: partition p of the column is active and collidable (<see cref="IsCollidable" />); a clear bit
        ///     may still be collidable. One lookup for the whole column (DEC-232).
        /// </summary>
        internal uint CollidablePartitionMask(int2 column){
            return _chunkMap.TryGetValue(column, out ChunkBehaviour behaviour) ? behaviour.CollidablePartitions : 0;
        }

        /// <summary>
        ///     Reclaims every active chunk with its partitions and raises the eviction events, so a regenerated
        ///     world starts from an empty pool and listeners (renderer, chunk manager) drop their state.
        /// </summary>
        internal void ReclaimAll(){
            int2[] active = new int2[_chunkMap.Count];
            _chunkMap.Keys.CopyTo(active, 0);
            foreach (int2 position in active) TryReclaimChunk(position);
        }

        /// <summary>
        ///     Destroys the pool's chunk objects (with their partitions and collider meshes), e.g. before the world
        ///     is built again with another draw distance. Call it after <see cref="ReclaimAll" />: only chunks back in
        ///     the pool are destroyed. Not needed when the world itself is destroyed, which destroys its children.
        /// </summary>
        internal void DestroyChunkObjects(){
            _pool.Clear();
        }

        internal void Dispose(){
            _emitEvictionEvents = false;
            OnChunkEvicted = null;
            OnPartitionEvicted = null;
            PartitionMeshWriting = null;
        }
    }
}
