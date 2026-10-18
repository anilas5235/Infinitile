using Engine.Scripts.WorldGen.Core;
using Unity.Burst;
using Unity.Mathematics;

namespace Engine.Scripts.WorldGen.Noise{
    /// <summary>
    ///     Peak (ridge) noise P = (1 − |n|)² with simplex n at a fixed wavelength (GEN-10, default 192 blocks).
    ///     Gives sharp ridges in [0,1]; jaggedness J multiplies it in the height formula (§5.7). Shared by both
    ///     architectures (GEN-11).
    /// </summary>
    [BurstCompile]
    public static class PeakNoise{
        /// <summary>Default wavelength in blocks (§5.6).</summary>
        public const float DefaultWavelength = 192f;

        /// <summary>P at world position <paramref name="p" /> (blocks).</summary>
        public static float Sample(float2 p, float wavelength, uint seed){
            float n = SeededSimplex.Noise(p / wavelength, seed, HashSalts.PeakNoise);
            float ridge = 1f - math.abs(n);
            return ridge * ridge;
        }
    }
}
