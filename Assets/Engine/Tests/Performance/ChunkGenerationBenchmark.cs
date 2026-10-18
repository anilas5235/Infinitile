using System.Collections.Generic;
using Engine.Scripts.Data;
using Engine.Scripts.Jobs.Chunk;
using NUnit.Framework;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.PerformanceTesting;

namespace Engine.Tests.Performance{
    /// <summary>
    ///     PERF-02: chunk data generation, ms per chunk on a single worker (ChunkJob.Run). One sample group per
    ///     architecture: the legacy noise terrain, the Terrain-first sampler (Phase 1) and the Biome-first sampler
    ///     (Phase 2). All write the chunk through the same RLE path.
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
            _results = new NativeParallelHashMap<int2, ChunkVoxelData>(1, Allocator.Persistent);
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
        public void ChunkJob_Run_TerrainRle_MsPerChunk(){
            ChunkJob job = new(){
                Jobs = _jobs,
                Results = _results.AsParallelWriter(),
                RandomSeed = _config.GlobalSeed,
                Config = _config
            };

            Measure.Method(() => job.Run(_jobs.Length))
                .SampleGroup(new SampleGroup("ChunkJob.Run TerrainRle ms/chunk", SampleUnit.Millisecond))
                .WarmupCount(WarmupCount)
                .MeasurementCount(MeasurementCount)
                .IterationsPerMeasurement(1)
                .CleanUp(DisposeResults)
                .Run();
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

        private void DisposeResults(){
            foreach (KeyValue<int2, ChunkVoxelData> kv in _results) kv.Value.Dispose();
            _results.Clear();
        }
    }
}
