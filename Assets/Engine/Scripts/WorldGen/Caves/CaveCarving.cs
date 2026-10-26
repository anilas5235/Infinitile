using Engine.Scripts.WorldGen.Settings;
using Unity.Burst;

namespace Engine.Scripts.WorldGen.Caves{
    /// <summary>
    ///     The halls and worms at a single voxel, evaluated from scratch: the reference the chunk passes must equal.
    ///     It walks every worm that can reach the voxel, so it is far too slow for generation; tests and tools use it.
    /// </summary>
    [BurstCompile]
    public static class CaveCarving{
        /// <summary>A hall or a worm carves voxel (x, y, z).</summary>
        public static bool IsCarved(int x, int y, int z, in WorldGenConfig cfg){
            Evaluate(x, y, z, cfg, out int carved);
            return carved != 0;
        }

        /// <summary>
        ///     <see cref="IsCarved" /> as a Burst direct call (1 or 0), so a managed caller gets the jobs' bits for the
        ///     worm paths' trigonometry.
        /// </summary>
        [BurstCompile(CompileSynchronously = true, FloatMode = FloatMode.Strict)]
        public static void Evaluate(int x, int y, int z, in WorldGenConfig cfg, out int carved){
            carved = 0;
            ref readonly CaveConfig c = ref cfg.Caves;
            if (!c.Enabled || y <= CaveColumns.Floor) return;
            CaveLimits limits = CaveColumns.Limits(x, z, cfg, default);
            if (Caverns.Carves(x, y, z, limits.Cavern, cfg)){
                carved = 1;
                return;
            }

            if (y >= c.WormMinY && y <= limits.Worm && Worms.Covers(x, y, z, cfg, default)) carved = 1;
        }
    }
}
