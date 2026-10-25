using System.Collections.Generic;
using System.Linq;
using Engine.Scripts.Data;
using Engine.Scripts.Jobs.Chunk;
using Engine.Scripts.VoxelConfig.Data.Generation;
using Engine.Scripts.WorldGen.Core;
using Engine.Scripts.WorldGen.Flora;
using Engine.Scripts.WorldGen.Settings;
using NUnit.Framework;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using static Engine.Scripts.Utils.VoxelConstants;
using Object = UnityEngine.Object;

namespace Engine.Tests.EditMode{
    /// <summary>
    ///     Beaches: dry columns whose top block lies less than the column's beach height above the water surface take
    ///     the biome's beach block on top and in the two blocks under it. The beach height follows a noise, so stretches
    ///     of shore have no beach. Beach-ground plants grow only there, other plants never; the chunk job agrees with
    ///     the pure region API in both architectures.
    /// </summary>
    public class BeachTests{
        private FloraFixture _fixture;
        private GeneratorConfig _config;
        private Biome[] _biomes;

        [SetUp]
        public void SetUp(){
            _fixture = new FloraFixture();
        }

        [TearDown]
        public void TearDown(){
            if (_config.Voxels.IsCreated) _config.Voxels.Dispose();
            _config.Dispose();
            _config = default;
            _fixture.Dispose();
        }

        /// <summary>
        ///     A meadow with a sand beach and a beach flower (also at the water surface), and dunes with a glass beach
        ///     that carries no plants. In Biome-first both straddle the water level.
        /// </summary>
        private void Use(GenArchitecture architecture){
            const int waterLevel = 124;
            FloraRule beachFlower = FloraFixture.Rule(_fixture.FlowerSo, .4f);
            beachFlower.RequiresBeachGround = true;
            beachFlower.MinHeightAboveWater = 0;
            Biome meadow = _fixture.MakeBiome("Meadow", .25f, waterLevel + 1f, 8f, _fixture.GrassSo,
                FloraFixture.Rule(_fixture.TuftSo, .3f), beachFlower, FloraFixture.Rule(_fixture.Grove, .03f));
            meadow.BeachBlock = _fixture.SandSo;
            Biome dunes = _fixture.MakeBiome("Dunes", .75f, waterLevel + 2f, 6f, _fixture.SandSo, FloraFixture.Rule(_fixture.TuftSo, .2f));
            dunes.BeachBlock = _fixture.GlassSo;
            _biomes = new[]{ meadow, dunes };
            _config = _fixture.GeneratorConfig(_fixture.Settings(architecture, BlendMethod.DistanceBlend, _biomes));
            Assert.AreEqual(waterLevel, _config.WorldGen.WaterLevel);
        }

        private bool IsBeach(int x, int z, int columnHeight, int surfaceSource){
            if (surfaceSource < 0) return false;
            int h = math.clamp(columnHeight, 0, MaxTerrainHeight);
            return SurfaceCover.IsBeach(x, z, h, _config.WorldGen.Biomes[surfaceSource], _config.WorldGen);
        }

        /// <summary>First chunk of the scanned area with many beach columns (sampled at stride 2).</summary>
        private int2 FindBeachChunk(){
            for (int cx = -8; cx <= 8; cx++)
            for (int cz = -8; cz <= 8; cz++){
                int beach = 0;
                for (int x = 0; x < ChunkWidth; x += 2)
                for (int z = 0; z < ChunkDepth; z += 2){
                    int wx = cx * ChunkWidth + x, wz = cz * ChunkDepth + z;
                    ColumnSample s = WorldSampler.SampleColumn(wx, wz, _config.WorldGen);
                    if (IsBeach(wx, wz, s.ColumnHeight, s.SurfaceSource)) beach++;
                }

                if (beach >= 15) return new int2(cx, cz);
            }

            Assert.Fail("no chunk with beaches in the scanned area");
            return default;
        }

        [Test]
        public void IsBeach_IsTheBandAboveTheWaterBelowTheColumnsBeachHeight(){
            Use(GenArchitecture.TerrainFirst);
            WorldGenConfig cfg = _config.WorldGen;
            int surface = cfg.WaterLevel - 1;
            Biome.BiomeDef def = new(){ beachHeight = 3 };
            int[] heights = new int[4];
            const int n = 4000;
            for (int i = 0; i < n; i++){
                int x = (i % 64) * 37 - 1200, z = (i / 64) * 41 - 900;
                int hb = SurfaceCover.BeachHeight(x, z, 3, cfg);
                Assert.That(hb, Is.InRange(0, 3));
                heights[hb]++;
                string at = $"({x},{z}) h_b={hb}";
                Assert.IsFalse(SurfaceCover.IsBeach(x, z, surface - 1, def, cfg), at + " under water");
                Assert.IsFalse(SurfaceCover.IsBeach(x, z, surface + 3, def, cfg), at + " above the band");
                for (int above = 0; above < 3; above++)
                    Assert.AreEqual(above < hb, SurfaceCover.IsBeach(x, z, surface + above, def, cfg), at + $" {above} above");
                Assert.IsFalse(SurfaceCover.IsBeach(x, z, surface, default, cfg), at + " biome without beaches");
            }

            float none = heights[0] / (float)n;
            TestContext.WriteLine($"beach height 0/1/2/3: {string.Join(" / ", heights.Select(c => (c / (float)n).ToString("P1")))}");
            Assert.That(none, Is.InRange(.15f, .75f), "a share of the shore has no beach");
            Assert.Greater(heights[3], 0, "some beaches reach the full height");
        }

