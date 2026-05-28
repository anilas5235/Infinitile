using System.Linq;
using Engine.Scripts.Components;
using Engine.Scripts.Jobs.ColliderBake;
using Engine.Scripts.Settings;
using Engine.Scripts.Utils;
using Unity.Mathematics;

namespace Engine.Scripts.Jobs.Core
{
    /// <summary>
    /// Job state handler for managing collider baking jobs for partitions.
    /// Handles enqueuing, dispatching, and result collection for collider generation.
    /// </summary>
    internal class ColliderJobStateHandler : JobStateHandler<int3>
    {
        private readonly ColliderBakeScheduler _colliderBakeScheduler;

        /// <summary>
        /// Initializes a new instance of the ColliderJobStateHandler class.
        /// </summary>
        /// <param name="settings">The voxel engine settings.</param>
        /// <param name="chunkManager">The chunk manager reference.</param>
        /// <param name="chunkPool">The chunk pool reference.</param>
        /// <param name="colliderBakeScheduler">The collider bake scheduler for dispatching collider jobs.</param>
        public ColliderJobStateHandler(VoxelEngineSettings settings, ChunkManager chunkManager, ChunkPool chunkPool,
            ColliderBakeScheduler colliderBakeScheduler)
            : base(settings, chunkManager, chunkPool)
        {
            _colliderBakeScheduler = colliderBakeScheduler;
        }

        /// <summary>
        /// Updates focus and reprioritizes all queued partitions based on distance from focus.
        /// </summary>
        /// <param name="focus">The new focus position.</param>
        public override void FocusUpdate(int3 focus)
        {
            WakeUp();
            Queue.UpdateAllPriorities(pos => PriorityUtil.DistPriority(ref pos, ref focus));
        }

        /// <summary>
        /// Enqueues partitions within update distance that need collider baking.
        /// </summary>
        /// <param name="focus">The current focus position.</param>
        /// <returns>True if any partitions were enqueued, false if queue is empty.</returns>
        protected override bool EnqueueStep(int3 focus)
        {
            int update = Settings.Chunk.UpdateDistance;

            for (int x = -update; x <= update; x++)
            for (int z = -update; z <= update; z++)
            for (int y = 0; y < VoxelConstants.PartitionsPerChunk; y++)
            {
                int3 pos = new(x + focus.x, y, z + focus.z);
                if (!Queue.Contains(pos) && ShouldScheduleForBaking(pos))
                    Queue.Enqueue(pos, PriorityUtil.DistPriority(ref pos, ref focus));
            }

            return Queue.Count > 0;
        }

        /// <summary>
        /// Dispatches a batch of partitions to the collider bake scheduler.
        /// </summary>
        /// <param name="focus">The current focus position.</param>
        /// <returns>True if jobs were dispatched or batch is full, false if scheduler is busy.</returns>
        protected override bool JobUpdateStep(int3 focus)
        {
            if (!_colliderBakeScheduler.IsReady) return false;

            int count = math.min(Settings.Scheduler.colliderBatchSize, Queue.Count);
            int accepted = 0;

            while (accepted < count && Queue.Count > 0)
            {
                int3 pos = Queue.Dequeue();
                if (!IsPartitionStillRelevant(pos, focus)) continue;
                if (!CanBakeColliderForChunk(pos) || !ShouldScheduleForBaking(pos)) continue;
                Set.Add(pos);
                accepted++;
            }

            if (accepted == 0) return true;

            _colliderBakeScheduler.Start(Set.ToList());

            return true;
        }

        /// <summary>
        /// Collects results from completed collider baking.
        /// </summary>
        /// <returns>True if collection completed, false if scheduler is still working.</returns>
        protected override bool CollectResultsStep()
        {
            if (!_colliderBakeScheduler.IsComplete || _colliderBakeScheduler.IsReady) return false;

            _colliderBakeScheduler.Complete();
            Set.Clear();
            return true;
        }

        /// <summary>
        /// Determines if a partition should be scheduled for collider baking.
        /// </summary>
        /// <param name="position">The partition position to check.</param>
        /// <returns>True if partition needs collider baking and is not already queued.</returns>
        private bool ShouldScheduleForBaking(int3 position)
        {
            return ChunkManager.IsChunkLoaded(position.xz) &&
                   (!ChunkPool.IsCollidable(position) || ChunkManager.ShouldReCollide(position)) &&
                   !Set.Contains(position);
        }

        /// <summary>
        /// Checks if a partition's parent chunk is active (has mesh generated).
        /// Required before collider baking can proceed.
        /// </summary>
        /// <param name="position">The partition position to check.</param>
        /// <returns>True if the partition's chunk is active.</returns>
        private bool CanBakeColliderForChunk(int3 position)
        {
            return ChunkPool.IsPartitionActive(position);
        }

        /// <summary>
        /// Checks if a partition is still within relevant range of the focus.
        /// </summary>
        /// <param name="position">The partition position to check.</param>
        /// <param name="focus">The current focus position.</param>
        /// <returns>True if partition is within update distance.</returns>
        private bool IsPartitionStillRelevant(int3 position, int3 focus)
        {
            int updateDistance = Settings.Chunk.UpdateDistance;
            return math.abs(position.x - focus.x) <= updateDistance &&
                   math.abs(position.z - focus.z) <= updateDistance;
        }
    }
}