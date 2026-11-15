using System.Collections.Generic;
using Engine.Scripts.Behaviour;
using Engine.Scripts.Data;
using Engine.Scripts.Jobs.Core;
using Engine.Scripts.Settings;
using Engine.Scripts.Utils;
using Engine.Scripts.Utils.Logger;
using Engine.Scripts.VoxelConfig.Registry;
using Engine.Scripts.World;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering;

namespace Engine.Scripts.Jobs.ColliderMeshing{
    /// <summary>
    ///     Schedules and executes mesh build jobs for Partitions, creates collider meshes
    ///     using a greedy meshing algorithm and applying the results to chunk behaviors.
    /// </summary>
    internal sealed class CMeshBuildScheduler : JobScheduler{
        private const MeshUpdateFlags MeshFlags = MeshUpdateFlags.DontRecalculateBounds |
                                                  MeshUpdateFlags.DontValidateIndices |
                                                  MeshUpdateFlags.DontResetBoneBounds;

        private readonly ChunkManager _chunkManager;
        private readonly ChunkPool _chunkPool;
        private readonly VoxelRegistry _voxelRegistry;
        private readonly VoxelWorld _world;
        private readonly int _buildBacklogLimit; // waiting GPU builds up to which the next batch is dispatched

        /// <summary>
        ///     Indicates whether the scheduler is ready to accept a new list of mesh build jobs.
        /// </summary>
        internal bool IsReady = true;

        private ChunkAccessor _chunkAccessor;

        private Mesh.MeshDataArray _colliderMeshDataArray;

        private NativeArray<VertexAttributeDescriptor> _colliderVertexParams;

        private JobHandle _handle;

        private NativeList<int3> _jobs;
        private int _claimed; // jobs of the batch whose partition Complete has claimed; it continues there after a cut
        private NativeParallelHashMap<int3, CMeshBuildJob.PartitionJobResult> _results;

        // Reused per batch instead of a new Mesh[] per Complete
        private readonly List<Mesh> _colliderMeshes;

        // Targets for results that Complete drops (chunk unloaded or partition reclaimed during the job)
        private readonly List<Mesh> _discardMeshes = new();

        // The partitions of a batch whose mesh was applied, for the pass over the applied meshes
        private readonly List<(int3 pos, ChunkPartition partition)> _applied;

        /// <summary>Partitions that got a mesh from a mesh job since the world was built.</summary>
        internal int MeshedPartitions{ get; private set; }

        /// <summary>Partitions without a voxel that became active without a mesh job and without a GPU build.</summary>
        internal int PartitionsWithoutMesh{ get; private set; }

        /// <summary>Meshed partitions whose collider mesh came out empty, so they needed no bake.</summary>
        internal int PartitionsWithoutBake{ get; private set; }

        /// <summary>
        ///     Initializes a new instance of the <see cref="CMeshBuildScheduler" /> class.
        /// </summary>
        /// <param name="settings">Voxel engine settings providing chunk size and draw distance.</param>
        /// <param name="chunkManager">Chunk manager used to access chunk data and state.</param>
        /// <param name="chunkPool">Pool providing reusable chunk behaviours and meshes.</param>
        /// <param name="voxelRegistry">Voxel registry providing render generation data for blocks.</param>
        /// <param name="world">World event hub used to trigger GPU mesh generation and receive completion callbacks.</param>
        internal CMeshBuildScheduler(
            VoxelEngineSettings settings,
            ChunkManager chunkManager,
            ChunkPool chunkPool,
            VoxelRegistry voxelRegistry,
            VoxelWorld world
        ){
            _chunkManager = chunkManager;
            _chunkPool = chunkPool;
            _voxelRegistry = voxelRegistry;
            _world = world;
            // Both follow the draw distance and are fixed here, when the world is built
            int drawDistance = settings.Chunk.DrawDistance;
            int meshBatch = settings.Scheduler.MeshBatchSizeAt(drawDistance);
            _buildBacklogLimit = 2 * settings.Scheduler.BuildSlotsAt(drawDistance);

            _colliderVertexParams = CVertex.CreateLayout(Allocator.Domain);

            // One result per job of a batch; a parallel writer cannot grow the map, so it is sized from the
            // batch size (and grown in Start for a larger batch) instead of the draw distance.
            _results = new NativeParallelHashMap<int3, CMeshBuildJob.PartitionJobResult>(meshBatch, Allocator.Domain);
            _jobs = new NativeList<int3>(Allocator.Domain);
            _colliderMeshes = new List<Mesh>(meshBatch);
            _applied = new List<(int3, ChunkPartition)>(meshBatch);

            // The job reads chunk data through the accessor; finish it before the manager edits or disposes a chunk.
            _chunkManager.AccessorChunkWriting += CompleteIfRunning;
        }

