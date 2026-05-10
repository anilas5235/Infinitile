using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Engine.Scripts.Utils.Extensions
{
    /// <summary>
    /// Burst-compatible math extensions for int, int2, int3, bool3, and vector conversions.
    /// Focuses on flattening and size operations for chunk and voxel calculations.
    /// </summary>
    [GenerateTestsForBurstCompatibility]
    public static class BurstMathExtensions
    {
        /// <summary>
        /// Returns (2r + 1)^3, the number of cells in a cubic area with radius r.
        /// </summary>
        [BurstCompile]
        public static int CubedSize(this int r)
        {
            return (2 * r + 1) * (2 * r + 1) * (2 * r + 1);
        }

        /// <summary>
        /// Returns (2r + 1)^2, the number of cells in a square area with radius r.
        /// </summary>
        [BurstCompile]
        public static int SquareSize(this int r)
        {
            return (2 * r + 1) * (2 * r + 1);
        }

        /// <summary>
        /// Returns (2r.x + 1) * (2r.y + 1) * (2r.z + 1) for an anisotropic radius.
        /// </summary>
        [BurstCompile]
        public static int CubedSize(this int3 r)
        {
            return (2 * r.x + 1) * (2 * r.y + 1) * (2 * r.z + 1);
        }

        /// <summary>
        /// Flattens 2D coordinates (x, y) into a one-dimensional index using the dimensions in <paramref name="vec" />.
        /// </summary>
        [BurstCompile]
        public static int Flatten(this int2 vec, int x, int y)
        {
            return x * vec.y +
                   y;
        }

        /// <summary>
        /// Flattens 3D coordinates (x, y, z) into a one-dimensional index (x * Y * Z + z * Y + y).
        /// </summary>
        [BurstCompile]
        public static int Flatten(this int3 vec, int x, int y, int z)
        {
            return x * vec.y * vec.z +
                   z * vec.y +
                   y;
        }

        /// <summary>
        /// Flattens a 3D position using an <see cref="int3" /> position struct.
        /// </summary>
        [BurstCompile]
        public static int Flatten(this int3 vec, in int3 pos)
        {
            return vec.Flatten(pos.x, pos.y, pos.z);
        }

        /// <summary>
        /// Reduces a <see cref="bool3" /> using logical OR.
        /// </summary>
        [BurstCompile]
        public static bool OrReduce(this bool3 val)
        {
            return val.x || val.y || val.z;
        }

        /// <summary>
        /// Reduces a <see cref="bool3" /> using logical AND.
        /// </summary>
        [BurstCompile]
        public static bool AndReduce(this bool3 val)
        {
            return val is { x: true, y: true, z: true };
        }

        /// <summary>
        /// Returns the volume of an <see cref="int3" /> vector (x * y * z).
        /// </summary>
        [BurstCompile]
        public static int Size(this int3 vec)
        {
            return vec.x * vec.y * vec.z;
        }

        /// <summary>
        /// Converts <see cref="int3" /> to <see cref="Vector3Int" />.
        /// </summary>
        [BurstCompile]
        public static Vector3Int GetVector3Int(this int3 vec)
        {
            return new Vector3Int(vec.x, vec.y, vec.z);
        }

        /// <summary>
        /// Converts <see cref="int3" /> to <see cref="Vector3" />.
        /// </summary>
        [BurstCompile]
        public static Vector3 GetVector3(this int3 vec)
        {
            return new Vector3(vec.x, vec.y, vec.z);
        }

        /// <summary>
        /// Converts <see cref="int2" /> to <see cref="Vector2" />.
        /// </summary>
        [BurstCompile]
        public static Vector3 GetVector2(this int2 vec)
        {
            return new Vector3(vec.x, vec.y);
        }
    }

    /// <summary>
    /// Non-Burst math extensions for distance and component-wise multiplication/division.
    /// </summary>
    public static class MathExtension
    {
        /// <summary>
        /// Returns the squared magnitude of an <see cref="int3" /> vector.
        /// </summary>
        public static int SqrMagnitude(this int3 vec)
        {
            return vec.x * vec.x + vec.y * vec.y + vec.z * vec.z;
        }

        /// <summary>
        /// Returns the squared magnitude of an <see cref="int2" /> vector.
        /// </summary>
        public static int SqrMagnitude(this int2 vec)
        {
            return vec.x * vec.x + vec.y * vec.y;
        }

        /// <summary>
        /// Multiplies two <see cref="int3" /> values component-wise.
        /// </summary>
        public static int3 MemberMultiply(this int3 a, int3 b)
        {
            return new int3(a.x * b.x, a.y * b.y, a.z * b.z);
        }

        /// <summary>
        /// Multiplies an <see cref="int3" /> by individual component values.
        /// </summary>
        public static int3 MemberMultiply(this int3 a, int x, int y, int z)
        {
            return new int3(a.x * x, a.y * y, a.z * z);
        }

        /// <summary>
        /// Divides two <see cref="int3" /> values component-wise.
        /// </summary>
        public static int3 MemberDivide(this int3 a, int3 b)
        {
            return new int3(a.x / b.x, a.y / b.y, a.z / b.z);
        }


        /// <summary>
        /// Returns the larger of two <see cref="half" /> values.
        /// </summary>
        public static half Max(half a, half b)
        {
            return a > b ? a : b;
        }
    }

    /// <summary>
    /// Common integer and floating-point vector constants used throughout the engine.
    /// </summary>
    public static class VectorConstants
    {
        /// <summary>Represents the zero vector (0, 0, 0).</summary>
        public static readonly int3 Int3Zero = new(0, 0, 0);

        /// <summary>Represents the one vector (1, 1, 1).</summary>
        public static readonly int3 Int3One = new(1, 1, 1);

        /// <summary>Represents the up vector (0, 1, 0).</summary>
        public static readonly int3 Int3Up = new(0, 1, 0);

        /// <summary>Represents the down vector (0, -1, 0).</summary>
        public static readonly int3 Int3Down = new(0, -1, 0);

        /// <summary>Represents the right vector (1, 0, 0).</summary>
        public static readonly int3 Int3Right = new(1, 0, 0);

        /// <summary>Represents the left vector (-1, 0, 0).</summary>
        public static readonly int3 Int3Left = new(-1, 0, 0);

        /// <summary>Represents the forward vector (0, 0, 1).</summary>
        public static readonly int3 Int3Forward = new(0, 0, 1);

        /// <summary>Represents the backward vector (0, 0, -1).</summary>
        public static readonly int3 Int3Backward = new(0, 0, -1);

        public static readonly int3[] Int3Directions =
        {
            Int3Forward,
            Int3Backward,
            Int3Right,
            Int3Left,
            Int3Up,
            Int3Down
        };

        /// <summary>Represents the zero vector (0, 0, 0).</summary>
        public static readonly float3 Float3Zero = new(0, 0, 0);

        /// <summary>Represents the one vector (1, 1, 1).</summary>
        public static readonly float3 Float3One = new(1, 1, 1);

        /// <summary>Represents the up vector (0, 1, 0).</summary>
        public static readonly float3 Float3Up = new(0, 1, 0);

        /// <summary>Represents the down vector (0, -1, 0).</summary>
        public static readonly float3 Float3Down = new(0, -1, 0);

        /// <summary>Represents the right vector (1, 0, 0).</summary>
        public static readonly float3 Float3Right = new(1, 0, 0);

        /// <summary>Represents the left vector (-1, 0, 0).</summary>
        public static readonly float3 Float3Left = new(-1, 0, 0);

        /// <summary>Represents the forward vector (0, 0, 1).</summary>
        public static readonly float3 Float3Forward = new(0, 0, 1);

        /// <summary>Represents the backward vector (0, 0, -1).</summary>
        public static readonly float3 Float3Backward = new(0, 0, -1);
    }

    /// <summary>
    /// Vector conversion, normalization, and direction helper extensions.
    /// </summary>
    public static class VectorExtension
    {
        /// <summary>
        /// Converts <see cref="Vector3Int" /> to <see cref="int3" />.
        /// </summary>
        public static int3 Int3(this Vector3Int vec)
        {
            return new int3(vec.x, vec.y, vec.z);
        }

        /// <summary>
        /// Converts a <see cref="Vector3" /> to <see cref="int3" /> using floor rounding.
        /// </summary>
        public static int3 Int3(this Vector3 vec)
        {
            return Vector3Int.FloorToInt(vec).Int3();
        }

        /// <summary>
        /// Converts <see cref="Vector3" /> to <see cref="float3" />.
        /// </summary>
        public static float3 Float3(this Vector3 vec)
        {
            return new float3(vec.x, vec.y, vec.z);
        }

        /// <summary>
        /// Converts a <see cref="Vector3" /> to <see cref="Vector3Int" /> using floor rounding.
        /// </summary>
        public static Vector3Int V3Int(this Vector3 vec)
        {
            return Vector3Int.FloorToInt(vec);
        }

        /// <summary>
        /// Converts <see cref="int2" /> to <see cref="float2" />.
        /// </summary>
        public static float2 Float2(this int2 vec)
        {
            return new float2(vec.x, vec.y);
        }

        /// <summary>
        /// Converts <see cref="Vector2" /> to <see cref="float2" />.
        /// </summary>
        public static float2 Float2(this Vector2 vec)
        {
            return new float2(vec.x, vec.y);
        }

        /// <summary>
        /// Normalizes a <see cref="float3" />.
        /// </summary>
        public static float3 Normalized(this float3 vec)
        {
            return math.normalize(vec);
        }
    }
}