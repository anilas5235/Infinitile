// csharp

using System;
using System.Runtime.InteropServices;
using Unity.Burst;
using Unity.Mathematics;
using static Unity.Mathematics.noise;

namespace Engine.Scripts.Noise
{
    /// <summary>
    ///     Immutable noise profile wrapper that holds configuration for multi-octave 2D noise
    ///     used in terrain generation.
    /// </summary>
    [BurstCompile]
    public readonly struct NoiseProfile
    {
        private readonly Settings _settings;

        /// <summary>
        ///     Initializes a new instance of the <see cref="NoiseProfile" /> struct with the given settings,
        ///     ensuring a valid scale value.
        /// </summary>
        /// <param name="settings">Noise parameters such as seed, scale, persistence, lacunarity and octaves.</param>
        public NoiseProfile(Settings settings)
        {
            // Kopiere settings, bevor Felder verändert werden (vermeidet Mutation eines readonly-Felds)
            Settings s = settings;
            if (s.Scale <= 0f) s.Scale = 0.0001f;

            _settings = s;
        }

        /// <summary>
        ///     Evaluates normalized noise (0..1) at the given 2D position using the configured profile.
        /// </summary>
        /// <param name="position">Position in world space used as input for the noise function.</param>
        /// <returns>Noise value in the range [0,1].</returns>
        public float GetNoise(float2 position)
        {
            return ComputeNoise(position) * 0.5f + 0.5f;
        }

        /// <summary>
        ///     Computes the raw (unnormalized) multi-octave noise value for the given position.
        /// </summary>
        /// <param name="position">Position in world space used as input for the noise function.</param>
        /// <returns>Noise value that can be outside the [0,1] range.</returns>
        private float ComputeNoise(float2 position)
        {
            float amplitude = 1f;
            float frequency = 1f;
            float noiseSum = 0f;

            // Seed als Float hinzufügen
            float2 samplePos = (position + _settings.Seed) / _settings.Scale;

            for (int i = 0; i < _settings.Octaves; i++)
            {
                float n = cnoise(samplePos * frequency);
                noiseSum += n * amplitude;

                amplitude *= _settings.Persistance;
                frequency *= _settings.Lacunarity;
            }

            return noiseSum;
        }

        /// <summary>
        ///     Serializable settings used to configure a <see cref="NoiseProfile" />.
        /// </summary>
        [Serializable]
        public struct Settings
        {
            /// <summary>
            ///     Integer seed used to offset noise sampling and make results deterministic.
            /// </summary>
            public int Seed;

            /// <summary>
            ///     Global scale applied to the input position; smaller values produce larger features.
            /// </summary>
            public float Scale;

            /// <summary>
            ///     Amplitude decay factor per octave (commonly in the range 0..1).
            /// </summary>
            public float Persistance;

            /// <summary>
            ///     Frequency multiplier per octave; values &gt; 1 add higher-frequency detail.
            /// </summary>
            public float Lacunarity;

            /// <summary>
            ///     Number of octaves to accumulate when sampling the noise.
            /// </summary>
            public int Octaves;
        }
    }

    /// <summary>
    ///     Packed noise sample containing humidity, temperature and height components.
    /// </summary>
    [BurstCompile]
    [StructLayout(LayoutKind.Sequential)]
    public struct NoiseValue
    {
        /// <summary>Humidity value (typically 0..1).</summary>
        public float Humidity;

        /// <summary>Temperature value (typically 0..1).</summary>
        public float Temperature;

        /// <summary>Height component used for terrain shaping.</summary>
        public float Height;
    }
}