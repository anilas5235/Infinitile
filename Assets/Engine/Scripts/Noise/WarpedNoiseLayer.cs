using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;

namespace Engine.Scripts.Noise
{
    /// <summary>
    ///     Encapsulates a single "warped, redistributed" noise layer: a base fBm noise,
    ///     domain-warped via two secondary noise profiles, then remapped through a
    ///     piecewise-linear redistribution curve. Used for large-scale features like
    ///     continentalness, where organic shapes and precise control over the value
    ///     distribution matter.
    /// </summary>
    [BurstCompile]
    public struct WarpedNoiseLayer : IDisposable
    {
        private NoiseProfile _baseNoise;
        private NoiseProfile _warpX;
        private NoiseProfile _warpY;
        private readonly float _warpStrength;
        private NativeArray<float2> _redistributionCurve;

        /// <summary>
        ///     Builds the layer and allocates the redistribution curve with the given allocator.
        ///     Caller owns the resulting NativeArray and must call <see cref="Dispose" />.
        /// </summary>
        public WarpedNoiseLayer(Settings settings)
        {
            _baseNoise = new NoiseProfile(settings.BaseNoise);

            NoiseProfile.Settings warpSettingsX = settings.WarpNoise;
            NoiseProfile.Settings warpSettingsY = settings.WarpNoise;
            warpSettingsY.Seed += settings.WarpSeedOffsetY;

            _warpX = new NoiseProfile(warpSettingsX);
            _warpY = new NoiseProfile(warpSettingsY);
            _warpStrength = settings.WarpStrength;

            float2[] points = settings.RedistributionPoints;
            if (points == null || points.Length == 0)
            {
                // Identity curve fallback: 0->0, 1->1, no remapping.
                points = new[] { new float2(0f, 0f), new float2(1f, 1f) };
            }

            _redistributionCurve = new NativeArray<float2>(points, Allocator.Domain);
        }

        /// <summary>
        ///     Evaluates the fully processed noise value (warped + redistributed) at the given position.
        /// </summary>
        public float GetNoise(float2 position)
        {
            float2 warpedPos = WarpPosition(position);
            float raw = _baseNoise.GetNoise(warpedPos);
            return ApplyRedistribution(raw);
        }

        private float2 WarpPosition(float2 position)
        {
            if (_warpStrength <= 0f)
            {
                return position;
            }

            float wx = _warpX.GetNoise(position) * 2f - 1f;
            float wy = _warpY.GetNoise(position) * 2f - 1f;
            return position + new float2(wx, wy) * _warpStrength;
        }

        private float ApplyRedistribution(float raw)
        {
            int last = _redistributionCurve.Length - 1;

            if (raw <= _redistributionCurve[0].x)
            {
                return _redistributionCurve[0].y;
            }

            if (raw >= _redistributionCurve[last].x)
            {
                return _redistributionCurve[last].y;
            }

            for (int i = 1; i <= last; i++)
            {
                if (raw > _redistributionCurve[i].x)
                {
                    continue;
                }

                float2 a = _redistributionCurve[i - 1];
                float2 b = _redistributionCurve[i];
                float t = math.unlerp(a.x, b.x, raw);
                return math.lerp(a.y, b.y, t);
            }

            return raw;
        }

        /// <summary>
        ///     Releases the redistribution curve's native memory. Must be called once
        ///     this layer is no longer needed (matching the allocator lifetime rules).
        /// </summary>
        public void Dispose()
        {
            if (_redistributionCurve.IsCreated)
            {
                _redistributionCurve.Dispose();
            }
        }

        [Serializable]
        public struct Settings
        {
            /// <summary>Settings for the primary fBm noise (frequency/octaves/etc).</summary>
            public NoiseProfile.Settings BaseNoise;

            /// <summary>Settings for the domain-warp noise (typically lower frequency than BaseNoise).</summary>
            public NoiseProfile.Settings WarpNoise;

            /// <summary>Strength of the domain warp offset, in world-space units.</summary>
            public float WarpStrength;

            /// <summary>Seed offset applied to the Y warp channel so X/Y don't correlate.</summary>
            public int WarpSeedOffsetY;

            /// <summary>
            ///     Piecewise-linear control points (input, output), sorted ascending by x.
            ///     Values outside [first.x, last.x] clamp to the nearest endpoint's y.
            /// </summary>
            public float2[] RedistributionPoints;
        }
    }
}