using System;
using System.Collections.Generic;
using Engine.Scripts.Settings;
using Engine.Scripts.ThirdParty.Priority_Queue;
using Engine.Scripts.Utils;
using Engine.Scripts.World;
using Unity.Mathematics;

namespace Engine.Scripts.Jobs.Core{
    /// <summary>
    ///     Abstract base class for job state handlers that manage queues of work items with priority-based scheduling.
    ///     One <see cref="Update" /> is one turn of the stage: collect the finished batch, look for new work, start
    ///     the next batch. A stage whose batch still runs passes its turn on. A turn is made of items of work (see
    ///     <see cref="FrameBudget" />): when the frame's budget runs out between two items the turn stops there,
    ///     and the next <see cref="Update" /> continues it.
    /// </summary>
    internal abstract class JobStateHandler<T> : IPipelineStage where T : struct, IEquatable<T>{
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

        /// <summary>Items per batch, as given to <see cref="Reserve" />.</summary>
        protected int BatchSize => _batchSize;
        private int _turnsSinceScan;

        /// <summary>Where a turn stands. A turn the budget cut continues at its step.</summary>
        private enum Step : byte{
            Collect,
            Scan,
            Dispatch
        }

        private Step _step;
        private int _scanRow; // the next row of the running scan
        private bool _worked; // the turn did main-thread work

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
        ///     One turn of the stage, or the rest of a turn the budget cut: applies the finished batch, scans for
        ///     new work when the queue runs short (or every <see cref="ScanInterval" /> turns) and starts the next
        ///     batch. The handler goes to sleep when a scan finds nothing and no batch runs.
        /// </summary>
        /// <param name="focus">The focus position (e.g., player chunk position).</param>
        /// <param name="budget">The frame's budget, asked before every item of work; null means no limit.</param>
        /// <returns>
        ///     <see cref="StageTurn.Done" /> when the turn did main-thread work and is finished,
        ///     <see cref="StageTurn.Cut" /> when the budget ran out inside it, and <see cref="StageTurn.None" /> when
        ///     the handler sleeps, its batch still runs or it may not dispatch, so the scheduler can give the frame
        ///     to another stage.
        /// </returns>
        public StageTurn Update(PriorityUtil.Focus focus, FrameBudget budget = null){
            budget ??= FrameBudget.Unlimited;

            if (_step == Step.Collect){
                if (Sleeping) return StageTurn.None;

                if (Set.Count > 0){
                    StageTurn collected;
                    using (VoxelProfilerMarkers.SchedulerCollect.Auto()) collected = CollectResultsStep(budget);
                    if (collected != StageTurn.Done) return collected; // the batch still runs, or the budget cut

                    _worked = true;
                }

                if (!CanDispatch) return EndTurn();

                if (Queue.Count < _batchSize || ++_turnsSinceScan >= ScanInterval){
                    _turnsSinceScan = 0;
                    _worked = true;
                    _scanRow = -ScanDistance;
                    _step = Step.Scan;
                }
                else{
                    _step = Step.Dispatch;
                }
            }

            if (_step == Step.Scan){
                using (VoxelProfilerMarkers.SchedulerScan.Auto()){
                    for (; _scanRow <= ScanDistance; _scanRow++){
                        if (!budget.Next()) return StageTurn.Cut;

                        EnqueueRow(focus, _scanRow);
                    }
                }

                if (Queue.Count == 0){
                    Sleeping = true; // nothing queued and nothing running
                    return EndTurn();
                }

                _step = Step.Dispatch;
            }

            StageTurn dispatched;
            using (VoxelProfilerMarkers.SchedulerDispatch.Auto()) dispatched = JobUpdateStep(focus, budget);
            if (dispatched == StageTurn.Cut) return StageTurn.Cut;

            if (dispatched == StageTurn.Done) _worked = true;
            return EndTurn();
        }

        /// <summary>Ends the turn: the next <see cref="Update" /> starts a new one.</summary>
        private StageTurn EndTurn(){
            StageTurn turn = _worked ? StageTurn.Done : StageTurn.None;
            _worked = false;
            _step = Step.Collect;
            return turn;
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
        ///     Rows on either side of the focus that a scan for new work visits: it calls
        ///     <see cref="EnqueueRow" /> for every x from -ScanDistance to ScanDistance.
        /// </summary>
        protected virtual int ScanDistance => 0;

        /// <summary>
        ///     Enqueues the new work items of one row of the stage's window: the columns <paramref name="x" />
        ///     chunks from the focus. A row is one item of work for the frame's budget, so a scan the budget cut
        ///     continues with the next row (and the focus of that frame).
        /// </summary>
        /// <param name="focus">The current focus position.</param>
        /// <param name="x">The row, in chunks from the focus.</param>
        protected virtual void EnqueueRow(PriorityUtil.Focus focus, int x){ }

        /// <summary>
        ///     Dispatches the next batch to the stage's job scheduler; the batch's items are in the in-flight set.
        ///     Called on every turn that may dispatch, and again for the rest of a dispatch the budget cut.
        /// </summary>
        /// <param name="focus">The current focus position.</param>
        /// <param name="budget">The frame's budget.</param>
        /// <returns>
        ///     <see cref="StageTurn.Done" /> when the step is finished, <see cref="StageTurn.Cut" /> when the budget
        ///     ran out inside it, <see cref="StageTurn.None" /> when the job scheduler is busy.
        /// </returns>
        protected virtual StageTurn JobUpdateStep(PriorityUtil.Focus focus, FrameBudget budget){
            return StageTurn.Done;
        }

        /// <summary>
        ///     Collects results from completed job batches.
        ///     Called at the start of a turn while a batch is in flight, and again for the rest of a collect the
        ///     budget cut.
        /// </summary>
        /// <param name="budget">The frame's budget.</param>
        /// <returns>
        ///     <see cref="StageTurn.Done" /> when the batch is applied, <see cref="StageTurn.Cut" /> when the budget
        ///     ran out inside it, <see cref="StageTurn.None" /> when the batch still runs.
        /// </returns>
        protected virtual StageTurn CollectResultsStep(FrameBudget budget){
            return StageTurn.Done;
        }
    }
}
