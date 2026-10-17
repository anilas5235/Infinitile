using System;
using Engine.Scripts.Jobs.Chunk;
using Engine.Scripts.Noise;
using Unity.Mathematics;
using UnityEngine;

namespace Engine.Scripts.Settings{
    /// <summary>
    ///     Configuration for world noise and height levels (water). Used during generation.
    /// </summary>
    [CreateAssetMenu(fileName = "NoiseSettings2D", menuName = "Voxel/NoiseSettings", order = 0)]
    public class NoiseSettings : ScriptableObject{
        /// <summary>
        ///     Water surface level in world Y coordinates.
        /// </summary>
        [Tooltip("Water surface level in world Y coordinates.")]
        public int WaterLevel = 124;

        public NoiseProfileSettings humidityProfile = new(){
            scale = 700f,
            persistance = 0.5f,
            lacunarity = 2f,
            octaves = 3
        };

        public NoiseProfileSettings temperatureProfile = new(){
            scale = 900f,
            persistance = 0.5f,
            lacunarity = 2f,
            octaves = 3
        };

        [Tooltip(
            "How much a high continental value (inland) dries out humidity. 0 = continental has no effect " +
            "on humidity; 1 = fully inland areas can lose all humidity. Negative values make inland areas " +
            "wetter instead (e.g. for an inverted/inland-sea world).")]
        [Range(-1f, 1f)]
        public float ContinentalHumidityInfluence = 0.4f;

        [Tooltip(
            "How much a high continental value (inland) widens temperature swings around the middle of the " +
            "range, mimicking real continentality (colder cold biomes, hotter hot biomes further inland). " +
            "0 = no effect; 1 = fully inland temperature deviation from the middle is doubled.")]
        [Range(0f, 2f)]
        public float ContinentalTemperatureInfluence = 0.6f;

        [Tooltip(
            "How strongly the elevation profile perturbs the continental shape, as a fraction of the full " +
            "[0,1] range (e.g. 0.35 = elevation can push the continental height up/down by up to ±0.175). " +
            "Elevation is applied as a centered detail offset on top of continental, not averaged with it, " +
            "so continental's large-scale shape (and its redistribution curve) is preserved.")]
        [Range(0f, 1f)]
        public float ElevationDetailStrength = 0.35f;

        public NoiseProfileSettings elevationProfile = new(){
            scale = 180f,
            persistance = 0.5f,
            lacunarity = 2f,
            octaves = 4
        };

        public WarpedNoiseLayerSettings continentalLayer = new(){
            baseNoise = new NoiseProfileSettings{
                scale = 2500f,
                persistance = 0.5f,
                lacunarity = 2f,
                octaves = 5
            },
            warpNoise = new NoiseProfileSettings{
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
            )
        };

        private void OnValidate(){
            continentalLayer.EnsureSevenLinearKeys();
        }

        /// <summary>
        ///     Builds the Burst-side noise parameters for these settings. Allocates native memory; the caller owns the
        ///     result (see <see cref="NoiseCalculator.NoiseParameters.Dispose" />). Shared by the engine provider and
        ///     the tests/benchmarks (PERF-02).
        /// </summary>
        /// <param name="seed">Global world seed.</param>
        public NoiseCalculator.NoiseParameters ToNoiseParameters(int seed){
            return new NoiseCalculator.NoiseParameters{
                Seed = seed,
                ElevationDetailStrength = ElevationDetailStrength,
                ContinentalHumidityInfluence = ContinentalHumidityInfluence,
                ContinentalTemperatureInfluence = ContinentalTemperatureInfluence,
                ElevationProfile = new NoiseProfile(elevationProfile.ToStruct(seed)),
                HumidityProfile = new NoiseProfile(humidityProfile.ToStruct(seed)),
                TemperatureProfile = new NoiseProfile(temperatureProfile.ToStruct(seed)),
                ContinentalLayer = new WarpedNoiseLayer(continentalLayer.ToStruct(seed))
            };
        }
    }

    [Serializable]
    public class NoiseProfileSettings{
        [Tooltip("Base noise scale.")] public float scale = 256;

        [Tooltip("Amplitude reduction per octave.")]
        public float persistance = 0.5f;

        [Tooltip("Frequency increase per octave.")]
        public float lacunarity = 2f;

        [Tooltip("Number of octaves.")] public int octaves = 4;

        public NoiseProfile.Settings ToStruct(int seed){
            return new NoiseProfile.Settings{
                Seed = seed,
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

        public WarpedNoiseLayer.Settings ToStruct(int seed){
            float2[] points = new float2[redistributionCurve.length];
            for (int i = 0; i < redistributionCurve.length; i++){
                Keyframe key = redistributionCurve.keys[i];
                points[i] = new float2(key.time, key.value);
            }

            return new WarpedNoiseLayer.Settings{
                BaseNoise = baseNoise.ToStruct(seed),
                WarpNoise = warpNoise.ToStruct(seed),
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
