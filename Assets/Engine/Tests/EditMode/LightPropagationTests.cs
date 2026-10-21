using Engine.Scripts.Data;
using Engine.Scripts.Jobs.Light;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using static Engine.Scripts.Utils.VoxelConstants;
using static Engine.Tests.EditMode.LightTestWorld;

namespace Engine.Tests.EditMode{
    /// <summary>Light propagation: sky light, emitters, attenuation and the chunk margin.</summary>
    public partial class LightPropagationTests{
        private static readonly int2 Origin = int2.zero;

        private static ushort UndergroundWithCavity(int3 p, int3 min, int3 max){
            return math.all(p >= min) && math.all(p <= max) ? Air : Stone;
        }

        [Test]
        public void Emitter_InOpenAir_FallsOffByManhattanDistance(){
            int3 lamp = new(16, 100, 16);
            using LightTestWorld world = new(1, p => p.Equals(lamp) ? RedLamp : Air);
            ChunkLightData light = world.LightChunk(Origin);

            Assert.AreEqual(15, VoxelLight.GetRed(LightAt(light, lamp)));
            for (int d = 1; d <= 16; d++){
                int expected = math.max(0, 15 - d);
                Assert.AreEqual(expected, VoxelLight.GetRed(LightAt(light, lamp + new int3(d, 0, 0))), $"+x {d}");
                Assert.AreEqual(expected, VoxelLight.GetRed(LightAt(light, lamp - new int3(0, d, 0))), $"-y {d}");
            }

            Assert.AreEqual(15 - 6, VoxelLight.GetRed(LightAt(light, lamp + new int3(2, 3, -1))));
            Assert.AreEqual(15, VoxelLight.GetSky(LightAt(light, lamp + new int3(1, 0, 0))), "open air is sky 15");
            light.Dispose();
        }

        [Test]
        public void Emitter_NearChunkBorder_LightsNeighbourChunkEdge(){
            // The lamp is in chunk (1, 0); the centre chunk only sees its light through the job's margin.
            int3 lamp = new(34, 100, 5);
            using LightTestWorld world = new(2, p => p.Equals(lamp) ? RedLamp : Air);
            ChunkLightData light = world.LightChunk(Origin);
            Assert.AreEqual(15 - 3, VoxelLight.GetRed(LightAt(light, new int3(31, 100, 5))));
            Assert.AreEqual(15 - 14, VoxelLight.GetRed(LightAt(light, new int3(20, 100, 5))));
            Assert.AreEqual(0, VoxelLight.GetRed(LightAt(light, new int3(19, 100, 5))));
            light.Dispose();
        }

        [Test]
        public void OpaqueWall_Blocks(){
            int3 lamp = new(6, 50, 6);
            using LightTestWorld world = new(1, p => {
                if (p.Equals(lamp)) return RedLamp;
                if (p.x == 10) return Stone; // wall between the two halves of the cave
                return UndergroundWithCavity(p, new int3(2, 46, 2), new int3(18, 54, 10));
            });
            ChunkLightData light = world.LightChunk(Origin);

            Assert.AreEqual(15 - 3, VoxelLight.GetRed(LightAt(light, new int3(9, 50, 6))));
            Assert.AreEqual(0, LightAt(light, new int3(10, 50, 6)), "the wall itself holds 0");
            for (int x = 11; x <= 18; x++)
                Assert.AreEqual(0, LightAt(light, new int3(x, 50, 6)), $"behind the wall x {x}");
            light.Dispose();
        }

        [TestCase(Leaves, 1)]
        [TestCase(Water, 2)]
        [TestCase(Glass, 0)]
        public void Medium_AttenuatesByItsValue(ushort medium, int attenuation){
            int3 lamp = new(4, 50, 8);
            using LightTestWorld world = new(1, p => {
                if (p.Equals(lamp)) return RedLamp;
                return p.y == 50 && p.z == 8 && p.x > 4 && p.x < 30 ? medium : Stone; // a sealed 1 × 1 tunnel
            });
            ChunkLightData light = world.LightChunk(Origin);

            for (int d = 1; d <= 15; d++){
                int expected = math.max(0, 15 - d * (1 + attenuation));
                Assert.AreEqual(expected, VoxelLight.GetRed(LightAt(light, lamp + new int3(d, 0, 0))), $"step {d}");
            }

            light.Dispose();
        }

