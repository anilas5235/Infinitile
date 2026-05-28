using System;
using Engine.Scripts.Data;
using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using static Engine.Scripts.Utils.VoxelConstants;

namespace Engine.Scripts.Jobs.Meshing
{
    internal partial struct MeshBuildJob
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

            public MeshBuffer MeshBuffer;

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
                MeshBuffer = new MeshBuffer
                {
                    CVertexBuffer = new NativeList<CVertex>(VoxelCount4, Allocator.Temp),
                    CIndexBuffer = new NativeList<ushort>(VoxelCount6, Allocator.Temp)
                };

                CollisionVoxels = new NativeHashSet<int3>(VoxelsPerPartition, Allocator.Temp);

                CollisionVertexCount = 0;
            }

            public void Dispose()
            {
                MeshBuffer.Dispose();
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
        private void ClearColMaskRegion(NativeArray<CMask> normalMask, int n, int width, int height, int axis1Limit)
        {
            for (int l = 0; l < height; ++l)
            for (int k = 0; k < width; ++k)
                normalMask[n + k + l * axis1Limit] = default;
        }

        #endregion
    }
}