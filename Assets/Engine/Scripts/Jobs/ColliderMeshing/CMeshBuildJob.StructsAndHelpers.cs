using System;
using Engine.Scripts.Data;
using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using static Engine.Scripts.Utils.VoxelConstants;

namespace Engine.Scripts.Jobs.ColliderMeshing
{
    internal partial struct CMeshBuildJob
    {
        #region Constants

        private const int VoxelCount4 = VoxelsPerPartition * 4;
        private const int VoxelCount6 = VoxelsPerPartition * 6;

        #endregion

        #region Structs

        [BurstCompile]
        internal struct PartitionJobResult
        {
            public int Index;
            public int3 PartitionPos;
            public Bounds ColliderBounds;
        }

        [BurstCompile]
        private struct PartitionJobData : IDisposable
        {
            public readonly Mesh.MeshData ColliderMesh;
            public readonly int2 ChunkPos;
            public readonly int3 PartitionPos;

            public CMeshBuffer CMeshBuffer;

            public NativeHashSet<int3> CollisionVoxels;

            public ChunkVoxelData ChunkVoxelData;
            public int CollisionVertexCount;

            public bool HasNoCollision => CollisionVoxels.IsEmpty;

            internal PartitionJobData(Mesh.MeshData colliderMesh, int3 partitionPos, ChunkVoxelData chunkVoxelData)
            {
                ColliderMesh = colliderMesh;
                PartitionPos = partitionPos;
                ChunkVoxelData = chunkVoxelData;
                ChunkPos = partitionPos.xz;
                CMeshBuffer = new CMeshBuffer
                {
                    CVertexBuffer = new NativeList<CVertex>(VoxelCount4, Allocator.Temp),
                    CIndexBuffer = new NativeList<ushort>(VoxelCount6, Allocator.Temp)
                };

                CollisionVoxels = new NativeHashSet<int3>(VoxelsPerPartition, Allocator.Temp);

                CollisionVertexCount = 0;
            }

            public void Dispose()
            {
                CMeshBuffer.Dispose();
                CollisionVoxels.Dispose();
            }
        }

        [BurstCompile]
        private struct AxisInfo
        {
            public int UAxis, VAxis, ULimit, VLimit;
        }

        [BurstCompile]
        private struct VQuad
        {
            public float3 V1, V2, V3, V4;
        }

        private interface IMaskComparable<T>
        {
            bool CompareTo(T other);
        }

        [BurstCompile]
        private readonly struct CMask : IMaskComparable<CMask>
        {
            internal readonly sbyte Normal;

            public CMask(sbyte normal)
            {
                Normal = normal;
            }

            public bool CompareTo(CMask other)
            {
                return Normal == other.Normal;
            }
        }

        #endregion

        #region Helpers

        [BurstCompile]
        private ushort GetVoxel(ref PartitionJobData jobData, in int3 voxelPos)
        {
            int3 chunkLocalPos = voxelPos;
            chunkLocalPos += jobData.PartitionPos * VoxelsPerPartition;
            return ChunkAccessor.InChunkBounds(chunkLocalPos)
                ? jobData.ChunkVoxelData.GetVoxel(chunkLocalPos)
                : Accessor.GetVoxelInPartition(jobData.PartitionPos, voxelPos);
        }
        
        [BurstCompile]
        private void CreateColliderQuad(ref PartitionJobData jobData, CMask mask, int3 directionMask, in VQuad verts)
        {
            float3 normal = directionMask * mask.Normal;

            AddColliderVertices(ref jobData.CMeshBuffer, in verts, normal);

            int baseVertexIndex = jobData.CollisionVertexCount;
            ref CMeshBuffer cMeshBuffer = ref jobData.CMeshBuffer;

            cMeshBuffer.AddCIndex(baseVertexIndex + 1);
            cMeshBuffer.AddCIndex(baseVertexIndex + 1 + mask.Normal);
            cMeshBuffer.AddCIndex(baseVertexIndex + 1 - mask.Normal);

            cMeshBuffer.AddCIndex(baseVertexIndex + 2);
            cMeshBuffer.AddCIndex(baseVertexIndex + 2 - mask.Normal);
            cMeshBuffer.AddCIndex(baseVertexIndex + 2 + mask.Normal);

            jobData.CollisionVertexCount += 4;
        }

        [BurstCompile]
        private void AddColliderVertices(ref CMeshBuffer cMesh, in VQuad verts, float3 normal)
        {
            CVertex vertex1 = new(verts.V1, normal);
            CVertex vertex2 = new(verts.V2, normal);
            CVertex vertex3 = new(verts.V3, normal);
            CVertex vertex4 = new(verts.V4, normal);

            cMesh.AddCVertex(vertex1);
            cMesh.AddCVertex(vertex2);
            cMesh.AddCVertex(vertex3);
            cMesh.AddCVertex(vertex4);
        }

        #endregion
    }
}