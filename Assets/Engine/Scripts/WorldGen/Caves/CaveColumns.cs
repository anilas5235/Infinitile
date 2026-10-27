using Engine.Scripts.WorldGen.Blending;
using Engine.Scripts.WorldGen.Core;
using Engine.Scripts.WorldGen.Settings;
using Unity.Mathematics;
using static Engine.Scripts.Utils.VoxelConstants;

namespace Engine.Scripts.WorldGen.Caves{
    /// <summary>Highest y the halls and the worms may carve in one column; below <see cref="CaveColumns.Floor" /> nothing is.</summary>
    public struct CaveLimits{
        public int Cavern;
        public int Worm;
    }

    /// <summary>
    ///     How close to the surface the cave passes may carve in a column, a pure function of the column's height.
    ///     Caverns (and the lava pockets with them) keep <c>Cover</c> blocks of rock; worms may carve up to the top
    ///     block, so a worm that wanders up breaks the surface, under water as well. No pass carves above the top
    ///     block, and water and ice lie only above it, so no water voxel is ever carved; water does not flow, so a
    ///     lake whose bed a worm opens hangs over the cave.
    /// </summary>
    public static class CaveColumns{
        /// <summary>The bottom layer: no pass carves y ≤ 2.</summary>
        public const int Floor = 2;

        /// <summary>Top block y of column (x, z), as <c>ChunkColumn.Height</c>: the sampled height, floored and clamped.</summary>
        public static int SurfaceY(int x, int z, in WorldGenConfig cfg, in BlendCache cache){
            WorldSampler.SampleHeight(x, z, cfg, cache, out float height);
            return math.clamp((int)math.floor(height), 0, MaxTerrainHeight);
        }

        /// <summary>
        ///     <see cref="SurfaceY" /> from the unblended height (<see cref="WorldSampler.SampleUnblendedHeight" />):
        ///     the same in Terrain-first, the hard biome's height in Biome-first.
        /// </summary>
        public static int UnblendedSurfaceY(int x, int z, in WorldGenConfig cfg){
            WorldSampler.SampleUnblendedHeight(x, z, cfg, out float height);
            return math.clamp((int)math.floor(height), 0, MaxTerrainHeight);
        }

        /// <summary>The voxel above top block <paramref name="height" /> is water.</summary>
        public static bool Submerged(int height, in WorldGenConfig cfg){
            return height + 1 < cfg.WaterLevel;
        }

        /// <summary>
        ///     The dry column (x, z) with top block <paramref name="height" /> lies in the entrance mask, where worms
        ///     start near the surface with a shaft (<see cref="Worms.Anchor" />).
        /// </summary>
        public static bool InEntranceMask(int x, int z, int height, in WorldGenConfig cfg){
            return !Submerged(height, cfg) && CaveMasks.Entrance(x, z, cfg) > cfg.Caves.EntranceThreshold;
        }

        /// <summary>Limits of a column with top block <paramref name="height" />.</summary>
        public static CaveLimits Limits(int height, in WorldGenConfig cfg){
            return new CaveLimits{ Cavern = height - cfg.Caves.Cover, Worm = height };
        }

        /// <summary>Limits of column (x, z), sampling its height.</summary>
        public static CaveLimits Limits(int x, int z, in WorldGenConfig cfg, in BlendCache cache){
            return Limits(SurfaceY(x, z, cfg, cache), cfg);
        }
    }
}
