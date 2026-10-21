using Engine.Scripts.Data;
using Engine.Scripts.Render;
using NUnit.Framework;
using Unity.Mathematics;

namespace Engine.Tests.EditMode{
    /// <summary>GPU light packing round trip through the C# port of the shader accessors.</summary>
    public class PointLightPackingTests{
        [Test]
        public void CornerLight_RoundTripsEveryChannelAndCorner([Values(1, 2, 3)] int seed){
            Random random = new((uint)seed);
            for (int i = 0; i < 1000; i++){
                ushort[] corners = new ushort[4];
                for (int c = 0; c < 4; c++)
                    corners[c] = VoxelLight.Pack(random.NextInt(16), random.NextInt(16), random.NextInt(16), random.NextInt(16));

                uint4 packed = new(random.NextUInt(), random.NextUInt(), random.NextUInt(), random.NextUInt());
                uint4 before = packed;
                PointLightPacking.SetCornerLight(ref packed, new uint4(corners[0], corners[1], corners[2], corners[3]));

                Assert.AreEqual(before.x, packed.x, "quad and texture index stay untouched");
                Assert.AreEqual(before.z, packed.z, "ao, depth fade and glow stay untouched");

                uint4 sky = PointLightPacking.GetSkyLight(packed);
                uint4 red = PointLightPacking.GetRedLight(packed);
                uint4 green = PointLightPacking.GetGreenLight(packed);
                uint4 blue = PointLightPacking.GetBlueLight(packed);
                for (int c = 0; c < 4; c++){
                    Assert.AreEqual(VoxelLight.GetSky(corners[c]), (int)sky[c], $"sky corner {c}");
                    Assert.AreEqual(VoxelLight.GetRed(corners[c]), (int)red[c], $"red corner {c}");
                    Assert.AreEqual(VoxelLight.GetGreen(corners[c]), (int)green[c], $"green corner {c}");
                    Assert.AreEqual(VoxelLight.GetBlue(corners[c]), (int)blue[c], $"blue corner {c}");
                }
            }
        }

        [Test]
        public void CornerLight_Layout_MatchesTheShaderLayout(){
            // y = sky (bits 0-15) | R (16-31); w = G (0-15) | B (16-31); corners ul, ur, dr, dl per channel.
            uint4 packed = 0;
            PointLightPacking.SetCornerLight(ref packed, new uint4(
                VoxelLight.Pack(1, 2, 3, 4), VoxelLight.Pack(5, 6, 7, 8),
                VoxelLight.Pack(9, 10, 11, 12), VoxelLight.Pack(13, 14, 15, 0)));
            Assert.AreEqual(0xD951u | 0xEA62u << 16, packed.y);
            Assert.AreEqual(0xFB73u | 0x0C84u << 16, packed.w);
        }

        [Test]
        public void CornerAverage_RoundsPerChannel(){
            ushort a = VoxelLight.Pack(15, 4, 0, 1);
            ushort b = VoxelLight.Pack(14, 4, 0, 0);
            ushort c = VoxelLight.Pack(14, 3, 1, 0);
            ushort d = VoxelLight.Pack(12, 3, 1, 0);
            ushort result = PointLightPacking.CornerLight(a, false, b, false, c, false, d, false);
            Assert.AreEqual(VoxelLight.Pack(14, 4, 1, 0), result); // (55+2)/4, (14+2)/4, (2+2)/4, (1+2)/4
        }

        [Test]
        public void CornerAverage_SkipsOpaqueVoxels(){
            ushort lit = VoxelLight.Pack(15, 0, 0, 0);
            ushort emitter = VoxelLight.PackRgb(15, 9, 3); // an opaque lamp holds its emission but is skipped
            ushort result = PointLightPacking.CornerLight(lit, false, emitter, true, lit, false, emitter, true);
            Assert.AreEqual(lit, result);
        }

        [Test]
        public void CornerAverage_DiagonalBehindTwoOpaqueSides_IsSkipped(){
            ushort dark = 0;
            ushort bright = VoxelLight.FullSky;
            ushort result = PointLightPacking.CornerLight(dark, false, 0, true, 0, true, bright, false);
            Assert.AreEqual(dark, result, "light must not leak through a closed corner");

            result = PointLightPacking.CornerLight(dark, false, dark, false, 0, true, bright, false);
            Assert.AreEqual(VoxelLight.Pack(5, 0, 0, 0), result, "one open side lets the diagonal count: (0+0+15+1)/3");
        }

        [Test]
        public void CornerAverage_AllOpaque_FallsBackToTheFrontVoxel(){
            ushort front = VoxelLight.PackRgb(15, 0, 0);
            ushort result = PointLightPacking.CornerLight(front, true, 1, true, 2, true, 3, true);
            Assert.AreEqual(front, result);
        }
    }
}
