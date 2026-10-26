using Engine.Scripts.WorldGen.Settings;
using Unity.Burst;

namespace Engine.Scripts.WorldGen.Caves{
    /// <summary>
    ///     The halls, worms and lava pockets at a single voxel, evaluated from scratch: the reference the chunk passes
    ///     must equal. It walks every worm that can reach the voxel, so it is far too slow for generation; tests and
    ///     tools use it.
    /// </summary>
    [BurstCompile]
    public static class CaveCarving{
        /// <summary>Result of <see cref="Evaluate" />: the voxel keeps the terrain fill.</summary>
        public const int Solid = 0;

        /// <summary>Result of <see cref="Evaluate" />: the voxel is carved and holds air.</summary>
        public const int Air = 1;

        /// <summary>Result of <see cref="Evaluate" />: the voxel is carved and holds lava.</summary>
        public const int Lava = 2;

        /// <summary>A hall, a worm or a lava pocket carves voxel (x, y, z), whether air or lava fills it.</summary>
        public static bool IsCarved(int x, int y, int z, in WorldGenConfig cfg){
            return Kind(x, y, z, cfg) != Solid;
        }

        /// <summary>What the cave passes leave at voxel (x, y, z): <see cref="Solid" />, <see cref="Air" /> or <see cref="Lava" />.</summary>
        public static int Kind(int x, int y, int z, in WorldGenConfig cfg){
            Evaluate(x, y, z, cfg, out int kind);
            return kind;
        }

        /// <summary>
        ///     <see cref="Kind" /> as a Burst direct call, so a managed caller gets the jobs' bits for the worm paths'
        ///     trigonometry.
        /// </summary>
        [BurstCompile(CompileSynchronously = true, FloatMode = FloatMode.Strict)]
        public static void Evaluate(int x, int y, int z, in WorldGenConfig cfg, out int kind){
            kind = Solid;
            ref readonly CaveConfig c = ref cfg.Caves;
            if (!c.Enabled || y <= CaveColumns.Floor) return;
            CaveLimits limits = CaveColumns.Limits(x, z, cfg, default);
            bool carved = Caverns.Carves(x, y, z, limits.Cavern, cfg) || LavaPockets.Carves(x, y, z, limits.Cavern, cfg)
                          || (y >= c.WormMinY && y <= limits.Worm && Worms.Covers(x, y, z, cfg, default));
            if (carved) kind = LavaPockets.Fills(x, y, z, cfg) ? Lava : Air;
        }
    }
}
