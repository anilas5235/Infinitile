using System.Runtime.CompilerServices;
using Engine.Scripts.Utils.Extensions;
using Engine.Scripts.WorldGen.Caves;
using Engine.Scripts.WorldGen.Core;
using Engine.Scripts.WorldGen.Settings;
using Unity.Collections;
using Unity.Mathematics;
using static Engine.Scripts.Utils.VoxelConstants;

namespace Engine.Scripts.Jobs.Chunk{
    /// <summary>
    ///     The 3D passes after the terrain fill: they carve or replace voxels below the surface and never touch the
    ///     column data, so the surface, the biome and the flora decisions stay those of the uncarved column. Every
    ///     pass reads only world coordinates, the seed and the config, so a voxel is the same in every chunk, slice
    ///     or volume that contains it. Each pass is its own non-inlined method.
    /// </summary>
    internal partial struct ChunkJob{
        /// <summary>
        ///     Bedrock floor: all of y 0, and y 1 and y 2 where a 3D hash falls below their share, so the floor is
        ///     ragged rather than flat (like the bedrock layer of Minecraft's terrain generator). Runs last among the
        ///     cave passes, so nothing carves it.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void ApplyBottomLayer(NativeArray<ushort> vox, int3 origin, in WorldGenConfig cfg){
            ref readonly CaveConfig caves = ref cfg.Caves;
            ushort bedrock = caves.BedrockVoxel;
            if (!caves.Enabled || bedrock == 0) return;

            for (int x = 0; x < ChunkWidth; x++)
            for (int z = 0; z < ChunkDepth; z++){
                int wx = origin.x + x, wz = origin.z + z;
                vox[ChunkSize.Flatten(x, 0, z)] = bedrock;
                if (Hash.Hash01(wx, 1, wz, cfg.Seed, HashSalts.BedrockLayer1) < caves.BedrockChance1)
                    vox[ChunkSize.Flatten(x, 1, z)] = bedrock;
                if (Hash.Hash01(wx, 2, wz, cfg.Seed, HashSalts.BedrockLayer2) < caves.BedrockChance2)
                    vox[ChunkSize.Flatten(x, 2, z)] = bedrock;
            }
        }
    }
}
