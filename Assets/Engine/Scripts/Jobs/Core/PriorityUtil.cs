using System;
using Engine.Scripts.Utils.Extensions;
using Unity.Burst;
using Unity.Mathematics;

namespace Engine.Scripts.Jobs.Core
{
    /// <summary>
    /// Utility class for calculating priority values based on distance for job scheduling.
    /// Uses squared magnitude calculations for efficient priority determination.
    /// </summary>
    [BurstCompile]
    public static class PriorityUtil
    {
        public readonly struct Focus : IEquatable<Focus>
        {
            public readonly int3 Pos;
            public readonly float3 Forward;

            public Focus(int3 pos, float3 forward)
            {
                Pos = pos;
                Forward = Quantize45(ref forward);
            }

            public bool Equals(Focus other)
            {
                return Pos.Equals(other.Pos) && Forward.Equals(other.Forward);
            }

            public override bool Equals(object obj)
            {
                return obj is Focus other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    return (Pos.GetHashCode() * 397) ^ Forward.GetHashCode();
                }
            }
        }
        
        private static float3 Quantize45(ref float3 v)
        {
            v = math.normalize(v);

            // 45° Schritte
            const float s = 0.70710678f;

            float3 q;

            q.x = QuantizeAxis(v.x, s);
            q.y = QuantizeAxis(v.y, s);
            q.z = QuantizeAxis(v.z, s);

            return math.normalize(q);
        }

        private static float QuantizeAxis(float a, float step)
        {
            return a switch
            {
                > 0.923f => 1f,
                > 0.383f => step,
                > -0.383f => 0f,
                > -0.923f => -step,
                _ => -1f
            };
        }

        /// <summary>
        /// Calculates priority based on squared distance from focus position for 3D coordinates.
        /// Lower values indicate higher priority (closer to focus).
        /// </summary>
        /// <param name="position">The position to calculate priority for.</param>
        /// <param name="focus">The focus position (reference point).</param>
        /// <returns>The squared distance as a priority value.</returns>
        [BurstCompile]
        public static float DistPriority(ref int3 position, ref Focus focus)
        {
            int dist = (position - focus.Pos).SqrMagnitude();
            if(dist == 0) return 0f;
            float3 dir = math.normalize(position - (float3)focus.Pos);

            float dot = math.dot(focus.Forward, dir);

            return dist * (3f - 2f * dot);
        }

        [BurstCompile]
        public static float ReVerseDistPriority(ref int3 position, ref Focus focus)
        {
            return -DistPriority(ref position, ref focus);
        }

        /// <summary>
        /// Calculates priority based on squared distance from focus position for 2D coordinates.
        /// Lower values indicate higher priority (closer to focus).
        /// </summary>
        /// <param name="position">The 2D position to calculate priority for.</param>
        /// <param name="focus">The 3D focus position (only xz components used).</param>
        /// <returns>The squared distance as a priority value.</returns>
        [BurstCompile]
        public static float DistPriority(ref int2 position, ref Focus focus)
        {
            int dist = (position - focus.Pos.xz).SqrMagnitude();
            if(dist == 0) return 0f;
            
            float2 dir = math.normalize(position - focus.Pos.xz);
            float2 forward = math.normalize(focus.Forward.xz);
            
            float dot = math.dot(forward, dir);
            
            return dist * (3f - 2f * dot);
        }

        [BurstCompile]
        public static float ReVerseDistPriority(ref int2 position, ref Focus focus)
        {
            return -DistPriority(ref position, ref focus);
        }
    }
}