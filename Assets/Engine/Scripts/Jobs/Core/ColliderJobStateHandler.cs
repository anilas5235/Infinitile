using System;
using Engine.Scripts.Jobs.ColliderBake;
using Engine.Scripts.Settings;
using Engine.Scripts.Utils;
using Engine.Scripts.World;
using Unity.Mathematics;

namespace Engine.Scripts.Jobs.Core{
    /// <summary>
    ///     Job state handler for managing collider baking jobs for partitions.
    ///     Handles enqueuing, dispatching, and result collection for collider generation.
    /// </summary>
    internal class ColliderJobStateHandler : JobStateHandler<int3>{
        private readonly ColliderBakeScheduler _colliderBakeScheduler;
        private readonly Func<PriorityUtil.Focus, int3, bool> _outsideWindow; // created once: FocusUpdate must not allocate

        /// <summary>
        ///     Initializes a new instance of the ColliderJobStateHandler class.
        /// </summary>
        /// <param name="settings">The voxel engine settings.</param>
        /// <param name="chunkManager">The chunk manager reference.</param>
        /// <param name="chunkPool">The chunk pool reference.</param>
        /// <param name="colliderBakeScheduler">The collider bake scheduler for dispatching collider jobs.</param>
        public ColliderJobStateHandler(VoxelEngineSettings settings, ChunkManager chunkManager, ChunkPool chunkPool,
            ColliderBakeScheduler colliderBakeScheduler)
            : base(settings, chunkManager, chunkPool){
            _colliderBakeScheduler = colliderBakeScheduler;
            _outsideWindow = (focus, pos) => !IsPartitionStillRelevant(pos, focus);
            int side = 2 * settings.Chunk.UpdateDistance + 1;
            Reserve(side * side * VoxelConstants.PartitionsPerChunk, settings.Scheduler.colliderBatchSize);
        }

        /// <summary>
        ///     Updates focus: drops the queued partitions that left the update distance and reprioritizes the rest
        ///     based on distance from focus.
        /// </summary>
        /// <param name="focus">The new focus position.</param>
        public override void FocusUpdate(PriorityUtil.Focus focus){
            WakeUp();
            PruneQueue(focus, _outsideWindow);
            // Static lambda with explicit state instead of a closure capturing the focus
            Queue.UpdateAllPriorities(focus, static (f, pos) => PriorityUtil.DistPriority(ref pos, ref f));
        }

        /// <summary>The scan visits the update distance.</summary>
        protected override int ScanDistance => Settings.Chunk.UpdateDistance;

        /// <summary>
        ///     Enqueues the partitions of one row within update distance that need collider baking.
        /// </summary>
        /// <param name="focus">The current focus position.</param>
        /// <param name="x">The row, in chunks from the focus.</param>
        protected override void EnqueueRow(PriorityUtil.Focus focus, int x){
            int update = Settings.Chunk.UpdateDistance;

            for (int z = -update; z <= update; z++){
                int2 column = focus.Pos.xz + new int2(x, z);
                // Only meshed (active) partitions: the layers outside the vertical draw window are never meshed, and
                // queueing them would keep the handler awake re-queueing what JobUpdateStep drops. Meshing wakes the
                // handler. A column whose active partitions are all collidable and hold no rebuild flag has nothing
                // to queue: one test for the column instead of one per partition (DEC-232).
                uint active = ChunkPool.ActivePartitionMask(column);
                if (active == 0) continue;
                if ((active & ~ChunkPool.CollidablePartitionMask(column)) == 0 && !ChunkManager.ColumnHasReCollide(column))
                    continue;

                for (int y = 0; y < VoxelConstants.PartitionsPerChunk; y++){
                    if ((active & (1u << y)) == 0) continue; // CanBakeColliderForChunk
                    int3 pos = new(column.x, y, column.y);
                    if (!Queue.Contains(pos) && ShouldScheduleForBaking(pos))
                        Queue.Enqueue(pos, PriorityUtil.DistPriority(ref pos, ref focus));
                }
            }
        }

        /// <summary>
        ///     Dispatches a batch of partitions to the collider bake scheduler. One item of work.
        /// </summary>
        /// <param name="focus">The current focus position.</param>
        /// <param name="budget">The frame's budget.</param>
        /// <returns>None while the scheduler is busy, Cut when the budget is used up, otherwise Done.</returns>
        protected override StageTurn JobUpdateStep(PriorityUtil.Focus focus, FrameBudget budget){
            if (!_colliderBakeScheduler.IsReady) return StageTurn.None;
            if (!budget.Next()) return StageTurn.Cut;

            int count = math.min(BatchSize, Queue.Count); // the size from the settings, read once and at least 1
            int accepted = 0;

            while (accepted < count && Queue.Count > 0){
                int3 pos = Queue.Dequeue();
                if (!IsPartitionStillRelevant(pos, focus)) continue;
                if (!CanBakeColliderForChunk(pos) || !ShouldScheduleForBaking(pos)) continue;
                Set.Add(pos);
                accepted++;
            }

            if (accepted == 0) return StageTurn.Done;

            _colliderBakeScheduler.Start(Set);

            return StageTurn.Done;
        }

        /// <summary>
        ///     Collects results from completed collider baking. The batch is applied as one item of work, at the
        ///     start of the turn.
        /// </summary>
        /// <param name="budget">The frame's budget.</param>
        /// <returns>None while the batch runs, Cut when the budget is used up, otherwise Done.</returns>
        protected override StageTurn CollectResultsStep(FrameBudget budget){
            // A batch that was already applied elsewhere only clears the set; waiting for it would stall the handler.
            if (!_colliderBakeScheduler.IsReady){
                if (!_colliderBakeScheduler.IsComplete) return StageTurn.None;
                if (!budget.Next()) return StageTurn.Cut;

                _colliderBakeScheduler.Complete();
            }

            Set.Clear();
            return StageTurn.Done;
        }

        /// <summary>
        ///     Determines if a partition should be scheduled for collider baking.
        /// </summary>
        /// <param name="position">The partition position to check.</param>
        /// <returns>True if partition needs collider baking and is not already queued.</returns>
        private bool ShouldScheduleForBaking(int3 position){
            return ChunkManager.IsChunkLoaded(position.xz) &&
                   (!ChunkPool.IsCollidable(position) || ChunkManager.ShouldReCollide(position)) &&
                   !Set.Contains(position);
        }

        /// <summary>
        ///     Checks if a partition's parent chunk is active (has mesh generated).
        ///     Required before collider baking can proceed.
        /// </summary>
        /// <param name="position">The partition position to check.</param>
        /// <returns>True if the partition's chunk is active.</returns>
        private bool CanBakeColliderForChunk(int3 position){
            return ChunkPool.IsPartitionActive(position);
        }

        /// <summary>
        ///     Checks if a partition is still within relevant range of the focus.
        /// </summary>
        /// <param name="position">The partition position to check.</param>
        /// <param name="focus">The current focus position.</param>
        /// <returns>True if partition is within update distance.</returns>
        private bool IsPartitionStillRelevant(int3 position, PriorityUtil.Focus focus){
            int updateDistance = Settings.Chunk.UpdateDistance;
            return math.abs(position.x - focus.Pos.x) <= updateDistance &&
                   math.abs(position.z - focus.Pos.z) <= updateDistance;
        }
    }
}
