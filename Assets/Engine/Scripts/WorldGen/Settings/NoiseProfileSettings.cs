using System;
using Engine.Scripts.Noise;
using Engine.Scripts.WorldGen.Core;
using Unity.Mathematics;
using UnityEngine;

namespace Engine.Scripts.WorldGen.Settings{
    [Serializable]
    public class NoiseProfileSettings{
        [Tooltip("Base noise scale.")] public float scale = 256;

        [Tooltip("Amplitude reduction per octave.")]
        public float persistance = 0.5f;

        [Tooltip("Frequency increase per octave.")]
        public float lacunarity = 2f;

        [Tooltip("Number of octaves.")] public int octaves = 4;

        /// <param name="seed">World seed.</param>
        /// <param name="salt">Per-instance gradient salt from <see cref="HashSalts" />.</param>
        public NoiseProfile.Settings ToStruct(int seed, uint salt){
            return new NoiseProfile.Settings{
                Seed = seed,
                Salt = salt,
                Scale = scale,
                Persistance = persistance,
                Lacunarity = lacunarity,
                Octaves = octaves
            };
        }
    }

    [Serializable]
    public class WarpedNoiseLayerSettings{
        /// <summary>Settings for the primary fBm noise (frequency/octaves/etc).</summary>
        public NoiseProfileSettings baseNoise;

        /// <summary>Settings for the domain-warp noise (typically lower frequency than BaseNoise).</summary>
        public NoiseProfileSettings warpNoise;

        /// <summary>Strength of the domain warp offset, in world-space units.</summary>
        public float warpStrength = 1f;

        public AnimationCurve redistributionCurve = new(
            new Keyframe(0f, 0f),
            new Keyframe(0.3f, 0.3f),
            new Keyframe(0.4f, 0.4f),
            new Keyframe(0.5f, 0.5f),
            new Keyframe(0.6f, 0.6f),
            new Keyframe(0.8f, 0.8f),
            new Keyframe(1f, 1f)
        );

        /// <param name="seed">World seed.</param>
        /// <param name="baseSalt">Gradient salt of the base noise.</param>
        /// <param name="warpSaltX">Gradient salt of the X warp channel.</param>
        /// <param name="warpSaltY">Gradient salt of the Y warp channel (must differ from X).</param>
        public WarpedNoiseLayer.Settings ToStruct(int seed, uint baseSalt, uint warpSaltX, uint warpSaltY){
            float2[] points = new float2[redistributionCurve.length];
            for (int i = 0; i < redistributionCurve.length; i++){
                Keyframe key = redistributionCurve.keys[i];
                points[i] = new float2(key.time, key.value);
            }

            return new WarpedNoiseLayer.Settings{
                BaseNoise = baseNoise.ToStruct(seed, baseSalt),
                WarpNoise = warpNoise.ToStruct(seed, warpSaltX),
                WarpSaltY = warpSaltY,
                WarpStrength = warpStrength,
                RedistributionPoints = points
            };
        }

        internal void EnsureSevenLinearKeys(){
            const int count = 7;

            if (redistributionCurve.keys.Length != count){
                Keyframe[] keys = new Keyframe[count];

                for (int i = 0; i < count; i++){
                    float t = i / (float)(count - 1);
                    float v = redistributionCurve.Evaluate(t);

                    Keyframe k = new(t, v);
                    keys[i] = k;
                }

                redistributionCurve = new AnimationCurve(keys);
            }
            else{
                Keyframe[] keys = redistributionCurve.keys;
                keys[0].time = 0f;
                keys[6].time = 1f;
                redistributionCurve = new AnimationCurve(keys);
            }
        }
    }
}
