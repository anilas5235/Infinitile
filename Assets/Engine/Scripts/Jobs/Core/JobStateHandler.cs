using System;
using System.Collections.Generic;
using Engine.Scripts.Components;
using Engine.Scripts.Settings;
using Engine.Scripts.ThirdParty.Priority_Queue;
using Unity.Mathematics;

namespace Engine.Scripts.Jobs.Core
{
    /// <summary>
    /// Abstract base class for job state handlers that manage queues of work items with priority-based scheduling.
    /// Implements a three-step state machine: UpdateQueues -> JobUpdate -> CollectResults.
    /// </summary>
    /// <typeparam name="T">The type of items in the queue (must be a struct that implements IEquatable).</typeparam>
    internal abstract class JobStateHandler<T> where T : struct, IEquatable<T>
    {
        /// <summary>
        /// Reference to the chunk manager.
        /// </summary>
        protected readonly ChunkManager ChunkManager;
        
        /// <summary>
        /// Reference to the chunk pool.
        /// </summary>
        protected readonly ChunkPool ChunkPool;

        /// <summary>
        /// Priority queue for managing work items.
        /// </summary>
        protected readonly SimpleFastPriorityQueue<T, int> Queue = new();
        
        /// <summary>
        /// Set of items currently being processed.
        /// </summary>
        protected readonly HashSet<T> Set = new();
        
        /// <summary>
        /// The voxel engine settings.
        /// </summary>
        protected readonly VoxelEngineSettings Settings;

        /// <summary>
        /// Initializes a new instance of the JobStateHandler class.
        /// </summary>
        /// <param name="settings">The voxel engine settings.</param>
        /// <param name="chunkManager">The chunk manager reference.</param>
        /// <param name="chunkPool">The chunk pool reference.</param>
        protected JobStateHandler(VoxelEngineSettings settings, ChunkManager chunkManager, ChunkPool chunkPool)
        {
            Settings = settings;
            ChunkManager = chunkManager;
            ChunkPool = chunkPool;
        }

        /// <summary>
        /// Gets the current step of the scheduler state machine.
        /// </summary>
        public SchedulerStep CurrentStep { get; private set; } = SchedulerStep.UpdateQueues;

        /// <summary>
        /// Gets the number of items in the priority queue.
        /// </summary>
        public int QueueCount => Queue.Count;

        /// <summary>
        /// Gets a value indicating whether this handler is sleeping (no active work).
        /// </summary>
        public bool Sleeping { get; private set; }

        /// <summary>
        /// Updates the handler state machine with a new focus position.
        /// Processes one step of the three-step cycle based on current state.
        /// </summary>
        /// <param name="focus">The focus position (e.g., player chunk position).</param>
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

        /// <summary>
        /// Updates the focus position and reprioritizes all queued items.
        /// Must be implemented by derived classes.
        /// </summary>
        /// <param name="focus">The new focus position.</param>
        public abstract void FocusUpdate(int3 focus);

        /// <summary>
        /// Wakes up the handler from sleeping state so it resumes processing.
        /// </summary>
        public void WakeUp()
        {
            Sleeping = false;
        }

        /// <summary>
        /// Enqueues new work items based on the focus position.
        /// Called during the UpdateQueues step.
        /// </summary>
        /// <param name="focus">The current focus position.</param>
        /// <returns>True if items were enqueued, false if queue is empty.</returns>
        protected virtual bool EnqueueStep(int3 focus)
        {
            return true;
        }

        /// <summary>
        /// Updates jobs and dispatches batch work to the appropriate scheduler.
        /// Called during the JobUpdate step.
        /// </summary>
        /// <param name="focus">The current focus position.</param>
        /// <returns>True if processing completed, false if still working.</returns>
        protected virtual bool JobUpdateStep(int3 focus)
        {
            return true;
        }

        /// <summary>
        /// Collects results from completed job batches.
        /// Called during the CollectResults step.
        /// </summary>
        /// <returns>True if result collection completed, false if still working.</returns>
        protected virtual bool CollectResultsStep()
        {
            return true;
        }
    }
}