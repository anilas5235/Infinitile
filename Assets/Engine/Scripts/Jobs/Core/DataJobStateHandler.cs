using System.Linq;
using Engine.Scripts.Components;
using Engine.Scripts.Jobs.Chunk;
using Engine.Scripts.Settings;
using Unity.Mathematics;

namespace Engine.Scripts.Jobs.Core
{
    /// <summary>
    /// Job state handler for managing chunk data generation jobs.
    /// Handles enqueuing, dispatching, and result collection for chunk data generation.
    /// </summary>
    internal class DataJobStateHandler : JobStateHandler<int2>
    {
        private readonly ChunkScheduler _chunkScheduler;
        private readonly MeshJobStateHandler _meshJobHandler;

        /// <summary>
        /// Initializes a new instance of the DataJobStateHandler class.
        /// </summary>
        /// <param name="settings">The voxel engine settings.</param>
        /// <param name="chunkManager">The chunk manager reference.</param>
        /// <param name="chunkPool">The chunk pool reference.</param>
        /// <param name="chunkScheduler">The chunk scheduler for dispatching data generation jobs.</param>
        /// <param name="meshJobHandler">The mesh job handler to wake up when data generation completes.</param>
        public DataJobStateHandler(VoxelEngineSettings settings, ChunkManager chunkManager, ChunkPool chunkPool,
            ChunkScheduler chunkScheduler, MeshJobStateHandler meshJobHandler) :
            base(settings, chunkManager, chunkPool)
        {
            _chunkScheduler = chunkScheduler;
            _meshJobHandler = meshJobHandler;
        }

        /// <summary>
        /// Updates focus and reprioritizes all queued chunks based on distance from focus.
        /// </summary>
        /// <param name="focus">The new focus position.</param>
        public override void FocusUpdate(int3 focus)
        {
            WakeUp();
            Queue.UpdateAllPriorities(pos => PriorityUtil.DistPriority(ref pos, ref focus));
        }

        /// <summary>
        /// Enqueues chunks within load distance that need data generation.
        /// </summary>
        /// <param name="focus">The current focus position.</param>
        /// <returns>True if any chunks were enqueued, false if queue is empty.</returns>
        protected override bool EnqueueStep(int3 focus)
        {
            int load = Settings.Chunk.LoadDistance;
            for (int x = -load; x <= load; x++)
            for (int z = -load; z <= load; z++)
            {
                int2 pos = focus.xz + new int2(x, z);
                if (!Queue.Contains(pos) && ShouldScheduleForGenerating(pos))
                    Queue.Enqueue(pos, PriorityUtil.DistPriority(ref pos, ref focus));
            }

            return Queue.Count > 0;
        }

        /// <summary>
        /// Dispatches a batch of chunks to the chunk scheduler for data generation.
        /// </summary>
        /// <param name="focus">The current focus position.</param>
        /// <returns>True if jobs were dispatched or batch is full, false if scheduler is busy.</returns>
        protected override bool JobUpdateStep(int3 focus)
        {
            if (!_chunkScheduler.IsReady) return false;

            int count = math.min(Settings.Scheduler.chunkGenBatchSize, Queue.Count);
            int accepted = 0;

            while (accepted < count && Queue.Count > 0)
            {
                int2 pos = Queue.Dequeue();
                if (!IsChunkStillRelevant(pos, focus)) continue;
                if (!ShouldScheduleForGenerating(pos)) continue;
                Set.Add(pos);
                accepted++;
            }

            if (accepted == 0) return true;

            _chunkScheduler.Start(Set.ToList());

            return true;
        }

        /// <summary>
        /// Collects results from completed chunk data generation and wakes up the mesh handler.
        /// </summary>
        /// <returns>True if collection completed, false if scheduler is still working.</returns>
        protected override bool CollectResultsStep()
        {
            if (!_chunkScheduler.IsComplete || _chunkScheduler.IsReady) return false;

            _chunkScheduler.Complete();
            Set.Clear();
            _meshJobHandler.WakeUp();
            return true;
        }

        /// <summary>
        /// Determines if a chunk should be scheduled for data generation.
        /// </summary>
        /// <param name="position">The chunk position to check.</param>
        /// <returns>True if chunk is not loaded and not already queued.</returns>
        private bool ShouldScheduleForGenerating(int2 position)
        {
            return !ChunkManager.IsChunkLoaded(position) && !Set.Contains(position);
        }

        /// <summary>
        /// Checks if a chunk is still within relevant range of the focus.
        /// </summary>
        /// <param name="position">The chunk position to check.</param>
        /// <param name="focus">The current focus position.</param>
        /// <returns>True if chunk is within load distance.</returns>
        private bool IsChunkStillRelevant(int2 position, int3 focus)
        {
            int loadDistance = Settings.Chunk.LoadDistance;
            return math.abs(position.x - focus.x) <= loadDistance &&
                   math.abs(position.y - focus.z) <= loadDistance;
        }
    }
}