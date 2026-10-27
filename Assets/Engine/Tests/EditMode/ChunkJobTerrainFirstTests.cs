using System.Collections.Generic;
using Engine.Scripts.Data;
using Engine.Scripts.Jobs.Chunk;
using Engine.Scripts.Utils.Extensions;
using Engine.Scripts.VoxelConfig.Data.Generation;
using Engine.Scripts.WorldGen.Core;
using Engine.Scripts.WorldGen.Settings;
using NUnit.Framework;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using static Engine.Scripts.Utils.VoxelConstants;
using Object = UnityEngine.Object;

namespace Engine.Tests.EditMode{
    /// <summary>
    ///     The chunk job on the Terrain-first sampler. Every column of a chunk is the
    ///     sampler's decision for that world column, independent of the chunk it lies in, so chunk borders carry
    ///     no seam; the blocks come from the surface-source biome and the ice override follows the climate switch.
    /// </summary>
    public class ChunkJobTerrainFirstTests{
        private const ushort TopA = TestFixtures.Grass;
        private const ushort UnderA = TestFixtures.Dirt;
        private const ushort TopB = TestFixtures.Dirt;
        private const ushort UnderB = TestFixtures.Stone;

        private readonly List<Object> _created = new();
        private GeneratorConfig _config;
        private NativeArray<ChunkColumn> _columns;
        private NativeArray<ushort> _vox;

        [SetUp]
        public void SetUp(){
            _config = TestFixtures.CreateGeneratorConfig(Allocator.Persistent);
            _columns = new NativeArray<ChunkColumn>(ChunkWidth * ChunkDepth, Allocator.Persistent);
            _vox = new NativeArray<ushort>(VoxelsPerChunk, Allocator.Persistent);
        }

        [TearDown]
        public void TearDown(){
            TestFixtures.DisposeGeneratorConfig(ref _config);
            _columns.Dispose();
            _vox.Dispose();
            foreach (Object o in _created) Object.DestroyImmediate(o);
            _created.Clear();
        }

        private T Create<T>(string name) where T : ScriptableObject{
            T so = ScriptableObject.CreateInstance<T>();
            so.name = name;
            _created.Add(so);
            return so;
        }

        private Biome MakeBiome(string name, float t, float f, float c, float e, float pv, float h){
            Biome b = Create<Biome>(name);
            b.TargetTemperature = t;
            b.TargetHumidity = f;
            b.TargetContinental = c;
            b.TargetErosion = e;
            b.TargetPV = pv;
            b.TargetHeight = h;
            return b;
        }

        /// <summary>
        ///     Replaces the fixture's empty world-gen config with a Terrain-first one: two biomes with distinct blocks
        ///     and the placeholder splines. Block ids are patched in because the test has no voxel registry.
        /// </summary>
        private void UseTerrainFirst(bool climateOverrides = true, bool withBiomes = true, BlendMethod material = BlendMethod.None){
            WorldGenSettings s = Create<WorldGenSettings>("WG");
            s.Architecture = GenArchitecture.TerrainFirst;
            s.MaterialMethod = material;
            s.EnableClimateOverrides = climateOverrides;
            s.Splines = Create<TerrainSplineSet>("Splines");
            s.Splines.ResetToPlaceholders();
            if (withBiomes){
                BiomeSet set = Create<BiomeSet>("Set");
                // The two differ only in temperature, so both occur within a modest world span.
                set.Biomes.Add(MakeBiome("Cold", .25f, .5f, .5f, .5f, 0f, .5f));
                set.Biomes.Add(MakeBiome("Warm", .75f, .5f, .5f, .5f, 0f, .5f));
                s.Biomes = set;
            }

            _config.WorldGen.Dispose();
            _config.WorldGen = s.ToConfig(TestFixtures.Seed);
            TestFixtures.SetFillVoxels(ref _config.WorldGen);
            if (!withBiomes) return;
            Biome.BiomeDef a = _config.WorldGen.Biomes[0];
            a.topBlock = TopA;
            a.underBlock = UnderA;
            a.stoneBlock = TestFixtures.Stone;
            a.freezesWater = 1;
            _config.WorldGen.Biomes[0] = a;
            Biome.BiomeDef b = _config.WorldGen.Biomes[1];
            b.topBlock = TopB;
            b.underBlock = UnderB;
            b.stoneBlock = TestFixtures.Stone;
            _config.WorldGen.Biomes[1] = b;
        }

