using System.Runtime.CompilerServices;
using static Engine.Scripts.Utils.VoxelConstants;

namespace Engine.Scripts.Data{
    /// <summary>
    ///     LGT-01: packing of the 16-bit voxel light, sky(4) | R(4) | G(4) | B(4), levels 0–15 per channel.
    ///     Channel index <c>c</c> occupies bits <c>4c … 4c + 3</c>: 0 = blue, 1 = green, 2 = red, 3 = sky.
    ///     The same layout is decompressed into the upper 16 bits of the per-voxel uint on the GPU (LGT-08).
    /// </summary>
    public static class VoxelLight{
        public const int Blue = 0;
        public const int Green = 1;
        public const int Red = 2;
        public const int Sky = 3;
        public const int ChannelCount = 4;

        /// <summary>Full sky light, no block light.</summary>
        public const ushort FullSky = MaxLightLevel << (4 * Sky);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ushort Pack(int sky, int red, int green, int blue){
            return (ushort)(((sky & 0xF) << (4 * Sky)) | ((red & 0xF) << (4 * Red)) |
                            ((green & 0xF) << (4 * Green)) | ((blue & 0xF) << (4 * Blue)));
        }

        /// <summary>Block light only (sky 0), e.g. a voxel's emission.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ushort PackRgb(int red, int green, int blue){
            return Pack(0, red, green, blue);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Get(ushort light, int channel){
            return (light >> (4 * channel)) & 0xF;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ushort With(ushort light, int channel, int level){
            int shift = 4 * channel;
            return (ushort)((light & ~(0xF << shift)) | ((level & 0xF) << shift));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int GetSky(ushort light){
            return Get(light, Sky);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int GetRed(ushort light){
            return Get(light, Red);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int GetGreen(ushort light){
            return Get(light, Green);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int GetBlue(ushort light){
            return Get(light, Blue);
        }
    }
}
