using Engine.Scripts.Utils.Extensions;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using static Engine.Scripts.Utils.VoxelConstants;

namespace Engine.Scripts.Jobs.ColliderMeshing{
    internal partial struct CMeshBuildJob{
        private static readonly Bounds Bounds = new((PartitionSize / (int3)2).GetVector3(), PartitionSize.GetVector3());

        private void WriteResults(int index, ref PartitionJobData jobData){
            FillColliderMeshData(in jobData);

            Results.TryAdd(
                jobData.PartitionPos,
                new PartitionJobResult{
                    Index = index,
                    PartitionPos = jobData.PartitionPos,
                    ColliderBounds = Bounds
                }
            );
        }

        private void FillColliderMeshData(in PartitionJobData jobData){
            CMeshBuffer cMeshBuffer = jobData.CMeshBuffer;
            Mesh.MeshData colliderMesh = jobData.ColliderMesh;

            int cVertexCount = cMeshBuffer.CVertexBuffer.Length;
            colliderMesh.SetVertexBufferParams(cVertexCount, ColliderVertexParams);
            colliderMesh.GetVertexData<CVertex>().CopyFrom(cMeshBuffer.CVertexBuffer.AsArray());

            int cIndexCount = cMeshBuffer.CIndexBuffer.Length;
            // 16-bit indices wrap above 65536 vertices (a checkerboard partition has 393216); such meshes
            // get 32-bit indices, smaller ones keep the compact 16-bit format.
            NativeArray<int> cIndices = cMeshBuffer.CIndexBuffer.AsArray();
            if (cVertexCount > ushort.MaxValue + 1){
                colliderMesh.SetIndexBufferParams(cIndexCount, IndexFormat.UInt32);
                if (cIndexCount > 0)
                    NativeArray<uint>.Copy(cIndices.Reinterpret<uint>(), 0, colliderMesh.GetIndexData<uint>(), 0, cIndexCount);
            }
            else{
                colliderMesh.SetIndexBufferParams(cIndexCount, IndexFormat.UInt16);
                NativeArray<ushort> cIndexBuffer = colliderMesh.GetIndexData<ushort>();
                for (int i = 0; i < cIndexCount; i++) cIndexBuffer[i] = (ushort)cIndices[i];
            }

            colliderMesh.subMeshCount = 1;
            SubMeshDescriptor cDesc = new(0, cIndexCount);
            colliderMesh.SetSubMesh(0, cDesc, MeshFlags);
        }
    }
}
