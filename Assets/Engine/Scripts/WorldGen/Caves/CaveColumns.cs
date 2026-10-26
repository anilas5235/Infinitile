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
    ///     How close to the surface the cave passes may carve in a column, a pure function of the column and the
    ///     terrain heights around it. Caverns, and worms outside an entrance, keep <c>Cover</c> blocks of rock. Next to
    ///     a submerged column every pass keeps <c>WaterCover</c> blocks below the lowest surface of the column's 3 × 3
    ///     neighbourhood, so no carved voxel touches a water voxel: water fills y &lt; WaterLevel only above the top
    ///     block of a submerged column, and a carved voxel lies at least one block below every neighbouring top block.
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
        ///     Worms may break the surface of the dry column (x, z) with top block <paramref name="height" />: it lies in
        ///     the entrance mask and no water probe lies within <c>EntranceWaterDistance</c>
        ///     (<see cref="SurfaceCover.NearWater(int,int,int,in WorldGenConfig,in BlendCache)" />).
        /// </summary>
        public static bool IsEntrance(int x, int z, int height, in WorldGenConfig cfg, in BlendCache cache){
            if (!InEntranceMask(x, z, height, cfg)) return false;
            int r = cfg.Caves.EntranceWaterDistance;
            return r <= 0 || !SurfaceCover.NearWater(x, z, r, cfg, cache);
        }

        /// <summary>
        ///     The dry column (x, z) with top block <paramref name="height" /> lies in the entrance mask; the water test
        ///     of <see cref="IsEntrance" /> left out.
        /// </summary>
        public static bool InEntranceMask(int x, int z, int height, in WorldGenConfig cfg){
            return !Submerged(height, cfg) && CaveMasks.Entrance(x, z, cfg) > cfg.Caves.EntranceThreshold;
        }

        /// <summary>
        ///     Limits of column (x, z) with top block <paramref name="height" />, the lowest top block
        ///     <paramref name="lowest" /> of its 3 × 3 neighbourhood and whether one of those columns is submerged.
        /// </summary>
        public static CaveLimits Limits(int x, int z, int height, int lowest, bool wet, in WorldGenConfig cfg,
            in BlendCache cache){
            ref readonly CaveConfig c = ref cfg.Caves;
            int covered = height - c.Cover;
            CaveLimits limits = new(){
                Cavern = covered,
                Worm = !wet && IsEntrance(x, z, height, cfg, cache) ? height : covered
            };
            if (!wet) return limits;
            int dry = lowest - c.WaterCover;
            limits.Cavern = math.min(limits.Cavern, dry);
            limits.Worm = math.min(limits.Worm, dry);
            return limits;
        }

        /// <summary>Limits of column (x, z), sampling the heights of its 3 × 3 neighbourhood.</summary>
        public static CaveLimits Limits(int x, int z, in WorldGenConfig cfg, in BlendCache cache){
            int height = 0, lowest = int.MaxValue;
            bool wet = false;
            for (int dx = -1; dx <= 1; dx++)
            for (int dz = -1; dz <= 1; dz++){
                int h = SurfaceY(x + dx, z + dz, cfg, cache);
                if (dx == 0 && dz == 0) height = h;
                lowest = math.min(lowest, h);
                wet |= Submerged(h, cfg);
            }

            return Limits(x, z, height, lowest, wet, cfg, cache);
        }
    }
}