        [TestCase(GenArchitecture.BiomeFirst)]
        [TestCase(GenArchitecture.TerrainFirst)]
        public void ChunkJob_BeachesAndBeachFloraMatchTheRegionApi(GenArchitecture architecture){
            Use(architecture);
            int2 originChunk = FindBeachChunk(), chunks = new(2, 2);
            ReferenceWorld reference = new(_fixture, _biomes, _config, originChunk, chunks);
            RegionSample region = reference.Region;
            List<Object> species = FloraConfigBuilder.CollectSpecies(_biomes);
            ushort flower = (ushort)(species.IndexOf(_fixture.FlowerSo) + 1);
            Assert.Greater(flower, 0);

            // Flora by column: beach flowers only on meadow beaches, other plants never on beaches, none on glass.
            int beaches = 0, flowers = 0;
            for (int x = reference.Origin.x; x < reference.Origin.x + reference.Size.x; x++)
            for (int z = reference.Origin.y; z < reference.Origin.y + reference.Size.y; z++){
                int i = region.Index(x, z);
                int source = region.SurfaceSource[i];
                bool beach = IsBeach(x, z, region.ColumnHeight[i], source);
                ushort plant = region.FloraSpecies[i];
                string at = $"({x},{z}) h={region.ColumnHeight[i]} surface {source}";
                if (beach) beaches++;
                if (plant == flower){
                    flowers++;
                    Assert.IsTrue(beach && source == 0, at + ": beach flower off a meadow beach");
                }
                else if (plant != 0) Assert.IsFalse(beach, at + $": plant {plant} on a beach");

                if (beach){
                    int h = reference.SurfaceAt(x, z);
                    ushort block = source == 0 ? FloraFixture.Sand : FloraFixture.Glass;
                    Assert.AreEqual(block, reference[new int3(x, h - 1, z)], at + " beach block under the top");
                    Assert.AreEqual(block, reference[new int3(x, h - 2, z)], at + " second beach block");
                    Assert.AreEqual(TestFixtures.Dirt, reference[new int3(x, h - 3, z)], at + " under block below the beach");
                }
            }

            TestContext.WriteLine($"{architecture}: {beaches} beach columns, {flowers} beach flowers in chunks {originChunk} +2×2");
            Assert.Greater(beaches, 30, "the area has beaches");
            Assert.Greater(flowers, 0, "beach flowers grow");

            // The chunk job equals the reference stamp around every surface.
            NativeList<int2> jobs = new(4, Allocator.TempJob);
            for (int cx = 0; cx < chunks.x; cx++)
            for (int cz = 0; cz < chunks.y; cz++)
                jobs.Add(originChunk + new int2(cx, cz));
            NativeParallelHashMap<int2, ChunkVoxelData> results = new(jobs.Length, Allocator.TempJob);
            try{
                new ChunkJob{ Jobs = jobs, Results = results.AsParallelWriter(), Config = _config }.Run(jobs.Length);
                int mismatches = 0;
                List<string> report = new();
                for (int x = reference.Origin.x; x < reference.Origin.x + reference.Size.x; x++)
                for (int z = reference.Origin.y; z < reference.Origin.y + reference.Size.y; z++){
                    int2 chunk = new((int)math.floor(x / (float)ChunkWidth), (int)math.floor(z / (float)ChunkDepth));
                    Assert.IsTrue(results.TryGetValue(chunk, out ChunkVoxelData data));
                    int h = reference.SurfaceAt(x, z);
                    for (int y = math.max(0, h - 5); y <= math.min(MaxTerrainHeight, h + 3); y++){
                        int3 p = new(x, y, z);
                        ushort expected = reference[p];
                        ushort actual = data.GetVoxel(new int3(x - chunk.x * ChunkWidth, y, z - chunk.y * ChunkDepth));
                        if (expected != actual && mismatches++ < 12) report.Add($"{p}: expected {expected}, got {actual}");
                    }
                }

                Assert.AreEqual(0, mismatches, "chunk voxels differ from the whole-world stamp:\n" + string.Join("\n", report));
            }
            finally{
                foreach (KeyValue<int2, ChunkVoxelData> kv in results) kv.Value.Dispose();
                results.Dispose();
                jobs.Dispose();
            }
        }
    }
}
