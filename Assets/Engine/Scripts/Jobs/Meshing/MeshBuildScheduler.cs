using System.Collections.Generic;
using System.Linq;
using Engine.Scripts.Behaviour;
using Engine.Scripts.Components;
using Engine.Scripts.Data;
using Engine.Scripts.Jobs.Core;
using Engine.Scripts.Settings;
using Engine.Scripts.Utils.Extensions;
using Engine.Scripts.Utils.Logger;
using Engine.Scripts.VoxelConfig.Data;
using Engine.Scripts.World;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace Engine.Scripts.Jobs.Meshing
{
    /// <summary>
    ///     Schedules and executes mesh build jobs for Partitions, creates collider meshes
    ///     using a greedy meshing algorithm and applying the results to chunk behaviors.
    /// </summary>
    internal sealed class MeshBuildScheduler : JobScheduler
    {
        private const MeshUpdateFlags MeshFlags = MeshUpdateFlags.DontRecalculateBounds |
                                                  MeshUpdateFlags.DontValidateIndices |
                                                  MeshUpdateFlags.DontResetBoneBounds;

        private readonly ChunkManager _chunkManager;
        private readonly ChunkPool _chunkPool;
        private readonly VoxelRegistry _voxelRegistry;
        private readonly VoxelWorld _world;

        private ChunkAccessor _chunkAccessor;

        private Mesh.MeshDataArray _colliderMeshDataArray;

        private NativeArray<VertexAttributeDescriptor> _colliderVertexParams;

        private JobHandle _handle;

        private NativeList<int3> _jobs;
        private NativeParallelHashMap<int3, MeshBuildJob.PartitionJobResult> _results;

        /// <summary>
        ///     Indicates whether the scheduler is ready to accept a new list of mesh build jobs.
        /// </summary>
        internal bool IsReady = true;

        /// <summary>
        ///     Initializes a new instance of the <see cref="MeshBuildScheduler" /> class.
        /// </summary>
        /// <param name="settings">Voxel engine settings providing chunk size and draw distance.</param>
        /// <param name="chunkManager">Chunk manager used to access chunk data and state.</param>
        /// <param name="chunkPool">Pool providing reusable chunk behaviours and meshes.</param>
        /// <param name="voxelRegistry">Voxel registry providing render generation data for blocks.</param>
        /// <param name="world">World event hub used to trigger GPU mesh generation and receive completion callbacks.</param>
        internal MeshBuildScheduler(
            VoxelEngineSettings settings,
            ChunkManager chunkManager,
            ChunkPool chunkPool,
            VoxelRegistry voxelRegistry,
            VoxelWorld world
        )
        {
            _chunkManager = chunkManager;
            _chunkPool = chunkPool;
            _voxelRegistry = voxelRegistry;
            _world = world;

            // Collider uses only Position and Normal from CVertex
            _colliderVertexParams = new NativeArray<VertexAttributeDescriptor>(2, Allocator.Domain)
            {
                [0] = new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float16, 4),
                [1] = new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.Float16, 4)
            };

            _results = new NativeParallelHashMap<int3, MeshBuildJob.PartitionJobResult>(
                settings.Chunk.DrawDistance.SquareSize(), Allocator.Domain);
            _jobs = new NativeList<int3>(Allocator.Domain);
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
        internal void Start(HashSet<int3> jobs)
        {
            StartRecord();

            IsReady = false;

            _chunkAccessor = _chunkManager.GetAccessor(jobs.ToList());

            foreach (int3 j in jobs) _jobs.Add(j);

            _colliderMeshDataArray = Mesh.AllocateWritableMeshData(_jobs.Length);

            MeshBuildJob job = new()
            {
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
        internal void Complete()
        {
            double start = Time.realtimeSinceStartupAsDouble;
            _handle.Complete();

            Mesh[] colliderMeshes = new Mesh[_jobs.Length];

            for (int index = 0; index < _jobs.Length; index++)
            {
                int3 pos = _jobs[index];
                ChunkPartition partition = _chunkPool.GetOrClaimPartition(pos);
                _chunkManager.ReMeshedPartition(pos);

                MeshBuildJob.PartitionJobResult result = _results[pos];

                colliderMeshes[result.Index] = partition.ColliderMesh;
                partition.ColliderMesh.bounds = result.ColliderBounds;
            }

            Mesh.ApplyAndDisposeWritableMeshData(
                _colliderMeshDataArray,
                colliderMeshes,
                MeshFlags
            );

            double totalTime = (Time.realtimeSinceStartupAsDouble - start) * 1000;
            if (totalTime >= 4)
                VoxelEngineLogger.Warn<MeshBuildScheduler>(
                    $"Built {_jobs.Length} meshes, Collected Results in <color=red>{totalTime:0.000}</color>ms"
                );

            _results.Clear();
            _jobs.Clear();

            IsReady = true;
            long totalJobTime = StopRecord();
            if (totalJobTime > 100)
                VoxelEngineLogger.Warn<MeshBuildScheduler>(
                    $"Total Mesh Build Time for {_jobs.Length} jobs: <color=red>{totalJobTime:0.000}</color>ms"
                );
        }

        /// <summary>
        ///     Disposes all native containers and completes any running jobs before releasing resources.
        /// </summary>
        internal void Dispose()
        {
            _handle.Complete();

            _colliderVertexParams.Dispose();
            _results.Dispose();
            _jobs.Dispose();
        }
    }
}