using Unity.Burst;
using Unity.Mathematics;

namespace Engine.Scripts.Jobs.Meshing
{
    internal partial struct MeshBuildJob
    {
        [BurstCompile(FloatMode = FloatMode.Fast, FloatPrecision = FloatPrecision.Low, CompileSynchronously = true)]
        private void CreateColliderQuad(ref PartitionJobData jobData, CMask mask, int3 directionMask, in VQuad verts)
        {
            float3 normal = directionMask * mask.Normal;

            AddColliderVertices(ref jobData.MeshBuffer, in verts, normal);

            int baseVertexIndex = jobData.CollisionVertexCount;
            ref MeshBuffer meshBuffer = ref jobData.MeshBuffer;

            meshBuffer.AddCIndex(baseVertexIndex + 1);
            meshBuffer.AddCIndex(baseVertexIndex + 1 + mask.Normal);
            meshBuffer.AddCIndex(baseVertexIndex + 1 - mask.Normal);

            meshBuffer.AddCIndex(baseVertexIndex + 2);
            meshBuffer.AddCIndex(baseVertexIndex + 2 - mask.Normal);
            meshBuffer.AddCIndex(baseVertexIndex + 2 + mask.Normal);

            jobData.CollisionVertexCount += 4;
        }

        [BurstCompile(FloatMode = FloatMode.Fast, FloatPrecision = FloatPrecision.Low, CompileSynchronously = true)]
        private void AddColliderVertices(ref MeshBuffer mesh, in VQuad verts, float3 normal)
        {
            CVertex vertex1 = new(verts.V1, normal);
            CVertex vertex2 = new(verts.V2, normal);
            CVertex vertex3 = new(verts.V3, normal);
            CVertex vertex4 = new(verts.V4, normal);

            mesh.AddCVertex(vertex1);
            mesh.AddCVertex(vertex2);
            mesh.AddCVertex(vertex3);
            mesh.AddCVertex(vertex4);
        }
    }
}