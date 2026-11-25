using System;
using System.Collections.Generic;
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
        /// <summary>
        ///     Partitions without a voxel that one turn may take from the queue, in mesh batches. They cost a claim in
        ///     the pool and nothing else, so a turn takes more of them than of the partitions that need a job.
        /// </summary>
        private const int EmptyPartitionsPerBatch = 4;

        private readonly CMeshBuildScheduler _cMeshBuildScheduler;
        private readonly ColliderJobStateHandler _colliderJobHandler;
        private readonly Func<PriorityUtil.Focus, int3, bool> _outsideWindow; // created once: FocusUpdate must not allocate
        private readonly Predicate<int3> _lostItsChunks; // created once: JobUpdateStep must not allocate
        private readonly List<int3> _emptyPartitions; // the partitions without a voxel of the current turn

        /// <summary>
        ///     Entries the dequeue loop takes from the queue per item of the frame's budget. Measured on 2026-11-15
        ///     in the Editor in a capped flight at draw distance 12: 5.2 to 5.4 us per entry (an upper bound, the
        ///     Editor was not focused), so a slice of 32 stays below 0.2 ms; a loop takes 79 entries at most. (With
        ///     the priority queue's heap check in every Dequeue, which ran under DEBUG until then and now needs the
        ///     define PRIORITY_QUEUE_VALIDATE, an entry cost 37 to 57 us and the slice was 4.)
        /// </summary>
        internal const int DefaultDequeueSlice = 32;

        // A dispatch the budget cut: the rest continues in the next frame, first the rest of the dequeue loop
        private bool _dispatching;
        private bool _dequeuing; // the dispatch is still taking its partitions from the queue
        private bool _dispatchCut;
        private int _nextEmpty;
        private int _sliceLeft; // entries the current item of the budget may still take from the queue

        /// <summary>
        ///     What the dequeue loop of the dispatch did since the world was built, for the measurement rig. Plain
        ///     counters: a few additions per entry and nothing else.
        /// </summary>
        internal struct DequeueCounts{
            /// <summary>
            ///     Runs of the dequeue loop (a loop the budget cut runs again in the next frame, and counts again),
            ///     and the entries the queue held at their start.
            /// </summary>
            internal long Calls, QueueAtStart;

            /// <summary>Runs of the dequeue loop the budget cut before the batch was complete.</summary>
            internal long Cuts;

            /// <summary>Entries taken from the queue.</summary>
            internal long Taken;

            /// <summary>Entries kept for the batch: with a voxel (mesh job), and without one (empty partition).</summary>
            internal long KeptWithVoxels, KeptEmpty;

            /// <summary>
            ///     Entries dropped: outside the draw window or under the pool's threshold (IsPartitionStillRelevant),
            ///     3x3 neighbourhood not loaded and lit (CanGenerateMeshForChunk), no mesh needed
            ///     (ShouldScheduleForMeshing).
            /// </summary>
            internal long DroppedNotRelevant, DroppedNotLit, DroppedNotNeeded;

            /// <summary>Loops that ended because the queue was empty.</summary>
            internal long QueueEmptied;

            /// <summary>
            ///     Dropped entries a later scan queued again, by the check that dropped them; only counted while
            ///     <see cref="TrackRequeues" /> is on.
            /// </summary>
            internal long RequeuedNotRelevant, RequeuedNotLit, RequeuedNotNeeded;
        }

        private const byte DropNotRelevant = 0;
        private const byte DropNotLit = 1;
        private const byte DropNotNeeded = 2;

        /// <summary>The counters of the dequeue loop (see <see cref="DequeueCounts" />).</summary>
        internal DequeueCounts Dequeued;

        /// <summary>
        ///     Entries the dequeue loop takes per item of the frame's budget (<see cref="DefaultDequeueSlice" />);
        ///     0 takes the whole batch as one item, as before 2026-11-15. Not a setting: tests and the measurement
        ///     rig change it.
        /// </summary>
        internal int DequeueSlice{ get; set; } = DefaultDequeueSlice;

        private Dictionary<int3, byte> _droppedEntries; // null unless the measurement rig tracks requeues

        /// <summary>
        ///     Remembers every dropped entry, so that <see cref="DequeueCounts" /> also counts the ones a scan queues
        ///     again. For the measurement rig only: it allocates.
        /// </summary>
        internal bool TrackRequeues{
            get => _droppedEntries != null;
            set => _droppedEntries = value ? _droppedEntries ?? new Dictionary<int3, byte>(4096) : null;
        }

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
            _outsideWindow = (focus, pos) => !IsInDrawWindow(pos, focus);
            _lostItsChunks = pos => !CanGenerateMeshForChunk(pos.xz);
            int side = 2 * settings.Chunk.ActiveDrawDistance + 1;
            int layers = math.min(VoxelConstants.PartitionsPerChunk, side);
            // The batch size follows the draw distance; it is fixed here, when the world is built
            Reserve(side * side * layers, settings.Scheduler.MeshBatchSizeAt(settings.Chunk.ActiveDrawDistance));
            _emptyPartitions = new List<int3>(EmptyPartitionsPerBatch * BatchSize);
        }

        /// <summary>Partitions handed to the mesh job per batch in this world.</summary>
        internal int MeshBatchSize => BatchSize;

        /// <summary>
        ///     Updates focus: drops the queued partitions that left the draw window and reprioritizes the rest based
        ///     on distance from focus. So the queue never holds more than the window.
        /// </summary>
        /// <param name="focus">The new focus position.</param>
        public override void FocusUpdate(PriorityUtil.Focus focus){
            WakeUp();
            PruneQueue(focus, _outsideWindow);
            // Static lambda with explicit state instead of a closure capturing the focus
            Queue.UpdateAllPriorities(focus, static (f, pos) => PriorityUtil.DistPriority(ref pos, ref f));
        }

        /// <summary>
        ///     The renderer builds the geometry of every dispatched partition on the GPU, a few per frame. While it
        ///     is behind, the next batch waits here: the partitions stay in the priority queue, which follows the
        ///     focus, instead of piling up in the renderer's first-in-first-out list.
        /// </summary>
        protected override bool CanDispatch => !_cMeshBuildScheduler.RendererBehind;

        /// <summary>The scan visits the draw distance.</summary>
        protected override int ScanDistance => Settings.Chunk.ActiveDrawDistance;

        /// <summary>
        ///     Enqueues the partitions of one row within draw distance that need mesh generation.
        ///     Only enqueues if the parent chunk has all neighboring chunks loaded.
        /// </summary>
        /// <param name="focus">The current focus position.</param>
        /// <param name="x">The row, in chunks from the focus.</param>
        protected override void EnqueueRow(PriorityUtil.Focus focus, int x){
            int draw = Settings.Chunk.ActiveDrawDistance;
            float prioThreshold = ChunkPool.GetPartitionPrioThreshold();
            // Only the vertical draw window is queued, the same filter IsPartitionStillRelevant applies;
            // queueing all layers would make JobUpdateStep drop and re-queue the others every cycle.
            int minY = math.max(0, focus.Pos.y - draw);
            int maxY = math.min(VoxelConstants.PartitionsPerChunk - 1, focus.Pos.y + draw);
            uint window = minY > maxY ? 0u : (uint)(((1ul << (maxY + 1)) - 1) & ~((1ul << minY) - 1));

            for (int z = -draw; z <= draw; z++){
                int2 column = focus.Pos.xz + new int2(x, z);
                if (!CanGenerateMeshForChunk(column)) continue;
                // A column whose window is active and holds no remesh flag has nothing to queue: one test for the
                // column instead of one per partition (DEC-232; the build height doubled the partitions)
                if ((window & ~ChunkPool.ActivePartitionMask(column)) == 0 && !ChunkManager.ColumnHasReMesh(column)) continue;

                for (int y = minY; y <= maxY; y++){
                    int3 pos = new(x + focus.Pos.x, y, z + focus.Pos.z);
                    if (Queue.Contains(pos) || !ShouldScheduleForMeshing(pos)) continue;

                    // The threshold is the pool's eviction priority, compare in the same measure
                    if (PriorityUtil.EvictionPriority(ref pos, ref focus) <= prioThreshold) continue;

                    if (_droppedEntries != null && _droppedEntries.Remove(pos, out byte dropped)) CountRequeue(dropped);
                    Queue.Enqueue(pos, PriorityUtil.DistPriority(ref pos, ref focus));
                }
            }
        }

        /// <summary>
        ///     Takes the next partitions from the queue. The ones that hold a voxel go to the mesh build scheduler
        ///     as a batch. The ones that hold only air need no mesh job, no GPU build and no collider bake: they
        ///     become active at once, with an empty mesh (see <see cref="CMeshBuildScheduler.ApplyEmpty" />). They
        ///     do not count against the batch, but a turn takes a limited number of them.
        ///     Items of work for the frame's budget: every <see cref="DequeueSlice" /> entries taken from the queue,
        ///     every partition without a voxel (its claim in the pool) and the start of the mesh job. Under the
        ///     budget a claim does not wait for a running collider bake: the dispatch stops before it without an item
        ///     and continues in the next frame. A dispatch the
        ///     budget cut keeps its partitions and continues where it stopped: a cut dequeue loop goes on filling
        ///     the same batch in the next frame, with that frame's focus. After a cut a partition without a voxel
        ///     that left the draw window or got a voxel in the meantime is skipped, and a partition with voxels
        ///     whose neighbourhood lost a chunk is left to the next scan.
        /// </summary>
        /// <param name="focus">The current focus position.</param>
        /// <param name="budget">The frame's budget.</param>
        /// <returns>None while the scheduler is busy, Cut when the budget ran out inside, otherwise Done.</returns>
        protected override StageTurn JobUpdateStep(PriorityUtil.Focus focus, FrameBudget budget){
            if (!_dispatching){
                if (!_cMeshBuildScheduler.IsReady) return StageTurn.None;
                if (!budget.Next()) return StageTurn.Cut;

                _dispatching = true;
                _dequeuing = true;
                _dispatchCut = false;
                _nextEmpty = 0;
                _sliceLeft = DequeueSlice; // the item just granted is the first slice
            }

            if (_dequeuing){
                if (!TakeFromQueue(focus, budget)) return CutDispatch();

                _dequeuing = false;
            }

            for (; _nextEmpty < _emptyPartitions.Count; _nextEmpty++){
                int3 partition = _emptyPartitions[_nextEmpty];
                // Frames passed since a cut dispatch took its partitions from the queue: the focus may have moved
                // on, and an edit may have placed a block in the partition. Such a partition is left to the next
                // scan, which finds it flagged (or inactive) and meshes it like any other; made active here, with
                // an empty mesh and its remesh flag consumed, the block would stay invisible and without collider.
                if (_dispatchCut &&
                    (!IsInDrawWindow(partition, focus) || ChunkManager.PartitionHasVoxels(partition))) continue;
                // Its claim would wait for a running bake: the dispatch stops without an item and goes on in the
                // next frame (the bake finishes on the worker threads; no stage starts a new one meanwhile).
                if (_cMeshBuildScheduler.EmptyClaimWaitsForBake(partition, budget)) return CutDispatch();
                if (!budget.Next()) return CutDispatch();

                _cMeshBuildScheduler.ApplyEmpty(partition);
            }

            // A claim in a full pool can reclaim a pooled chunk, and the world then unloads its data: a partition
            // of this batch that lost a chunk of its neighbourhood is left to the next scan. The same holds for a
            // chunk that was unloaded in the frames a cut dispatch waited.
            if (Set.Count > 0 && (_emptyPartitions.Count > 0 || _dispatchCut)) Set.RemoveWhere(_lostItsChunks);

            if (Set.Count > 0){
                if (!budget.Next()) return CutDispatch();

                _cMeshBuildScheduler.Start(Set);
            }

            _emptyPartitions.Clear();
            _dispatching = false;
            return StageTurn.Done;
        }

        /// <summary>
        ///     The dequeue loop: takes entries from the queue until the batch or the partitions without a voxel are
        ///     complete or the queue is empty, and asks the budget for every <see cref="DequeueSlice" /> entries.
        ///     The first slice of a dispatch is the item <see cref="JobUpdateStep" /> was granted; a loop the budget
        ///     cut starts its next frame with a new slice, the frame's first item, so it takes an entry in every
        ///     frame.
        /// </summary>
        /// <returns>False when the budget ran out before the loop was done.</returns>
        private bool TakeFromQueue(PriorityUtil.Focus focus, FrameBudget budget){
            float prioThreshold = ChunkPool.GetPartitionPrioThreshold();
            int emptyLimit = EmptyPartitionsPerBatch * BatchSize;

            using (VoxelProfilerMarkers.MeshBuildDequeue.Auto()){
                Dequeued.Calls++;
                Dequeued.QueueAtStart += Queue.Count;
                while (Set.Count < BatchSize && _emptyPartitions.Count < emptyLimit && Queue.Count > 0){
                    if (DequeueSlice > 0){
                        if (_sliceLeft <= 0){
                            if (!budget.Next()){
                                Dequeued.Cuts++;
                                return false;
                            }

                            _sliceLeft = DequeueSlice;
                        }

                        _sliceLeft--;
                    }

                    int3 partition = Queue.Dequeue();
                    Dequeued.Taken++;
                    if (!IsPartitionStillRelevant(partition, focus, prioThreshold)){
                        Dequeued.DroppedNotRelevant++;
                        _droppedEntries?.TryAdd(partition, DropNotRelevant);
                        continue;
                    }

                    if (!CanGenerateMeshForChunk(partition.xz)){
                        Dequeued.DroppedNotLit++;
                        _droppedEntries?.TryAdd(partition, DropNotLit);
                        continue;
                    }

                    if (!ShouldScheduleForMeshing(partition)){
                        Dequeued.DroppedNotNeeded++;
                        _droppedEntries?.TryAdd(partition, DropNotNeeded);
                        continue;
                    }

                    if (ChunkManager.PartitionHasVoxels(partition)){
                        Set.Add(partition);
                        Dequeued.KeptWithVoxels++;
                    }
                    else{
                        _emptyPartitions.Add(partition);
                        Dequeued.KeptEmpty++;
                    }
                }

                if (Queue.Count == 0) Dequeued.QueueEmptied++;
            }

            return true;
        }

        private void CountRequeue(byte dropped){
            switch (dropped){
                case DropNotRelevant: Dequeued.RequeuedNotRelevant++; break;
                case DropNotLit: Dequeued.RequeuedNotLit++; break;
                default: Dequeued.RequeuedNotNeeded++; break;
            }
        }

        private StageTurn CutDispatch(){
            _dispatchCut = true;
            return StageTurn.Cut;
        }

        /// <summary>
        ///     Collects results from completed mesh building and wakes up the collider handler. Every claim in the
        ///     pool is an item of work (see <see cref="CMeshBuildScheduler.Complete" />).
        /// </summary>
        /// <param name="budget">The frame's budget.</param>
        /// <returns>None while the batch runs, Cut when the budget ran out inside the collect, otherwise Done.</returns>
        protected override StageTurn CollectResultsStep(FrameBudget budget){
            // A batch that was already applied elsewhere only clears the set; waiting for it would stall the handler.
            if (!_cMeshBuildScheduler.IsReady){
                if (!_cMeshBuildScheduler.IsComplete) return StageTurn.None;
                if (!_cMeshBuildScheduler.Complete(budget)) return StageTurn.Cut;
            }

            Set.Clear();
            _colliderJobHandler.WakeUp();
            return StageTurn.Done;
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
            return IsInDrawWindow(position, focus) &&
                   PriorityUtil.EvictionPriority(ref position, ref focus) > prioThreshold;
        }

        /// <summary>Whether a partition lies within the draw distance of the focus on every axis.</summary>
        private bool IsInDrawWindow(int3 position, PriorityUtil.Focus focus){
            int drawDistance = Settings.Chunk.ActiveDrawDistance;
            return math.abs(position.x - focus.Pos.x) <= drawDistance &&
                   math.abs(position.z - focus.Pos.z) <= drawDistance &&
                   math.abs(position.y - focus.Pos.y) <= drawDistance;
        }
    }
}
