using System.Collections.Generic;
using System.Diagnostics;
using Engine.Scripts.Data;
using Engine.Scripts.Jobs.Chunk;
using Engine.Scripts.Jobs.Light;
using Engine.Scripts.VoxelConfig.Data.Voxel;
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
    ///     Light job, ms per chunk on a single worker (LightJob.Run). Natural terrain is the
    ///     Terrain-first six-biome world with the placeholder flora (trees, leaves, water); the worst case is a
    ///     cavity full of emitters under a leaf roof, where the BFS has to visit every voxel of the region for all
    ///     four channels. The forest row lights the same chunks grown with the shipped Forest species. Budget: ≤ 1 ms per chunk on natural terrain.
    /// </summary>
    [TestFixture]
    internal class LightJobBenchmark{
        private const int WarmupCount = 3;
        private const int MeasurementCount = 20;
        private const int ChunkCount = 8;

        // Light table ids: the fixture ids plus the placeholder flora (BenchmarkFixtures.AddPlaceholderFlora)
        private const ushort Leaves = 16;
        private const ushort Lava = 17;
        private const int TableSize = 18;

        private readonly List<UnityEngine.Object> _created = new();
        private NativeParallelHashMap<int2, ChunkVoxelData> _chunks;
        private NativeList<int2> _jobs;
        private NativeParallelHashMap<int2, ChunkLightData> _results;
        private VoxelLightTable _table;

        [SetUp]
        public void SetUp(){
            _chunks = new NativeParallelHashMap<int2, ChunkVoxelData>(ChunkCount * 9, Allocator.Persistent);
            _jobs = new NativeList<int2>(ChunkCount, Allocator.Persistent);
            _results = new NativeParallelHashMap<int2, ChunkLightData>(ChunkCount, Allocator.Persistent);
            _table = new VoxelLightTable(TableSize, Allocator.Persistent);
            for (int id = 0; id < TableSize; id++) _table.Set(id, 0, 0); // air and foliage (6…13) are clear
            foreach (ushort solid in new ushort[]{
                         BenchmarkFixtures.Grass, BenchmarkFixtures.Dirt, BenchmarkFixtures.Stone, BenchmarkFixtures.Ice, 14, 15
                     })
                _table.Set(solid, 15, 0);
            _table.Set(BenchmarkFixtures.Water, 2, 0);
            _table.Set(Leaves, 1, 0);
            _table.Set(Lava, 15, VoxelLight.PackRgb(15, 9, 3));
        }

        [TearDown]
        public void TearDown(){
            DisposeResults();
            _results.Dispose();
            foreach (KeyValue<int2, ChunkVoxelData> kv in _chunks) kv.Value.Dispose();
            _chunks.Dispose();
            _jobs.Dispose();
            _table.Dispose();
            foreach (UnityEngine.Object o in _created) UnityEngine.Object.DestroyImmediate(o);
            _created.Clear();
        }

        [Test]
        [Performance]
        public void LightJob_Run_NaturalTerrain_MsPerChunk(){
            GeneratorConfig config = BenchmarkFixtures.CreatePlaceholderGeneratorConfig(GenArchitecture.TerrainFirst,
                BlendMethod.None, Allocator.Persistent, _created, true);
            GenerateScatteredChunks(ref config);
            MeasurePerChunk("LightJob.Run natural terrain ms/chunk");
        }

        /// <summary>
        ///     The same chunks on the DistanceBlend world with the shipped Forest rules on every land biome, so the light
        ///     runs through the crowns of the tree species with the attenuation of their leaf voxels.
        /// </summary>
        [Test]
        [Performance]
        public void LightJob_Run_ForestSpecies_MsPerChunk(){
            GeneratorConfig config = BenchmarkFixtures.CreateSpeciesGeneratorConfig(GenArchitecture.TerrainFirst,
                SpeciesFlora.ShippedForest, Allocator.Persistent, _created, out Dictionary<Voxel, ushort> ids);
            GenerateScatteredChunks(ref config);
            _table.Dispose();
            _table = BenchmarkFixtures.CreateLightTable(ids, Allocator.Persistent);
            MeasurePerChunk("LightJob.Run forest species ms/chunk");
        }

        [Test]
        [Performance]
        public void LightJob_Run_WorstCase_MsPerChunk(){
            // Chunks (0…3, 0…1) with their rings: ChunkCount distinct centres
            for (int x = -1; x <= 4; x++)
            for (int z = -1; z <= 2; z++){
                int2 chunk = new(x, z);
                _chunks.Add(chunk, EmitterCavityChunk(chunk));
                if (x is >= 0 and <= 3 && z is >= 0 and <= 1) _jobs.Add(chunk);
            }
            MeasurePerChunk("LightJob.Run worst case ms/chunk");
        }

        /// <summary>
        ///     Generates the same scattered chunks as the generation benchmark's per-method rows, each with its 3 × 3
        ///     ring, into the light input and adds the centres as jobs; disposes <paramref name="config" />.
        /// </summary>
        private void GenerateScatteredChunks(ref GeneratorConfig config){
            try{
                Random rng = new(42);
                NativeList<int2> generate = new(ChunkCount * 9, Allocator.Persistent);
                for (int i = 0; i < ChunkCount; i++){
                    int2 center = rng.NextInt2(-1000, 1000);
                    _jobs.Add(center);
                    for (int x = -1; x <= 1; x++)
                    for (int z = -1; z <= 1; z++)
                        generate.Add(center + new int2(x, z));
                }

                new ChunkJob{
                    Jobs = generate,
                    Results = _chunks.AsParallelWriter(),
                    Config = config
                }.Run(generate.Length);
                generate.Dispose();
            }
            finally{
                BenchmarkFixtures.DisposeGeneratorConfig(ref config);
            }
        }

        /// <summary>
        ///     Stone floor (y &lt; 20), an air cavity up to y 235 with a Lava emitter on every 8th voxel in x, y and
        ///     z, and a leaf roof (y ≥ 236) that lets the sky in only a few voxels deep.
        /// </summary>
        private static ChunkVoxelData EmitterCavityChunk(int2 chunk){
            ChunkVoxelData data = new(4096);
            ushort run = BenchmarkFixtures.Stone;
            int count = 0;

            void Add(ushort id){
                if (id == run){
                    count++;
                    return;
                }

                data.AddVoxels(run, count);
                run = id;
                count = 1;
            }

            for (int x = 0; x < ChunkWidth; x++)
            for (int z = 0; z < ChunkDepth; z++){
                bool lampColumn = (chunk.x * ChunkWidth + x) % 8 == 4 && (chunk.y * ChunkDepth + z) % 8 == 4;
                for (int y = 0; y < ChunkHeight; y++){
                    ushort id = y < 20 ? BenchmarkFixtures.Stone
                        : y >= 236 ? Leaves
                        : lampColumn && y % 8 == 4 ? Lava
                        : BenchmarkFixtures.Air;
                    Add(id);
                }
            }

            data.AddVoxels(run, count);
            return data;
        }

        private void MeasurePerChunk(string sampleGroup){
            LightJob job = new(){
                Jobs = _jobs,
                Chunks = _chunks.AsReadOnly(),
                Table = _table,
                Results = _results.AsParallelWriter()
            };

            // Measure.Method records the time of one call; one call here lights all chunks, so the per-chunk mean is
            // recorded with Measure.Custom instead.
            SampleGroup group = new(sampleGroup, SampleUnit.Millisecond);
            for (int i = 0; i < WarmupCount + MeasurementCount; i++){
                Stopwatch watch = Stopwatch.StartNew();
                job.Run(_jobs.Length);
                watch.Stop();
                DisposeResults();
                if (i >= WarmupCount) Measure.Custom(group, watch.Elapsed.TotalMilliseconds / _jobs.Length);
            }
        }

        private void DisposeResults(){
            foreach (KeyValue<int2, ChunkLightData> kv in _results) kv.Value.Dispose();
            _results.Clear();
        }
    }
}
