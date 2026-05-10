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
        /// <summary>
        /// Calculates priority based on squared distance from focus position for 3D coordinates.
        /// Lower values indicate higher priority (closer to focus).
        /// </summary>
        /// <param name="position">The position to calculate priority for.</param>
        /// <param name="focus">The focus position (reference point).</param>
        /// <returns>The squared distance as a priority value.</returns>
        [BurstCompile]
        public static int DistPriority(ref int3 position, ref int3 focus)
        {
            return (position - focus).SqrMagnitude();
        }

        /// <summary>
        /// Calculates priority based on squared distance from focus position for 2D coordinates.
        /// The focus Z component is extracted from focus.xz for comparison.
        /// Lower values indicate higher priority (closer to focus).
        /// </summary>
        /// <param name="position">The 2D position to calculate priority for.</param>
        /// <param name="focus">The 3D focus position (only xz components used).</param>
        /// <returns>The squared distance as a priority value.</returns>
        [BurstCompile]
        public static int DistPriority(ref int2 position, ref int3 focus)
        {
            return (position - focus.xz).SqrMagnitude();
        }
    }
}