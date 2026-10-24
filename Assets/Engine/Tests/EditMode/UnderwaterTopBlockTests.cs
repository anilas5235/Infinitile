using System.Collections.Generic;
using Engine.Scripts.Data;
using Engine.Scripts.Jobs.Chunk;
using Engine.Scripts.VoxelConfig.Data.Generation;
using Engine.Scripts.WorldGen.Core;
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
    ///     Columns whose top block lies under water take the biome's underwater block instead of its top block, so lake
    ///     and sea floors carry no grass. A top block at <c>WaterLevel - 1</c> touches the water surface and stays dry.
    ///     The chunk job agrees with the pure region API in both architectures.
    /// </summary>
    public class UnderwaterTopBlockTests{
        private const ushort TopA = TestFixtures.Grass;
        private const ushort UnderA = TestFixtures.Dirt;
        private const ushort WetA = TestFixtures.Stone;
        private const ushort TopB = TestFixtures.Dirt;
        private const ushort UnderB = TestFixtures.Stone;
        private const ushort WetB = TestFixtures.Grass;

        private readonly List<Object> _created = new();
        private GeneratorConfig _config;
        private NativeArray<ChunkColumn> _columns;

        [SetUp]
        public void SetUp(){
            _config = TestFixtures.CreateGeneratorConfig(Allocator.Persistent);
            _columns = new NativeArray<ChunkColumn>(ChunkWidth * ChunkDepth, Allocator.Persistent);
        }

        [TearDown]
        public void TearDown(){
            TestFixtures.DisposeGeneratorConfig(ref _config);
            _columns.Dispose();
            foreach (Object o in _created) Object.DestroyImmediate(o);
            _created.Clear();
        }

        private T Create<T>(string name) where T : ScriptableObject{
            T so = ScriptableObject.CreateInstance<T>();
            so.name = name;
            _created.Add(so);
            return so;
        }

        private Biome MakeBiome(string name, float temperature, float offset, float amplitude){
            Biome b = Create<Biome>(name);
            b.TargetTemperature = temperature;
            b.TargetHumidity = .5f;
            b.TargetContinental = .5f;
            b.TargetErosion = .5f;
            b.TargetPV = 0f;
            b.TargetHeight = .5f;
            b.HeightOffset = offset;
            b.HeightAmplitude = amplitude;
            b.OctaveWeights = new[]{ 1f, .6f, .3f, .12f, .05f, .02f };
            return b;
        }

        /// <summary>
        ///     Two biomes with distinct top, under and underwater blocks. In Biome-first both straddle the water level
        ///     (offsets just below and above it); Terrain-first takes the heights from the placeholder splines. Block
        ///     ids are patched in because the test has no voxel registry.
        /// </summary>
        private void Use(GenArchitecture architecture){
            int waterLevel = _config.WorldGen.WaterLevel;
            WorldGenSettings s = Create<WorldGenSettings>("WG");
            s.Architecture = architecture;
            s.Splines = Create<TerrainSplineSet>("Splines");
            s.Splines.ResetToPlaceholders();
            BiomeSet set = Create<BiomeSet>("Set");
            set.Biomes.Add(MakeBiome("Cold", .25f, waterLevel - 2f, 8f));
            set.Biomes.Add(MakeBiome("Warm", .75f, waterLevel + 1f, 8f));
            s.Biomes = set;

            _config.WorldGen.Dispose();
            _config.WorldGen = s.ToConfig(TestFixtures.Seed);
            Patch(0, TopA, UnderA, WetA);
            Patch(1, TopB, UnderB, WetB);
        }

        private void Patch(int biome, ushort top, ushort under, ushort wet){
            Biome.BiomeDef def = _config.WorldGen.Biomes[biome];
            def.topBlock = top;
            def.underBlock = under;
            def.underwaterTopBlock = wet;
            def.stoneBlock = TestFixtures.Stone;
            _config.WorldGen.Biomes[biome] = def;
        }

        /// <summary>The top block the column must carry: the underwater block when the voxel above it is water.</summary>
        private ushort ExpectedTop(int columnHeight, int surfaceSource){
            Biome.BiomeDef def = _config.WorldGen.Biomes[surfaceSource];
            int h = math.clamp(columnHeight, 0, MaxTerrainHeight);
            return h + 1 < _config.WorldGen.WaterLevel ? def.underwaterTopBlock : def.topBlock;
        }

        /// <summary>First chunk of the scanned area whose columns lie both under and above water.</summary>
        private int2 FindShoreChunk(){
            int waterLevel = _config.WorldGen.WaterLevel;
            for (int cx = -8; cx <= 8; cx++)
            for (int cz = -8; cz <= 8; cz++){
                bool wet = false, dry = false;
                for (int x = 0; x < ChunkWidth; x += 3)
                for (int z = 0; z < ChunkDepth; z += 3){
                    ColumnSample s = WorldSampler.SampleColumn(cx * ChunkWidth + x, cz * ChunkDepth + z, _config.WorldGen);
                    if (s.ColumnHeight + 1 < waterLevel) wet = true;
                    else dry = true;
                }

                if (wet && dry) return new int2(cx, cz);
            }

            Assert.Fail("no chunk with a shore line in the scanned area");
            return default;
        }

        [Test]
        public void TopBlockOf_FollowsTheWaterSurface(){
            const int waterLevel = 124;
            Biome.BiomeDef def = new(){ topBlock = TopA, underBlock = UnderA, underwaterTopBlock = WetA };
            Assert.AreEqual(WetA, ChunkJob.TopBlockOf(def, 0, waterLevel), "deep floor");
            Assert.AreEqual(WetA, ChunkJob.TopBlockOf(def, waterLevel - 2, waterLevel), "one water voxel above");
            Assert.AreEqual(TopA, ChunkJob.TopBlockOf(def, waterLevel - 1, waterLevel), "upper face at the water surface");
            Assert.AreEqual(TopA, ChunkJob.TopBlockOf(def, waterLevel, waterLevel), "above water");

            def.underwaterTopBlock = 0;
            Assert.AreEqual(TopA, ChunkJob.TopBlockOf(def, waterLevel - 10, waterLevel), "0 keeps the top block");
        }

        [TestCase(GenArchitecture.BiomeFirst)]
        [TestCase(GenArchitecture.TerrainFirst)]
        public void PrepareChunkMaps_SubmergedColumnsTakeTheUnderwaterBlock(GenArchitecture architecture){
            Use(architecture);
            int2 chunk = FindShoreChunk();
            int3 origin = new(chunk.x * ChunkWidth, 0, chunk.y * ChunkDepth);
            ChunkJob.PrepareChunkMaps(ref _config, ref origin, _columns);

            int wet = 0, dry = 0;
            for (int x = 0; x < ChunkWidth; x++)
            for (int z = 0; z < ChunkDepth; z++){
                ChunkColumn col = _columns[ChunkJob.GetColumnIdx(x, z, ChunkDepth)];
                Assert.GreaterOrEqual(col.SurfaceSource, 0);
                Assert.AreEqual(ExpectedTop(col.Height, col.SurfaceSource), col.TopBlock, $"({x},{z}) h={col.Height}");
                Assert.AreEqual(_config.WorldGen.Biomes[col.SurfaceSource].underBlock, col.UnderBlock);
                if (col.Height + 1 < _config.WorldGen.WaterLevel) wet++;
                else dry++;
            }

            Assert.Greater(wet, 0, "some columns lie under water");
            Assert.Greater(dry, 0, "some columns lie above water");
        }

        [TestCase(GenArchitecture.BiomeFirst)]
        [TestCase(GenArchitecture.TerrainFirst)]
        public void ChunkJob_TopBlocksAgreeWithSampleRegion(GenArchitecture architecture){
            Use(architecture);
            int2 chunk = FindShoreChunk();
            int2 origin = chunk * new int2(ChunkWidth, ChunkDepth);
            RegionSample region = RegionSample.Sample(_config.WorldGen, origin, new int2(ChunkWidth, ChunkDepth));
            int waterLevel = _config.WorldGen.WaterLevel;

            NativeList<int2> jobs = new(1, Allocator.TempJob);
            jobs.Add(chunk);
            NativeParallelHashMap<int2, ChunkVoxelData> results = new(1, Allocator.TempJob);
            try{
                new ChunkJob{ Jobs = jobs, Results = results.AsParallelWriter(), Config = _config }.Run(jobs.Length);
                Assert.IsTrue(results.TryGetValue(chunk, out ChunkVoxelData data));

                int wet = 0;
                for (int x = 0; x < ChunkWidth; x++)
                for (int z = 0; z < ChunkDepth; z++){
                    int i = region.Index(origin.x + x, origin.y + z);
                    int h = math.clamp(region.ColumnHeight[i], 0, MaxTerrainHeight);
                    int source = region.SurfaceSource[i];
                    string where = $"({origin.x + x},{origin.y + z}) h={h}";
                    Assert.AreEqual(ExpectedTop(h, source), data.GetVoxel(new int3(x, h, z)), where + " top");
                    Assert.AreEqual(_config.WorldGen.Biomes[source].underBlock, data.GetVoxel(new int3(x, h - 1, z)),
                        where + " under");
                    if (h + 1 >= waterLevel) continue;
                    wet++;
                    ushort above = data.GetVoxel(new int3(x, h + 1, z));
                    Assert.IsTrue(above == TestFixtures.Water || above == TestFixtures.Ice, where + " water above");
                }

                Assert.Greater(wet, 0, "the chunk has submerged columns");
            }
            finally{
                foreach (KeyValue<int2, ChunkVoxelData> kv in results) kv.Value.Dispose();
                results.Dispose();
                jobs.Dispose();
            }
        }
    }
}
