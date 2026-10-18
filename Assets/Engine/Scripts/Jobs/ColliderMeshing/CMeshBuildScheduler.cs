using System.Collections.Generic;
using Engine.Scripts.Behaviour;
using Engine.Scripts.Components;
using Engine.Scripts.Data;
using Engine.Scripts.Jobs.Core;
using Engine.Scripts.Settings;
using Engine.Scripts.Utils;
using Engine.Scripts.Utils.Extensions;
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

        /// <summary>
        ///     Indicates whether the scheduler is ready to accept a new list of mesh build jobs.
        /// </summary>
        internal bool IsReady = true;

        private ChunkAccessor _chunkAccessor;

        private Mesh.MeshDataArray _colliderMeshDataArray;

        private NativeArray<VertexAttributeDescriptor> _colliderVertexParams;

        private JobHandle _handle;

        private NativeList<int3> _jobs;
        private NativeParallelHashMap<int3, CMeshBuildJob.PartitionJobResult> _results;

        // PERF-04: reused per batch instead of a new Mesh[] per Complete
        private readonly List<Mesh> _colliderMeshes;

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

            // Collider uses only Position and Normal from CVertex
            _colliderVertexParams = new NativeArray<VertexAttributeDescriptor>(2, Allocator.Domain){
                [0] = new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float16, 4),
                [1] = new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.Float16, 4)
            };

            // F-16: one result per job of a batch; a parallel writer cannot grow the map, so it is sized from the
            // batch size (and grown in Start for a larger batch) instead of the draw distance.
            _results = new NativeParallelHashMap<int3, CMeshBuildJob.PartitionJobResult>(
                math.max(1, settings.Scheduler.meshingBatchSize), Allocator.Domain);
            _jobs = new NativeList<int3>(Allocator.Domain);
            _colliderMeshes = new List<Mesh>(math.max(1, settings.Scheduler.meshingBatchSize));

            // F-01: the job reads chunk data through the accessor; finish it before the manager edits or disposes a chunk.
            _chunkManager.AccessorChunkWriting += CompleteIfRunning;
        }

        /// <summary>
        ///     Gets a value indicating whether the currently scheduled mesh build jobs have completed.
        /// </summary>
        internal bool IsComplete => _handle.IsCompleted;

        /// <summary>
        ///     Starts a mesh build job for the given list of chunk positions.
        ///     Allocates writable mesh data arrays and schedules the Burst job.
        /// </summary>
        /// <param name="jobs">List of chunk positions whose meshes should be generated or updated.</param>
        internal void Start(HashSet<int3> jobs){
            using ProfilerMarker.AutoScope _ = VoxelProfilerMarkers.MeshBuildSchedule.Auto(); // PERF-01
            StartRecord();

            IsReady = false;

            _chunkAccessor = _chunkManager.GetAccessor(jobs); // PERF-04

            foreach (int3 j in jobs) _jobs.Add(j);
            if (_results.Capacity < _jobs.Length) _results.Capacity = _jobs.Length; // F-16

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
        /// </summary>
        internal void Complete(){
            if (IsReady) return;
            using ProfilerMarker.AutoScope _ = VoxelProfilerMarkers.MeshBuildComplete.Auto(); // PERF-01
#if VOXEL_TIMING_LOGS
            double start = Time.realtimeSinceStartupAsDouble;
#endif
            _handle.Complete();

            _colliderMeshes.Clear();
            for (int index = 0; index < _jobs.Length; index++) _colliderMeshes.Add(null);

            for (int index = 0; index < _jobs.Length; index++){
                int3 pos = _jobs[index];
                ChunkPartition partition = _chunkPool.GetOrClaimPartition(pos);
                _chunkManager.ReMeshedPartition(pos);

                CMeshBuildJob.PartitionJobResult result = _results[pos];

                _colliderMeshes[result.Index] = partition.ColliderMesh;
                partition.ColliderMesh.bounds = result.ColliderBounds;
            }

            Mesh.ApplyAndDisposeWritableMeshData(
                _colliderMeshDataArray,
                _colliderMeshes,
                MeshFlags
            );
            _colliderMeshes.Clear();

#if VOXEL_TIMING_LOGS // PERF-04: the timing string was built per batch in the hot path; the PERF-01 markers cover it
            double totalTime = (Time.realtimeSinceStartupAsDouble - start) * 1000;
            if (totalTime >= 4){
                VoxelEngineLogger.Warn<CMeshBuildScheduler>(
                    $"Built {_jobs.Length} meshes, Collected Results in <color=red>{totalTime:0.000}</color>ms"
                );
            }
#endif

            _results.Clear();
            _jobs.Clear();

            IsReady = true;
#if VOXEL_TIMING_LOGS
            long totalJobTime = StopRecord();
            if (totalJobTime > 100){
                VoxelEngineLogger.Warn<CMeshBuildScheduler>(
                    $"Total Mesh Build Time for {_jobs.Length} jobs: <color=red>{totalJobTime:0.000}</color>ms"
                );
            }
#else
            StopRecord();
#endif
        }

        /// <summary>
        ///     F-01: waits for the running job when the chunk manager is about to write a chunk the job may read.
        ///     Only the job is completed, its results are applied later by <see cref="Complete" />: applying them here
        ///     re-entered <see cref="Complete" /> when claiming a partition evicted a chunk of the running batch.
        /// </summary>
        private void CompleteIfRunning(){
            _handle.Complete();
        }

        /// <summary>
        ///     Disposes all native containers and completes any running jobs before releasing resources.
        /// </summary>
        internal void Dispose(){
            _chunkManager.AccessorChunkWriting -= CompleteIfRunning;
            _handle.Complete();

            _colliderVertexParams.Dispose();
            _results.Dispose();
            _jobs.Dispose();
        }
    }
}
