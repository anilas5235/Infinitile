using Engine.Scripts.WorldGen.Core;
using Engine.Scripts.WorldGen.Noise;
using Engine.Scripts.WorldGen.Settings;
using Unity.Mathematics;

namespace Engine.Scripts.WorldGen.Caves{
    /// <summary>
    ///     The 2D masks of the cave passes, one seeded simplex instance each: where halls may form, where worms may
    ///     break the surface and where the cave floor turns to lava. Values in −1…1; each pass compares its mask with
    ///     its threshold.
    /// </summary>
    public static class CaveMasks{
        /// <summary>Cavern mask of column (<paramref name="x" />, <paramref name="z" />).</summary>
        public static float Cavern(int x, int z, in WorldGenConfig cfg){
            return SeededSimplex.Noise(new float2(x, z) / cfg.Caves.CavernMaskWavelength, cfg.Seed, HashSalts.CavernMask);
        }

        /// <summary>Entrance mask of column (<paramref name="x" />, <paramref name="z" />).</summary>
        public static float Entrance(int x, int z, in WorldGenConfig cfg){
            return SeededSimplex.Noise(new float2(x, z) / cfg.Caves.EntranceWavelength, cfg.Seed, HashSalts.EntranceMask);
        }

        /// <summary>Lava mask of column (<paramref name="x" />, <paramref name="z" />).</summary>
        public static float Lava(int x, int z, in WorldGenConfig cfg){
            return SeededSimplex.Noise(new float2(x, z) / cfg.Caves.LavaMaskWavelength, cfg.Seed, HashSalts.LavaMask);
        }
    }
}
