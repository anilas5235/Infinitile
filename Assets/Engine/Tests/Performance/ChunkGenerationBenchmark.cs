using System.Collections.Generic;
using System.Diagnostics;
using Engine.Scripts.Data;
using Engine.Scripts.Jobs.Chunk;
using Engine.Scripts.VoxelConfig.Data.Generation;
using Engine.Scripts.WorldGen.Caves;
using Engine.Scripts.WorldGen.Settings;
using NUnit.Framework;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.PerformanceTesting;
using static Engine.Scripts.Utils.VoxelConstants;

namespace Engine.Tests.Performance{
    /// <summary>
    ///     Chunk data generation, ms per chunk on a single worker (ChunkJob.Run). One sample group per architecture
    ///     (Terrain-first and Biome-first sampler); both write the chunk through the same RLE path.
    ///     <para>
    ///         The per-method rows (architecture x blend method) average over a fixed set of
    ///         <see cref="MethodChunkCount" /> scattered chunks, because the cost of the spatial methods depends on
    ///         how many columns lie near a boundary; the single-chunk rows above stay comparable with earlier
    ///         baselines.
    ///     </para>
    /// </summary>
    [TestFixture]
    internal class ChunkGenerationBenchmark{
        private const int WarmupCount = 3;
        private const int MeasurementCount = 20;

        private readonly List<UnityEngine.Object> _created = new();
        private GeneratorConfig _config;
        private NativeList<int2> _jobs;
        private NativeParallelHashMap<int2, ChunkVoxelData> _results;

        [SetUp]
        public void SetUp(){
            _config = BenchmarkFixtures.CreateGeneratorConfig(Allocator.Persistent);
            _jobs = new NativeList<int2>(1, Allocator.Persistent);
            _jobs.Add(new int2(3, -7));
            _results = new NativeParallelHashMap<int2, ChunkVoxelData>(MethodChunkCount, Allocator.Persistent);
        }

        [TearDown]
        public void TearDown(){
            DisposeResults();
            _results.Dispose();
            _jobs.Dispose();
            BenchmarkFixtures.DisposeGeneratorConfig(ref _config);
            foreach (UnityEngine.Object o in _created) UnityEngine.Object.DestroyImmediate(o);
            _created.Clear();
        }

        [Test]
        [Performance]
        public void ChunkJob_Run_TerrainFirst_MsPerChunk(){
            BenchmarkFixtures.DisposeGeneratorConfig(ref _config);
            _config = BenchmarkFixtures.CreateTerrainFirstGeneratorConfig(Allocator.Persistent, _created);
            ChunkJob job = new(){
                Jobs = _jobs,
                Results = _results.AsParallelWriter(),
                Config = _config
            };

            Measure.Method(() => job.Run(_jobs.Length))
                .SampleGroup(new SampleGroup("ChunkJob.Run TerrainFirst ms/chunk", SampleUnit.Millisecond))
                .WarmupCount(WarmupCount)
                .MeasurementCount(MeasurementCount)
                .IterationsPerMeasurement(1)
                .CleanUp(DisposeResults)
                .Run();
        }

        [Test]
        [Performance]
        public void ChunkJob_Run_BiomeFirst_MsPerChunk(){
            BenchmarkFixtures.DisposeGeneratorConfig(ref _config);
            _config = BenchmarkFixtures.CreateBiomeFirstGeneratorConfig(Allocator.Persistent, _created);
            ChunkJob job = new(){
                Jobs = _jobs,
                Results = _results.AsParallelWriter(),
                Config = _config
            };

            Measure.Method(() => job.Run(_jobs.Length))
                .SampleGroup(new SampleGroup("ChunkJob.Run BiomeFirst ms/chunk", SampleUnit.Millisecond))
                .WarmupCount(WarmupCount)
                .MeasurementCount(MeasurementCount)
                .IterationsPerMeasurement(1)
                .CleanUp(DisposeResults)
                .Run();
        }

        private const int MethodChunkCount = 16;

