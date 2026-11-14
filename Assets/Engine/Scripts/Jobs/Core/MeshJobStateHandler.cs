using System;
using System.Collections.Generic;
using Engine.Scripts.Jobs.ColliderMeshing;
using Engine.Scripts.Settings;
using Engine.Scripts.Utils;
using Engine.Scripts.World;
using Unity.Mathematics;

namespace Engine.Scripts.Jobs.Core{
    /// <summary>
    ///     Job state handler for managing mesh building jobs for partitions.
    ///     Handles enqueuing, dispatching, and result collection for mesh generation.
    /// </summary>
    internal class MeshJobStateHandler : JobStateHandler<int3>{
        /// <summary>
        ///     Partitions without a voxel that one turn may take from the queue, in mesh batches. They cost a claim in
        ///     the pool and nothing else, so a turn takes more of them than of the partitions that need a job.
        /// </summary>
        private const int EmptyPartitionsPerBatch = 4;

        private readonly CMeshBuildScheduler _cMeshBuildScheduler;
        private readonly ColliderJobStateHandler _colliderJobHandler;
        private readonly Func<PriorityUtil.Focus, int3, bool> _outsideWindow; // created once: FocusUpdate must not allocate
        private readonly Predicate<int3> _lostItsChunks; // created once: JobUpdateStep must not allocate
        private readonly List<int3> _emptyPartitions; // the partitions without a voxel of the current turn

        /// <summary>
        ///     Initializes a new instance of the MeshJobStateHandler class.
        /// </summary>
        /// <param name="settings">The voxel engine settings.</param>
        /// <param name="chunkManager">The chunk manager reference.</param>
        /// <param name="chunkPool">The chunk pool reference.</param>
        /// <param name="cMeshBuildScheduler">The mesh build scheduler for dispatching mesh building jobs.</param>
        /// <param name="colliderJobHandler">The collider job handler to wake up when meshing completes.</param>
        public MeshJobStateHandler(VoxelEngineSettings settings, ChunkManager chunkManager, ChunkPool chunkPool,
            CMeshBuildScheduler cMeshBuildScheduler, ColliderJobStateHandler colliderJobHandler) :
            base(settings, chunkManager, chunkPool){
            _cMeshBuildScheduler = cMeshBuildScheduler;
            _colliderJobHandler = colliderJobHandler;
            _outsideWindow = (focus, pos) => !IsInDrawWindow(pos, focus);
            _lostItsChunks = pos => !CanGenerateMeshForChunk(pos.xz);
            int side = 2 * settings.Chunk.DrawDistance + 1;
            int layers = math.min(VoxelConstants.PartitionsPerChunk, side);
            // The batch size follows the draw distance; it is fixed here, when the world is built
            Reserve(side * side * layers, settings.Scheduler.MeshBatchSizeAt(settings.Chunk.DrawDistance));
            _emptyPartitions = new List<int3>(EmptyPartitionsPerBatch * BatchSize);
        }

        /// <summary>Partitions handed to the mesh job per batch in this world.</summary>
        internal int MeshBatchSize => BatchSize;

        /// <summary>
        ///     Updates focus: drops the queued partitions that left the draw window and reprioritizes the rest based
        ///     on distance from focus. So the queue never holds more than the window.
        /// </summary>
        /// <param name="focus">The new focus position.</param>
        public override void FocusUpdate(PriorityUtil.Focus focus){
            WakeUp();
            PruneQueue(focus, _outsideWindow);
            // Static lambda with explicit state instead of a closure capturing the focus
            Queue.UpdateAllPriorities(focus, static (f, pos) => PriorityUtil.DistPriority(ref pos, ref f));
        }

        /// <summary>
        ///     The renderer builds the geometry of every dispatched partition on the GPU, a few per frame. While it
        ///     is behind, the next batch waits here: the partitions stay in the priority queue, which follows the
        ///     focus, instead of piling up in the renderer's first-in-first-out list.
        /// </summary>
        protected override bool CanDispatch => !_cMeshBuildScheduler.RendererBehind;

        /// <summary>
        ///     Enqueues partitions within draw distance that need mesh generation.
        ///     Only enqueues if the parent chunk has all neighboring chunks loaded.
        /// </summary>
        /// <param name="focus">The current focus position.</param>
        /// <returns>True if any partitions were enqueued, false if queue is empty.</returns>
        protected override bool EnqueueStep(PriorityUtil.Focus focus){
            int draw = Settings.Chunk.DrawDistance;
            float prioThreshold = ChunkPool.GetPartitionPrioThreshold();

            for (int x = -draw; x <= draw; x++)
            for (int z = -draw; z <= draw; z++){
                if (!CanGenerateMeshForChunk(focus.Pos.xz + new int2(x, z))) continue;
                // Only the vertical draw window is queued, the same filter IsPartitionStillRelevant applies;
                // queueing all layers would make JobUpdateStep drop and re-queue the others every cycle.
                int minY = math.max(0, focus.Pos.y - draw);
                int maxY = math.min(VoxelConstants.PartitionsPerChunk - 1, focus.Pos.y + draw);
                for (int y = minY; y <= maxY; y++){
                    int3 pos = new(x + focus.Pos.x, y, z + focus.Pos.z);
                    if (Queue.Contains(pos) || !ShouldScheduleForMeshing(pos)) continue;

                    // The threshold is the pool's eviction priority, compare in the same measure
                    if (PriorityUtil.EvictionPriority(ref pos, ref focus) <= prioThreshold) continue;

                    Queue.Enqueue(pos, PriorityUtil.DistPriority(ref pos, ref focus));
                }
            }

            return Queue.Count > 0;
        }

