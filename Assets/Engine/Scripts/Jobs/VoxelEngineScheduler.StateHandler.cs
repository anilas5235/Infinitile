using System;
using System.Collections.Generic;
using System.Linq;
using Engine.Scripts.Components;
using Engine.Scripts.Jobs.Chunk;
using Engine.Scripts.Jobs.ColliderBake;
using Engine.Scripts.Jobs.Meshing;
using Engine.Scripts.Settings;
using Engine.Scripts.ThirdParty.Priority_Queue;
using Engine.Scripts.Utils;
using Unity.Mathematics;
using static Engine.Scripts.Jobs.PriorityUtil;

namespace Engine.Scripts.Jobs
{
    internal abstract class JobStateHandler<T> where T : struct, IEquatable<T>
    {
        protected readonly ChunkManager ChunkManager;
        protected readonly ChunkPool ChunkPool;

        protected readonly SimpleFastPriorityQueue<T, int> Queue = new();
        protected readonly HashSet<T> Set = new();
        protected readonly VoxelEngineSettings Settings;

        protected JobStateHandler(VoxelEngineSettings settings, ChunkManager chunkManager, ChunkPool chunkPool)
        {
            Settings = settings;
            ChunkManager = chunkManager;
            ChunkPool = chunkPool;
        }

        public SchedulerStep CurrentStep { get; private set; } = SchedulerStep.UpdateQueues;

        public int QueueCount => Queue.Count;

        public bool Sleeping { get; private set; }

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
                    if (JobUpdateStep(focus))
                        CurrentStep = Set.Count > 0 ? SchedulerStep.CollectResults : SchedulerStep.UpdateQueues;
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

        protected virtual bool EnqueueStep(int3 focus)
        {
            return true;
        }

        protected virtual bool JobUpdateStep(int3 focus)
        {
            return true;
        }

        protected virtual bool CollectResultsStep()
        {
            return true;
        }
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
                    Queue.Enqueue(pos, DistPriority(ref pos, ref focus));
            }

            return Queue.Count > 0;
        }

        protected override bool JobUpdateStep(int3 focus)
        {
            if (!_chunkScheduler.IsReady) return false;

            int count = math.min(Settings.Scheduler.chunkGenBatchSize, Queue.Count);
            int accepted = 0;

            while (accepted < count && Queue.Count > 0)
            {
                int2 pos = Queue.Dequeue();
                if (!IsChunkStillRelevant(pos, focus)) continue;
                if (ShouldScheduleForGenerating(pos))
                {
                    Set.Add(pos);
                    accepted++;
                }
            }

            if (accepted == 0) return true;

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

        private bool ShouldScheduleForGenerating(int2 position)
        {
            return !ChunkManager.IsChunkLoaded(position) && !Set.Contains(position);
        }

        private bool IsChunkStillRelevant(int2 position, int3 focus)
        {
            int loadDistance = Settings.Chunk.LoadDistance;
            return math.abs(position.x - focus.x) <= loadDistance &&
                   math.abs(position.y - focus.z) <= loadDistance;
        }
    }

    internal class MeshJobStateHandler : JobStateHandler<int3>
    {
        private readonly ColliderJobStateHandler _colliderJobHandler;
        private readonly MeshBuildScheduler _meshBuildScheduler;

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
            if (!_meshBuildScheduler.IsReady) return false;

            int count = math.min(Settings.Scheduler.meshingBatchSize, Queue.Count);
            int accepted = 0;

            while (accepted < count && Queue.Count > 0)
            {
                int3 chunk = Queue.Dequeue();
                if (!IsPartitionStillRelevant(chunk, focus)) continue;
                if (CanGenerateMeshForChunk(chunk) && ShouldScheduleForMeshing(chunk))
                {
                    Set.Add(chunk);
                    accepted++;
                }
            }

            if (accepted == 0) return true;

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

        private bool ShouldScheduleForMeshing(int3 position)
        {
            return ChunkManager.IsChunkLoaded(position.xz) &&
                   (!ChunkPool.IsPartitionActive(position) || ChunkManager.ShouldReMesh(position)) &&
                   !Set.Contains(position);
        }

        private bool IsPartitionStillRelevant(int3 position, int3 focus)
        {
            int drawDistance = Settings.Chunk.DrawDistance;
            return math.abs(position.x - focus.x) <= drawDistance &&
                   math.abs(position.z - focus.z) <= drawDistance;
        }
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
                    Queue.Enqueue(pos, DistPriority(ref pos, ref focus));
            }

            return Queue.Count > 0;
        }

        protected override bool JobUpdateStep(int3 focus)
        {
            if (!_colliderBakeScheduler.IsReady) return false;

            int count = math.min(Settings.Scheduler.colliderBatchSize, Queue.Count);
            int accepted = 0;

            while (accepted < count && Queue.Count > 0)
            {
                int3 pos = Queue.Dequeue();
                if (!IsPartitionStillRelevant(pos, focus)) continue;
                if (CanBakeColliderForChunk(pos) && ShouldScheduleForBaking(pos))
                {
                    Set.Add(pos);
                    accepted++;
                }
            }

            if (accepted == 0) return true;

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

        private bool ShouldScheduleForBaking(int3 position)
        {
            return ChunkManager.IsChunkLoaded(position.xz) &&
                   (!ChunkPool.IsCollidable(position) || ChunkManager.ShouldReCollide(position)) &&
                   !Set.Contains(position);
        }

        private bool CanBakeColliderForChunk(int3 position)
        {
            return ChunkPool.IsPartitionActive(position);
        }

        private bool IsPartitionStillRelevant(int3 position, int3 focus)
        {
            int updateDistance = Settings.Chunk.UpdateDistance;
            return math.abs(position.x - focus.x) <= updateDistance &&
                   math.abs(position.z - focus.z) <= updateDistance;
        }
    }

    internal enum SchedulerStep
    {
        UpdateQueues,
        JobUpdate,
        CollectResults
    }
}