        private void Prepare(int2 chunk){
            int3 origin = new(chunk.x * ChunkWidth, 0, chunk.y * ChunkDepth);
            ChunkJob.PrepareChunkMaps(ref _config, ref origin, _columns);
        }

        private ChunkColumn Column(int x, int z){
            return _columns[ChunkJob.GetColumnIdx(x, z, ChunkDepth)];
        }

        private static void AssertMatchesSampler(in ChunkColumn col, in ColumnSample s, string where){
            Assert.AreEqual(math.clamp(s.ColumnHeight, 0, MaxTerrainHeight), col.Height, where + " Height");
            Assert.AreEqual(s.Height, col.HeightF, where + " HeightF");
            Assert.AreEqual((ushort)math.max(0, s.HardBiome), col.Biome, where + " Biome");
            Assert.AreEqual(s.SurfaceSource, col.SurfaceSource, where + " SurfaceSource");
            Assert.AreEqual(s.Fields.T, col.Temperature, where + " Temperature");
            Assert.AreEqual(s.Fields.F, col.Humidity, where + " Humidity");
            Assert.AreEqual(s.FloraSpecies, col.FloraSpecies, where + " FloraSpecies");
            Assert.AreEqual(s.FloraSource, col.FloraSource, where + " FloraSource");
        }

        // --- Columns and blocks -------------------------------------------------------------------------------------------

        [Test]
        public void PrepareChunkMaps_TerrainFirst_EveryColumnIsTheSamplerDecision(){
            UseTerrainFirst();
            int2 chunk = new(3, -2);
            Prepare(chunk);

            for (int x = 0; x < ChunkWidth; x++)
            for (int z = 0; z < ChunkDepth; z++){
                int wx = chunk.x * ChunkWidth + x, wz = chunk.y * ChunkDepth + z;
                ColumnSample s = WorldSampler.SampleColumn(wx, wz, _config.WorldGen);
                AssertMatchesSampler(Column(x, z), s, $"({wx},{wz})");
            }
        }

        [Test]
        public void PrepareChunkMaps_TerrainFirst_TakesBlocksFromTheSurfaceSource(){
            UseTerrainFirst();
            int sourcesSeen = 0;
            for (int k = 0; k < 24 && sourcesSeen != 3; k++){
                Prepare(new int2(k * 31, k * 17));
                for (int x = 0; x < ChunkWidth; x++)
                for (int z = 0; z < ChunkDepth; z++){
                    ChunkColumn col = Column(x, z);
                    Assert.That(col.SurfaceSource, Is.InRange(0, 1));
                    Assert.AreEqual(col.SurfaceSource, (int)col.Biome, "without blending the surface source is the hard biome");
                    Biome.BiomeDef def = _config.WorldGen.Biomes[col.SurfaceSource];
                    Assert.AreEqual(def.topBlock, col.TopBlock);
                    Assert.AreEqual(def.underBlock, col.UnderBlock);
                    Assert.AreEqual(def.stoneBlock, col.StoneBlock);
                    sourcesSeen |= 1 << col.SurfaceSource;
                }
            }

            Assert.AreEqual(3, sourcesSeen, "both biomes should occur somewhere in the sampled chunks");
        }

        [Test]
        public void PrepareChunkMaps_TerrainFirst_WithoutBiomesYieldsAirColumns(){
            UseTerrainFirst(withBiomes: false);
            Prepare(new int2(0, 0));
            ChunkColumn col = Column(5, 9);
            Assert.AreEqual(-1, col.SurfaceSource);
            Assert.AreEqual(0, col.Biome);
            Assert.AreEqual(0, col.TopBlock);
            Assert.AreEqual(0, col.UnderBlock);
            Assert.AreEqual(0, col.StoneBlock);
            Assert.That(col.Height, Is.InRange(0, MaxTerrainHeight), "the spline height exists without biomes");
        }