        [Test]
        [Performance]
        public void ChunkJob_Run_PerMethod_MsPerChunk(
            [Values(GenArchitecture.TerrainFirst, GenArchitecture.BiomeFirst)] GenArchitecture architecture,
            [Values(BlendMethod.None, BlendMethod.DistanceBlend, BlendMethod.DistortedBoundary, BlendMethod.ParameterSpaceWeighting)]
            BlendMethod method){
            BenchmarkFixtures.DisposeGeneratorConfig(ref _config);
            _config = BenchmarkFixtures.CreatePlaceholderGeneratorConfig(architecture, method, Allocator.Persistent, _created);
            MeasurePerChunk($"ChunkJob.Run {architecture} {method} ms/chunk");
        }

        /// <summary>
        ///     The same chunks and world with the placeholder flora rules, so the difference to
        ///     <see cref="ChunkJob_Run_PerMethod_MsPerChunk" /> is the cost of the flora decision (with its ring of
        ///     r_tree + reach columns) and of stamping.
        /// </summary>
        [Test]
        [Performance]
        public void ChunkJob_Run_PerMethod_WithFlora_MsPerChunk(
            [Values(GenArchitecture.TerrainFirst, GenArchitecture.BiomeFirst)] GenArchitecture architecture,
            [Values(BlendMethod.None, BlendMethod.DistanceBlend, BlendMethod.DistortedBoundary, BlendMethod.ParameterSpaceWeighting)]
            BlendMethod method){
            BenchmarkFixtures.DisposeGeneratorConfig(ref _config);
            _config = BenchmarkFixtures.CreatePlaceholderGeneratorConfig(architecture, method, Allocator.Persistent, _created, true);
            Assert.IsTrue(_config.WorldGen.HasFlora);
            MeasurePerChunk($"ChunkJob.Run {architecture} {method} flora ms/chunk");
        }

        /// <summary>
        ///     The flora world of <see cref="ChunkJob_Run_PerMethod_WithFlora_MsPerChunk" /> with DistanceBlend, where every
        ///     biome has a snow top block, freezing water and a strata block, so each column also decides snow, ice and
        ///     strata shift and every stone voxel is tested against the bands.
        /// </summary>
        [Test]
        [Performance]
        public void ChunkJob_Run_SnowAndStrata_MsPerChunk(
            [Values(GenArchitecture.TerrainFirst, GenArchitecture.BiomeFirst)] GenArchitecture architecture){
            BenchmarkFixtures.DisposeGeneratorConfig(ref _config);
            _config = BenchmarkFixtures.CreatePlaceholderGeneratorConfig(architecture, BlendMethod.DistanceBlend,
                Allocator.Persistent, _created, true);
            for (int b = 0; b < _config.WorldGen.BiomeCount; b++){
                Biome.BiomeDef def = _config.WorldGen.Biomes[b];
                def.snowTopBlock = BenchmarkFixtures.Dirt;
                def.freezesWater = 1;
                def.strataBlock = BenchmarkFixtures.Dirt;
                _config.WorldGen.Biomes[b] = def;
            }

            MeasurePerChunk($"ChunkJob.Run {architecture} DistanceBlend snow strata ms/chunk");
        }

        /// <summary>
        ///     The flora world of <see cref="ChunkJob_Run_PerMethod_WithFlora_MsPerChunk" /> with DistanceBlend, where every
        ///     biome has beaches, so shore columns also roll the beach noise and probe for water nearby, and flora rules
        ///     test the beach ground.
        /// </summary>
        [Test]
        [Performance]
        public void ChunkJob_Run_Beaches_MsPerChunk(
            [Values(GenArchitecture.TerrainFirst, GenArchitecture.BiomeFirst)] GenArchitecture architecture){
            BenchmarkFixtures.DisposeGeneratorConfig(ref _config);
            _config = BenchmarkFixtures.CreatePlaceholderGeneratorConfig(architecture, BlendMethod.DistanceBlend,
                Allocator.Persistent, _created, true);
            for (int b = 0; b < _config.WorldGen.BiomeCount; b++){
                Biome.BiomeDef def = _config.WorldGen.Biomes[b];
                def.beachBlock = BenchmarkFixtures.Dirt;
                def.beachHeight = 3;
                def.beachFloraGround = 1;
                _config.WorldGen.Biomes[b] = def;
            }

            _config.WorldGen.HasBeaches = true;
            Assert.Greater(_config.WorldGen.BeachWaterDistance, 0);
            MeasurePerChunk($"ChunkJob.Run {architecture} DistanceBlend beaches ms/chunk");
        }

