using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;
using static Unity.Mathematics.noise;

namespace Engine.Scripts.Noise
{
    /// <summary>
    ///     Immutable noise profile wrapper that holds configuration for multi-octave 2D noise
    ///     used in terrain generation.
    /// </summary>
    [BurstCompile]
    public struct NoiseProfile : IDisposable
    {
        [ReadOnly] private NativeArray<float2> _octaveData; // (frequency, amplitude)
        private readonly float _maxAmplitude;
        private readonly float2 _seedOffset;
        private readonly float _scale;

        /// <summary>
        ///     Initializes a new instance of the <see cref="NoiseProfile" /> struct with the given settings,
        ///     ensuring a valid scale value.
        /// </summary>
        /// <param name="settings">Noise parameters such as seed, scale, persistence, lacunarity and octaves.</param>
        public NoiseProfile(Settings settings)
        {
            Settings s = settings;
            _scale = s.Scale <= 0f ? 0.0001f : s.Scale;

            // Precompute octave data for efficient noise evaluation
            _seedOffset = new float2(s.Seed);

            _octaveData = new NativeArray<float2>(s.Octaves, Allocator.Domain);

            float amplitude = 1f;
            float frequency = 1f;
            float maxAmp = 0f;

            for (int i = 0; i < s.Octaves; i++)
            {
                _octaveData[i] = new float2(frequency, amplitude);
                maxAmp += amplitude;

                amplitude *= s.Persistance;
                frequency *= s.Lacunarity;
            }

            _maxAmplitude = maxAmp;
        }

        /// <summary>
        ///     Evaluates normalized noise (0..1) at the given 2D position using the configured profile.
        /// </summary>
        /// <param name="pos">Position in world space used as input for the noise function.</param>
        /// <returns>Noise value in the range [0,1].</returns>
        public float GetNoise(float2 pos)
        {
            float2 samplePos = (pos + _seedOffset) / _scale;

            float sum = 0f;
            foreach (float2 af in _octaveData) sum += cnoise(samplePos * af.x) * af.y;

            return math.remap(-_maxAmplitude, _maxAmplitude, 0f, 1f, sum);
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

        public void Dispose()
        {
            _octaveData.Dispose();
        }
    }
}