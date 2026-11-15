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

        // A dispatch the budget cut: its partitions are taken from the queue, the rest continues in the next frame
        private bool _dispatching;
        private bool _dispatchCut;
        private int _nextEmpty;

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

        /// <summary>The scan visits the draw distance.</summary>
        protected override int ScanDistance => Settings.Chunk.DrawDistance;

        /// <summary>
        ///     Enqueues the partitions of one row within draw distance that need mesh generation.
        ///     Only enqueues if the parent chunk has all neighboring chunks loaded.
        /// </summary>
        /// <param name="focus">The current focus position.</param>
        /// <param name="x">The row, in chunks from the focus.</param>
        protected override void EnqueueRow(PriorityUtil.Focus focus, int x){
            int draw = Settings.Chunk.DrawDistance;
            float prioThreshold = ChunkPool.GetPartitionPrioThreshold();

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
        }

        /// <summary>
        ///     Takes the next partitions from the queue. The ones that hold a voxel go to the mesh build scheduler
        ///     as a batch. The ones that hold only air need no mesh job, no GPU build and no collider bake: they
        ///     become active at once, with an empty mesh (see <see cref="CMeshBuildScheduler.ApplyEmpty" />). They
        ///     do not count against the batch, but a turn takes a limited number of them.
        ///     Items of work for the frame's budget: taking the partitions from the queue, every partition without
        ///     a voxel (its claim in the pool) and the start of the mesh job. A dispatch the budget cut keeps its
        ///     partitions and continues where it stopped; a partition without a voxel that left the draw window or
        ///     got a voxel in the meantime is skipped.
        /// </summary>
        /// <param name="focus">The current focus position.</param>
        /// <param name="budget">The frame's budget.</param>
        /// <returns>None while the scheduler is busy, Cut when the budget ran out inside, otherwise Done.</returns>
        protected override StageTurn JobUpdateStep(PriorityUtil.Focus focus, FrameBudget budget){
            if (!_dispatching){
                if (!_cMeshBuildScheduler.IsReady) return StageTurn.None;
                if (!budget.Next()) return StageTurn.Cut;

                float prioThreshold = ChunkPool.GetPartitionPrioThreshold();
                int emptyLimit = EmptyPartitionsPerBatch * BatchSize;

                using (VoxelProfilerMarkers.MeshBuildDequeue.Auto()){
                    while (Set.Count < BatchSize && _emptyPartitions.Count < emptyLimit && Queue.Count > 0){
                        int3 partition = Queue.Dequeue();
                        if (!IsPartitionStillRelevant(partition, focus, prioThreshold)) continue;
                        if (!CanGenerateMeshForChunk(partition.xz)) continue;
                        if (!ShouldScheduleForMeshing(partition)) continue;

                        if (ChunkManager.PartitionHasVoxels(partition)) Set.Add(partition);
                        else _emptyPartitions.Add(partition);
                    }
                }

                _dispatching = true;
                _dispatchCut = false;
                _nextEmpty = 0;
            }

            for (; _nextEmpty < _emptyPartitions.Count; _nextEmpty++){
                int3 partition = _emptyPartitions[_nextEmpty];
                // Frames passed since a cut dispatch took its partitions from the queue: the focus may have moved
                // on, and an edit may have placed a block in the partition. Such a partition is left to the next
                // scan, which finds it flagged (or inactive) and meshes it like any other; made active here, with
                // an empty mesh and its remesh flag consumed, the block would stay invisible and without collider.
                if (_dispatchCut &&
                    (!IsInDrawWindow(partition, focus) || ChunkManager.PartitionHasVoxels(partition))) continue;
                if (!budget.Next()) return CutDispatch();

                _cMeshBuildScheduler.ApplyEmpty(partition);
            }

            // A claim in a full pool can reclaim a pooled chunk, and the world then unloads its data: a partition
            // of this batch that lost a chunk of its neighbourhood is left to the next scan. The same holds for a
            // chunk that was unloaded in the frames a cut dispatch waited.
            if (Set.Count > 0 && (_emptyPartitions.Count > 0 || _dispatchCut)) Set.RemoveWhere(_lostItsChunks);

            if (Set.Count > 0){
                if (!budget.Next()) return CutDispatch();

                _cMeshBuildScheduler.Start(Set);
            }

            _emptyPartitions.Clear();
            _dispatching = false;
            return StageTurn.Done;
        }

        private StageTurn CutDispatch(){
            _dispatchCut = true;
            return StageTurn.Cut;
        }

        /// <summary>
        ///     Collects results from completed mesh building and wakes up the collider handler. Every claim in the
        ///     pool is an item of work (see <see cref="CMeshBuildScheduler.Complete" />).
        /// </summary>
        /// <param name="budget">The frame's budget.</param>
        /// <returns>None while the batch runs, Cut when the budget ran out inside the collect, otherwise Done.</returns>
        protected override StageTurn CollectResultsStep(FrameBudget budget){
            // A batch that was already applied elsewhere only clears the set; waiting for it would stall the handler.
            if (!_cMeshBuildScheduler.IsReady){
                if (!_cMeshBuildScheduler.IsComplete) return StageTurn.None;
                if (!_cMeshBuildScheduler.Complete(budget)) return StageTurn.Cut;
            }

            Set.Clear();
            _colliderJobHandler.WakeUp();
            return StageTurn.Done;
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