        /// <summary>
        ///     The DistanceBlend world in which every land biome carries the rules of the shipped Forest biome: its tree
        ///     species, undergrowth, mushrooms and fallen logs with their density noises. This is the cost of the
        ///     species set in play, so the row moves when the shipped Forest rules or species are tuned.
        /// </summary>
        [Test]
        [Performance]
        public void ChunkJob_Run_ForestSpecies_MsPerChunk(
            [Values(GenArchitecture.TerrainFirst, GenArchitecture.BiomeFirst)] GenArchitecture architecture){
            BenchmarkFixtures.DisposeGeneratorConfig(ref _config);
            _config = BenchmarkFixtures.CreateSpeciesGeneratorConfig(architecture, SpeciesFlora.ShippedForest,
                Allocator.Persistent, _created, out _);
            Assert.Greater(_config.WorldGen.FloraPadding, 0);
            MeasurePerChunk($"ChunkJob.Run {architecture} DistanceBlend forest species ms/chunk");
        }

        /// <summary>
        ///     Worst case: every land biome grows only the shipped variant with the largest reach, so every chunk evaluates
        ///     the largest padding and is filled with the biggest trees as densely as their spacing allows.
        /// </summary>
        [Test]
        [Performance]
        public void ChunkJob_Run_LargestReach_MsPerChunk(
            [Values(GenArchitecture.TerrainFirst, GenArchitecture.BiomeFirst)] GenArchitecture architecture){
            BenchmarkFixtures.DisposeGeneratorConfig(ref _config);
            _config = BenchmarkFixtures.CreateSpeciesGeneratorConfig(architecture, SpeciesFlora.LargestReach,
                Allocator.Persistent, _created, out _);
            Assert.Greater(_config.WorldGen.FloraReach, 0);
            Assert.Greater(_config.WorldGen.FloraPadding, _config.WorldGen.FloraReach);
            UnityEngine.Debug.Log($"largest reach {_config.WorldGen.FloraReach}, padding {_config.WorldGen.FloraPadding}");
            MeasurePerChunk($"ChunkJob.Run {architecture} DistanceBlend largest reach ms/chunk");
        }

        /// <summary>
        ///     Typical land with caves: the flora world of <see cref="ChunkJob_Run_PerMethod_WithFlora_MsPerChunk" /> with
        ///     DistanceBlend and the cave settings, lava, ores and bedrock of the shipped asset. Without caves, the same
        ///     world has the cave passes switched off, so the difference is their cost. The row moves with the shipped
        ///     cave tuning.
        /// </summary>
        [Test]
        [Performance]
        public void ChunkJob_Run_Caves_MsPerChunk(
            [Values(GenArchitecture.TerrainFirst, GenArchitecture.BiomeFirst)] GenArchitecture architecture,
            [Values(true, false)] bool caves){
            BenchmarkFixtures.DisposeGeneratorConfig(ref _config);
            _config = BenchmarkFixtures.CreatePlaceholderGeneratorConfig(architecture, BlendMethod.DistanceBlend,
                Allocator.Persistent, _created, true);
            BenchmarkFixtures.UseShippedCaves(ref _config, Allocator.Persistent);
            _config.WorldGen.Caves.Enabled = caves;
            if (caves) LogWormSteps(architecture + " shipped caves");
            MeasurePerChunk($"ChunkJob.Run {architecture} DistanceBlend caves {(caves ? "on" : "off")} ms/chunk");
        }

