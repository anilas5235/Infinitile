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

namespace Engine.Scripts.Components
{
    /// <summary>
    ///     Pool for <see cref="ChunkBehaviour" /> instances (render + collider). Handles activation & reclamation.
    ///     Uses a priority queue ordered by distance to focus for eviction.
    /// </summary>
    internal class ChunkPool
    {
        internal Action<int2> OnChunkEvicted;
        internal Action<int3> OnPartitionEvicted;

        private readonly Dictionary<int2, ChunkBehaviour> _chunkMap;
        private readonly int _chunkPoolSize;
        private readonly SimpleFastPriorityQueue<int2, int> _chunkQueue;
        private readonly int _colliderPoolSize;
        private readonly SimpleFastPriorityQueue<int3, int> _colliderQueue;
        private readonly HashSet<int3> _colliderSet;
        private readonly Dictionary<int3, ChunkPartition> _meshMap;
        private readonly int _partitionPoolSize;
        private readonly SimpleFastPriorityQueue<int3, int> _partitionQueue;
        private readonly ObjectPool<ChunkBehaviour> _pool;
        private bool _emitEvictionEvents = true;

        private int3 _focus;

        /// <summary>
        ///     Constructs a pool based on draw/update distances.
        /// </summary>
        internal ChunkPool(Transform transform, VoxelEngineSettings settings)
        {
            _chunkPoolSize = (settings.Chunk.DrawDistance + 2).SquareSize();
            _chunkMap = new Dictionary<int2, ChunkBehaviour>(_chunkPoolSize);

            _partitionPoolSize = settings.Chunk.DrawDistance.SquareSize() * PartitionsPerChunk;
            _meshMap = new Dictionary<int3, ChunkPartition>(_partitionPoolSize);

            _colliderPoolSize = settings.Chunk.UpdateDistance.SquareSize() * PartitionsPerChunk;
            _colliderSet = new HashSet<int3>(_colliderPoolSize);

            _chunkQueue = new SimpleFastPriorityQueue<int2, int>();
            _partitionQueue = new SimpleFastPriorityQueue<int3, int>();
            _colliderQueue = new SimpleFastPriorityQueue<int3, int>();

            _pool = new ObjectPool<ChunkBehaviour>(
                () =>
                {
                    GameObject go = Object.Instantiate(settings.Chunk.ChunkPrefab, transform);
                    go.SetActive(false);
                    ChunkBehaviour chunkBehaviour = go.GetComponent<ChunkBehaviour>();
                    chunkBehaviour.Init();
                    return chunkBehaviour;
                },
                chunkBehaviour => chunkBehaviour.gameObject.SetActive(true),
                chunkBehaviour => chunkBehaviour.gameObject.SetActive(false),
                null, false, _chunkPoolSize, _chunkPoolSize
            );
        }

        internal bool IsPartitionActive(int3 pos)
        {
            return _meshMap.ContainsKey(pos);
        }

        /// <summary>
        ///     Returns whether a chunk already has a baked collider.
        /// </summary>
        internal bool IsCollidable(int3 pos)
        {
            return _colliderSet.Contains(pos);
        }

        /// <summary>
        ///     Updates focus and queue priorities.
        /// </summary>
        internal void FocusUpdate(int3 focus)
        {
            _focus = focus;
            _chunkQueue.UpdateAllPriorities(PriorityCalc);
            _partitionQueue.UpdateAllPriorities(PriorityCalc);
            _colliderQueue.UpdateAllPriorities(PriorityCalc);
        }


        private int PriorityCalc(int2 position)
        {
            return -DistPriority(ref position, ref _focus);
        }

        private int PriorityCalc(int3 position)
        {
            return -DistPriority(ref position, ref _focus);
        }

        /// <summary>
        ///     Returns whether a chunk is active (rendered).
        /// </summary>
        internal bool IsChunkActive(int2 pos)
        {
            return _chunkMap.ContainsKey(pos);
        }

        /// <summary>
        ///     Returns existing instance or claims a new one.
        /// </summary>
        internal ChunkBehaviour GetOrClaimChunk(int2 position)
        {
            return IsChunkActive(position) ? _chunkMap[position] : ClaimChunk(position);
        }

        /// <summary>
        ///     Claims a new instance; evicts oldest if capacity reached.
        /// </summary>
        internal ChunkBehaviour ClaimChunk(int2 position)
        {
            if (_chunkMap.ContainsKey(position))
                throw new InvalidOperationException($"Chunk ({position}) already active");
            if (_chunkQueue.Count >= _chunkPoolSize)
            {
                int2 reclaim = _chunkQueue.Dequeue();
                ReclaimChunk(reclaim);
            }

            ChunkBehaviour behaviour = _pool.Get();
            behaviour.transform.position = new float3(position.x * ChunkWidth, 0, position.y * ChunkDepth);
            behaviour.name = $"Chunk({position.x},{position.y})";
            _chunkMap.Add(position, behaviour);
            _chunkQueue.Enqueue(position, PriorityCalc(position));
            return behaviour;
        }

        internal int GetPartitionPrioThreshold()
        {
            return _partitionQueue.Count < _partitionPoolSize
                ? int.MinValue
                : PriorityCalc(_partitionQueue.First);
        }

        internal ChunkPartition GetOrClaimPartition(int3 position)
        {
            return IsPartitionActive(position) ? GetPartition(position) : ClaimPartition(position);
        }

        public ChunkPartition GetPartition(int3 pos)
        {
            return _meshMap[pos];
        }

        private ChunkPartition ClaimPartition(int3 position)
        {
            if (_meshMap.ContainsKey(position))
                throw new InvalidOperationException($"Partition ({position}) already active");

            ChunkBehaviour behaviour = GetOrClaimChunk(position.xz);
            ChunkPartition partition = behaviour.GetPartition(position.y);

            partition.Clear();
            _meshMap.Add(position, partition);
            _partitionQueue.Enqueue(position, PriorityCalc(position));

            if (_partitionQueue.Count <= _partitionPoolSize) return partition;

            int3 reclaim = _partitionQueue.Dequeue();
            ReclaimPartition(reclaim);
            return partition;
        }

        private void ReclaimChunk(int2 reclaim)
        {
            ChunkBehaviour reclaimBehaviour = _chunkMap[reclaim];
            reclaimBehaviour.ClearData();
            _pool.Release(reclaimBehaviour);
            _chunkMap.Remove(reclaim);

            for (int pId = 0; pId < PartitionsPerChunk; pId++)
            {
                int3 partitionPos = new(reclaim.x, pId, reclaim.y);
                ReclaimPartition(partitionPos);
            }

            if (_emitEvictionEvents) OnChunkEvicted?.Invoke(reclaim);
        }

        private void ReclaimPartition(int3 partitionPos)
        {
            if (_meshMap.TryGetValue(partitionPos, out ChunkPartition reclaimPartition))
            {
                reclaimPartition.Clear();
                _meshMap.Remove(partitionPos);
            }

            if (_partitionQueue.Contains(partitionPos)) _partitionQueue.Remove(partitionPos);
            if (_colliderQueue.Contains(partitionPos)) _colliderQueue.Remove(partitionPos);
            _colliderSet.Remove(partitionPos);

            if (_emitEvictionEvents) OnPartitionEvicted?.Invoke(partitionPos);
        }

        /// <summary>
        ///     Callback after collider bake: mark chunk collidable.
        /// </summary>
        internal void ColliderBaked(int3 position)
        {
            _colliderSet.Add(position);
        }

        internal void Dispose()
        {
            _emitEvictionEvents = false;
            OnChunkEvicted = null;
            OnPartitionEvicted = null;
        }
    }
}