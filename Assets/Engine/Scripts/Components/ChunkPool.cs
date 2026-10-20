using System;
using System.Collections.Generic;
using Engine.Scripts.Behaviour;
using Engine.Scripts.Settings;
using Engine.Scripts.ThirdParty.Priority_Queue;
using Engine.Scripts.Utils.Extensions;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Pool;
using static Engine.Scripts.Jobs.Core.PriorityUtil;
using static Engine.Scripts.Utils.VoxelConstants;
using Object = UnityEngine.Object;

namespace Engine.Scripts.Components{
    /// <summary>
    ///     Pool for <see cref="ChunkBehaviour" /> instances (render + collider). Handles activation & reclamation.
    ///     Uses a priority queue ordered by distance to focus for eviction.
    /// </summary>
    internal class ChunkPool{
        private readonly Dictionary<int2, ChunkBehaviour> _chunkMap;
        private readonly int _chunkPoolSize;
        private readonly SimpleFastPriorityQueue<int2, float> _chunkQueue;
        private readonly int _colliderPoolSize;
        private readonly SimpleFastPriorityQueue<int3, float> _colliderQueue;
        private readonly HashSet<int3> _colliderSet;
        private readonly Dictionary<int3, ChunkPartition> _meshMap;
        private readonly int _partitionPoolSize;
        private readonly SimpleFastPriorityQueue<int3, float> _partitionQueue;
        private readonly ObjectPool<ChunkBehaviour> _pool;
        internal Action<int2> OnChunkEvicted;
        internal Action<int3> OnPartitionEvicted;

        /// <summary>
        ///     F-04: raised before a partition's collider mesh is cleared or rewritten. A collider bake job may still be
        ///     reading that mesh, so listeners finish it first.
        /// </summary>
        internal Action PartitionMeshWriting;
        private bool _emitEvictionEvents = true;

        private Focus _focus;

        /// <summary>
        ///     Constructs a pool based on draw/update distances.
        /// </summary>
        internal ChunkPool(Transform transform, VoxelEngineSettings settings){
            _chunkPoolSize = (settings.Chunk.DrawDistance + 2).SquareSize();
            _chunkMap = new Dictionary<int2, ChunkBehaviour>(_chunkPoolSize);

            _partitionPoolSize = settings.Chunk.DrawDistance.SquareSize() * PartitionsPerChunk;
            _meshMap = new Dictionary<int3, ChunkPartition>(_partitionPoolSize);

            _colliderPoolSize = settings.Chunk.UpdateDistance.SquareSize() * PartitionsPerChunk;
            // PERF-04: an entry per active partition (cleared on reclaim), so the set never rehashes during flight
            _colliderSet = new HashSet<int3>(_partitionPoolSize);

            _chunkQueue = new SimpleFastPriorityQueue<int2, float>();
            _partitionQueue = new SimpleFastPriorityQueue<int3, float>();
            _colliderQueue = new SimpleFastPriorityQueue<int3, float>();
            // PERF-04: the queues hold at most one entry over the pool size before a reclaim
            _chunkQueue.Reserve(_chunkPoolSize + 1);
            _partitionQueue.Reserve(_partitionPoolSize + 1);
            _colliderQueue.Reserve(_colliderPoolSize + 1);

            _pool = new ObjectPool<ChunkBehaviour>(
                () => {
                    GameObject go = Object.Instantiate(settings.Chunk.ChunkPrefab, transform);
                    go.name = "Chunk"; // PERF-04: named once; renaming per claim built a string every claim
                    go.SetActive(false);
                    ChunkBehaviour chunkBehaviour = go.GetComponent<ChunkBehaviour>();
                    chunkBehaviour.Init();
                    return chunkBehaviour;
                },
                chunkBehaviour => chunkBehaviour.gameObject.SetActive(true),
                chunkBehaviour => chunkBehaviour.gameObject.SetActive(false),
                null, false, _chunkPoolSize, _chunkPoolSize
            );

            // PERF-04: instantiate the whole pool now; claiming a chunk during flight must not Instantiate.
            ChunkBehaviour[] warmUp = new ChunkBehaviour[_chunkPoolSize];
            for (int i = 0; i < warmUp.Length; i++) warmUp[i] = _pool.Get();
            for (int i = 0; i < warmUp.Length; i++) _pool.Release(warmUp[i]);
        }

        internal bool IsPartitionActive(int3 pos){
            return _meshMap.ContainsKey(pos);
        }

