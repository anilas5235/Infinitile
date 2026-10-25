using Unity.Burst;

namespace Engine.Scripts.WorldGen.Core{
    /// <summary>
    ///     Integer hashing for every random decision in world generation. Each decision passes its
    ///     own salt from <see cref="HashSalts" />, so two decisions at the same column never correlate.
    ///     Pure function of (x, z, seed, salt), or (x, y, z, seed, salt) for voxel decisions: the same inputs give the same value on every thread, in Burst
    ///     and in Mono, and independent of chunk boundaries.
    /// </summary>
    [BurstCompile]
    public static class Hash{
        /// <summary>
        ///     32-bit finaliser with full avalanche (every input bit flips every output bit with p ≈ 0.5).
        ///     Constants after "lowbias32" by Chris Wellons (nullprogram.com/blog/2018/07/31, public domain).
        /// </summary>
        public static uint Mix(uint h){
            h ^= h >> 16;
            h *= 0x7feb352du;
            h ^= h >> 15;
            h *= 0x846ca68bu;
            h ^= h >> 16;
            return h;
        }

        /// <summary>Hashes an integer lattice point together with the world seed and a per-decision salt.</summary>
        public static uint Hash32(int x, int z, uint seed, uint salt){
            // Multiplying each coordinate by a different odd constant before mixing keeps (x, z) and (z, x) apart;
            // the golden-ratio and MurmurHash3 constants only spread the bits before Mix.
            uint h = Mix((uint)x * 0x9E3779B1u ^ salt);
            h = Mix(h ^ (uint)z * 0x85EBCA77u);
            return Mix(h ^ seed);
        }

        /// <summary>Uniform float in [0, 1) built from the top 24 bits of <see cref="Hash32" /> (exact in float32).</summary>
        public static float Hash01(int x, int z, uint seed, uint salt){
            return (Hash32(x, z, seed, salt) >> 8) * (1f / 16777216f);
        }

        /// <summary>
        ///     Hashes an integer lattice point in 3D (a voxel position) together with the world seed and a salt. Same
        ///     construction as the 2D <see cref="Hash32(int,int,uint,uint)" />, with a third odd constant for y.
        /// </summary>
        public static uint Hash32(int x, int y, int z, uint seed, uint salt){
            uint h = Mix((uint)x * 0x9E3779B1u ^ salt);
            h = Mix(h ^ (uint)y * 0xC2B2AE3Du);
            h = Mix(h ^ (uint)z * 0x85EBCA77u);
            return Mix(h ^ seed);
        }

        /// <summary>Uniform float in [0, 1) from the top 24 bits of the 3D <see cref="Hash32(int,int,int,uint,uint)" />.</summary>
        public static float Hash01(int x, int y, int z, uint seed, uint salt){
            return (Hash32(x, y, z, seed, salt) >> 8) * (1f / 16777216f);
        }

        /// <summary>Uniform float in [-1, 1) built from <see cref="Hash01" />.</summary>
        public static float HashSigned(int x, int z, uint seed, uint salt){
            return Hash01(x, z, seed, salt) * 2f - 1f;
        }
    }
}
