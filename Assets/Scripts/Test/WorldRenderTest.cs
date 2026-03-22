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
        private UnsafeIntervalList<ushort> _voxels2;

        private void Start()
        {
            _voxels = new UnsafeIntervalList<ushort>(10, Allocator.Domain);
            _voxels.AddInterval(0, VoxelsPerChunk);
            for (int j = 0; j < 64; j++)
            {
                int index = ChunkSize.Flatten(new int3((j % 16) * 2, 0, 2 * (int)math.floor(j / 16.0f)));
                _voxels.Set(index, (ushort)j);
            }
            vWRenderer.AddOrUpdateChunk(new int2(0, 0), _voxels);
            vWRenderer.AddOrUpdateChunk(new int2(0, 1), _voxels);
            vWRenderer.AddOrUpdateChunk(new int2(1, 1), _voxels);
            vWRenderer.AddOrUpdateChunk(new int2(1, 0), _voxels);
            vWRenderer.UpdatePartitions(new HashSet<int3>
            {
                new(0,0,0),
                new(0,0,1),
                new(1,0,1),
                new(1,0,0),
            });
            
            _voxels2 = new UnsafeIntervalList<ushort>(1, Allocator.Domain);
            _voxels2.AddInterval(1, VoxelsPerChunk);
            
            vWRenderer.AddOrUpdateChunk(new int2(2, 0), _voxels2);
            
            HashSet<int3> partitionsToUpdate = new();

            for (int i = 0; i < PartitionsPerChunk; i++)
            {
                partitionsToUpdate.Add(new int3(2, i, 0));
            }
            
            vWRenderer.UpdatePartitions(partitionsToUpdate);
        }

        private void OnDestroy()
        {
            _voxels.Dispose();
            _voxels2.Dispose();
        }
    }
}