        /// <summary>
        ///     Gets a value indicating whether the currently scheduled mesh build jobs have completed.
        /// </summary>
        internal bool IsComplete => _handle.IsCompleted;

        /// <summary>
        ///     True while more partitions of earlier batches wait in the renderer for a GPU build slot than two rounds
        ///     of its slots take: the slots do not run empty between two mesh batches, and the waiting list stays
        ///     short enough to follow the focus. Always false without a renderer.
        /// </summary>
        internal bool RendererBehind => _world.PendingPartitionBuilds > _buildBacklogLimit;

        /// <summary>
        ///     Starts a mesh build job for the given list of chunk positions.
        ///     Allocates writable mesh data arrays and schedules the Burst job.
        /// </summary>
        /// <param name="jobs">List of chunk positions whose meshes should be generated or updated.</param>
        internal void Start(HashSet<int3> jobs){
            using ProfilerMarker.AutoScope _ = VoxelProfilerMarkers.MeshBuildSchedule.Auto();
            StartRecord();

            IsReady = false;

            _chunkAccessor = _chunkManager.GetAccessor(jobs);

            foreach (int3 j in jobs){
                _jobs.Add(j);
                _chunkManager.ReMeshStarted(j);
            }

            if (_results.Capacity < _jobs.Length) _results.Capacity = _jobs.Length;

            _colliderMeshDataArray = Mesh.AllocateWritableMeshData(_jobs.Length);

            CMeshBuildJob job = new(){
                Accessor = _chunkAccessor,
                Jobs = _jobs,
                ColliderVertexParams = _colliderVertexParams,
                ColliderMeshDataArray = _colliderMeshDataArray,
                RenderGenData = _voxelRegistry.GetVoxelGenData(),
                Results = _results.AsParallelWriter()
            };

            _handle = job.Schedule(_jobs.Length, 1);
            _world.RequestPartitionBuild(jobs);
        }

        /// <summary>
        ///     Completes the scheduled mesh build jobs, applies the generated mesh data to chunk meshes
        ///     and colliders, recalculates bounds and logs timing information.
        ///     Items of work for <paramref name="budget" />: every claim of a partition in the pool, and the rest
        ///     (all meshes of the batch are assigned and applied in one call). When the budget runs out in between,
        ///     the batch stays unapplied and the next call continues with the next claim.
        /// </summary>
        /// <param name="budget">The frame's budget; null means no limit.</param>
        /// <returns>True when the batch is applied; false when the budget cut the call.</returns>
        internal bool Complete(FrameBudget budget = null){
            if (IsReady) return true;
            budget ??= FrameBudget.Unlimited;
            using ProfilerMarker.AutoScope _ = VoxelProfilerMarkers.MeshBuildComplete.Auto();
#if VOXEL_TIMING_LOGS
            double start = Time.realtimeSinceStartupAsDouble;
#endif
            using (VoxelProfilerMarkers.MeshBuildWaitBake.Auto()){
                _handle.Complete();
                _chunkPool.NotifyPartitionMeshWriting(); // a running bake may still read the meshes written below
            }

            // Claim every partition before any mesh is assigned: a claim can reclaim a pooled chunk of this batch
            // (the world then unloads its data), which would clear a mesh already handed out. A partition claimed
            // in an earlier frame of a cut call that was reclaimed since is dropped below like any other.
            using (VoxelProfilerMarkers.MeshBuildClaim.Auto()){
                for (; _claimed < _jobs.Length; _claimed++){
                    if (!budget.Next()) return false;

                    int3 pos = _jobs[_claimed];
                    if (_chunkManager.IsChunkLoaded(pos.xz)) _chunkPool.GetOrClaimPartition(pos);
                }
            }

            if (!budget.Next()) return false;

            _colliderMeshes.Clear();
            for (int index = 0; index < _jobs.Length; index++) _colliderMeshes.Add(null);

            int discarded = 0;
            for (int index = 0; index < _jobs.Length; index++){
                int3 pos = _jobs[index];
                CMeshBuildJob.PartitionJobResult result = _results[pos];

                // The chunk was unloaded or the partition reclaimed after the job started. Its result is dropped;
                // the partition is inactive, so it is meshed again once its chunk is back.
                if (!_chunkManager.IsChunkLoaded(pos.xz) || !_chunkPool.IsPartitionActive(pos)){
                    _colliderMeshes[result.Index] = GetDiscardMesh(discarded++);
                    continue;
                }

                ChunkPartition partition = _chunkPool.GetPartition(pos);
                _chunkManager.ReMeshedPartition(pos);

                partition.MarkMeshChanged(); // a bake of the previous mesh is stale
                _colliderMeshes[result.Index] = partition.ColliderMesh;
                partition.ColliderMesh.bounds = result.ColliderBounds;
                _applied.Add((pos, partition));
            }

            // Every entry of the mesh data array needs a target mesh
            using (VoxelProfilerMarkers.MeshBuildApply.Auto()){
                Mesh.ApplyAndDisposeWritableMeshData(
                    _colliderMeshDataArray,
                    _colliderMeshes,
                    MeshFlags
                );
            }

            _colliderMeshes.Clear();
            for (int i = 0; i < discarded; i++) _discardMeshes[i].Clear();

            // A mesh without a vertex (water, plants, or blocks with no open face) needs no bake: the partition
            // has no collider, and one from before its last solid face went is removed here.
            MeshedPartitions += _applied.Count;
            for (int i = 0; i < _applied.Count; i++){
                (int3 pos, ChunkPartition partition) = _applied[i];
                if (partition.ColliderMesh.vertexCount > 0) continue;
                MarkCollidableWithoutCollider(pos, partition);
                PartitionsWithoutBake++;
            }

            _applied.Clear();

#if VOXEL_TIMING_LOGS
            double totalTime = (Time.realtimeSinceStartupAsDouble - start) * 1000;
            if (totalTime >= 4){
                VoxelEngineLogger.Warn<CMeshBuildScheduler>(
                    $"Built {_jobs.Length} meshes, Collected Results in <color=red>{totalTime:0.000}</color>ms"
                );
            }
#endif

#if VOXEL_TIMING_LOGS
            int builtJobs = _jobs.Length; // the log below runs after the clear
#endif
            _results.Clear();
            _jobs.Clear();
            _claimed = 0;

            IsReady = true;
#if VOXEL_TIMING_LOGS
            double totalJobTime = StopRecord();
            if (totalJobTime > 100){
                VoxelEngineLogger.Warn<CMeshBuildScheduler>(
                    $"Total Mesh Build Time for {builtJobs} jobs: <color=red>{totalJobTime:0.000}</color>ms"
                );
            }
#else
            StopRecord();
#endif
            return true;
        }