        /// <summary>
        ///     Returns whether a chunk already has a baked collider.
        /// </summary>
        internal bool IsCollidable(int3 pos){
            return _colliderSet.Contains(pos);
        }

        /// <summary>
        ///     Updates focus and queue priorities.
        /// </summary>
        internal void FocusUpdate(Focus focus){
            _focus = focus;
            // N-01 / PERF-04: static lambdas with explicit state; instance method groups allocate a delegate per call
            // F-23 / DEC-14: reclaim by plain distance
            _chunkQueue.UpdateAllPriorities(focus, static (f, pos) => EvictionPriority(ref pos, ref f));
            _partitionQueue.UpdateAllPriorities(focus, static (f, pos) => EvictionPriority(ref pos, ref f));
            _colliderQueue.UpdateAllPriorities(focus, static (f, pos) => EvictionPriority(ref pos, ref f));
        }

        private float PriorityCalc(int2 position){
            return EvictionPriority(ref position, ref _focus); // F-23
        }

        private float PriorityCalc(int3 position){
            return EvictionPriority(ref position, ref _focus); // F-23
        }

        /// <summary>
        ///     Returns whether a chunk is active (rendered).
        /// </summary>
        private bool IsChunkActive(int2 pos){
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
                int2 reclaim = _chunkQueue.Dequeue();
                ReclaimChunk(reclaim);
            }

            ChunkBehaviour behaviour = _pool.Get();
            behaviour.Transform.position = new float3(position.x * ChunkWidth, 0, position.y * ChunkDepth);
            _chunkMap.Add(position, behaviour);
            _chunkQueue.Enqueue(position, PriorityCalc(position));
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

            ChunkBehaviour behaviour = GetOrClaimChunk(position.xz);
            ChunkPartition partition = behaviour.GetPartition(position.y);

            PartitionMeshWriting?.Invoke(); // F-04
            partition.Clear();
            _meshMap.Add(position, partition);
            _partitionQueue.Enqueue(position, PriorityCalc(position));

            if (_partitionQueue.Count <= _partitionPoolSize) return partition;

            int3 reclaim = _partitionQueue.Dequeue();
            ReclaimPartition(reclaim);
            return partition;
        }

        private void ReclaimChunk(int2 reclaim){
            ChunkBehaviour reclaimBehaviour = _chunkMap[reclaim];
            reclaimBehaviour.ClearData();
            _pool.Release(reclaimBehaviour);
            _chunkMap.Remove(reclaim);

            for (int pId = 0; pId < PartitionsPerChunk; pId++){
                int3 partitionPos = new(reclaim.x, pId, reclaim.y);
                ReclaimPartition(partitionPos);
            }

            if (_emitEvictionEvents) OnChunkEvicted?.Invoke(reclaim);
        }

        private void ReclaimPartition(int3 partitionPos){
            if (_meshMap.TryGetValue(partitionPos, out ChunkPartition reclaimPartition)){
                PartitionMeshWriting?.Invoke(); // F-04
                reclaimPartition.Clear();
                _meshMap.Remove(partitionPos);
            }

            if (_partitionQueue.Contains(partitionPos)) _partitionQueue.Remove(partitionPos);
            if (_colliderQueue.Contains(partitionPos)) _colliderQueue.Remove(partitionPos);
            _colliderSet.Remove(partitionPos);

            if (_emitEvictionEvents) OnPartitionEvicted?.Invoke(partitionPos);
        }

        /// <summary>
        ///     F-04: tells listeners that collider meshes of active partitions are about to be rewritten.
        /// </summary>
        internal void NotifyPartitionMeshWriting(){
            PartitionMeshWriting?.Invoke();
        }

        /// <summary>
        ///     Callback after collider bake: mark chunk collidable.
        /// </summary>
        internal void ColliderBaked(int3 position){
            _colliderSet.Add(position);
        }

        /// <summary>
        ///     INT-30: reclaims every active chunk with its partitions and raises the eviction events, so a regenerated
        ///     world starts from an empty pool and listeners (renderer, chunk manager) drop their state.
        /// </summary>
        internal void ReclaimAll(){
            int2[] active = new int2[_chunkMap.Count];
            _chunkMap.Keys.CopyTo(active, 0);
            foreach (int2 position in active){
                if (_chunkQueue.Contains(position)) _chunkQueue.Remove(position);
                ReclaimChunk(position);
            }
        }

        internal void Dispose(){
            _emitEvictionEvents = false;
            OnChunkEvicted = null;
            OnPartitionEvicted = null;
            PartitionMeshWriting = null;
        }
    }
}
