using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;
using static Engine.Scripts.Utils.VoxelConstants;

namespace Engine.Scripts.Jobs.Meshing
{
    /// <summary>
    ///     Burst-optimized greedy mesher for voxel chunks. Merges contiguous faces into maximal rectangles
    ///     to reduce vertex/index count. Produces render and collider data plus foliage (billboard) quads.
    /// </summary>
    internal partial struct MeshBuildJob
    {
        private void MeshCollision(ref PartitionJobData jobData)
        {
            if (jobData.HasNoCollision) return;
            // Sweep along each principal axis (X, Y, Z)
            ColliderSliceMeshBuild(ref jobData);
        }

        private void ColliderSliceMeshBuild(ref PartitionJobData jobData)
        {
            // Sweep along each principal axis (X, Y, Z)
            for (int mainAxis = 0; mainAxis < 3; mainAxis++)
            {
                // Define orthogonal axes for the 2D slice (U and V plane)
                int uAxis = (mainAxis + 1) % 3;
                int vAxis = (mainAxis + 2) % 3;

                int mainAxisLimit = PartitionSize[mainAxis];

                AxisInfo axisInfo = new()
                {
                    UAxis = uAxis,
                    VAxis = vAxis,
                    ULimit = PartitionSize[uAxis],
                    VLimit = PartitionSize[vAxis]
                };

                int3 pos = int3.zero;

                int3 directionMask = int3.zero;
                directionMask[mainAxis] = 1;

                BuildColliderSlice(ref jobData, pos, mainAxis, mainAxisLimit, directionMask, axisInfo);
            }
        }


        private void BuildColliderSlice(ref PartitionJobData jobData, int3 pos, int mainAxis, int mainAxisLimit,
            int3 directionMask, AxisInfo axisInfo)
        {
            // Temporary mask buffer for the current slice (U x V)
            int uvGridSize = axisInfo.ULimit * axisInfo.VLimit;
            NativeArray<CMask> posColMask = new(uvGridSize, Allocator.Temp);
            NativeArray<CMask> negColMask = new(uvGridSize, Allocator.Temp);

            for (pos[mainAxis] = 0; pos[mainAxis] < mainAxisLimit;)
            {
                bool info = BuildColliderMasks(ref jobData, pos, directionMask, axisInfo, ref posColMask,
                    ref negColMask);

                // Move to the actual slice index we just built the mask for
                ++pos[mainAxis];

                if (!info) continue;

                BuildColliderQuads(ref jobData, axisInfo, pos, posColMask, directionMask);
                BuildColliderQuads(ref jobData, axisInfo, pos - directionMask, negColMask, -directionMask);
            }

            posColMask.Dispose();
            negColMask.Dispose();
        }


        /// <summary>
        ///     Builds collider quads for one slice using greedy merging.
        /// </summary>
        [BurstCompile(FloatMode = FloatMode.Fast, FloatPrecision = FloatPrecision.Low, CompileSynchronously = true)]
        private void BuildColliderQuads(ref PartitionJobData jobData, AxisInfo axInfo, int3 pos,
            NativeArray<CMask> colliderMask, int3 directionMask)
        {
            int3 uDelta = int3.zero;
            int3 vDelta = int3.zero;

            // Greedy merge over the collider mask
            int maskIndex = 0;
            for (int v = 0; v < axInfo.VLimit; v++)
            for (int u = 0; u < axInfo.ULimit;)
                if (colliderMask[maskIndex].Normal != 0)
                {
                    CMask current = colliderMask[maskIndex];
                    pos[axInfo.UAxis] = u;
                    pos[axInfo.VAxis] = v;

                    int quadWidth = FindColQuadWidth(colliderMask, maskIndex, current, u, axInfo.ULimit);
                    int quadHeight = FindColQuadHeight(colliderMask, maskIndex, current, axInfo.ULimit,
                        axInfo.VLimit,
                        quadWidth, v);

                    uDelta[axInfo.UAxis] = quadWidth;
                    vDelta[axInfo.VAxis] = quadHeight;

                    VQuad quadVerts = new()
                    {
                        V1 = pos,
                        V2 = pos + uDelta,
                        V3 = pos + vDelta,
                        V4 = pos + uDelta + vDelta
                    };

                    CreateColliderQuad(
                        ref jobData,
                        current,
                        directionMask,
                        in quadVerts
                    );

                    ClearColMaskRegion(colliderMask, maskIndex, quadWidth, quadHeight, axInfo.ULimit);
                    uDelta = int3.zero;
                    vDelta = int3.zero;

                    u += quadWidth;
                    maskIndex += quadWidth;
                }
                else
                {
                    u++;
                    maskIndex++;
                }
        }
    }
}