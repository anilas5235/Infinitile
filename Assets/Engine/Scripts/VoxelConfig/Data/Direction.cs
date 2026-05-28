using System;
using Unity.Mathematics;
using UnityEngine;
using static Engine.Scripts.Utils.Extensions.VectorConstants;

namespace Engine.Scripts.VoxelConfig.Data
{
    /// <summary>
    ///     Cardinal directions used for voxel face orientation and neighbor queries.
    /// </summary>
    public enum Direction
    {
        Right, // +x direction
        Left, // -x direction
        Up, // +y direction
        Down, // -y direction
        Forward, // z+ direction
        Backward // -z direction
    }

    /// <summary>
    ///     Extension helpers for converting <see cref="Direction" /> values to vectors and getting opposite directions.
    /// </summary>
    public static class DirectionUtils
    {
        /// <summary>
        /// Determines a direction from an <see cref="int3" /> normal vector based on its dominant component.
        /// </summary>
        /// <param name="vec">The vector to convert.</param>
        /// <returns>The matching cardinal direction.</returns>
        public static Direction ToDirection(this int3 vec)
        {
            if (vec.x < vec.y && vec.z < vec.y) return Direction.Up;
            if (vec.x > vec.y && vec.z > vec.y) return Direction.Down;
            if (vec.y < vec.x && vec.z < vec.x) return Direction.Right;
            if (vec.y > vec.x && vec.z > vec.x) return Direction.Left;
            if (vec.x < vec.z && vec.y < vec.z) return Direction.Forward;
            if (vec.x > vec.z && vec.y > vec.z) return Direction.Backward;

            throw new Exception("Invalid direction vector");
        }

        /// <summary>
        /// Converts a direction to its corresponding <see cref="int3" /> unit vector.
        /// </summary>
        /// <param name="direction">The direction to convert.</param>
        /// <returns>The matching unit vector.</returns>
        public static int3 ToInt3(this Direction direction)
        {
            return direction switch
            {
                Direction.Right => Int3Right,
                Direction.Left => Int3Left,
                Direction.Up => Int3Up,
                Direction.Down => Int3Down,
                Direction.Forward => Int3Forward,
                Direction.Backward => Int3Backward,
                _ => throw new ArgumentOutOfRangeException(nameof(direction), direction, null)
            };
        }

        /// <summary>
        /// Gets a relative up vector for the given direction.
        /// </summary>
        /// <param name="direction">The reference direction.</param>
        /// <returns>A vector representing the relative up direction.</returns>
        public static int3 RelativeUp(this Direction direction)
        {
            return direction switch
            {
                Direction.Up => Int3Forward,
                Direction.Down => Int3Forward,
                _ => Int3Up
            };
        }

        /// <summary>
        /// Gets a relative down vector for the given direction.
        /// </summary>
        /// <param name="direction">The reference direction.</param>
        /// <returns>A vector representing the relative down direction.</returns>
        public static int3 RelativeDown(this Direction direction)
        {
            return -direction.RelativeUp();
        }

        /// <summary>
        /// Gets a relative right vector for the given direction.
        /// </summary>
        /// <param name="direction">The reference direction.</param>
        /// <returns>A vector representing the relative right direction.</returns>
        public static int3 RelativeRight(this Direction direction)
        {
            return direction switch
            {
                Direction.Right => Int3Forward,
                Direction.Left => Int3Backward,
                Direction.Forward => Int3Left,
                Direction.Backward => Int3Right,
                Direction.Up => Int3Right,
                Direction.Down => Int3Left,
                _ => throw new ArgumentOutOfRangeException(nameof(direction), direction, null)
            };
        }

        /// <summary>
        /// Gets a relative left vector for the given direction.
        /// </summary>
        /// <param name="direction">The reference direction.</param>
        /// <returns>A vector representing the relative left direction.</returns>
        public static int3 RelativeLeft(this Direction direction)
        {
            return -direction.RelativeRight();
        }

        /// <summary>
        ///     Converts a <see cref="Direction" /> into a corresponding <see cref="Vector3Int" /> unit vector.
        /// </summary>
        /// <param name="direction">Direction to convert.</param>
        /// <returns>Unit vector matching the given direction.</returns>
        public static Vector3Int GetVector(this Direction direction)
        {
            return direction switch
            {
                Direction.Up => Vector3Int.up,
                Direction.Down => Vector3Int.down,
                Direction.Forward => Vector3Int.forward,
                Direction.Backward => Vector3Int.back,
                Direction.Right => Vector3Int.right,
                Direction.Left => Vector3Int.left,

                _ => throw new ArgumentOutOfRangeException(nameof(direction), direction, null)
            };
        }

        /// <summary>
        ///     Converts a <see cref="Direction" /> into a corresponding <see cref="int3" /> unit vector.
        /// </summary>
        /// <param name="direction">Direction to convert.</param>
        /// <returns>Unit vector matching the given direction.</returns>
        public static int3 GetInt3(this Direction direction)
        {
            return direction switch
            {
                Direction.Up => new int3(0, 1, 0),
                Direction.Down => new int3(0, -1, 0),
                Direction.Forward => new int3(0, 0, 1),
                Direction.Backward => new int3(0, 0, -1),
                Direction.Right => new int3(1, 0, 0),
                Direction.Left => new int3(-1, 0, 0),

                _ => throw new ArgumentOutOfRangeException(nameof(direction), direction, null)
            };
        }

        /// <summary>
        ///     Gets the opposite direction for the given <see cref="Direction" /> value.
        /// </summary>
        /// <param name="direction">Direction whose opposite should be returned.</param>
        /// <returns>Opposite direction.</returns>
        public static Direction GetOpposite(this Direction direction)
        {
            return direction switch
            {
                Direction.Right => Direction.Left,
                Direction.Left => Direction.Right,
                Direction.Up => Direction.Down,
                Direction.Down => Direction.Up,
                Direction.Forward => Direction.Backward,
                Direction.Backward => Direction.Forward,
                _ => throw new ArgumentOutOfRangeException(nameof(direction), direction, null)
            };
        }
    }
}