        [Test]
        public void PrepareChunkMaps_TerrainFirst_SpatialMaterialMethodsAreTheSamplerDecision(
            [Values(BlendMethod.DistanceBlend, BlendMethod.DistortedBoundary)] BlendMethod material){
            // With the chunk's lattice cache the surface source is still the pure
            // sampler's pick, also where it differs from the hard biome (a transition or a warped boundary).
            UseTerrainFirst(material: material);
            int picked = 0;
            for (int k = 0; k < 24 && picked == 0; k++){
                int2 chunk = new(k * 31, k * 17);
                Prepare(chunk);
                for (int x = 0; x < ChunkWidth; x++)
                for (int z = 0; z < ChunkDepth; z++){
                    int wx = chunk.x * ChunkWidth + x, wz = chunk.y * ChunkDepth + z;
                    ColumnSample s = WorldSampler.SampleColumn(wx, wz, _config.WorldGen);
                    AssertMatchesSampler(Column(x, z), s, $"({wx},{wz})");
                    Biome.BiomeDef def = _config.WorldGen.Biomes[s.SurfaceSource];
                    Assert.AreEqual(def.topBlock, Column(x, z).TopBlock, "blocks come from the surface source");
                    if (s.SurfaceSource != s.HardBiome) picked++;
                }
            }

            Assert.Greater(picked, 0, "some column takes its surface from a biome other than its hard class");
        }

        // --- Chunk borders ------------------------------------------------------------------------------------------------

        [Test]
        public void PrepareChunkMaps_TerrainFirst_NeighbouringChunksAgreeAtTheBorder(){
            UseTerrainFirst();
            int2 left = new(2, 5), right = new(3, 5);
            Prepare(left);
            ChunkColumn[] leftEdge = new ChunkColumn[ChunkDepth];
            for (int z = 0; z < ChunkDepth; z++) leftEdge[z] = Column(ChunkWidth - 1, z);
            Prepare(right);

            for (int z = 0; z < ChunkDepth; z++){
                int wz = right.y * ChunkDepth + z;
                ColumnSample a = WorldSampler.SampleColumn(right.x * ChunkWidth - 1, wz, _config.WorldGen);
                ColumnSample b = WorldSampler.SampleColumn(right.x * ChunkWidth, wz, _config.WorldGen);
                AssertMatchesSampler(leftEdge[z], a, $"left edge z={z}");
                AssertMatchesSampler(Column(0, z), b, $"right edge z={z}");
                Assert.Less(math.abs(leftEdge[z].HeightF - Column(0, z).HeightF), 8f,
                    $"z={z}: the height is continuous across the chunk border");
            }
        }

        [Test]
        public void ChunkJob_TerrainFirst_VoxelsAtTheBorderMatchTheSampler(){
            UseTerrainFirst();
            NativeList<int2> jobs = new(2, Allocator.TempJob);
            jobs.Add(new int2(-1, 0));
            jobs.Add(new int2(0, 0));
            NativeParallelHashMap<int2, ChunkVoxelData> results = new(2, Allocator.TempJob);
            try{
                new ChunkJob{
                    Jobs = jobs, Results = results.AsParallelWriter(), Config = _config
                }.Run(jobs.Length);

                Assert.IsTrue(results.TryGetValue(new int2(-1, 0), out ChunkVoxelData west));
                Assert.IsTrue(results.TryGetValue(new int2(0, 0), out ChunkVoxelData east));
                // Caves stay on; rows with a column whose top block a worm carved are skipped.
                int checkedRows = 0;
                for (int z = 0; z < ChunkDepth; z++){
                    ColumnSample a = WorldSampler.SampleColumn(-1, z, _config.WorldGen);
                    ColumnSample b = WorldSampler.SampleColumn(0, z, _config.WorldGen);
                    if (TestFixtures.SurfaceCarved(-1, z, a.ColumnHeight, _config.WorldGen) ||
                        TestFixtures.SurfaceCarved(0, z, b.ColumnHeight, _config.WorldGen)) continue;
                    checkedRows++;
                    Biome.BiomeDef defA = _config.WorldGen.Biomes[a.SurfaceSource];
                    Biome.BiomeDef defB = _config.WorldGen.Biomes[b.SurfaceSource];
                    ushort expectedA = defA.topBlock; // the patched defs have no underwater block
                    ushort expectedB = defB.topBlock;
                    Assert.AreEqual(expectedA, west.GetVoxel(new int3(ChunkWidth - 1, a.ColumnHeight, z)), $"west z={z}");
                    Assert.AreEqual(expectedB, east.GetVoxel(new int3(0, b.ColumnHeight, z)), $"east z={z}");
                    Assert.AreEqual(defA.underBlock, west.GetVoxel(new int3(ChunkWidth - 1, a.ColumnHeight - 1, z)));
                    Assert.AreEqual(defB.underBlock, east.GetVoxel(new int3(0, b.ColumnHeight - 1, z)));
                }

                Assert.Greater(checkedRows, 0, "some row lies outside the entrances");
            }
            finally{
                foreach (KeyValue<int2, ChunkVoxelData> kv in results) kv.Value.Dispose();
                results.Dispose();
                jobs.Dispose();
            }
        }