        /// <summary>
        ///     Takes the next partitions from the queue. The ones that hold a voxel go to the mesh build scheduler
        ///     as a batch. The ones that hold only air need no mesh job, no GPU build and no collider bake: they
        ///     become active at once, with an empty mesh (see <see cref="CMeshBuildScheduler.ApplyEmpty" />). They
        ///     do not count against the batch, but a turn takes a limited number of them.
        /// </summary>
        /// <param name="focus">The current focus position.</param>
        /// <returns>True if jobs were dispatched or batch is full, false if scheduler is busy.</returns>
        protected override bool JobUpdateStep(PriorityUtil.Focus focus){
            if (!_cMeshBuildScheduler.IsReady) return false;

            float prioThreshold = ChunkPool.GetPartitionPrioThreshold();
            int emptyLimit = EmptyPartitionsPerBatch * BatchSize;

            while (Set.Count < BatchSize && _emptyPartitions.Count < emptyLimit && Queue.Count > 0){
                int3 partition = Queue.Dequeue();
                if (!IsPartitionStillRelevant(partition, focus, prioThreshold)) continue;
                if (!CanGenerateMeshForChunk(partition.xz)) continue;
                if (!ShouldScheduleForMeshing(partition)) continue;

                if (ChunkManager.PartitionHasVoxels(partition)) Set.Add(partition);
                else _emptyPartitions.Add(partition);
            }

            if (_emptyPartitions.Count > 0){
                for (int i = 0; i < _emptyPartitions.Count; i++) _cMeshBuildScheduler.ApplyEmpty(_emptyPartitions[i]);
                _emptyPartitions.Clear();
                // A claim in a full pool can reclaim a pooled chunk, and the world then unloads its data: a
                // partition of this batch that lost a chunk of its neighbourhood is left to the next scan.
                if (Set.Count > 0) Set.RemoveWhere(_lostItsChunks);
            }

            if (Set.Count > 0) _cMeshBuildScheduler.Start(Set);

            return true;
        }

        /// <summary>
        ///     Collects results from completed mesh building and wakes up the collider handler.
        /// </summary>
        /// <returns>True if collection completed, false if scheduler is still working.</returns>
        protected override bool CollectResultsStep(){
            // A batch that was already applied elsewhere only clears the set; waiting for it would stall the handler.
            if (!_cMeshBuildScheduler.IsReady){
                if (!_cMeshBuildScheduler.IsComplete) return false;
                _cMeshBuildScheduler.Complete();
            }

            Set.Clear();
            _colliderJobHandler.WakeUp();
            return true;
        }

        /// <summary>
        ///     Checks if the chunk and all neighboring chunks are loaded and lit (faces at chunk borders sample the
        ///     neighbour's light). Required before mesh generation can proceed.
        /// </summary>
        /// <param name="position">The chunk position to check.</param>
        /// <returns>True if the chunk and all neighbors are loaded and lit.</returns>
        private bool CanGenerateMeshForChunk(int2 position){
            for (int x = -1; x <= 1; x++)
            for (int z = -1; z <= 1; z++){
                if (!ChunkManager.TryGetChunk(position + new int2(x, z), out Data.Chunk chunk) || !chunk.LightReady)
                    return false;
            }

            return true;
        }

        /// <summary>
        ///     Determines if a partition should be scheduled for meshing.
        /// </summary>
        /// <param name="position">The partition position to check.</param>
        /// <returns>True if partition needs meshing and is not already queued.</returns>
        private bool ShouldScheduleForMeshing(int3 position){
            return ChunkManager.IsChunkLoaded(position.xz) &&
                   (!ChunkPool.IsPartitionActive(position) || ChunkManager.ShouldReMesh(position)) &&
                   !Set.Contains(position);
        }

        /// <summary>
        ///     Checks if a partition is still within relevant range and priority threshold of the focus.
        /// </summary>
        /// <param name="position">The partition position to check.</param>
        /// <param name="focus">The current focus position.</param>
        /// <param name="prioThreshold">The priority threshold for relevance.</param>
        /// <returns>True if partition is still relevant.</returns>
        private bool IsPartitionStillRelevant(int3 position, PriorityUtil.Focus focus, float prioThreshold){
            return IsInDrawWindow(position, focus) &&
                   PriorityUtil.EvictionPriority(ref position, ref focus) > prioThreshold;
        }

        /// <summary>Whether a partition lies within the draw distance of the focus on every axis.</summary>
        private bool IsInDrawWindow(int3 position, PriorityUtil.Focus focus){
            int drawDistance = Settings.Chunk.DrawDistance;
            return math.abs(position.x - focus.Pos.x) <= drawDistance &&
                   math.abs(position.z - focus.Pos.z) <= drawDistance &&
                   math.abs(position.y - focus.Pos.y) <= drawDistance;
        }
    }
}
