using System;
using Engine.Scripts.WorldGen.Core;
using Engine.Scripts.WorldGen.Noise;
using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;

namespace Engine.Scripts.Noise{
    /// <summary>
    ///     Immutable fBm wrapper over <see cref="SeededSimplex" /> (GEN-15) for the [0,1] climate fields.
    ///     Each instance has its own gradient salt and a hashed sample offset with |o| ≤ 10⁴ (GEN-05), so two
    ///     profiles with the same seed never show the same features and a different seed is never a plain
    ///     translation of the same world (F-02, D-03).
    /// </summary>
    [BurstCompile]
    public struct NoiseProfile : IDisposable{
        /// <summary>Upper bound of the hashed per-instance sample offset in blocks (GEN-05).</summary>
        public const float MaxOffset = 10000f;

        [ReadOnly] private NativeArray<float2> _octaveData; // (frequency, amplitude)
        private readonly float _maxAmplitude;
        private readonly float2 _offset;
        private readonly float _scale;
        private readonly uint _seed;
        private readonly uint _salt;

        /// <summary>
        ///     Initializes a new instance of the <see cref="NoiseProfile" /> struct with the given settings,
        ///     ensuring a valid scale value.
        /// </summary>
        /// <param name="settings">Noise parameters such as seed, salt, scale, persistence, lacunarity and octaves.</param>
        public NoiseProfile(Settings settings){
            Settings s = settings;
            _scale = s.Scale <= 0f ? 0.0001f : s.Scale;
            _seed = (uint)s.Seed;
            _salt = s.Salt;
            _offset = HashedOffset(_seed, _salt);

            // Precompute octave data for efficient noise evaluation
            _octaveData = new NativeArray<float2>(s.Octaves, Allocator.Domain);

            float amplitude = 1f;
            float frequency = 1f;
            float maxAmp = 0f;

            for (int i = 0; i < s.Octaves; i++){
                _octaveData[i] = new float2(frequency, amplitude);
                maxAmp += amplitude;

                amplitude *= s.Persistance;
                frequency *= s.Lacunarity;
            }

            _maxAmplitude = maxAmp;
        }

        /// <summary>Per-instance sample offset in blocks, each component uniform in [-MaxOffset, MaxOffset) (GEN-05).</summary>
        public static float2 HashedOffset(uint seed, uint salt){
            return new float2(
                Hash.HashSigned(0, 0, seed, salt ^ HashSalts.OffsetX),
                Hash.HashSigned(0, 0, seed, salt ^ HashSalts.OffsetZ)) * MaxOffset;
        }

        /// <summary>
        ///     Evaluates normalized noise (0..1) at the given 2D position using the configured profile.
        /// </summary>
        /// <param name="pos">Position in world space used as input for the noise function.</param>
        /// <returns>Noise value in the range [0,1].</returns>
        public float GetNoise(float2 pos){
            float2 samplePos = (pos + _offset) / _scale;

            float sum = 0f;
            for (int i = 0; i < _octaveData.Length; i++){
                float2 af = _octaveData[i];
                sum += SeededSimplex.Noise(samplePos * af.x, _seed, HashSalts.FbmOctave(_salt, i)) * af.y;
            }

            return math.saturate(math.remap(-_maxAmplitude, _maxAmplitude, 0f, 1f, sum));
        }

        /// <summary>
        ///     Serializable settings used to configure a <see cref="NoiseProfile" />.
        /// </summary>
        [Serializable]
        public struct Settings{
            /// <summary>
            ///     World seed; together with <see cref="Salt" /> it selects the lattice gradients and the sample offset.
            /// </summary>
            public int Seed;

            /// <summary>
            ///     Per-instance salt from <see cref="HashSalts" /> that decorrelates this profile from every other one.
            /// </summary>
            public uint Salt;

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

        public void Dispose(){
            if (!_octaveData.IsCreated) return; // D-05: a default profile (config without noise) owns nothing
            _octaveData.Dispose();
        }
    }
}
