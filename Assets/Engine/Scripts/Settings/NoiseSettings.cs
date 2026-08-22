using System;
using Engine.Scripts.Noise;
using Unity.Mathematics;
using UnityEngine;

namespace Engine.Scripts.Settings
{
    /// <summary>
    ///     Configuration for world noise and height levels (water). Used during generation.
    /// </summary>
    [CreateAssetMenu(fileName = "NoiseSettings2D", menuName = "Voxel/NoiseSettings", order = 0)]
    public class NoiseSettings : ScriptableObject
    {
        /// <summary>
        ///     Water surface level in world Y coordinates.
        /// </summary>
        [Tooltip("Water surface level in world Y coordinates.")]
        public int WaterLevel = 124;

        [Tooltip("Scale factor for humidity climate noise sampling.")]
        public float HumidityScale = 0.001f;

        [Tooltip("Scale factor for temperature climate noise sampling.")]
        public float TemperatureScale = 0.0009f;

        public NoiseProfileSettings elevationProfile = new()
        {
            scale = 180f,
            persistance = 0.5f,
            lacunarity = 2f,
            octaves = 4
        };

        public WarpedNoiseLayerSettings continentalLayer = new()
        {
            baseNoise = new NoiseProfileSettings
            {
                scale = 2500f,
                persistance = 0.5f,
                lacunarity = 2f,
                octaves = 5
            },
            warpNoise = new NoiseProfileSettings
            {
                scale = 1800f,
                persistance = 0.5f,
                lacunarity = 2f,
                octaves = 2
            },
            warpStrength = 400f,
            warpSeedOffsetY = 1000,
            redistributionCurve = new AnimationCurve(
                new Keyframe(0f, 0.02f),
                new Keyframe(0.3f, 0.12f),
                new Keyframe(0.4f, 0.28f),
                new Keyframe(0.5f, 0.42f),
                new Keyframe(0.6f, 0.55f),
                new Keyframe(0.8f, 0.78f),
                new Keyframe(1f, 1f)
            ),
        };

        private void OnValidate()
        {
            continentalLayer.EnsureSevenLinearKeys();
        }
    }

    [Serializable]
    public class NoiseProfileSettings
    {
        [Tooltip("Base noise scale.")] public float scale = 256;

        [Tooltip("Amplitude reduction per octave.")]
        public float persistance = 0.5f;

        [Tooltip("Frequency increase per octave.")]
        public float lacunarity = 2f;

        [Tooltip("Number of octaves.")] public int octaves = 4;

        public NoiseProfile.Settings ToStruct(int seed)
        {
            return new NoiseProfile.Settings
            {
                Seed = seed,
                Scale = scale,
                Persistance = persistance,
                Lacunarity = lacunarity,
                Octaves = octaves
            };
        }
    }

    [Serializable]
    public class WarpedNoiseLayerSettings
    {
        /// <summary>Settings for the primary fBm noise (frequency/octaves/etc).</summary>
        public NoiseProfileSettings baseNoise;

        /// <summary>Settings for the domain-warp noise (typically lower frequency than BaseNoise).</summary>
        public NoiseProfileSettings warpNoise;

        /// <summary>Strength of the domain warp offset, in world-space units.</summary>
        public float warpStrength = 1f;

        /// <summary>Seed offset applied to the Y warp channel so X/Y don't correlate.</summary>
        public int warpSeedOffsetY = 1000;

        public AnimationCurve redistributionCurve = new(
            new Keyframe(0f, 0f),
            new Keyframe(0.3f, 0.3f),
            new Keyframe(0.4f, 0.4f),
            new Keyframe(0.5f, 0.5f),
            new Keyframe(0.6f, 0.6f),
            new Keyframe(0.8f, 0.8f),
            new Keyframe(1f, 1f)
        );

        public WarpedNoiseLayer.Settings ToStruct(int seed)
        {
            float2[] points = new float2[redistributionCurve.length];
            for (int i = 0; i < redistributionCurve.length; i++)
            {
                Keyframe key = redistributionCurve.keys[i];
                points[i] = new float2(key.time, key.value);
            }

            return new WarpedNoiseLayer.Settings
            {
                BaseNoise = baseNoise.ToStruct(seed),
                WarpNoise = warpNoise.ToStruct(seed),
                WarpStrength = warpStrength,
                RedistributionPoints = points,
            };
        }

        internal void EnsureSevenLinearKeys()
        {
            const int count = 7;

            if (redistributionCurve.keys.Length != count)
            {
                Keyframe[] keys = new Keyframe[count];

                for (int i = 0; i < count; i++)
                {
                    float t = i / (float)(count - 1);
                    float v = redistributionCurve.Evaluate(t);

                    Keyframe k = new(t, v);
                    keys[i] = k;
                }

                redistributionCurve = new AnimationCurve(keys);
            }
            else
            {
                Keyframe[] keys = redistributionCurve.keys;
                keys[0].time = 0f;
                keys[6].time = 1f;
                redistributionCurve = new AnimationCurve(keys);
            }
        }
    }
}