        [Test]
        public void Channels_AreIndependent(){
            int3 red = new(8, 60, 8);
            int3 lava = new(24, 60, 24);
            using LightTestWorld world = new(1, p => {
                if (p.Equals(red)) return RedLamp;
                if (p.Equals(lava)) return Lava;
                return UndergroundWithCavity(p, new int3(1, 55, 1), new int3(30, 65, 30));
            });
            ChunkLightData light = world.LightChunk(Origin);

            ushort nearRed = LightAt(light, red + new int3(1, 0, 0));
            Assert.AreEqual(14, VoxelLight.GetRed(nearRed));
            Assert.AreEqual(0, VoxelLight.GetGreen(nearRed), "a red emitter leaves green at 0");
            Assert.AreEqual(0, VoxelLight.GetBlue(nearRed), "a red emitter leaves blue at 0");
            Assert.AreEqual(0, VoxelLight.GetSky(nearRed), "sealed cave has no sky");

            ushort nearLava = LightAt(light, lava + new int3(0, 2, 0));
            Assert.AreEqual(13, VoxelLight.GetRed(nearLava));
            Assert.AreEqual(7, VoxelLight.GetGreen(nearLava));
            Assert.AreEqual(1, VoxelLight.GetBlue(nearLava));
            Assert.AreEqual(VoxelLight.PackRgb(15, 9, 3), LightAt(light, lava), "an opaque emitter holds its emission");
            light.Dispose();
        }

        [Test]
        public void SkyColumn_StaysFullDownToTheGround(){
            int3 glass = new(5, 150, 5);
            using LightTestWorld world = new(1, p => {
                if (p.Equals(glass)) return Glass;
                if (p.x == 20 && p.z == 20 && p.y > 120 && p.y <= 123) return Water;
                if (p.x == 12 && p.z == 12 && p.y == 140) return Leaves;
                return p.y <= 120 ? Stone : Air;
            });
            ChunkLightData light = world.LightChunk(Origin);

            for (int y = 121; y < ChunkHeight; y++)
                Assert.AreEqual(15, VoxelLight.GetSky(LightAt(light, new int3(5, y, 5))), $"through glass y {y}");
            Assert.AreEqual(0, LightAt(light, new int3(5, 120, 5)), "the ground is opaque");

            // A water column standing on the ground (sides open to the sky): side light wins (15 - 1 - 2 = 12).
            Assert.AreEqual(12, VoxelLight.GetSky(LightAt(light, new int3(20, 122, 20))));
            // Under a single leaf the column gets its light from the sides: 14 (not 15 - 2 through the leaf).
            Assert.AreEqual(14, VoxelLight.GetSky(LightAt(light, new int3(12, 139, 12))));
            Assert.AreEqual(14, VoxelLight.GetSky(LightAt(light, new int3(12, 121, 12))));
            light.Dispose();
        }

        [Test]
        public void SkyUnderWater_LosesThreePerVoxel(){
            // A wide lake (no side light): sky enters from above only.
            using LightTestWorld world = new(1, p => p.y <= 100 ? Stone : p.y <= 110 ? Water : Air);
            ChunkLightData light = world.LightChunk(Origin);
            int[] expected ={ 12, 9, 6, 3, 0, 0 };
            for (int i = 0; i < expected.Length; i++)
                Assert.AreEqual(expected[i], VoxelLight.GetSky(LightAt(light, new int3(16, 110 - i, 16))), $"depth {i}");
            light.Dispose();
        }

