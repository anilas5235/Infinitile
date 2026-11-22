using System.Collections.Generic;
using System.Diagnostics;
using Engine.Editor.WorldGen;
using Engine.Scripts.Jobs.Chunk;
using Engine.Scripts.WorldGen.Biomes;
using Engine.Scripts.WorldGen.Density;
using Engine.Scripts.WorldGen.Graph;
using Engine.Scripts.WorldGen.Settings;
using NUnit.Framework;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.PerformanceTesting;

namespace Engine.Tests.Performance{
    /// <summary>
    ///     The parts of P2 that §2.4 lists apart (WorldGen v2 V3): the compiled biome lookup alone, on every column of the
    ///     padded 64 × 64 biome map as if no cell were uniform (the pass itself looks up far fewer), and the 256-bit
    ///     biome masks of DEC-144 against a 64-bit mask, one test per padded column. The pass rows themselves (P2, P3)
    ///     are in <see cref="DensityChunkBenchmark" />.
    /// </summary>
    public class BiomePassBenchmark{
        private const int WarmupCount = 3, MeasurementCount = 15, Repeats = 8;
        private const int Columns = DensityLayout.BiomeSpan * DensityLayout.BiomeSpan;

        private BiomeLayoutData _layout;
        private NativeArray<float> _climate; // [column][5] over the fixture's chunks
        private NativeArray<byte> _ids;
        private int _chunks;

        [OneTimeSetUp]
        public void OneTimeSetUp(){
            GraphCompileResult r = DensityFixture.Compile();
            Assert.IsTrue(r.Succeeded, string.Join("\n", r.Messages));
            using CompiledNoiseGraph graph = r.Graph;
            GeneratorConfigScope scope = new();
            try{
                List<float> climate = new();
                List<byte> ids = new();
                foreach ((DensityChunkClass _, int2 chunk) in DensityFixture.AllChunks()){
                    using DensityProbeResult probe = DensityProbe.Run(graph, scope.Config.WorldGen, chunk * DensityLayout.ChunkSize, DensityChunk.PassBiomes);
                    for (int z = 0; z < DensityLayout.BiomeSpan; z++)
                    for (int x = 0; x < DensityLayout.BiomeSpan; x++){
                        // bilinear over the 17 × 17 lattice, as P2 interpolates (the exact offset does not matter for the cost)
                        float fx = math.min(x / 4f, DensityLayout.BiomeLattice - 1.001f), fz = math.min(z / 4f, DensityLayout.BiomeLattice - 1.001f);
                        int i = (int)fx, j = (int)fz;
                        float tx = fx - i, tz = fz - j;
                        for (int p = 0; p < BiomeBox.Parameters; p++)
                            climate.Add(math.lerp(math.lerp(probe.ClimateAt(p, i, j), probe.ClimateAt(p, i + 1, j), tx),
                                math.lerp(probe.ClimateAt(p, i, j + 1), probe.ClimateAt(p, i + 1, j + 1), tx), tz));
                        ids.Add(probe.PaddedHardAt(x - DensityLayout.BiomePadding, z - DensityLayout.BiomePadding));
                    }

                    _chunks++;
                }

                _climate = new NativeArray<float>(climate.ToArray(), Allocator.Persistent);
                _ids = new NativeArray<byte>(ids.ToArray(), Allocator.Persistent);
                _layout = scope.Config.WorldGen.Layout;
                scope.Config.WorldGen.Layout = default; // kept for the tests
            }
            finally{
                scope.Dispose();
            }
        }

        [OneTimeTearDown]
        public void OneTimeTearDown(){
            if (_climate.IsCreated) _climate.Dispose();
            if (_ids.IsCreated) _ids.Dispose();
            _layout.Dispose();
        }

        [Test]
        [Performance]
        public void BiomeLookup_AllPaddedColumns_MsPerChunk(){
            NativeArray<int> sink = new(1, Allocator.TempJob);
            try{
                LookupJob job = new(){ Lookup = _layout.Main, Climate = _climate, Count = _climate.Length / BiomeBox.Parameters, Sink = sink };
                Measure(() => job.Run(), "BiomeLookup all 64x64 padded columns ms/chunk");
            }
            finally{
                sink.Dispose();
            }
        }

        [Test]
        [Performance]
        public void BiomeMask_256Bit_MsPerChunk(){
            NativeArray<int> sink = new(1, Allocator.TempJob);
            try{
                MaskJob job = new(){ Mask = _layout.Water, Ids = _ids, Sink = sink };
                Measure(() => job.Run(), "BiomeMask 256-bit Contains per padded column ms/chunk");
            }
            finally{
                sink.Dispose();
            }
        }

        [Test]
        [Performance]
        public void BiomeMask_64Bit_MsPerChunk(){
            NativeArray<int> sink = new(1, Allocator.TempJob);
            try{
                Mask64Job job = new(){ Mask = _layout.Water.W0, Ids = _ids, Sink = sink };
                Measure(() => job.Run(), "BiomeMask 64-bit Contains per padded column ms/chunk");
            }
            finally{
                sink.Dispose();
            }
        }

        private void Measure(System.Action run, string name){
            SampleGroup group = new(name, SampleUnit.Millisecond);
            for (int i = 0; i < WarmupCount + MeasurementCount; i++){
                Stopwatch watch = Stopwatch.StartNew();
                for (int k = 0; k < Repeats; k++) run();
                watch.Stop();
                if (i >= WarmupCount) Unity.PerformanceTesting.Measure.Custom(group, watch.Elapsed.TotalMilliseconds / Repeats / _chunks);
            }
        }

        [BurstCompile(FloatMode = FloatMode.Strict)]
        private unsafe struct LookupJob : IJob{
            [ReadOnly] public BiomeLookup Lookup;
            [ReadOnly] public NativeArray<float> Climate;
            public int Count;
            public NativeArray<int> Sink;

            public void Execute(){
                float* p = (float*)Climate.GetUnsafeReadOnlyPtr();
                int sum = 0;
                for (int i = 0; i < Count; i++) sum += Lookup.BiomeAt(p + i * BiomeBox.Parameters);
                Sink[0] = sum;
            }
        }

        [BurstCompile(FloatMode = FloatMode.Strict)]
        private struct MaskJob : IJob{
            public BiomeMask Mask;
            [ReadOnly] public NativeArray<byte> Ids;
            public NativeArray<int> Sink;

            public void Execute(){
                int sum = 0;
                for (int i = 0; i < Ids.Length; i++)
                    if (Mask.Contains(Ids[i]))
                        sum++;
                Sink[0] = sum;
            }
        }

        [BurstCompile(FloatMode = FloatMode.Strict)]
        private struct Mask64Job : IJob{
            public ulong Mask;
            [ReadOnly] public NativeArray<byte> Ids;
            public NativeArray<int> Sink;

            public void Execute(){
                int sum = 0;
                for (int i = 0; i < Ids.Length; i++)
                    if (((Mask >> (Ids[i] & 63)) & 1UL) != 0)
                        sum++;
                Sink[0] = sum;
            }
        }

        private sealed class GeneratorConfigScope : System.IDisposable{
            public GeneratorConfig Config;

            public GeneratorConfigScope(){
                Config = BenchmarkFixtures.CreateGeneratorConfig(Allocator.Persistent);
                Config.WorldGen.Seed = DensityFixture.Seed;
                Config.WorldGen.DensityStoneVoxel = BenchmarkFixtures.Stone;
                DensityFixture.UseShippedBiomes(ref Config.WorldGen);
            }

            public void Dispose(){
                BenchmarkFixtures.DisposeGeneratorConfig(ref Config);
            }
        }
    }
}
