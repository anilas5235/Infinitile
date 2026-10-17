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
    ///     generation method; at the moment the engine has one (noise terrain + RLE write).
    /// </summary>
    [TestFixture]
    internal class ChunkGenerationBenchmark{
        private const int WarmupCount = 3;
        private const int MeasurementCount = 20;

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
            _config.Dispose();
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

        private void DisposeResults(){
            foreach (KeyValue<int2, ChunkVoxelData> kv in _results) kv.Value.Dispose();
            _results.Clear();
        }
    }
}
