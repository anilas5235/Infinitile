using Engine.Scripts.VoxelConfig.Data;
using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;

namespace Engine.Scripts.Jobs.ColliderMeshing
{
    internal partial struct CMeshBuildJob
    {
        [BurstCompile]
        private bool BuildColliderMasks(ref PartitionJobData jobData, int3 posItr, int3 dirMask, AxisInfo axInfo,
            ref NativeArray<CMask> posNormalMask, ref NativeArray<CMask> negNormalMask)
        {
            int n = 0;
            bool hasCollision = false;

            for (posItr[axInfo.VAxis] = 0; posItr[axInfo.VAxis] < axInfo.VLimit; ++posItr[axInfo.VAxis])
            for (posItr[axInfo.UAxis] = 0; posItr[axInfo.UAxis] < axInfo.ULimit; ++posItr[axInfo.UAxis])
            {
                posNormalMask[n] = default;
                negNormalMask[n] = default;

                if (!jobData.CollisionVoxels.Contains(posItr))
                {
                    n++;
                    continue;
                }

                hasCollision |= TryAddCollisionMask(jobData, dirMask, posItr, n, posNormalMask, true);
                hasCollision |= TryAddCollisionMask(jobData, dirMask, posItr, n, negNormalMask, false);
                n++;
            }

            return hasCollision;
        }

        [BurstCompile]
        private bool TryAddCollisionMask(PartitionJobData jobData, int3 dirMask, int3 pos, int n,
            NativeArray<CMask> cMask, bool posNormal)
        {
            int3 neighborCoord = pos + dirMask * (posNormal ? 1 : -1);

            ushort neighborVoxel = GetVoxel(ref jobData, neighborCoord);

            VoxelRenderDef neighborDef = RenderGenData.GetRenderDef(neighborVoxel);

            if (neighborDef.Collision)
            {
                cMask[n] = default;
                return false;
            }

            cMask[n] = new CMask(posNormal ? (sbyte)1 : (sbyte)-1);
            return true;
        }


        [BurstCompile]
        private int FindColQuadWidth(NativeArray<CMask> cMasks, int n, CMask currentMask, int start, int max)
        {
            int width;
            for (width = 1; start + width < max && cMasks[n + width].CompareTo(currentMask); width++)
            {
            }

            return width;
        }

        [BurstCompile]
        private int FindColQuadHeight(NativeArray<CMask> cMasks, int n, CMask currentMask, int axis1Limit,
            int axis2Limit, int width, int j)
        {
            int height;
            bool done = false;
            for (height = 1; j + height < axis2Limit; height++)
            {
                for (int k = 0; k < width; ++k)
                {
                    if (cMasks[n + k + height * axis1Limit].CompareTo(currentMask)) continue;
                    done = true;
                    break;
                }

                if (done) break;
            }

            return height;
        }
        
        [BurstCompile]
        private void ClearColMaskRegion(NativeArray<CMask> normalMask, int n, int width, int height, int axis1Limit)
        {
            for (int l = 0; l < height; ++l)
            for (int k = 0; k < width; ++k)
                normalMask[n + k + l * axis1Limit] = default;
        }
    }
}