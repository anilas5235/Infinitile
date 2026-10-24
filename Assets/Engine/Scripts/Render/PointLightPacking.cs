using Engine.Scripts.Data;
using Unity.Mathematics;

namespace Engine.Scripts.Render{
    /// <summary>
    ///     C# port of the per-corner light and depth fade accessors in VoxelCommon.hlsl and of the corner averaging
    ///     in PointBuilder.compute, kept in step with the shaders so tests can check the packing round trip.
    ///     <c>PointData.packed.y</c> = sky | red &lt;&lt; 16, <c>packed.w</c> = green | blue &lt;&lt; 16; each 16-bit
    ///     channel holds four 4-bit corners in the order up-left, up-right, down-right, down-left.
    ///     <c>packed.z</c> = ao | depth fade (quarter blocks) &lt;&lt; 8 | surface alpha (unorm8) &lt;&lt; 16 | glow &lt;&lt; 24.
    /// </summary>
    internal static class PointLightPacking{
        internal const int OpaqueAttenuation = 15;

        internal static uint4 UnpackCorners(uint bits){
            return new uint4(bits & 0xF, bits >> 4 & 0xF, bits >> 8 & 0xF, bits >> 12 & 0xF);
        }

        internal static uint PackCorners(uint4 corners){
            return (corners.x & 0xF) | (corners.y & 0xF) << 4 | (corners.z & 0xF) << 8 | (corners.w & 0xF) << 12;
        }

        internal static uint4 GetSkyLight(uint4 packed){
            return UnpackCorners(packed.y & 0xFFFF);
        }

        internal static uint4 GetRedLight(uint4 packed){
            return UnpackCorners(packed.y >> 16);
        }

        internal static uint4 GetGreenLight(uint4 packed){
            return UnpackCorners(packed.w & 0xFFFF);
        }

        internal static uint4 GetBlueLight(uint4 packed){
            return UnpackCorners(packed.w >> 16);
        }

        /// <summary>Writes the four corners' voxel light values (sky|R|G|B each) into y and w.</summary>
        internal static void SetCornerLight(ref uint4 packed, uint4 cornerLight){
            uint4 sky = cornerLight >> 12 & 0xF;
            uint4 red = cornerLight >> 8 & 0xF;
            uint4 green = cornerLight >> 4 & 0xF;
            uint4 blue = cornerLight & 0xF;
            packed.y = PackCorners(sky) | PackCorners(red) << 16;
            packed.w = PackCorners(green) | PackCorners(blue) << 16;
        }

        internal static float GetDepthFadeDist(uint4 packed){
            return (packed.z >> 8 & 0xFF) * 0.25f;
        }

        internal static void SetDepthFadeDist(ref uint4 packed, float depthFadeDist){
            uint quarters = depthFadeDist > 0f ? math.clamp((uint)math.round(depthFadeDist * 4f), 1u, 255u) : 0u;
            packed.z = packed.z & 0xFFFF00FF | quarters << 8;
        }

        internal static float GetSurfaceAlpha(uint4 packed){
            return (packed.z >> 16 & 0xFF) / 255f;
        }

        internal static void SetSurfaceAlpha(ref uint4 packed, uint surfaceAlpha){
            packed.z = packed.z & 0xFF00FFFF | (surfaceAlpha & 0xFF) << 16;
        }

        /// <summary>
        ///     Light at one face corner: per channel the rounded average over the front, the two side and the diagonal
        ///     voxel, skipping opaque ones (the diagonal also when both sides are opaque); the front voxel's light when
        ///     every voxel is skipped.
        /// </summary>
        internal static ushort CornerLight(ushort front, bool frontOpaque, ushort sideA, bool sideAOpaque,
            ushort sideB, bool sideBOpaque, ushort diagonal, bool diagonalOpaque){
            bool openD = (!sideAOpaque || !sideBOpaque) && !diagonalOpaque;
            uint4 sum = 0;
            uint n = 0;
            if (!frontOpaque){ sum += Unpack(front); n++; }
            if (!sideAOpaque){ sum += Unpack(sideA); n++; }
            if (!sideBOpaque){ sum += Unpack(sideB); n++; }
            if (openD){ sum += Unpack(diagonal); n++; }

            if (n == 0) return front;
            uint4 avg = (sum + n / 2) / n;
            return VoxelLight.Pack((int)avg.x, (int)avg.y, (int)avg.z, (int)avg.w);
        }

        private static uint4 Unpack(ushort light){
            return new uint4((uint)VoxelLight.GetSky(light), (uint)VoxelLight.GetRed(light),
                (uint)VoxelLight.GetGreen(light), (uint)VoxelLight.GetBlue(light));
        }
    }
}
