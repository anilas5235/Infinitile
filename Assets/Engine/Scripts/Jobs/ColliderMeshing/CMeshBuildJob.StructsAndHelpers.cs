using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using static Engine.Scripts.Utils.VoxelConstants;

namespace Engine.Scripts.Jobs.ColliderMeshing{
    internal partial struct CMeshBuildJob{
        #region Constants

        // Start sizes of the vertex and index lists; they grow when a partition needs more (a surface partition
        // has a few hundred quads, a checkerboard 98,304).
        private const int InitialVertexCapacity = 1024;
        private const int InitialIndexCapacity = InitialVertexCapacity / 4 * 6;

        #endregion

        #region Structs

        [BurstCompile]
        internal struct PartitionJobResult{
            public int Index;
            public int3 PartitionPos;
            public Bounds ColliderBounds;
        }

        [BurstCompile]
        private struct PartitionJobData : IDisposable{
            public readonly Mesh.MeshData ColliderMesh;
            public readonly int2 ChunkPos;
            public readonly int3 PartitionPos;

            public CMeshBuffer CMeshBuffer;

            /// <summary>Collision flag per voxel of the partition plus a 1-voxel border, see <see cref="MaskIndex" />.</summary>
            public NativeArray<byte> CollisionMask;

            public int CollisionVertexCount;

            internal PartitionJobData(Mesh.MeshData colliderMesh, int3 partitionPos){
                ColliderMesh = colliderMesh;
                PartitionPos = partitionPos;
                ChunkPos = partitionPos.xz;
                CMeshBuffer = new CMeshBuffer{
                    CVertexBuffer = new NativeList<CVertex>(InitialVertexCapacity, Allocator.Temp),
                    CIndexBuffer = new NativeList<int>(InitialIndexCapacity, Allocator.Temp)
                };

                CollisionMask = new NativeArray<byte>(MaskVolume, Allocator.Temp);

                CollisionVertexCount = 0;
            }

            public void Dispose(){
                CMeshBuffer.Dispose();
                CollisionMask.Dispose();
            }
        }

        [BurstCompile]
        private struct AxisInfo{
            public int UAxis, VAxis, ULimit, VLimit;

            /// <summary>Index steps in <see cref="PartitionJobData.CollisionMask" /> along the main, U and V axes.</summary>
            public int MainStride, UStride, VStride;
        }

        [BurstCompile]
        private struct VQuad{
            public float3 V1, V2, V3, V4;
        }

        private interface IMaskComparable<T>{
            bool CompareTo(T other);
        }

        [BurstCompile]
        private readonly struct CMask : IMaskComparable<CMask>{
            internal readonly sbyte Normal;

            public CMask(sbyte normal){
                Normal = normal;
            }

            public bool CompareTo(CMask other){
                return Normal == other.Normal;
            }
        }

        #endregion

        #region Helpers

        [BurstCompile]
        private void CreateColliderQuad(ref PartitionJobData jobData, CMask mask, in VQuad verts){
            AddColliderVertices(ref jobData.CMeshBuffer, in verts);

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
        private void AddColliderVertices(ref CMeshBuffer cMesh, in VQuad verts){
            CVertex vertex1 = new(verts.V1);
            CVertex vertex2 = new(verts.V2);
            CVertex vertex3 = new(verts.V3);
            CVertex vertex4 = new(verts.V4);

            cMesh.AddCVertex(vertex1);
            cMesh.AddCVertex(vertex2);
            cMesh.AddCVertex(vertex3);
            cMesh.AddCVertex(vertex4);
        }

        #endregion
    }
}
