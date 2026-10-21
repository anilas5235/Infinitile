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
    ///     The chunk job on the Biome-first sampler. Every column is the sampler's decision for that world column;
    ///     blocks come from the surface-source biome; without blending a height step at a biome boundary is expected
    ///     and must be the same step the sampler produces.
    /// </summary>
    public class ChunkJobBiomeFirstTests{
        private const ushort TopA = TestFixtures.Grass;
        private const ushort UnderA = TestFixtures.Dirt;
        private const ushort TopB = TestFixtures.Dirt;
        private const ushort UnderB = TestFixtures.Stone;

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

        private Biome MakeBiome(string name, float t, float offset, float amp, float j){
            Biome b = Create<Biome>(name);
            b.TargetTemperature = t;
            b.TargetHumidity = .5f;
            b.TargetContinental = .5f;
            b.TargetErosion = .5f;
            b.TargetPV = 0f;
            b.HeightOffset = offset;
            b.HeightAmplitude = amp;
            b.OctaveWeights = new[]{ 1f, .6f, .3f, .12f, .05f, .02f };
            b.Jaggedness = j;
            return b;
        }

        /// <summary>
        ///     Two Biome-first biomes that differ in temperature and in their height parameters, so the boundary
        ///     between them carries a cliff (offset 120 vs 160). Block ids are patched in (no registry).
        /// </summary>
        private void UseBiomeFirst(BlendMethod material = BlendMethod.None, BlendMethod height = BlendMethod.None){
            WorldGenSettings s = Create<WorldGenSettings>("WG");
            s.Architecture = GenArchitecture.BiomeFirst;
            s.MaterialMethod = material;
            s.HeightMethod = height;
            BiomeSet set = Create<BiomeSet>("Set");
            set.Biomes.Add(MakeBiome("Cold", .25f, 160f, 14f, 15f));
            set.Biomes.Add(MakeBiome("Warm", .75f, 120f, 6f, 0f));
            s.Biomes = set;

            _config.WorldGen.Dispose();
            _config.WorldGen = s.ToConfig(TestFixtures.Seed);
            Biome.BiomeDef a = _config.WorldGen.Biomes[0];
            a.topBlock = TopA;
            a.underBlock = UnderA;
            a.stoneBlock = TestFixtures.Stone;
            _config.WorldGen.Biomes[0] = a;
            Biome.BiomeDef b = _config.WorldGen.Biomes[1];
            b.topBlock = TopB;
            b.underBlock = UnderB;
            b.stoneBlock = TestFixtures.Stone;
            _config.WorldGen.Biomes[1] = b;
        }

        private void Prepare(int2 chunk){
            int3 origin = new(chunk.x * ChunkWidth, 0, chunk.y * ChunkDepth);
            ChunkJob.PrepareChunkMaps(_config.GlobalSeed, ref _config, ref origin, _columns);
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

        [Test]
        public void PrepareChunkMaps_BiomeFirst_EveryColumnIsTheSamplerDecision(){
            UseBiomeFirst();
            int2 chunk = new(-4, 6);
            Prepare(chunk);

            for (int x = 0; x < ChunkWidth; x++)
            for (int z = 0; z < ChunkDepth; z++){
                int wx = chunk.x * ChunkWidth + x, wz = chunk.y * ChunkDepth + z;
                ColumnSample s = WorldSampler.SampleColumn(wx, wz, _config.WorldGen);
                AssertMatchesSampler(Column(x, z), s, $"({wx},{wz})");
            }
        }

        [Test]
        public void PrepareChunkMaps_BiomeFirst_TakesBlocksFromTheSurfaceSource(){
            UseBiomeFirst();
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
        public void PrepareChunkMaps_BiomeFirst_CliffsAtBoundariesAreTheSamplersOwn(){
            // Without blending, visible cliffs at boundaries are expected; they must still be the per-column
            // sampler result, i.e. both sides of the boundary agree with the sampler and the step size is the
            // difference of the two biome formulas, not an artefact of the chunk.
            UseBiomeFirst();
            int cliffs = 0;
            for (int k = 0; k < 40 && cliffs == 0; k++){
                int2 chunk = new(k * 23 - 400, k * 29);
                Prepare(chunk);
                for (int x = 1; x < ChunkWidth; x++)
                for (int z = 0; z < ChunkDepth; z++){
                    ChunkColumn a = Column(x - 1, z), b = Column(x, z);
                    if (a.Biome == b.Biome) continue;
                    int wx = chunk.x * ChunkWidth + x, wz = chunk.y * ChunkDepth + z;
                    AssertMatchesSampler(a, WorldSampler.SampleColumn(wx - 1, wz, _config.WorldGen), $"({wx - 1},{wz})");
                    AssertMatchesSampler(b, WorldSampler.SampleColumn(wx, wz, _config.WorldGen), $"({wx},{wz})");
                    if (math.abs(a.HeightF - b.HeightF) > 8f) cliffs++;
                }
            }

            Assert.Greater(cliffs, 0, "a boundary with a > 8 block step exists (offsets 160 vs 120, no blend)");
        }

        [Test]
        public void PrepareChunkMaps_BiomeFirst_NeighbouringChunksAgreeAtTheBorder(){
            UseBiomeFirst();
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
            }
        }

        [Test]
        public void PrepareChunkMaps_BiomeFirst_SpatialMethodsAreTheSamplerDecision(
            [Values(BlendMethod.DistanceBlend, BlendMethod.DistortedBoundary)] BlendMethod height){
            // The chunk's padded lattice cache gives the pure sampler's result, also at
            // boundaries, where the blended height and the dithered surface pick differ from the hard biome.
            UseBiomeFirst(BlendMethod.DistanceBlend, height);
            int blended = 0;
            for (int k = 0; k < 40 && blended == 0; k++){
                int2 chunk = new(k * 23 - 400, k * 29);
                Prepare(chunk);
                for (int x = 0; x < ChunkWidth; x++)
                for (int z = 0; z < ChunkDepth; z++){
                    int wx = chunk.x * ChunkWidth + x, wz = chunk.y * ChunkDepth + z;
                    ColumnSample s = WorldSampler.SampleColumn(wx, wz, _config.WorldGen);
                    AssertMatchesSampler(Column(x, z), s, $"({wx},{wz})");
                    if (s.HeightWeights.Max(2) < 1f) blended++;
                }
            }

            Assert.Greater(blended, 0, "some sampled column lies in a transition");
        }

        [Test]
        public void ChunkJob_BiomeFirst_VoxelsMatchTheSampler(){
            UseBiomeFirst();
            NativeList<int2> jobs = new(1, Allocator.TempJob);
            jobs.Add(new int2(7, -3));
            NativeParallelHashMap<int2, ChunkVoxelData> results = new(1, Allocator.TempJob);
            try{
                new ChunkJob{
                    Jobs = jobs, Results = results.AsParallelWriter(), RandomSeed = _config.GlobalSeed, Config = _config
                }.Run(jobs.Length);

                Assert.IsTrue(results.TryGetValue(new int2(7, -3), out ChunkVoxelData chunk));
                for (int x = 0; x < ChunkWidth; x += 5)
                for (int z = 0; z < ChunkDepth; z += 3){
                    ColumnSample s = WorldSampler.SampleColumn(7 * ChunkWidth + x, -3 * ChunkDepth + z, _config.WorldGen);
                    Biome.BiomeDef def = _config.WorldGen.Biomes[s.SurfaceSource];
                    Assert.AreEqual(def.topBlock, chunk.GetVoxel(new int3(x, s.ColumnHeight, z)), $"top ({x},{z})");
                    Assert.AreEqual(def.underBlock, chunk.GetVoxel(new int3(x, s.ColumnHeight - 1, z)), $"under ({x},{z})");
                    Assert.AreEqual(def.stoneBlock, chunk.GetVoxel(new int3(x, s.ColumnHeight - 5, z)), $"stone ({x},{z})");
                }
            }
            finally{
                foreach (KeyValue<int2, ChunkVoxelData> kv in results) kv.Value.Dispose();
                results.Dispose();
                jobs.Dispose();
            }
        }
    }
}
