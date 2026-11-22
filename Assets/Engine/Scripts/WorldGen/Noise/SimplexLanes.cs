using System.Runtime.CompilerServices;
using Engine.Scripts.WorldGen.Core;
using Unity.Burst;
using Unity.Mathematics;

namespace Engine.Scripts.WorldGen.Noise{
    /// <summary>
    ///     Lane-wise form of <see cref="SeededSimplex" />: one call evaluates the noise for an array of sample points,
    ///     four lanes at a time in SIMD registers, without a branch per sample. Every lane gives exactly the bits of the
    ///     scalar <see cref="SeededSimplex.Noise(float2,uint,uint)" /> and
    ///     <see cref="SeededSimplex.Noise(float3,uint,uint)" /> for the same point: the same operations run in the same
    ///     order, the simplex and gradient choices are selects instead of branches, and a corner outside the kernel
    ///     radius gives +0 exactly as the scalar early return does.
    ///     <para>
    ///         The arrays are read and written in whole groups of four, so <c>count</c> is rounded up to a multiple of
    ///         four: callers keep their arrays padded and the padding lanes finite.
    ///     </para>
    /// </summary>
    [BurstCompile]
    public static unsafe class SimplexLanes{
        // The same literals and constant expressions as SeededSimplex, so the compiler folds them to the same floats.
        private const float SkewF2 = 0.3660254037844386f;
        private const float UnskewG2 = 0.21132486540518713f;
        private const float OutputScale = 70f;
        private const float SkewF3 = 1f / 3f;
        private const float UnskewG3 = 1f / 6f;
        private const float OutputScale3 = 32f;

        /// <summary>Lanes rounded up to whole SIMD groups of four.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Padded(int count){
            return (count + 3) & ~3;
        }

        /// <summary>result[i] = SeededSimplex.Noise((x[i], z[i]), seed, salt) for i &lt; Padded(count).</summary>
        public static void Noise2(float* x, float* z, float* result, int count, uint seed, uint salt){
            int n = Padded(count);
            for (int i = 0; i < n; i += 4){
                float4 px = *(float4*)(x + i);
                float4 pz = *(float4*)(z + i);
                *(float4*)(result + i) = Noise2(px, pz, seed, salt);
            }
        }

        /// <summary>
        ///     acc[i] = acc[i] + SeededSimplex.Noise((x[i], z[i]) * freq, seed, salt) * amp, the octave step of an fBm
        ///     sum, with the same rounding as the scalar sum.
        /// </summary>
        public static void AccumulateFbm2(float* x, float* z, float* acc, int count, float freq, float amp, uint seed, uint salt){
            int n = Padded(count);
            for (int i = 0; i < n; i += 4){
                float4 px = *(float4*)(x + i) * freq;
                float4 pz = *(float4*)(z + i) * freq;
                float4 v = Noise2(px, pz, seed, salt);
                *(float4*)(acc + i) = *(float4*)(acc + i) + v * amp;
            }
        }

        /// <summary>acc[i] = acc[i] + (1 − |noise|) * amp, the octave step of a ridged sum.</summary>
        public static void AccumulateRidged2(float* x, float* z, float* acc, int count, float freq, float amp, uint seed, uint salt){
            int n = Padded(count);
            for (int i = 0; i < n; i += 4){
                float4 px = *(float4*)(x + i) * freq;
                float4 pz = *(float4*)(z + i) * freq;
                float4 v = Noise2(px, pz, seed, salt);
                *(float4*)(acc + i) = *(float4*)(acc + i) + (1f - math.abs(v)) * amp;
            }
        }

        /// <summary>result[i] = SeededSimplex.Noise((x[i], y[i], z[i]), seed, salt) for i &lt; Padded(count).</summary>
        public static void Noise3(float* x, float* y, float* z, float* result, int count, uint seed, uint salt){
            int n = Padded(count);
            for (int i = 0; i < n; i += 4){
                float4 px = *(float4*)(x + i);
                float4 py = *(float4*)(y + i);
                float4 pz = *(float4*)(z + i);
                *(float4*)(result + i) = Noise3(px, py, pz, seed, salt);
            }
        }

        /// <summary>acc[i] = acc[i] + SeededSimplex.Noise((x·fxz, y·fy, z·fxz)) * amp, one 3D fBm octave.</summary>
        public static void AccumulateFbm3(float* x, float* y, float* z, float* acc, int count, float freqXZ, float freqY,
            float amp, uint seed, uint salt){
            int n = Padded(count);
            for (int i = 0; i < n; i += 4){
                float4 px = *(float4*)(x + i) * freqXZ;
                float4 py = *(float4*)(y + i) * freqY;
                float4 pz = *(float4*)(z + i) * freqXZ;
                float4 v = Noise3(px, py, pz, seed, salt);
                *(float4*)(acc + i) = *(float4*)(acc + i) + v * amp;
            }
        }

