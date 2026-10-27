using Engine.Scripts.WorldGen.Core;
using Engine.Scripts.WorldGen.Noise;
using Engine.Scripts.WorldGen.Settings;
using Unity.Mathematics;

namespace Engine.Scripts.WorldGen.Caves{
    /// <summary>
    ///     Lava at the bottom of the world, inside the 2D lava mask (<see cref="CaveMasks.Lava" />): flat pockets that a
    ///     squashed 3D noise carves in a low band whether or not a cave reaches them, and every carved voxel at or below
    ///     <c>LavaLevel</c>, so a pocket or a deep cave has a lava floor under an air gap. The pockets follow the halls'
    ///     threshold that rises towards both edges of the band (the depth fade of Infinitile v0.3.0,
    ///     ChunkGenerationCavesOres.CarveCaves, which also filled carved voxels below y 5 with lava) and shrink towards
    ///     the edge of the mask like the halls. Without a lava voxel there are neither pockets nor lava.
    /// </summary>
    public static class LavaPockets{
        /// <summary>The column's mask holds lava at all.</summary>
        public static bool MaskOpen(float mask, in CaveConfig c){
            return c.LavaVoxel != 0 && mask > c.LavaMaskThreshold;
        }

        /// <summary>Rise of the pocket threshold from the lava mask value <paramref name="mask" /> of a column.</summary>
        public static float MaskRaise(float mask, in CaveConfig c){
            return Caverns.FadeRaise(mask, c.LavaMaskThreshold);
        }

        /// <summary>Pocket threshold at height <paramref name="y" /> in a column whose mask raises it by <paramref name="raise" />.</summary>
        public static float Threshold(int y, float raise, in CaveConfig c){
            return Caverns.BandThreshold(y, c.LavaPocketBandMin, c.LavaPocketBandMax, c.LavaPocketThreshold,
                c.LavaPocketEdgeRise, c.LavaPocketEdgeRise) + raise;
        }

        /// <summary>Pocket noise at voxel (x, y, z).</summary>
        public static float Noise(int x, int y, int z, in WorldGenConfig cfg){
            float2 w = cfg.Caves.LavaPocketWavelength;
            return SeededSimplex.Noise(new float3(x / w.x, y / w.y, z / w.x), cfg.Seed, HashSalts.LavaPocket);
        }

        /// <summary>
        ///     Lowest and highest y a pocket may carve in a column whose halls may carve up to <paramref name="top" />
        ///     (<see cref="CaveLimits.Cavern" />), so pockets keep the halls' cover; empty (x &gt; y) when none.
        /// </summary>
        public static int2 Band(int top, in CaveConfig c){
            return new int2(math.max(c.LavaPocketBandMin, CaveColumns.Floor + 1), math.min(c.LavaPocketBandMax, top));
        }

        /// <summary>A pocket carves voxel (x, y, z) in a column whose halls may carve up to <paramref name="top" />.</summary>
        public static bool Carves(int x, int y, int z, int top, in WorldGenConfig cfg){
            ref readonly CaveConfig c = ref cfg.Caves;
            int2 band = Band(top, c);
            if (y < band.x || y > band.y) return false;
            float mask = CaveMasks.Lava(x, z, cfg);
            if (!MaskOpen(mask, c)) return false;
            return Noise(x, y, z, cfg) > Threshold(y, MaskRaise(mask, c), c);
        }

        /// <summary>A carved voxel at height <paramref name="y" /> of column (x, z) holds lava rather than air.</summary>
        public static bool Fills(int x, int y, int z, in WorldGenConfig cfg){
            ref readonly CaveConfig c = ref cfg.Caves;
            return y <= c.LavaLevel && MaskOpen(CaveMasks.Lava(x, z, cfg), c);
        }
    }
}
