using System;
using Engine.Scripts.Utils.Extensions;
using Unity.Burst;
using Unity.Mathematics;

namespace Engine.Scripts.Jobs.Core{
    /// <summary>
    ///     Utility class for calculating priority values based on distance for job scheduling.
    ///     Uses squared magnitude calculations for efficient priority determination.
    /// </summary>
    [BurstCompile]
    public static class PriorityUtil{
        private static float3 Quantize45(ref float3 v){
            v = math.normalize(v);

            float3 q = new(
                QuantizeAxis(v.x),
                QuantizeAxis(v.y),
                QuantizeAxis(v.z)
            );

            return math.normalize(q);
        }

        private static float QuantizeAxis(float a){
            const float s = 0.70710678f;
            return a switch{
                > 0.923f => 1f,
                > 0.383f => s,
                > -0.383f => 0f,
                > -0.923f => -s,
                _ => -1f
            };
        }

        /// <summary>
        ///     Calculates priority based on squared distance from focus position for 3D coordinates.
        ///     Lower values indicate higher priority (closer to focus).
        /// </summary>
        /// <param name="position">The position to calculate priority for.</param>
        /// <param name="focus">The focus position (reference point).</param>
        /// <returns>The squared distance as a priority value.</returns>
        [BurstCompile]
        public static float DistPriority(ref int3 position, ref Focus focus){
            int dist = (position - focus.Pos).SqrMagnitude();
            if (dist == 0) return 0f;
            float3 dir = math.normalize(position - (float3)focus.Pos);

            float dot = math.dot(focus.Forward, dir);

            return dist * (3f - 2f * dot);
        }

        /// <summary>
        ///     Calculates priority based on squared distance from focus position for 2D coordinates.
        ///     Lower values indicate higher priority (closer to focus).
        /// </summary>
        /// <param name="position">The 2D position to calculate priority for.</param>
        /// <param name="focus">The 3D focus position (only xz components used).</param>
        /// <returns>The squared distance as a priority value.</returns>
        [BurstCompile]
        public static float DistPriority(ref int2 position, ref Focus focus){
            int dist = (position - focus.Pos.xz).SqrMagnitude();
            if (dist == 0) return 0f;

            // A vertical forward has no xz component; normalising it gives NaN, so skip the direction term.
            float2 forwardXz = focus.Forward.xz;
            if (math.lengthsq(forwardXz) < 1e-6f) return dist * 3f;

            float2 dir = math.normalize(position - focus.Pos.xz);
            float2 forward = math.normalize(forwardXz);

            float dot = math.dot(forward, dir);

            return dist * (3f - 2f * dot);
        }

        /// <summary>
        ///     Eviction order is the square ring around the focus (Chebyshev distance), without the view weighting of
        ///     <see cref="DistPriority(ref int3, ref Focus)" />; the farthest entry has the lowest value and is
        ///     dequeued first. The load, draw and update areas are squares, so a straight-line distance would rank the
        ///     corners of an area (r·√2) behind entries outside it and evict and regenerate them over and over; with
        ///     view weighting, a chunk behind the player inside the area would go first. Within a ring, the squared
        ///     distance breaks the tie (the fraction stays below 1, so it never crosses a ring).
        /// </summary>
        [BurstCompile]
        public static float EvictionPriority(ref int3 position, ref Focus focus){
            int3 d = math.abs(position - focus.Pos);
            return RingPriority(math.cmax(d), math.dot(d, d));
        }

        /// <inheritdoc cref="EvictionPriority(ref int3, ref Focus)" />
        [BurstCompile]
        public static float EvictionPriority(ref int2 position, ref Focus focus){
            int2 d = math.abs(position - focus.Pos.xz);
            return RingPriority(math.cmax(d), math.dot(d, d));
        }

        private static float RingPriority(int ring, int sqrDistance){
            // sqrDistance <= 3 ring² in 3D (2 ring² in 2D)
            return -(ring + sqrDistance / (3f * ring * ring + 1f));
        }

        public readonly struct Focus : IEquatable<Focus>{
            public readonly int3 Pos;
            public readonly float3 Forward;

            public Focus(int3 pos, float3 forward){
                Pos = pos;
                Forward = Quantize45(ref forward);
            }

            public bool Equals(Focus other){
                return Pos.Equals(other.Pos) && Forward.Equals(other.Forward);
            }

            public override bool Equals(object obj){
                return obj is Focus other && Equals(other);
            }

            public override int GetHashCode(){
                unchecked{
                    return (Pos.GetHashCode() * 397) ^ Forward.GetHashCode();
                }
            }
        }
    }
}
