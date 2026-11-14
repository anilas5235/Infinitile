using System;
using System.Collections.Generic;
using Engine.Scripts.Settings;
using Engine.Scripts.ThirdParty.Priority_Queue;
using Engine.Scripts.World;
using Unity.Mathematics;

namespace Engine.Scripts.Jobs.Core{
    /// <summary>
    ///     Abstract base class for job state handlers that manage queues of work items with priority-based scheduling.
    ///     One <see cref="Update" /> is one turn of the stage: collect the finished batch, look for new work, start
    ///     the next batch. A stage whose batch still runs passes its turn on.
    /// </summary>
    internal abstract class JobStateHandler<T> where T : struct, IEquatable<T>{
        /// <summary>
        ///     Reference to the chunk manager.
        /// </summary>
        protected readonly ChunkManager ChunkManager;

        /// <summary>
        ///     Reference to the chunk pool.
        /// </summary>
        protected readonly ChunkPool ChunkPool;

        /// <summary>
        ///     Priority queue for managing work items.
        /// </summary>
        protected readonly SimpleFastPriorityQueue<T, float> Queue = new();

        /// <summary>
        ///     Set of items currently being processed.
        /// </summary>
        protected readonly HashSet<T> Set = new();

        /// <summary>
        ///     The voxel engine settings.
        /// </summary>
        protected readonly VoxelEngineSettings Settings;

        /// <summary>
        ///     Initializes a new instance of the JobStateHandler class.
        /// </summary>
        /// <param name="settings">The voxel engine settings.</param>
        /// <param name="chunkManager">The chunk manager reference.</param>
        /// <param name="chunkPool">The chunk pool reference.</param>
        protected JobStateHandler(VoxelEngineSettings settings, ChunkManager chunkManager, ChunkPool chunkPool){
            Settings = settings;
            ChunkManager = chunkManager;
            ChunkPool = chunkPool;
        }

        /// <summary>
        ///     Turns between two scans for new work while the queue still holds a full batch. A scan visits every
        ///     column of the stage's window, so it does not run on every turn; a queue shorter than one batch is
        ///     scanned at once, and the handler only goes to sleep after a scan that found nothing.
        /// </summary>
        private const int ScanInterval = 4;

        private int _batchSize = 1;
        private int _turnsSinceScan;

        /// <summary>
        ///     Pre-sizes the queue and the in-flight set so that filling them during flight does not allocate.
        /// </summary>
        protected void Reserve(int queueCapacity, int batchSize){
            _batchSize = math.max(1, batchSize);
            Queue.Reserve(queueCapacity);
            Set.EnsureCapacity(_batchSize);
        }

        /// <summary>
        ///     Gets the number of items in the priority queue.
        /// </summary>
        public int QueueCount => Queue.Count;

        /// <summary>
        ///     Gets a value indicating whether this handler is sleeping (no active work).
        /// </summary>
        public bool Sleeping{ get; private set; }

        /// <summary>
        ///     True while the stage may start a batch. A stage that has to wait for something outside the scheduler
        ///     (the mesh stage waits for the renderer) keeps its queue and neither scans nor dispatches meanwhile.
        /// </summary>
        protected virtual bool CanDispatch => true;

        /// <summary>
        ///     One turn of the stage: applies the finished batch, scans for new work when the queue runs short (or
        ///     every <see cref="ScanInterval" /> turns) and starts the next batch. The handler goes to sleep when a
        ///     scan finds nothing and no batch runs.
        /// </summary>
        /// <param name="focus">The focus position (e.g., player chunk position).</param>
        /// <returns>
        ///     True when the turn did main-thread work; false when the handler sleeps, its batch still runs or it
        ///     may not dispatch, so the scheduler can give the frame to another stage.
        /// </returns>
        public bool Update(PriorityUtil.Focus focus){
            if (Sleeping) return false;

            bool worked = false;
            if (Set.Count > 0){
                if (!CollectResultsStep()) return false; // the batch still runs
                worked = true;
            }

            if (!CanDispatch) return worked;

            if (Queue.Count < _batchSize || ++_turnsSinceScan >= ScanInterval){
                _turnsSinceScan = 0;
                worked = true;
                if (!EnqueueStep(focus)){
                    Sleeping = true; // nothing queued and nothing running
                    return true;
                }
            }

            return JobUpdateStep(focus) || worked;
        }

        /// <summary>
        ///     Removes the queued items <paramref name="outsideWindow" /> returns true for and makes the next turn
        ///     scan: the window moved with the focus. Without it the entries the focus left behind would stay
        ///     queued until they reach the head of the queue, and the queue would grow past its reserved capacity
        ///     during a flight.
        /// </summary>
        protected void PruneQueue(PriorityUtil.Focus focus, Func<PriorityUtil.Focus, T, bool> outsideWindow){
            Queue.RemoveAll(focus, outsideWindow);
            _turnsSinceScan = ScanInterval;
        }

        /// <summary>
        ///     Updates the focus position and reprioritizes all queued items.
        ///     Must be implemented by derived classes.
        /// </summary>
        /// <param name="focus">The new focus position.</param>
        public abstract void FocusUpdate(PriorityUtil.Focus focus);

        /// <summary>
        ///     Wakes up the handler from sleeping state so it resumes processing.
        /// </summary>
        public void WakeUp(){
            Sleeping = false;
        }

        /// <summary>
        ///     Enqueues new work items based on the focus position.
        ///     Called when a turn scans for work.
        /// </summary>
        /// <param name="focus">The current focus position.</param>
        /// <returns>True if items were enqueued, false if queue is empty.</returns>
        protected virtual bool EnqueueStep(PriorityUtil.Focus focus){
            return true;
        }

        /// <summary>
        ///     Dispatches the next batch to the stage's job scheduler; the batch's items are in the in-flight set.
        ///     Called on every turn that may dispatch.
        /// </summary>
        /// <param name="focus">The current focus position.</param>
        /// <returns>True if processing completed, false if still working.</returns>
        protected virtual bool JobUpdateStep(PriorityUtil.Focus focus){
            return true;
        }

        /// <summary>
        ///     Collects results from completed job batches.
        ///     Called at the start of a turn while a batch is in flight.
        /// </summary>
        /// <returns>True if result collection completed, false if still working.</returns>
        protected virtual bool CollectResultsStep(){
            return true;
        }
    }
}
