using Engine.Scripts.Components;
using Engine.Scripts.Jobs.Light;
using Engine.Scripts.Settings;
using Unity.Mathematics;

namespace Engine.Scripts.Jobs.Core{
    /// <summary>
    ///     Job state handler for the light step between data generation and meshing. Lights loaded chunks
    ///     whose 3 × 3 neighbourhood has voxel data, within <c>LoadDistance - 1</c> of the focus.
    /// </summary>
    internal class LightJobStateHandler : JobStateHandler<int2>{
        private readonly LightScheduler _lightScheduler;
        private readonly MeshJobStateHandler _meshJobHandler;

        public LightJobStateHandler(VoxelEngineSettings settings, ChunkManager chunkManager, ChunkPool chunkPool,
            LightScheduler lightScheduler, MeshJobStateHandler meshJobHandler) :
            base(settings, chunkManager, chunkPool){
            _lightScheduler = lightScheduler;
            _meshJobHandler = meshJobHandler;
            int side = 2 * LightDistance + 1;
            Reserve(side * side, settings.Scheduler.lightBatchSize);
        }

        /// <summary>The ring the light job can serve: each lit chunk needs its neighbours' voxel data.</summary>
        private int LightDistance => math.max(0, Settings.Chunk.LoadDistance - 1);

        public override void FocusUpdate(PriorityUtil.Focus focus){
            WakeUp();
            // Static lambda with explicit state instead of a closure capturing the focus
            Queue.UpdateAllPriorities(focus, static (f, pos) => PriorityUtil.DistPriority(ref pos, ref f));
        }

        protected override bool EnqueueStep(PriorityUtil.Focus focus){
            int distance = LightDistance;
            for (int x = -distance; x <= distance; x++)
            for (int z = -distance; z <= distance; z++){
                int2 pos = focus.Pos.xz + new int2(x, z);
                if (Queue.Contains(pos) || !ShouldScheduleForLighting(pos)) continue;

                Queue.Enqueue(pos, PriorityUtil.DistPriority(ref pos, ref focus));
            }

            return Queue.Count > 0;
        }

        protected override bool JobUpdateStep(PriorityUtil.Focus focus){
            if (!_lightScheduler.IsReady) return false;

            int count = math.min(Settings.Scheduler.lightBatchSize, Queue.Count);
            int accepted = 0;

            while (accepted < count && Queue.Count > 0){
                int2 pos = Queue.Dequeue();
                if (!IsChunkStillRelevant(pos, focus)) continue;
                if (!ShouldScheduleForLighting(pos)) continue;
                Set.Add(pos);
                accepted++;
            }

            if (accepted == 0) return true;

            _lightScheduler.Start(Set);
            return true;
        }

        /// <summary>
        ///     Applies the finished batch and wakes the mesh handler. A batch that was already completed and applied
        ///     early (an edit or unload of a chunk it read) only clears the set.
        /// </summary>
        protected override bool CollectResultsStep(){
            if (!_lightScheduler.IsReady){
                if (!_lightScheduler.IsComplete) return false;
                _lightScheduler.Complete();
            }

            Set.Clear();
            _meshJobHandler.WakeUp();
            return true;
        }

        private bool ShouldScheduleForLighting(int2 position){
            if (Set.Contains(position)) return false;
            if (!ChunkManager.TryGetChunk(position, out Data.Chunk chunk) || chunk.LightReady) return false;

            for (int x = -1; x <= 1; x++)
            for (int z = -1; z <= 1; z++)
                if (!ChunkManager.IsChunkLoaded(position + new int2(x, z)))
                    return false;

            return true;
        }

        private bool IsChunkStillRelevant(int2 position, PriorityUtil.Focus focus){
            int distance = LightDistance;
            return math.abs(position.x - focus.Pos.x) <= distance &&
                   math.abs(position.y - focus.Pos.z) <= distance;
        }
    }
}
