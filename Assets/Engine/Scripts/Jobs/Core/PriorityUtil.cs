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
                Forward = forward;
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

            float3 dir = math.normalize(position - (float3)focus.Pos);

            return DistPriority(dist, ref dir, ref focus);
        }

        [BurstCompile]
        public static float DistPriority(float dist, ref float3 dir, ref Focus focus)
        {
            return dist * (3f - 2f *math.dot(focus.Forward, dir));
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

            float3 dir = math.normalize(new float3(position.x, 0f, position.y) - new float3(focus.Pos.x, 0f, focus.Pos.z));

            return DistPriority(dist, ref dir, ref focus);
        }
    }
}