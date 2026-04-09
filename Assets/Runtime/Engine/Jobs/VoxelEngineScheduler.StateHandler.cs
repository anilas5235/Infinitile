using System;
using System.Collections.Generic;
using System.Linq;
using Runtime.Engine.Components;
using Runtime.Engine.Jobs.Chunk;
using Runtime.Engine.Jobs.ColliderBake;
using Runtime.Engine.Jobs.Meshing;
using Runtime.Engine.Settings;
using Runtime.Engine.ThirdParty.Priority_Queue;
using Runtime.Engine.Utils;
using Unity.Mathematics;
using static Runtime.Engine.Jobs.PriorityUtil;

namespace Runtime.Engine.Jobs
{
    internal abstract class JobStateHandler<T> where T : struct, IEquatable<T>
    {
        protected readonly VoxelEngineSettings Settings;
        protected readonly ChunkManager ChunkManager;
        protected readonly ChunkPool ChunkPool;
        public SchedulerStep CurrentStep { get; private set; } = SchedulerStep.UpdateQueues;

        protected readonly SimpleFastPriorityQueue<T, int> Queue = new();
        protected readonly HashSet<T> Set = new();

        public int QueueCount => Queue.Count;

        public bool Sleeping { get; private set; }

        protected JobStateHandler(VoxelEngineSettings settings, ChunkManager chunkManager, ChunkPool chunkPool)
        {
            Settings = settings;
            ChunkManager = chunkManager;
            ChunkPool = chunkPool;
        }

        public void Update(int3 focus)
        {
            if (Sleeping) return;
            switch (CurrentStep)
            {
                case SchedulerStep.UpdateQueues:
                    if (EnqueueStep(focus)) CurrentStep = SchedulerStep.JobUpdate;
                    else Sleeping = true;
                    break;
                case SchedulerStep.JobUpdate:
                    if (JobUpdateStep(focus)) CurrentStep = SchedulerStep.CollectResults;
                    break;
                case SchedulerStep.CollectResults:
                    if (CollectResultsStep()) CurrentStep = SchedulerStep.UpdateQueues;
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        public abstract void FocusUpdate(int3 focus);

        public void WakeUp()
        {
            Sleeping = false;
        }

        protected virtual bool EnqueueStep(int3 focus) => true;

        protected virtual bool JobUpdateStep(int3 focus) => true;

        protected virtual bool CollectResultsStep() => true;
    }

    internal class DataJobStateHandler : JobStateHandler<int2>
    {
        private readonly ChunkScheduler _chunkScheduler;
        private readonly MeshJobStateHandler _meshJobHandler;

        public DataJobStateHandler(VoxelEngineSettings settings, ChunkManager chunkManager, ChunkPool chunkPool,
            ChunkScheduler chunkScheduler, MeshJobStateHandler meshJobHandler) :
            base(settings, chunkManager, chunkPool)
        {
            _chunkScheduler = chunkScheduler;
            _meshJobHandler = meshJobHandler;
        }

        public override void FocusUpdate(int3 focus)
        {
            WakeUp();
            Queue.UpdateAllPriorities(pos => DistPriority(ref pos, ref focus));
        }

        protected override bool EnqueueStep(int3 focus)
        {
            int load = Settings.Chunk.LoadDistance;
            for (int x = -load; x <= load; x++)
            for (int z = -load; z <= load; z++)
            {
                int2 pos = focus.xz + new int2(x, z);
                if (!Queue.Contains(pos) && ShouldScheduleForGenerating(pos))
                {
                    Queue.Enqueue(pos, DistPriority(ref pos, ref focus));
                }
            }

            return Queue.Count > 0;
        }

        protected override bool JobUpdateStep(int3 focus)
        {
            if (!_chunkScheduler.IsReady) return true;

            int count = math.min(Settings.Scheduler.StreamingBatchSize, Queue.Count);

            for (int i = 0; i < count; i++) Set.Add(Queue.Dequeue());
            _chunkScheduler.Start(Set.ToList());

            return true;
        }

        protected override bool CollectResultsStep()
        {
            if (!_chunkScheduler.IsComplete || _chunkScheduler.IsReady) return false;

            _chunkScheduler.Complete();
            Set.Clear();
            _meshJobHandler.WakeUp();
            return true;
        }

        private bool ShouldScheduleForGenerating(int2 position) =>
            !ChunkManager.IsChunkLoaded(position) && !Set.Contains(position);
    }

    internal class MeshJobStateHandler : JobStateHandler<int3>
    {
        private readonly MeshBuildScheduler _meshBuildScheduler;
        private readonly ColliderJobStateHandler _colliderJobHandler;

        public MeshJobStateHandler(VoxelEngineSettings settings, ChunkManager chunkManager, ChunkPool chunkPool,
            MeshBuildScheduler meshBuildScheduler, ColliderJobStateHandler colliderJobHandler) :
            base(settings, chunkManager, chunkPool)
        {
            _meshBuildScheduler = meshBuildScheduler;
            _colliderJobHandler = colliderJobHandler;
        }

        public override void FocusUpdate(int3 focus)
        {
            WakeUp();
            Queue.UpdateAllPriorities(pos => DistPriority(ref pos, ref focus));
        }

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

                    if (-DistPriority(ref pos, ref focus) <= prioThreshold) continue;

                    Queue.Enqueue(pos, DistPriority(ref pos, ref focus));
                }
            }

