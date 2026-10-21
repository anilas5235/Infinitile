using System.Collections.Generic;
using System.Diagnostics;
using Engine.Scripts.Data;
using Engine.Scripts.Jobs.Chunk;
using Engine.Scripts.WorldGen.Settings;
using NUnit.Framework;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.PerformanceTesting;

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
                RandomSeed = _config.GlobalSeed,
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
                RandomSeed = _config.GlobalSeed,
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

        private void MeasurePerChunk(string sampleGroup){
            _jobs.Clear();
            Random rng = new(42);
            for (int i = 0; i < MethodChunkCount; i++) _jobs.Add(rng.NextInt2(-1000, 1000));
            ChunkJob job = new(){
                Jobs = _jobs,
                Results = _results.AsParallelWriter(),
                RandomSeed = _config.GlobalSeed,
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