        [Test]
        public void ChunkJob_CompilesSynchronouslyWithStrictFloats(){
            // Every job that calls the sampler compiles synchronously with FloatMode.Strict.
            BurstCompileAttribute burst = (BurstCompileAttribute)System.Attribute.GetCustomAttribute(
                typeof(ChunkJob), typeof(BurstCompileAttribute));
            Assert.IsNotNull(burst, "ChunkJob is Burst-compiled");
            Assert.IsTrue(burst.CompileSynchronously, "CompileSynchronously");
            Assert.AreEqual(FloatMode.Strict, burst.FloatMode, "FloatMode.Strict");
        }

        // --- Climate overrides --------------------------------------------------------------------------------------------

        private void ColdFloodedColumns(){
            for (int x = 0; x < ChunkWidth; x++)
            for (int z = 0; z < ChunkDepth; z++)
                _columns[ChunkJob.GetColumnIdx(x, z, ChunkDepth)] = new ChunkColumn{
                    Height = 100, HeightF = 100f, Biome = 0, SurfaceSource = 0, FloraSource = -1,
                    TopBlock = TopA, UnderBlock = UnderA, StoneBlock = TestFixtures.Stone,
                    Temperature = 0.1f, Humidity = 0.5f,
                    Frozen = SurfaceCover.WaterFreezes(x, z, 0.1f, _config.WorldGen.Biomes[0], _config.WorldGen) ? (byte)1 : (byte)0
                };
        }

        [Test]
        public void FillTerrain_IceOverrideFollowsEnableClimateOverrides(){
            int surface = _config.WorldGen.WaterLevel - 1;

            UseTerrainFirst(climateOverrides: true);
            ColdFloodedColumns();
            ChunkJob.FillTerrain(_vox, _columns, ref _config);
            Assert.AreEqual(TestFixtures.Ice, _vox[ChunkSize.Flatten(3, surface, 4)], "overrides on: cold water freezes");

            UseTerrainFirst(climateOverrides: false);
            ColdFloodedColumns();
            ChunkJob.FillTerrain(_vox, _columns, ref _config);
            Assert.AreEqual(TestFixtures.Water, _vox[ChunkSize.Flatten(3, surface, 4)], "overrides off: no ice");
            Assert.AreEqual(TestFixtures.Air, _vox[ChunkSize.Flatten(3, surface + 1, 4)]);
        }

        [Test]
        public void ApplySurfaceCover_IceFollowsTheHardBiome(){
            // The surface pick dithers along blend borders; the ice follows the hard biome, so its edge is a line.
            // Only biome 0 has FreezesWater; every column's surface pick is the other biome.
            UseTerrainFirst(climateOverrides: true);
            ColdFloodedColumns();
            for (int x = 0; x < ChunkWidth; x++)
            for (int z = 0; z < ChunkDepth; z++){
                int i = ChunkJob.GetColumnIdx(x, z, ChunkDepth);
                ChunkColumn col = _columns[i];
                col.Biome = (ushort)(x < ChunkWidth / 2 ? 0 : 1);
                col.SurfaceSource = 1 - col.Biome;
                col.Frozen = 0;
                _columns[i] = col;
            }

            ChunkJob.ApplySurfaceCover(_columns, int3.zero, _config.WorldGen, default);

            int frozen = 0;
            for (int x = 0; x < ChunkWidth; x++)
            for (int z = 0; z < ChunkDepth; z++){
                ChunkColumn col = _columns[ChunkJob.GetColumnIdx(x, z, ChunkDepth)];
                bool wanted = col.Biome == 0 &&
                              SurfaceCover.WaterFreezes(x, z, col.Temperature, _config.WorldGen.Biomes[0], _config.WorldGen);
                Assert.AreEqual(wanted, col.Frozen == 1, $"column ({x}, {z}), hard biome {col.Biome}");
                frozen += col.Frozen;
            }

            Assert.Greater(frozen, 0, "some cold water of the freezing biome freezes");
        }
    }
}