        /// <summary>acc[i] = acc[i] + (1 − |noise|) * amp, one 3D ridged octave.</summary>
        public static void AccumulateRidged3(float* x, float* y, float* z, float* acc, int count, float freqXZ, float freqY,
            float amp, uint seed, uint salt){
            int n = Padded(count);
            for (int i = 0; i < n; i += 4){
                float4 px = *(float4*)(x + i) * freqXZ;
                float4 py = *(float4*)(y + i) * freqY;
                float4 pz = *(float4*)(z + i) * freqXZ;
                float4 v = Noise3(px, py, pz, seed, salt);
                *(float4*)(acc + i) = *(float4*)(acc + i) + (1f - math.abs(v)) * amp;
            }
        }

        // --- 2D ---------------------------------------------------------------------------------------------------

        /// <summary>Four 2D samples; lane k equals SeededSimplex.Noise(new float2(px[k], pz[k]), seed, salt).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float4 Noise2(float4 px, float4 pz, uint seed, uint salt){
            float4 s = (px + pz) * SkewF2;
            float4 cellFx = math.floor(px + s);
            float4 cellFz = math.floor(pz + s);
            int4 cellX = (int4)cellFx;
            int4 cellZ = (int4)cellFz;

            float4 t = (cellFx + cellFz) * UnskewG2;
            float4 d0x = px - (cellFx - t);
            float4 d0z = pz - (cellFz - t);

            bool4 lower = d0x > d0z; // lower triangle: step (1, 0), else (0, 1)
            float4 stepX = math.select(0f, 1f, lower);
            float4 stepZ = math.select(1f, 0f, lower);
            int4 stepXi = math.select(new int4(0), new int4(1), lower);
            int4 stepZi = math.select(new int4(1), new int4(0), lower);

            float4 d1x = d0x - stepX + UnskewG2;
            float4 d1z = d0z - stepZ + UnskewG2;
            float4 d2x = d0x - 1f + 2f * UnskewG2;
            float4 d2z = d0z - 1f + 2f * UnskewG2;

            float4 n = Corner2(d0x, d0z, cellX, cellZ, seed, salt)
                       + Corner2(d1x, d1z, cellX + stepXi, cellZ + stepZi, seed, salt)
                       + Corner2(d2x, d2z, cellX + 1, cellZ + 1, seed, salt);

            return math.clamp(OutputScale * n, -1f, 1f);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float4 Corner2(float4 dx, float4 dz, int4 cx, int4 cz, uint seed, uint salt){
            float4 t = 0.5f - (dx * dx + dz * dz);
            bool4 outside = t < 0f;
            t *= t;
            uint4 hash = Hash32(cx, cz, seed, salt);

            // Gradient: one of (±1, ±1), (±1, 0), (0, ±1) from the top three hash bits, as SeededSimplex.Gradient.
            uint4 g = hash >> 29;
            bool4 bit0 = (g & 1u) == 0u;
            float4 s0 = math.select(-1f, 1f, bit0);
            float4 s1 = math.select(-1f, 1f, (g & 2u) == 0u);
            bool4 diagonal = (g & 4u) == 0u;
            float4 gx = math.select(math.select(0f, s1, bit0), s0, diagonal);
            float4 gz = math.select(math.select(s1, 0f, bit0), s1, diagonal);

            float4 r = t * t * (gx * dx + gz * dz);
            return math.select(r, 0f, outside);
        }

        // --- 3D ---------------------------------------------------------------------------------------------------

        /// <summary>Four 3D samples; lane k equals SeededSimplex.Noise(new float3(px[k], py[k], pz[k]), seed, salt).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float4 Noise3(float4 px, float4 py, float4 pz, uint seed, uint salt){
            float4 s = (px + py + pz) * SkewF3;
            float4 cellFx = math.floor(px + s);
            float4 cellFy = math.floor(py + s);
            float4 cellFz = math.floor(pz + s);
            int4 cx = (int4)cellFx;
            int4 cy = (int4)cellFy;
            int4 cz = (int4)cellFz;

            float4 t = (cellFx + cellFy + cellFz) * UnskewG3;
            float4 d0x = px - (cellFx - t);
            float4 d0y = py - (cellFy - t);
            float4 d0z = pz - (cellFz - t);

