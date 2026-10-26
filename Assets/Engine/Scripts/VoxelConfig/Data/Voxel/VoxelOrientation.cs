using Engine.Scripts.VoxelConfig.Data.Mesh;
using Unity.Mathematics;

namespace Engine.Scripts.VoxelConfig.Data.Voxel{
    /// <summary>How a voxel may be turned when it is placed; the authored asset is the unturned orientation.</summary>
    public enum VoxelFacing : byte{
        /// <summary>No facing: one orientation (with the top/bottom toggle off).</summary>
        None,

        /// <summary>Lies along one of the three axes (Y as authored, X, Z), like a log.</summary>
        Axis,

        /// <summary>The front (+Z as authored) turns to one of the four horizontal directions.</summary>
        Horizontal,

        /// <summary>The front turns to one of the four horizontal directions or straight up or down.</summary>
        Full
    }

    /// <summary>
    ///     Orientations of rotatable voxels. An orientation is one byte: the facing index in the low bits plus
    ///     <see cref="UpsideDown" />. Facing index 0 with the bit clear is the authored orientation (axis Y, front +Z,
    ///     lower half), so every voxel's base id has orientation 0. The rotations are exact integer matrices; a variant's
    ///     shape is its base shape turned about the voxel centre by <see cref="Rotation" />.
    /// </summary>
    public static class VoxelOrientation{
        /// <summary>Set for the upper (upside-down) variant of a voxel with the top/bottom toggle.</summary>
        public const byte UpsideDown = 1 << 3;

        /// <summary>Mask of the facing index in an orientation byte.</summary>
        public const byte FacingMask = UpsideDown - 1;

        /// <summary>Orientation bytes are below this, so a per-base table needs this many slots.</summary>
        public const int Slots = UpsideDown << 1;

        // Facing indices of the Axis mode
        public const byte AxisY = 0;
        public const byte AxisX = 1;
        public const byte AxisZ = 2;

        // Facing indices of the Horizontal and Full modes (Horizontal uses the first four)
        public const byte FrontPosZ = 0;
        public const byte FrontPosX = 1;
        public const byte FrontNegZ = 2;
        public const byte FrontNegX = 3;
        public const byte FrontPosY = 4;
        public const byte FrontNegY = 5;

        /// <summary>Number of facings of a mode: 1 / 3 / 4 / 6.</summary>
        public static int FacingCount(VoxelFacing facing){
            return facing switch{
                VoxelFacing.Axis => 3,
                VoxelFacing.Horizontal => 4,
                VoxelFacing.Full => 6,
                _ => 1
            };
        }

        /// <summary>Number of orientations (the base included): facings × 2 with the top/bottom toggle.</summary>
        public static int Count(VoxelFacing facing, bool topBottom){
            return FacingCount(facing) * (topBottom ? 2 : 1);
        }

        /// <summary>Orientation byte of a facing index and half.</summary>
        public static byte Make(int facingIndex, bool upsideDown){
            return (byte)(facingIndex | (upsideDown ? UpsideDown : 0));
        }

        /// <summary>
        ///     Position of an orientation in the variant order of its base: facing index, then top/bottom. 0 is the base;
        ///     -1 when the mode has no such orientation.
        /// </summary>
        public static int Index(VoxelFacing facing, bool topBottom, byte orientation){
            int facingIndex = orientation & FacingMask;
            bool upper = (orientation & UpsideDown) != 0;
            if (orientation >= Slots || facingIndex >= FacingCount(facing) || (upper && !topBottom)) return -1;
            return facingIndex * (topBottom ? 2 : 1) + (upper ? 1 : 0);
        }

        /// <summary>Orientation byte at a position of the variant order; the inverse of <see cref="Index" />.</summary>
        public static byte AtIndex(bool topBottom, int index){
            return topBottom ? Make(index >> 1, (index & 1) != 0) : (byte)index;
        }

