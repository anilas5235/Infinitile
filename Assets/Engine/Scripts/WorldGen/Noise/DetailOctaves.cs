using Engine.Scripts.WorldGen.Core;
using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;

namespace Engine.Scripts.WorldGen.Noise{
    /// <summary>
    ///     The K detail octaves N_i: separate single-frequency simplex noises in [-1,1], zero-centred, at
    ///     fixed wavelengths (default 512, 256, 128, 64, 32, 16 blocks). Frequencies are never blended;
    ///     biomes and splines only scale these octaves linearly. Both architectures sample the same instances.
    /// </summary>
    [BurstCompile]
    public static class DetailOctaves{
        /// <summary>Upper bound of K; the per-column octave samples are kept in a <see cref="FixedList64Bytes{T}" />.</summary>
        public const int MaxOctaves = 15;

        /// <summary>Default K.</summary>
        public const int DefaultOctaveCount = 6;

        /// <summary>Default wavelength of octave <paramref name="i" /> in blocks: 512 / 2^i.</summary>
        public static float DefaultWavelength(int i){
            return 512f / (1 << i);
        }

        /// <summary>Octave <paramref name="i" /> at world position <paramref name="p" /> (blocks).</summary>
        public static float Sample(int i, float2 p, float wavelength, uint seed){
            return SeededSimplex.Noise(p / wavelength, seed, HashSalts.DetailOctave(i));
        }

        /// <summary>All K octaves at <paramref name="p" />; entry i is N_i. K = <paramref name="wavelengths" />.Length.</summary>
        public static FixedList64Bytes<float> SampleAll(float2 p, in NativeArray<float> wavelengths, uint seed){
            FixedList64Bytes<float> samples = new();
            int count = math.min(wavelengths.Length, MaxOctaves);
            for (int i = 0; i < count; i++) samples.Add(Sample(i, p, wavelengths[i], seed));
            return samples;
        }
    }
}
