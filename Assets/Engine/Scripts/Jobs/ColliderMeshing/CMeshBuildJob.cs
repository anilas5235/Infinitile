using Engine.Scripts.Data;
using Engine.Scripts.Utils;
using Engine.Scripts.VoxelConfig.Data;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace Engine.Scripts.Jobs.ColliderMeshing
{
    /// <summary>
    ///     Burst-compiled parallel job that generates collider mesh data for a list of chunk positions
    ///     using the greedy mesher and writes the results into provided <see cref="UnityEngine.Mesh.MeshDataArray" />
    ///     instances while recording the position-to-index mapping.
    /// </summary>
    [BurstCompile]
    internal partial struct CMeshBuildJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<VertexAttributeDescriptor> ColliderVertexParams;
        [ReadOnly] public ChunkAccessor Accessor;
        [ReadOnly] public NativeList<int3> Jobs;
        [ReadOnly] public VoxelEngineRenderGenData RenderGenData;


        [WriteOnly] public NativeParallelHashMap<int3, PartitionJobResult>.ParallelWriter Results;
        public Mesh.MeshDataArray ColliderMeshDataArray;

        /// <summary>
        ///     Executes mesh generation for the given job index by processing the corresponding chunk position,
        ///     generating mesh data using the greedy meshing algorithm, and writing the results to the output arrays
        ///     and mapping. This method is called in parallel for each index in the <see cref="Jobs" /> list.
        /// </summary>
        /// <param name="index">Index of the chunk position to process within the <see cref="Jobs" /> list.</param>
        public void Execute(int index)
        {
            int3 position = Jobs[index];
            Accessor.TryGetChunk(position.xz, out ChunkVoxelData chunk);
            PartitionJobData jobData = new(ColliderMeshDataArray[index], position, chunk);

            SortVoxels(ref jobData);

            MeshCollision(ref jobData);

            WriteResults(index, ref jobData);

            jobData.Dispose();
        }
        
        private void SortVoxels(ref PartitionJobData jobData)
        {
            for (int y = 0; y < VoxelConstants.PartitionHeight; y++)
            for (int z = 0; z < VoxelConstants.PartitionDepth; z++)
            for (int x = 0; x < VoxelConstants.PartitionWidth; x++)
            {
                int3 localPos = new(x, y, z);
                ushort voxelId = GetVoxel(ref jobData, localPos);
                VoxelRenderDef renderDef = RenderGenData.GetRenderDef(voxelId);

                if (renderDef.Collision) jobData.CollisionVoxels.Add(localPos);
            }
        }
    }
}