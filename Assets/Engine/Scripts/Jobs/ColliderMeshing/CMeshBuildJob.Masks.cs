using Engine.Scripts.Data;
using Engine.Scripts.Utils.Collections;
using Engine.Scripts.Utils.Extensions;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using static Engine.Scripts.Utils.VoxelConstants;

namespace Engine.Scripts.Jobs.ColliderMeshing{
    internal partial struct CMeshBuildJob{
        // Collision mask: the partition plus a 1-voxel border in x, y and z, one byte per voxel (1 = collision),
        // in the chunk's Flatten order (x, z, y) so a column is a contiguous run (spec 20.3). The border is read
        // only along the face axes, so the four corner columns stay 0.
        private const int MaskWidth = PartitionWidth + 2;
        private const int MaskHeight = PartitionHeight + 2;
        private const int MaskDepth = PartitionDepth + 2;
        private const int MaskVolume = MaskWidth * MaskHeight * MaskDepth;

        private const int MaskStrideX = MaskDepth * MaskHeight;
        private const int MaskStrideZ = MaskHeight;
        private const int MaskStrideY = 1;

        /// <summary>Mask index of a partition-local position; each coordinate may be -1 or the partition size.</summary>
        private static int MaskIndex(int x, int y, int z){
            return (x + 1) * MaskStrideX + (z + 1) * MaskStrideZ + y + 1;
        }

        private static int MaskStride(int axis){
            return axis switch{
                0 => MaskStrideX,
                1 => MaskStrideY,
                _ => MaskStrideZ
            };
        }

        /// <summary>
        ///     Fills <see cref="PartitionJobData.CollisionMask" /> from the interval lists of the partition's chunk and
        ///     its four face neighbours, walking the runs of each column instead of searching per voxel. Missing
        ///     chunks and positions outside the world height read as no collision. Returns false, before reading
        ///     the neighbours, when the partition itself has no collision voxel.
        /// </summary>
        private bool FillCollisionMask(ref PartitionJobData jobData){
            // Partitions span the whole chunk in x and z, so the border columns lie in the face neighbours
            const int last = PartitionWidth - 1;
            if (!FillColumns(ref jobData, int2.zero, int2.zero, new int2(last, PartitionDepth - 1))) return false;

            FillColumns(ref jobData, new int2(-1, 0), new int2(-1, 0), new int2(-1, PartitionDepth - 1));
            FillColumns(ref jobData, new int2(1, 0), new int2(PartitionWidth, 0),
                new int2(PartitionWidth, PartitionDepth - 1));
            FillColumns(ref jobData, new int2(0, -1), new int2(0, -1), new int2(last, -1));
            FillColumns(ref jobData, new int2(0, 1), new int2(0, PartitionDepth), new int2(last, PartitionDepth));
            return true;
        }

        /// <summary>
        ///     Fills the mask columns <paramref name="min" />..<paramref name="max" /> (partition-local x, z) from the
        ///     chunk at <paramref name="chunkOffset" /> from the partition's chunk. Returns true if a collision voxel
        ///     lies inside the partition's own y range.
        /// </summary>
        private unsafe bool FillColumns(ref PartitionJobData jobData, int2 chunkOffset, int2 min, int2 max){
            if (!Accessor.TryGetChunk(jobData.ChunkPos + chunkOffset, out ChunkVoxelData chunk)) return false;

            UnsafeIntervalList<ushort> voxels = chunk.GetData();
            byte* mask = (byte*)jobData.CollisionMask.GetUnsafePtr();
            int2 toChunkLocal = -chunkOffset * new int2(ChunkWidth, ChunkDepth);
            int baseY = jobData.PartitionPos.y * PartitionHeight;
            // World y range of the mask column (partition plus border), clipped to the world height
            int yMin = math.max(baseY - 1, 0);
            int yMax = math.min(baseY + PartitionHeight + 1, ChunkHeight);
            bool interior = false;

            for (int x = min.x; x <= max.x; x++)
            for (int z = min.y; z <= max.y; z++){
                int columnStart = ChunkSize.Flatten(x + toChunkLocal.x, 0, z + toChunkLocal.y);
                // Mask index of world y 0 in this column, so a chunk index i maps to i - columnStart + maskY0
                int maskY0 = MaskIndex(x, -baseY, z);
                int end = columnStart + yMax;
                int i = columnStart + yMin;
                int nodeIndex = voxels.NodeIndex(i);

                while (i < end){
                    UnsafeIntervalList<ushort>.Node node = voxels.NodeAt(nodeIndex++);
                    int runEnd = math.min(node.Count, end);
                    if (RenderGenData.VoxelRenderDefs[node.Value].Collision){
                        UnsafeUtility.MemSet(mask + i - columnStart + maskY0, 1, runEnd - i);
                        interior |= i - columnStart < baseY + PartitionHeight && runEnd - columnStart > baseY;
                    }

                    i = runEnd;
                }
            }

            return interior;
        }

        /// <summary>
        ///     Face masks of one slice: a collision voxel gets a positive (negative) face where its neighbour along
        ///     the main axis has no collision. Returns true if the slice has any face.
        /// </summary>
        private unsafe bool BuildColliderMasks(ref PartitionJobData jobData, int slice, AxisInfo axInfo,
            ref NativeArray<CMask> posNormalMask, ref NativeArray<CMask> negNormalMask){
            byte* mask = (byte*)jobData.CollisionMask.GetUnsafeReadOnlyPtr();
            int sliceBase = MaskIndex(0, 0, 0) + slice * axInfo.MainStride;
            int n = 0;
            bool hasCollision = false;

            for (int v = 0; v < axInfo.VLimit; v++)
            for (int u = 0; u < axInfo.ULimit; u++){
                int i = sliceBase + v * axInfo.VStride + u * axInfo.UStride;
                sbyte pos = 0;
                sbyte neg = 0;
                if (mask[i] != 0){
                    if (mask[i + axInfo.MainStride] == 0) pos = 1;
                    if (mask[i - axInfo.MainStride] == 0) neg = -1;
                }

                posNormalMask[n] = new CMask(pos);
                negNormalMask[n] = new CMask(neg);
                hasCollision |= (pos | neg) != 0;
                n++;
            }

            return hasCollision;
        }

        [BurstCompile]
        private int FindColQuadWidth(NativeArray<CMask> cMasks, int n, CMask currentMask, int start, int max){
            int width;
            for (width = 1; start + width < max && cMasks[n + width].CompareTo(currentMask); width++){ }

            return width;
        }

        [BurstCompile]
        private int FindColQuadHeight(NativeArray<CMask> cMasks, int n, CMask currentMask, int axis1Limit,
            int axis2Limit, int width, int j){
            int height;
            bool done = false;
            for (height = 1; j + height < axis2Limit; height++){
                for (int k = 0; k < width; ++k){
                    if (cMasks[n + k + height * axis1Limit].CompareTo(currentMask)) continue;
                    done = true;
                    break;
                }

                if (done) break;
            }

            return height;
        }

        [BurstCompile]
        private void ClearColMaskRegion(NativeArray<CMask> normalMask, int n, int width, int height, int axis1Limit){
            for (int l = 0; l < height; ++l)
            for (int k = 0; k < width; ++k)
                normalMask[n + k + l * axis1Limit] = default;
        }
    }
}
