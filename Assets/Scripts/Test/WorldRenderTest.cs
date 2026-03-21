using System;
using System.Collections.Generic;
using Runtime.Engine.Utils.Collections;
using Runtime.Engine.Utils.Extensions;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using static Runtime.Engine.Utils.VoxelConstants;

namespace Test
{
    public class WorldRenderTest : MonoBehaviour
    {
        [SerializeField] private VoxelWorldRender vWRenderer;
        private UnsafeIntervalList<ushort> _voxels;

        private void Start()
        {
            _voxels = new UnsafeIntervalList<ushort>(10, Allocator.Domain);
            _voxels.AddInterval(0, VoxelsPerPartition);
            for (int j = 0; j < 64; j++)
            {
                int index = PartitionSize.Flatten(new int3((j % 16) * 2, 0, 2 * (int)math.floor(j / 16.0f)));
                _voxels.Set(index, (ushort)j);
            }
            vWRenderer.AddOrUpdateChunk(new int2(0, 0), _voxels);
            vWRenderer.UpdatePartitions(new List<int3>
            {
                new(0,0,0),
            });
        }
    }
}