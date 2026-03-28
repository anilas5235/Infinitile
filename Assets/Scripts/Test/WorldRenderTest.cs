using System.Collections;
using System.Collections.Generic;
using Runtime.Engine.Behaviour;
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
        [SerializeField] private VoxelWorldRenderer vWRenderer;
        private UnsafeIntervalList<ushort> _voxels;
        private UnsafeIntervalList<ushort> _voxels2;

        private async void Start()
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
            await vWRenderer.UpdatePartitions(new HashSet<int3>
            {
                new(0, 0, 0),
                new(0, 0, 1),
                new(1, 0, 1),
                new(1, 0, 0),
            });

            _voxels2 = new UnsafeIntervalList<ushort>(1, Allocator.Domain);
            _voxels2.AddInterval(1, VoxelsPerChunk);

            StartCoroutine(AddStoneChunks());
        }

        private IEnumerator AddStoneChunks()
        {
            for (int z = 0; z < 16; z++)
            for (int x = 0; x < 16; x++)
            {
                Awaitable<bool> handle = ChunkAddAndUpdate(new int2(2 + x, z), _voxels2);
                while (!handle.GetAwaiter().IsCompleted)
                {
                    yield return null;
                }
            }
        }

        private async Awaitable<bool> ChunkAddAndUpdate(int2 chunkPos, UnsafeIntervalList<ushort> voxels)
        {
            vWRenderer.AddOrUpdateChunk(chunkPos, voxels);

            HashSet<int3> partitionsToUpdate = new();

            for (int y = 0; y < PartitionsPerChunk; y++)
            {
                partitionsToUpdate.Add(new int3(chunkPos.x, y, chunkPos.y));
            }

            await vWRenderer.UpdatePartitions(partitionsToUpdate);
            return true;
        }

        private void OnDestroy()
        {
            _voxels.Dispose();
            _voxels2.Dispose();
        }
    }
}