            return Queue.Count > 0;
        }

        protected override bool JobUpdateStep(int3 focus)
        {
            if (!_meshBuildScheduler.IsReady) return true;

            int count = math.min(Settings.Scheduler.MeshingBatchSize, Queue.Count);

            for (int i = 0; i < count; i++)
            {
                int3 chunk = Queue.Dequeue();
                if (CanGenerateMeshForChunk(chunk)) Set.Add(chunk);
            }

            _meshBuildScheduler.Start(Set);

            return true;
        }

        protected override bool CollectResultsStep()
        {
            if (!_meshBuildScheduler.IsComplete || _meshBuildScheduler.IsReady) return false;

            _meshBuildScheduler.Complete();
            Set.Clear();
            _colliderJobHandler.WakeUp();
            return true;
        }

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

        private bool ShouldScheduleForMeshing(int3 position) =>
            (!ChunkPool.IsPartitionActive(position) || ChunkManager.ShouldReMesh(position)) &&
            !Set.Contains(position);
    }

    internal class ColliderJobStateHandler : JobStateHandler<int3>
    {
        private readonly ColliderBakeScheduler _colliderBakeScheduler;

        public ColliderJobStateHandler(VoxelEngineSettings settings, ChunkManager chunkManager, ChunkPool chunkPool,
            ColliderBakeScheduler colliderBakeScheduler)
            : base(settings, chunkManager, chunkPool)
        {
            _colliderBakeScheduler = colliderBakeScheduler;
        }

        public override void FocusUpdate(int3 focus)
        {
            WakeUp();
            Queue.UpdateAllPriorities(pos => DistPriority(ref pos, ref focus));
        }

        protected override bool EnqueueStep(int3 focus)
        {
            int update = Settings.Chunk.UpdateDistance;

            for (int x = -update; x <= update; x++)
            for (int z = -update; z <= update; z++)
            for (int y = 0; y < VoxelConstants.PartitionsPerChunk; y++)
            {
                int3 pos = new(x + focus.x, y, z + focus.z);
                if (!Queue.Contains(pos) && ShouldScheduleForBaking(pos))
                {
                    Queue.Enqueue(pos, DistPriority(ref pos, ref focus));
                }
            }

            return Queue.Count > 0;
        }

        protected override bool JobUpdateStep(int3 focus)
        {
            int count = math.min(Settings.Scheduler.ColliderBatchSize, Queue.Count);

            for (int i = 0; i < count; i++)
            {
                int3 pos = Queue.Dequeue();
                if (CanBakeColliderForChunk(pos)) Set.Add(pos);
            }

            _colliderBakeScheduler.Start(Set.ToList());

            return true;
        }

        protected override bool CollectResultsStep()
        {
            if (!_colliderBakeScheduler.IsComplete || _colliderBakeScheduler.IsReady) return false;

            _colliderBakeScheduler.Complete();
            Set.Clear();
            return true;
        }

        private bool ShouldScheduleForBaking(int3 position) =>
            (!ChunkPool.IsCollidable(position) || ChunkManager.ShouldReCollide(position)) &&
            !Set.Contains(position);

        private bool CanBakeColliderForChunk(int3 position) => ChunkPool.IsPartitionActive(position);
    }

    internal enum SchedulerStep
    {
        UpdateQueues,
        JobUpdate,
        CollectResults,
    }
}