        /// <summary>
        ///     The cave world of <see cref="ChunkJob_Run_Caves_MsPerChunk" /> with the cavern and lava masks open
        ///     everywhere and the largest worm density, so every chunk carves halls, lava pockets and many worms.
        /// </summary>
        [Test]
        [Performance]
        public void ChunkJob_Run_CaveWorstCase_MsPerChunk(
            [Values(GenArchitecture.TerrainFirst, GenArchitecture.BiomeFirst)] GenArchitecture architecture){
            BenchmarkFixtures.DisposeGeneratorConfig(ref _config);
            _config = BenchmarkFixtures.CreatePlaceholderGeneratorConfig(architecture, BlendMethod.DistanceBlend,
                Allocator.Persistent, _created, true);
            BenchmarkFixtures.UseShippedCaves(ref _config, Allocator.Persistent);
            BenchmarkFixtures.MakeCaveWorstCase(ref _config.WorldGen.Caves);
            LogWormSteps(architecture + " cave worst case");
            MeasurePerChunk($"ChunkJob.Run {architecture} DistanceBlend cave worst case ms/chunk");
        }

        /// <summary>
        ///     Logs the worm steps the measured chunks walk and those whose sphere box overlaps the chunk, as the worm
        ///     pass enumerates them: every worm that starts within the padding, with its branch.
        /// </summary>
        private void LogWormSteps(string label){
            WorldGenConfig cfg = _config.WorldGen;
            ref readonly CaveConfig c = ref cfg.Caves;
            Random rng = new(42);
            long walked = 0, carved = 0;
            int worms = 0;
            for (int i = 0; i < MethodChunkCount; i++){
                int2 origin = rng.NextInt2(-1000, 1000) * new int2(ChunkWidth, ChunkDepth);
                int padding = c.WormPadding + cfg.FloraPadding;
                int2 min = origin - padding, max = origin + new int2(ChunkWidth - 1,
                    ChunkDepth - 1) + padding;
                int2 firstCell = Worms.Cell(min, cfg), lastCell = Worms.Cell(max, cfg);
                for (int cx = firstCell.x; cx <= lastCell.x; cx++)
                for (int cz = firstCell.y; cz <= lastCell.y; cz++)
                for (int k = 0; k < Worms.MaxPerCell; k++){
                    int2 cell = new(cx, cz);
                    if (!Worms.Present(cell, k, cfg)) continue;
                    int2 start = Worms.StartColumn(cell, k, cfg);
                    if (math.any(start < min) || math.any(start > max)) continue;
                    if (!Worms.Begin(cell, k, cfg, default, out Worm worm)) continue;
                    worms++;
                    Worm branch = default;
                    bool branched = false;
                    do{
                        Count(worm);
                        if (worm.Step == worm.BranchStep) branched = Worms.Branch(worm, c, out branch);
                    } while (Worms.Next(ref worm, c));

                    if (!branched) continue;
                    do{
                        Count(branch);
                    } while (Worms.Next(ref branch, c));
                }

                void Count(in Worm w){
                    walked++;
                    float2 lo = w.Position.xz - w.Radius - origin, hi = w.Position.xz + w.Radius - origin;
                    if (hi.x >= 0 && lo.x < ChunkWidth && hi.y >= 0 &&
                        lo.y < ChunkDepth)
                        carved++;
                }
            }

            UnityEngine.Debug.Log($"{label}: {(double)worms / MethodChunkCount:F1} worms, {(double)walked / MethodChunkCount:F0} " +
                                  $"walked and {(double)carved / MethodChunkCount:F0} carving steps per chunk");
        }

        private void MeasurePerChunk(string sampleGroup){
            _jobs.Clear();
            Random rng = new(42);
            for (int i = 0; i < MethodChunkCount; i++) _jobs.Add(rng.NextInt2(-1000, 1000));
            ChunkJob job = new(){
                Jobs = _jobs,
                Results = _results.AsParallelWriter(),
                Config = _config
            };

            // Measure.Method records the time of one call; one call here generates all chunks, so the per-chunk mean
            // is recorded with Measure.Custom instead.
            SampleGroup group = new(sampleGroup, SampleUnit.Millisecond);
            for (int i = 0; i < WarmupCount + MeasurementCount; i++){
                Stopwatch watch = Stopwatch.StartNew();
                job.Run(_jobs.Length);
                watch.Stop();
                DisposeResults();
                if (i >= WarmupCount) Measure.Custom(group, watch.Elapsed.TotalMilliseconds / MethodChunkCount);
            }
        }

        private void DisposeResults(){
            foreach (KeyValue<int2, ChunkVoxelData> kv in _results) kv.Value.Dispose();
            _results.Clear();
        }
    }
}
