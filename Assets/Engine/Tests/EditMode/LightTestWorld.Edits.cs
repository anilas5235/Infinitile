using Engine.Scripts.Data;
using Engine.Scripts.Jobs.Light;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using static Engine.Scripts.Utils.VoxelConstants;

namespace Engine.Tests.EditMode{
    /// <summary>Edits with the incremental <see cref="LightEditJob" />.</summary>
    internal sealed partial class LightTestWorld{
        /// <summary>
        ///     Edits one voxel and updates <paramref name="lights" /> (must hold the 3 × 3 chunks around the edit)
        ///     incrementally. Returns the changed-partition flags (caller disposes).
        /// </summary>
        internal NativeArray<byte> EditAndRelight(int3 world, ushort id, NativeParallelHashMap<int2, ChunkLightData> lights){
            int2 center = ChunkOf(world);
            ushort old = Set(world, id);
            NativeArray<ChunkVoxelData> voxels = new(LightEditJob.ChunkCount, Allocator.TempJob);
            NativeArray<ChunkLightData> light = new(LightEditJob.ChunkCount, Allocator.TempJob);
            NativeArray<byte> changed = new(LightEditJob.ChunkCount * PartitionsPerChunk, Allocator.TempJob);

            for (int dx = -1; dx <= 1; dx++)
            for (int dz = -1; dz <= 1; dz++){
                int2 offset = new(dx, dz);
                int k = LightEditJob.ChunkIndex(offset);
                voxels[k] = Chunks[center + offset];
                light[k] = lights[center + offset];
            }

            new LightEditJob{
                Table = Table,
                Voxels = voxels,
                Lights = light,
                CenterChunk = center,
                Position = world,
                OldVoxel = old,
                ChangedPartitions = changed
            }.Run();

            for (int dx = -1; dx <= 1; dx++)
            for (int dz = -1; dz <= 1; dz++){
                int2 offset = new(dx, dz);
                lights[center + offset] = light[LightEditJob.ChunkIndex(offset)];
            }

            voxels.Dispose();
            light.Dispose();
            return changed;
        }
    }
}
