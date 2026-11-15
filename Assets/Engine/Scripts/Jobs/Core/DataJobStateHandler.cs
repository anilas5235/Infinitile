using System;
using Engine.Scripts.Jobs.Chunk;
using Engine.Scripts.Settings;
using Engine.Scripts.World;
using Unity.Mathematics;

namespace Engine.Scripts.Jobs.Core{
    /// <summary>
    ///     Job state handler for managing chunk data generation jobs.
    ///     Handles enqueuing, dispatching, and result collection for chunk data generation.
    /// </summary>
    internal class DataJobStateHandler : JobStateHandler<int2>{
        private readonly ChunkScheduler _chunkScheduler;
        private readonly LightJobStateHandler _lightJobHandler;
        private readonly Func<PriorityUtil.Focus, int2, bool> _outsideWindow; // created once: FocusUpdate must not allocate

        /// <summary>
        ///     Initializes a new instance of the DataJobStateHandler class.
        /// </summary>
        /// <param name="settings">The voxel engine settings.</param>
        /// <param name="chunkManager">The chunk manager reference.</param>
        /// <param name="chunkPool">The chunk pool reference.</param>
        /// <param name="chunkScheduler">The chunk scheduler for dispatching data generation jobs.</param>
        /// <param name="lightJobHandler">The light job handler to wake up when data generation completes.</param>
        public DataJobStateHandler(VoxelEngineSettings settings, ChunkManager chunkManager, ChunkPool chunkPool,
            ChunkScheduler chunkScheduler, LightJobStateHandler lightJobHandler) :
            base(settings, chunkManager, chunkPool){
            _chunkScheduler = chunkScheduler;
            _lightJobHandler = lightJobHandler;
            _outsideWindow = (focus, pos) => !IsChunkStillRelevant(pos, focus);
            int side = 2 * settings.Chunk.LoadDistance + 1;
            Reserve(side * side, settings.Scheduler.chunkGenBatchSize);
        }

        /// <summary>
        ///     Updates focus: drops the queued chunks that left the load distance and reprioritizes the rest based
        ///     on distance from focus.
        /// </summary>
        /// <param name="focus">The new focus position.</param>
        public override void FocusUpdate(PriorityUtil.Focus focus){
            WakeUp();
            PruneQueue(focus, _outsideWindow);
            // Static lambda with explicit state instead of a closure capturing the focus
            Queue.UpdateAllPriorities(focus, static (f, pos) => PriorityUtil.DistPriority(ref pos, ref f));
        }

        /// <summary>The scan visits the load distance.</summary>
        protected override int ScanDistance => Settings.Chunk.LoadDistance;

        /// <summary>
        ///     Enqueues the chunks of one row within load distance that need data generation.
        /// </summary>
        /// <param name="focus">The current focus position.</param>
        /// <param name="x">The row, in chunks from the focus.</param>
        protected override void EnqueueRow(PriorityUtil.Focus focus, int x){
            int load = Settings.Chunk.LoadDistance;
            for (int z = -load; z <= load; z++){
                int2 pos = focus.Pos.xz + new int2(x, z);
                if (Queue.Contains(pos) || !ShouldScheduleForGenerating(pos)) continue;

                Queue.Enqueue(pos, PriorityUtil.DistPriority(ref pos, ref focus));
            }
        }

        /// <summary>
        ///     Dispatches a batch of chunks to the chunk scheduler for data generation. One item of work.
        /// </summary>
        /// <param name="focus">The current focus position.</param>
        /// <param name="budget">The frame's budget.</param>
        /// <returns>None while the scheduler is busy, Cut when the budget is used up, otherwise Done.</returns>
        protected override StageTurn JobUpdateStep(PriorityUtil.Focus focus, FrameBudget budget){
            if (!_chunkScheduler.IsReady) return StageTurn.None;
            if (!budget.Next()) return StageTurn.Cut;

            int count = math.min(BatchSize, Queue.Count); // the size from the settings, read once and at least 1
            int accepted = 0;

            while (accepted < count && Queue.Count > 0){
                int2 pos = Queue.Dequeue();
                if (!IsChunkStillRelevant(pos, focus)) continue;
                if (!ShouldScheduleForGenerating(pos)) continue;
                Set.Add(pos);
                accepted++;
            }

            if (accepted == 0) return StageTurn.Done;

            _chunkScheduler.Start(Set);

            return StageTurn.Done;
        }

        /// <summary>
        ///     Collects results from completed chunk data generation and wakes up the light handler. The upload of
        ///     every chunk is an item of work (see <see cref="ChunkScheduler.Complete" />).
        /// </summary>
        /// <param name="budget">The frame's budget.</param>
        /// <returns>None while the batch runs, Cut when the budget ran out inside the collect, otherwise Done.</returns>
        protected override StageTurn CollectResultsStep(FrameBudget budget){
            // A batch that was already applied elsewhere only clears the set; waiting for it would stall the handler.
            if (!_chunkScheduler.IsReady){
                if (!_chunkScheduler.IsComplete) return StageTurn.None;
                if (!_chunkScheduler.Complete(budget)) return StageTurn.Cut;
            }

            Set.Clear();
            _lightJobHandler.WakeUp();
            return StageTurn.Done;
        }

        /// <summary>
        ///     Determines if a chunk should be scheduled for data generation.
        /// </summary>
        /// <param name="position">The chunk position to check.</param>
        /// <returns>True if chunk is not loaded and not already queued.</returns>
        private bool ShouldScheduleForGenerating(int2 position){
            return !ChunkManager.IsChunkLoaded(position) && !Set.Contains(position);
        }

        /// <summary>
        ///     Checks if a chunk is still within relevant range of the focus.
        /// </summary>
        /// <param name="position">The chunk position to check.</param>
        /// <param name="focus">The current focus position.</param>
        /// <returns>True if chunk is within load distance.</returns>
        private bool IsChunkStillRelevant(int2 position, PriorityUtil.Focus focus){
            int loadDistance = Settings.Chunk.LoadDistance;
            return math.abs(position.x - focus.Pos.x) <= loadDistance &&
                   math.abs(position.y - focus.Pos.z) <= loadDistance;
        }
    }
}