        [Test]
        public void TopOfWorld_NonClearVoxel_SeesSkyAboveTheWorld(){
            using LightTestWorld world = new(1, p => p.y == ChunkHeight - 1 ? Leaves : p.y <= 200 ? Stone : Air);
            ChunkLightData light = world.LightChunk(Origin);
            Assert.AreEqual(13, VoxelLight.GetSky(LightAt(light, new int3(16, 255, 16))));
            Assert.AreEqual(12, VoxelLight.GetSky(LightAt(light, new int3(16, 254, 16))));
            light.Dispose();
        }

        /// <summary>The frontier seeding gives the same light as seeding every lit voxel.</summary>
        [Test]
        public void FrontierSeeding_EqualsExhaustiveSeeding([Values(1, 2, 3)] int seed){
            using LightTestWorld world = new(1, RandomTerrain(seed));
            ChunkLightData fast = world.LightChunk(Origin);

            LightRegion region = new(LightJob.BoxSize, LightJob.BoxSize, -(LightJob.Margin + 1), Allocator.Persistent);
            Fill(ref region, world, Origin, 1);
            LightQueue queue = new(64, Allocator.Persistent);
            region.SeedSky(ref queue);
            queue.Clear();
            for (int i = 0; i < region.Light.Length; i++)
                if (region.Light[i] != 0)
                    queue.Enqueue(i);
            region.Propagate(ref queue);
            ChunkLightData reference = ChunkLightData.CreateEmpty(1024);
            region.WriteChunk(new int2(LightJob.Margin + 1), ref reference);

            AssertEqual(reference, fast, "frontier");
            queue.Dispose();
            region.Dispose();
            fast.Dispose();
            reference.Dispose();
        }

        /// <summary>The centre chunk lit from its 3 × 3 region equals one computation over 5 × 5 chunks.</summary>
        [Test]
        public void BorderExactness_3x3_Equals_5x5([Values(4, 5)] int seed){
            using LightTestWorld world = new(2, RandomTerrain(seed));
            ChunkLightData small = world.LightChunk(Origin);

            int size = 5 * ChunkWidth + 2;
            LightRegion region = new(size, size, -(2 * ChunkWidth + 1), Allocator.Persistent);
            Fill(ref region, world, Origin, 2);
            LightQueue queue = new(64, Allocator.Persistent);
            region.SeedSky(ref queue);
            region.SeedEmitters(ref queue);
            region.Propagate(ref queue);
            ChunkLightData large = ChunkLightData.CreateEmpty(1024);
            region.WriteChunk(new int2(2 * ChunkWidth + 1), ref large);

            AssertEqual(large, small, "3x3 vs 5x5");
            queue.Dispose();
            region.Dispose();
            small.Dispose();
            large.Dispose();
        }

        internal static void Fill(ref LightRegion region, LightTestWorld world, int2 center, int radius){
            for (int dx = -radius; dx <= radius; dx++)
            for (int dz = -radius; dz <= radius; dz++){
                int2 chunk = center + new int2(dx, dz);
                region.FillVoxels(world.Chunks[chunk].Data, chunk * ChunkSizeXZ, world.Table);
            }
        }

        internal static void AssertEqual(in ChunkLightData expected, in ChunkLightData actual, string label){
            Assert.AreEqual(VoxelsPerChunk, actual.Data.Length, $"{label}: length");
            for (int x = 0; x < ChunkWidth; x++)
            for (int z = 0; z < ChunkDepth; z++)
            for (int y = 0; y < ChunkHeight; y++){
                int3 p = new(x, y, z);
                ushort e = expected.GetLight(p);
                ushort a = actual.GetLight(p);
                if (e != a)
                    Assert.Fail($"{label}: light at {p} expected sky {VoxelLight.GetSky(e)} rgb " +
                                $"({VoxelLight.GetRed(e)}, {VoxelLight.GetGreen(e)}, {VoxelLight.GetBlue(e)}) but was sky " +
                                $"{VoxelLight.GetSky(a)} rgb ({VoxelLight.GetRed(a)}, {VoxelLight.GetGreen(a)}, {VoxelLight.GetBlue(a)})");
            }
        }
    }
}
