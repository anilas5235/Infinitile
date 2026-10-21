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
        private readonly CMeshBuildScheduler _cMeshBuildScheduler;
        private readonly ColliderJobStateHandler _colliderJobHandler;

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
            int side = 2 * settings.Chunk.DrawDistance + 1;
            int layers = math.min(VoxelConstants.PartitionsPerChunk, side);
            Reserve(side * side * layers, settings.Scheduler.meshingBatchSize);
        }

        /// <summary>
        ///     Updates focus and reprioritizes all queued partitions based on distance from focus.
        /// </summary>
        /// <param name="focus">The new focus position.</param>
        public override void FocusUpdate(PriorityUtil.Focus focus){
            WakeUp();
            // Static lambda with explicit state instead of a closure capturing the focus
            Queue.UpdateAllPriorities(focus, static (f, pos) => PriorityUtil.DistPriority(ref pos, ref f));
        }

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
        ///     Dispatches a batch of partitions to the mesh build scheduler.
        /// </summary>
        /// <param name="focus">The current focus position.</param>
        /// <returns>True if jobs were dispatched or batch is full, false if scheduler is busy.</returns>
        protected override bool JobUpdateStep(PriorityUtil.Focus focus){
            if (!_cMeshBuildScheduler.IsReady) return false;

            int count = math.min(Settings.Scheduler.meshingBatchSize, Queue.Count);
            float prioThreshold = ChunkPool.GetPartitionPrioThreshold();
            int accepted = 0;

            while (accepted < count && Queue.Count > 0){
                int3 partition = Queue.Dequeue();
                if (!IsPartitionStillRelevant(partition, focus, prioThreshold)) continue;
                if (!CanGenerateMeshForChunk(partition.xz)) continue;
                if (!ShouldScheduleForMeshing(partition)) continue;

                Set.Add(partition);
                accepted++;
            }

            if (accepted == 0) return true;

            _cMeshBuildScheduler.Start(Set);

            return true;
        }

        /// <summary>
        ///     Collects results from completed mesh building and wakes up the collider handler.
        /// </summary>
        /// <returns>True if collection completed, false if scheduler is still working.</returns>
        protected override bool CollectResultsStep(){
            if (!_cMeshBuildScheduler.IsComplete || _cMeshBuildScheduler.IsReady) return false;

            _cMeshBuildScheduler.Complete();
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
            int drawDistance = Settings.Chunk.DrawDistance;
            return math.abs(position.x - focus.Pos.x) <= drawDistance &&
                   math.abs(position.z - focus.Pos.z) <= drawDistance &&
                   math.abs(position.y - focus.Pos.y) <= drawDistance &&
                   PriorityUtil.EvictionPriority(ref position, ref focus) > prioThreshold;
        }
    }
}