        /// <summary>
        ///     Makes a partition without a voxel active without a mesh job: it has no geometry (every face belongs
        ///     to the voxel it is a face of) and no collider, so the pooled partition, cleared by the claim, is
        ///     already its final state. No GPU build is requested and no bake is needed. The partition counts in
        ///     the pool like a meshed one, so the pool evicts as before; an edit that places a block in it flags it
        ///     for remeshing, and it is then meshed like any other.
        /// </summary>
        internal void ApplyEmpty(int3 pos){
            _chunkManager.ReMeshStarted(pos);
            // Already active: its mesh is empty, because a partition that ever held a voxel keeps its bit
            if (_chunkPool.IsPartitionActive(pos) || !_chunkManager.IsChunkLoaded(pos.xz)) return;

            ChunkPartition partition = _chunkPool.GetOrClaimPartition(pos);
            // The claim reclaims the farthest partition of a full pool, which may be this one
            if (!_chunkPool.IsPartitionActive(pos)) return;
            MarkCollidableWithoutCollider(pos, partition);
            PartitionsWithoutMesh++;
        }

        private void MarkCollidableWithoutCollider(int3 pos, ChunkPartition partition){
            _chunkPool.ColliderBaked(pos);
            _chunkManager.ReCollidedPartition(pos);
            partition.ApplyColliderMesh(); // an empty mesh removes the collider
        }

        /// <summary>
        ///     Waits for the running job when the chunk manager is about to write a chunk the job may read.
        ///     Only the job is completed, its results are applied later by <see cref="Complete" />: applying them here
        ///     would re-enter <see cref="Complete" /> when claiming a partition evicts a chunk of the running batch.
        /// </summary>
        internal void CompleteIfRunning(){
            if (_handle.IsCompleted){
                _handle.Complete();
                return;
            }

            using ProfilerMarker.AutoScope _ = VoxelProfilerMarkers.WaitMeshBuild.Auto();
            _handle.Complete();
        }

        /// <summary>
        ///     Mesh that receives a dropped result; created only when a batch actually drops one.
        /// </summary>
        private Mesh GetDiscardMesh(int index){
            while (_discardMeshes.Count <= index) _discardMeshes.Add(new Mesh{ name = "PColliderDiscard" });
            return _discardMeshes[index];
        }

        /// <summary>
        ///     Disposes all native containers and completes any running jobs before releasing resources.
        /// </summary>
        internal void Dispose(){
            _chunkManager.AccessorChunkWriting -= CompleteIfRunning;
            _handle.Complete();

            // A batch that was never completed still owns its writable mesh data
            if (!IsReady) _colliderMeshDataArray.Dispose();
            foreach (Mesh mesh in _discardMeshes) CoreUtils.Destroy(mesh);
            _discardMeshes.Clear();

            _colliderVertexParams.Dispose();
            _results.Dispose();
            _jobs.Dispose();
        }
    }
}