            // The simplex of the cube that holds the point, as the nested comparisons of SeededSimplex.Noise(float3):
            // a = x >= y, b = y >= z, c = x >= z select the same two steps in every case, ties included.
            bool4 a = d0x >= d0y;
            bool4 b = d0y >= d0z;
            bool4 c = d0x >= d0z;
            bool4 s1x = a & (b | c);
            bool4 s1y = !a & b;
            bool4 s1z = !b & (!a | !c);
            bool4 s2x = a | (b & c);
            bool4 s2y = (a & b) | !a;
            bool4 s2z = !b | (!a & !c);

            float4 d1x = d0x - math.select(0f, 1f, s1x) + UnskewG3;
            float4 d1y = d0y - math.select(0f, 1f, s1y) + UnskewG3;
            float4 d1z = d0z - math.select(0f, 1f, s1z) + UnskewG3;
            float4 d2x = d0x - math.select(0f, 1f, s2x) + 2f * UnskewG3;
            float4 d2y = d0y - math.select(0f, 1f, s2y) + 2f * UnskewG3;
            float4 d2z = d0z - math.select(0f, 1f, s2z) + 2f * UnskewG3;
            float4 d3x = d0x - 1f + 3f * UnskewG3;
            float4 d3y = d0y - 1f + 3f * UnskewG3;
            float4 d3z = d0z - 1f + 3f * UnskewG3;

            float4 n = Corner3(d0x, d0y, d0z, cx, cy, cz, seed, salt)
                       + Corner3(d1x, d1y, d1z, cx + math.select(new int4(0), new int4(1), s1x), cy + math.select(new int4(0), new int4(1), s1y),
                           cz + math.select(new int4(0), new int4(1), s1z), seed, salt)
                       + Corner3(d2x, d2y, d2z, cx + math.select(new int4(0), new int4(1), s2x), cy + math.select(new int4(0), new int4(1), s2y),
                           cz + math.select(new int4(0), new int4(1), s2z), seed, salt)
                       + Corner3(d3x, d3y, d3z, cx + 1, cy + 1, cz + 1, seed, salt);

            return math.clamp(OutputScale3 * n, -1f, 1f);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float4 Corner3(float4 dx, float4 dy, float4 dz, int4 cx, int4 cy, int4 cz, uint seed, uint salt){
            float4 t = 0.6f - (dx * dx + dy * dy + dz * dz);
            bool4 outside = t < 0f;
            t *= t;
            uint4 hash = Hash32(cx, cy, cz, seed, salt);

            // One of the 12 cube-edge directions, as SeededSimplex.Gradient3.
            uint4 g = (hash >> 8) % 12u;
            float4 s0 = math.select(-1f, 1f, (g & 1u) == 0u);
            float4 s1 = math.select(-1f, 1f, (g & 2u) == 0u);
            uint4 axis = g >> 2; // 0: xy, 1: xz, 2: yz
            bool4 axis0 = axis == 0u;
            bool4 axis1 = axis == 1u;
            float4 gx = math.select(0f, s0, axis0 | axis1);
            float4 gy = math.select(math.select(s0, 0f, axis1), s1, axis0);
            float4 gz = math.select(s1, 0f, axis0);

            float4 r = t * t * (gx * dx + gy * dy + gz * dz);
            return math.select(r, 0f, outside);
        }

        // --- Hash -------------------------------------------------------------------------------------------------

        /// <summary>Lane form of <see cref="Hash.Hash32(int,int,uint,uint)" />.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint4 Hash32(int4 x, int4 z, uint seed, uint salt){
            uint4 h = Mix(((uint4)x * 0x9E3779B1u) ^ salt);
            h = Mix(h ^ ((uint4)z * 0x85EBCA77u));
            return Mix(h ^ seed);
        }

        /// <summary>Lane form of <see cref="Hash.Hash32(int,int,int,uint,uint)" />.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint4 Hash32(int4 x, int4 y, int4 z, uint seed, uint salt){
            uint4 h = Mix(((uint4)x * 0x9E3779B1u) ^ salt);
            h = Mix(h ^ ((uint4)y * 0xC2B2AE3Du));
            h = Mix(h ^ ((uint4)z * 0x85EBCA77u));
            return Mix(h ^ seed);
        }

        /// <summary>Lane form of <see cref="Hash.Mix" />.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint4 Mix(uint4 h){
            h ^= h >> 16;
            h *= 0x7feb352du;
            h ^= h >> 15;
            h *= 0x846ca68bu;
            h ^= h >> 16;
            return h;
        }
    }
}
