using Engine.Scripts.Utils;
using Engine.Scripts.VoxelConfig.Data;
using Unity.Mathematics;

namespace Engine.Scripts.Jobs.Meshing
{
    internal partial struct MeshBuildJob
    {
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