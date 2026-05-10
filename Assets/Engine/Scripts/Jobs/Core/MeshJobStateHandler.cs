using Engine.Scripts.Components;
using Engine.Scripts.Jobs.Meshing;
using Engine.Scripts.Settings;
using Engine.Scripts.Utils;
using Unity.Mathematics;

namespace Engine.Scripts.Jobs.Core
{
    /// <summary>
    /// Job state handler for managing mesh building jobs for partitions.
    /// Handles enqueuing, dispatching, and result collection for mesh generation.
    /// </summary>
    internal class MeshJobStateHandler : JobStateHandler<int3>
    {
        private readonly ColliderJobStateHandler _colliderJobHandler;
        private readonly MeshBuildScheduler _meshBuildScheduler;

        /// <summary>
        /// Initializes a new instance of the MeshJobStateHandler class.
        /// </summary>
        /// <param name="settings">The voxel engine settings.</param>
        /// <param name="chunkManager">The chunk manager reference.</param>
        /// <param name="chunkPool">The chunk pool reference.</param>
        /// <param name="meshBuildScheduler">The mesh build scheduler for dispatching mesh building jobs.</param>
        /// <param name="colliderJobHandler">The collider job handler to wake up when meshing completes.</param>
        public MeshJobStateHandler(VoxelEngineSettings settings, ChunkManager chunkManager, ChunkPool chunkPool,
            MeshBuildScheduler meshBuildScheduler, ColliderJobStateHandler colliderJobHandler) :
            base(settings, chunkManager, chunkPool)
        {
            _meshBuildScheduler = meshBuildScheduler;
            _colliderJobHandler = colliderJobHandler;
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
        /// Enqueues partitions within draw distance that need mesh generation.
        /// Only enqueues if the parent chunk has all neighboring chunks loaded.
        /// </summary>
        /// <param name="focus">The current focus position.</param>
        /// <returns>True if any partitions were enqueued, false if queue is empty.</returns>
        protected override bool EnqueueStep(int3 focus)
        {
            int draw = Settings.Chunk.DrawDistance;
            int prioThreshold = ChunkPool.GetPartitionPrioThreshold();

            for (int x = -draw; x <= draw; x++)
            for (int z = -draw; z <= draw; z++)
            {
                if (!CanGenerateMeshForChunk(focus + new int3(x, 0, z))) continue;
                for (int y = 0; y < VoxelConstants.PartitionsPerChunk; y++)
                {
                    int3 pos = new(x + focus.x, y, z + focus.z);
                    if (Queue.Contains(pos) || !ShouldScheduleForMeshing(pos)) continue;

                    if (-PriorityUtil.DistPriority(ref pos, ref focus) <= prioThreshold) continue;

                    Queue.Enqueue(pos, PriorityUtil.DistPriority(ref pos, ref focus));
                }
            }

            return Queue.Count > 0;
        }

        /// <summary>
        /// Dispatches a batch of partitions to the mesh build scheduler.
        /// </summary>
        /// <param name="focus">The current focus position.</param>
        /// <returns>True if jobs were dispatched or batch is full, false if scheduler is busy.</returns>
        protected override bool JobUpdateStep(int3 focus)
        {
            if (!_meshBuildScheduler.IsReady) return false;

            int count = math.min(Settings.Scheduler.meshingBatchSize, Queue.Count);
            int prioThreshold = ChunkPool.GetPartitionPrioThreshold();
            int accepted = 0;

            while (accepted < count && Queue.Count > 0)
            {
                int3 chunk = Queue.Dequeue();
                if (!IsPartitionStillRelevant(chunk, focus, prioThreshold)) continue;
                if (!CanGenerateMeshForChunk(chunk) || !ShouldScheduleForMeshing(chunk)) continue;
                Set.Add(chunk);
                accepted++;
            }

            if (accepted == 0) return true;

            _meshBuildScheduler.Start(Set);

            return true;
        }

        /// <summary>
        /// Collects results from completed mesh building and wakes up the collider handler.
        /// </summary>
        /// <returns>True if collection completed, false if scheduler is still working.</returns>
        protected override bool CollectResultsStep()
        {
            if (!_meshBuildScheduler.IsComplete || _meshBuildScheduler.IsReady) return false;

            _meshBuildScheduler.Complete();
            Set.Clear();
            _colliderJobHandler.WakeUp();
            return true;
        }

        /// <summary>
        /// Checks if all neighboring chunks are loaded for a given chunk position.
        /// Required before mesh generation can proceed.
        /// </summary>
        /// <param name="position">The chunk position to check.</param>
        /// <returns>True if the chunk and all neighbors are loaded.</returns>
        private bool CanGenerateMeshForChunk(int3 position)
        {
            bool result = true;

            for (int x = -1; x <= 1; x++)
            for (int z = -1; z <= 1; z++)
            {
                int2 pos = position.xz + new int2(x, z);
                result &= ChunkManager.IsChunkLoaded(pos);
            }

            return result;
        }

        /// <summary>
        /// Determines if a partition should be scheduled for meshing.
        /// </summary>
        /// <param name="position">The partition position to check.</param>
        /// <returns>True if partition needs meshing and is not already queued.</returns>
        private bool ShouldScheduleForMeshing(int3 position)
        {
            return ChunkManager.IsChunkLoaded(position.xz) &&
                   (!ChunkPool.IsPartitionActive(position) || ChunkManager.ShouldReMesh(position)) &&
                   !Set.Contains(position);
        }

        /// <summary>
        /// Checks if a partition is still within relevant range and priority threshold of the focus.
        /// </summary>
        /// <param name="position">The partition position to check.</param>
        /// <param name="focus">The current focus position.</param>
        /// <param name="prioThreshold">The priority threshold for relevance.</param>
        /// <returns>True if partition is still relevant.</returns>
        private bool IsPartitionStillRelevant(int3 position, int3 focus, int prioThreshold)
        {
            int drawDistance = Settings.Chunk.DrawDistance;
            return math.abs(position.x - focus.x) <= drawDistance &&
                   math.abs(position.z - focus.z) <= drawDistance &&
                   -PriorityUtil.DistPriority(ref position, ref focus) > prioThreshold;
        }
    }
}