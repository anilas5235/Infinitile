using Engine.Scripts.Utils.Extensions;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using static Engine.Scripts.Utils.VoxelConstants;

namespace Engine.Scripts.Jobs.Meshing
{
    internal partial struct MeshBuildJob
    {
        private static readonly Bounds Bounds = new((PartitionSize / (int3)2).GetVector3(), PartitionSize.GetVector3());

        private void WriteResults(int index, ref PartitionJobData jobData)
        {
            FillColliderMeshData(in jobData);

            Results.TryAdd(
                jobData.PartitionPos,
                new PartitionJobResult
                {
                    Index = index,
                    PartitionPos = jobData.PartitionPos,
                    ColliderBounds = Bounds
                }
            );
        }

        private void FillColliderMeshData(in PartitionJobData jobData)
        {
            MeshBuffer meshBuffer = jobData.MeshBuffer;
            Mesh.MeshData colliderMesh = jobData.ColliderMesh;

            int cVertexCount = meshBuffer.CVertexBuffer.Length;
            colliderMesh.SetVertexBufferParams(cVertexCount, ColliderVertexParams);
            colliderMesh.GetVertexData<CVertex>().CopyFrom(meshBuffer.CVertexBuffer.AsArray());

            int cIndexCount = meshBuffer.CIndexBuffer.Length;
            colliderMesh.SetIndexBufferParams(cIndexCount, IndexFormat.UInt16);
            NativeArray<ushort> cIndexBuffer = colliderMesh.GetIndexData<ushort>();
            if (cIndexCount > 0)
                NativeArray<ushort>.Copy(meshBuffer.CIndexBuffer.AsArray(), 0, cIndexBuffer, 0, cIndexCount);

            colliderMesh.subMeshCount = 1;
            SubMeshDescriptor cDesc = new(0, cIndexCount);
            colliderMesh.SetSubMesh(0, cDesc, MeshFlags);
        }
    }
}