        /// <summary>
        ///     Rotation of the authored shape for an orientation. The upper half is 180° about Z (the authored front
        ///     axis), applied before the facing: Axis X is 90° about Z, Axis Z 90° about X; the fronts +X, −Z, −X are
        ///     90°, 180°, 270° about Y, and front ±Y is ∓90° about X.
        /// </summary>
        public static int3x3 Rotation(VoxelFacing facing, byte orientation){
            int facingIndex = orientation & FacingMask;
            int3x3 turn = facing switch{
                VoxelFacing.Axis => facingIndex switch{
                    AxisX => RotZ90,
                    AxisZ => RotX90,
                    _ => int3x3.identity
                },
                VoxelFacing.Horizontal or VoxelFacing.Full => facingIndex switch{
                    FrontPosX => RotY90,
                    FrontNegZ => math.mul(RotY90, RotY90),
                    FrontNegX => math.mul(RotY90, math.mul(RotY90, RotY90)),
                    FrontPosY => math.transpose(RotX90),
                    FrontNegY => RotX90,
                    _ => int3x3.identity
                },
                _ => int3x3.identity
            };
            return (orientation & UpsideDown) != 0 ? math.mul(turn, RotZ180) : turn;
        }

        // Columns are the images of +X, +Y, +Z.
        private static readonly int3x3 RotZ90 = new(new int3(0, 1, 0), new int3(-1, 0, 0), new int3(0, 0, 1));
        private static readonly int3x3 RotX90 = new(new int3(1, 0, 0), new int3(0, 0, 1), new int3(0, -1, 0));
        private static readonly int3x3 RotY90 = new(new int3(0, 0, -1), new int3(0, 1, 0), new int3(1, 0, 0));
        private static readonly int3x3 RotZ180 = new(new int3(-1, 0, 0), new int3(0, -1, 0), new int3(0, 0, 1));

        /// <summary>Unit direction a draw condition stands for; zero for <see cref="QuadDrawCondition.Always" />.</summary>
        public static int3 Direction(QuadDrawCondition condition){
            return condition switch{
                QuadDrawCondition.Up => new int3(0, 1, 0),
                QuadDrawCondition.Down => new int3(0, -1, 0),
                QuadDrawCondition.Forward => new int3(0, 0, 1),
                QuadDrawCondition.Backward => new int3(0, 0, -1),
                QuadDrawCondition.Left => new int3(-1, 0, 0),
                QuadDrawCondition.Right => new int3(1, 0, 0),
                _ => int3.zero
            };
        }

        /// <summary>Draw condition of a unit axis direction; <see cref="QuadDrawCondition.Always" /> for anything else.</summary>
        public static QuadDrawCondition Condition(int3 direction){
            if (direction.y == 1) return QuadDrawCondition.Up;
            if (direction.y == -1) return QuadDrawCondition.Down;
            if (direction.z == 1) return QuadDrawCondition.Forward;
            if (direction.z == -1) return QuadDrawCondition.Backward;
            if (direction.x == -1) return QuadDrawCondition.Left;
            return direction.x == 1 ? QuadDrawCondition.Right : QuadDrawCondition.Always;
        }

        /// <summary>The draw condition a quad gets after the rotation; Always stays Always.</summary>
        public static QuadDrawCondition Remap(QuadDrawCondition condition, int3x3 rotation){
            return condition == QuadDrawCondition.Always
                ? QuadDrawCondition.Always
                : Condition(math.mul(rotation, Direction(condition)));
        }

        /// <summary>Name suffix of a variant for logs and debug output, e.g. <c>#x</c>, <c>#-z</c>, <c>#+xu</c>; empty for the base.</summary>
        public static string Suffix(VoxelFacing facing, byte orientation){
            int facingIndex = orientation & FacingMask;
            string facingName = facingIndex == 0
                ? ""
                : facing == VoxelFacing.Axis
                    ? facingIndex == AxisX ? "x" : "z"
                    : facingIndex switch{
                        FrontPosX => "+x",
                        FrontNegZ => "-z",
                        FrontNegX => "-x",
                        FrontPosY => "+y",
                        _ => "-y"
                    };
            string half = (orientation & UpsideDown) != 0 ? "u" : "";
            return facingName.Length + half.Length == 0 ? "" : "#" + facingName + half;
        }
    }
}
