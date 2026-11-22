using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Engine.Editor.WorldGen;
using Engine.Scripts.Data;
using Engine.Scripts.Jobs.Chunk;
using Engine.Scripts.Jobs.ColliderMeshing;
using Engine.Scripts.Jobs.Light;
using Engine.Scripts.VoxelConfig.Data.Internal;
using Engine.Scripts.WorldGen.Density;
using Engine.Scripts.WorldGen.Graph;
using NUnit.Framework;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.PerformanceTesting;
using UnityEngine;
using Debug = UnityEngine.Debug;
using static Engine.Scripts.Utils.VoxelConstants;

namespace Engine.Tests.Performance{
    /// <summary>
    ///     The density generator of WorldGen v2 on the benchmark fixture (DEC-147, §17.5), ms per chunk per chunk class on
    ///     a single worker: P1 alone, P1 with P4's terrain (P4 is the difference), the same with cells 4 × 4 × 4
    ///     (W2-DEN-06) and the whole chunk job with the placeholder fill and the run-length encoding. The downstream rows
    ///     (W2-PERF-03) light and mesh the class chunks for colliders. The fixture graph runs as generated code, as the
    ///     shipped graph does in the game.
    /// </summary>
    [TestFixture]
    internal class DensityChunkBenchmark{
        private const int WarmupCount = 3;
        private const int MeasurementCount = 20;

        private CompiledNoiseGraph _graph, _graph4;
        private GeneratorConfig _config;

        private static readonly DensityChunkClass[] Classes = DensityFixture.Classes;

        [OneTimeSetUp]
        public void OneTimeSetUp(){
            _graph = CompileOrFail(File.ReadAllText(DensityFixture.GraphPath));
            _graph4 = CompileOrFail(Cell4Json());
            _config = BenchmarkFixtures.CreateGeneratorConfig(Allocator.Persistent);
            _config.WorldGen.Seed = DensityFixture.Seed;
            _config.WorldGen.DensityStoneVoxel = BenchmarkFixtures.Stone;
        }

        [OneTimeTearDown]
        public void OneTimeTearDown(){
            _graph?.Dispose();
            _graph4?.Dispose();
            BenchmarkFixtures.DisposeGeneratorConfig(ref _config);
        }

        /// <summary>The fixture graph with the terrain marker at cell height 4 (W2-DEN-06).</summary>
        internal static string Cell4Json(){
            string json = File.ReadAllText(DensityFixture.GraphPath);
            const string from = "\"cellHeight\": 8,\n      \"in\": \"terrain_slid_density\"";
            string normalized = json.Replace("\r\n", "\n");
            if (!normalized.Contains(from)) throw new InvalidOperationException("The fixture graph's terrain marker has moved; update Cell4Json.");
            return normalized.Replace(from, "\"cellHeight\": 4,\n      \"in\": \"terrain_slid_density\"");
        }

        private static CompiledNoiseGraph CompileOrFail(string json){
            GraphCompileResult r = GraphCompiler.Compile(json, DensityFixture.GraphPath);
            if (!r.Succeeded) Assert.Fail("Compile failed:\n" + string.Join("\n", r.Messages));
            return r.Graph;
        }

        private static void AssertFits<TPrograms>(CompiledNoiseGraph graph) where TPrograms : struct, IGraphPrograms{
            Assert.IsTrue(GraphEvaluator.Fits<TPrograms>(graph), $"{typeof(TPrograms).Name} is out of date; see NoiseGraphBenchmark.GeneratedCode_IsUpToDate.");
        }

        #region Generation

        [Test]
        [Performance]
        public void DensityChunk_P1_MsPerChunk([ValueSource(nameof(Classes))] DensityChunkClass c){
            MeasureProbe<DensityFixtureCode>(_graph, c, DensityChunk.PassP1, $"DensityChunk P1 {c} ms/chunk");
        }

        [Test]
        [Performance]
        public void DensityChunk_Terrain_MsPerChunk([ValueSource(nameof(Classes))] DensityChunkClass c){
            MeasureProbe<DensityFixtureCode>(_graph, c, DensityChunk.PassTerrain, $"DensityChunk P1+P4 terrain {c} ms/chunk");
        }

        [Test]
        [Performance]
        public void DensityChunk_Terrain_Cell4_MsPerChunk([ValueSource(nameof(Classes))] DensityChunkClass c){
            MeasureProbe<DensityFixtureCell4Code>(_graph4, c, DensityChunk.PassTerrain, $"DensityChunk P1+P4 terrain cells 4x4x4 {c} ms/chunk");
        }

        /// <summary>The engine's chunk job over the class's chunks: the passes, the placeholder fill and the RLE.</summary>
        [Test]
        [Performance]
        public void DensityChunkJob_Run_MsPerChunk([ValueSource(nameof(Classes))] DensityChunkClass c){
            AssertFits<DensityFixtureCode>(_graph);
            DensityTerrainGraph.TryCreate(_graph, out DensityTerrainGraph terrain, out _);
            NativeList<int2> jobs = new(8, Allocator.Persistent);
            NativeParallelHashMap<int2, ChunkVoxelData> results = new(8, Allocator.Persistent);
            try{
                foreach (int2 chunk in DensityFixture.Chunks(c)) jobs.Add(chunk);
                DensityChunkJob<DensityFixtureCode> job = new(){ Jobs = jobs, Results = results.AsParallelWriter(), Config = _config, Terrain = terrain };
                SampleGroup group = new($"DensityChunkJob.Run {c} ms/chunk", SampleUnit.Millisecond);
                for (int i = 0; i < WarmupCount + MeasurementCount; i++){
                    Stopwatch watch = Stopwatch.StartNew();
                    job.Run(jobs.Length);
                    watch.Stop();
                    DisposeChunks(results);
                    if (i >= WarmupCount) Measure.Custom(group, watch.Elapsed.TotalMilliseconds / jobs.Length);
                }
            }
            finally{
                DisposeChunks(results);
                results.Dispose();
                jobs.Dispose();
            }
        }

        private void MeasureProbe<TPrograms>(CompiledNoiseGraph graph, DensityChunkClass c, int passes, string sampleGroup)
            where TPrograms : struct, IGraphPrograms{
            AssertFits<TPrograms>(graph);
            IReadOnlyList<int2> chunks = DensityFixture.Chunks(c);
            Assert.Greater(chunks.Count, 0, "the fixture lists no chunks of this class");
            DensityProbeJob<TPrograms> job = DensityProbe.Create<TPrograms>(graph, _config.WorldGen, int2.zero, passes, out DensityProbeResult result);
            try{
                SampleGroup group = new(sampleGroup, SampleUnit.Millisecond);
                for (int i = 0; i < WarmupCount + MeasurementCount; i++){
                    Stopwatch watch = Stopwatch.StartNew();
                    foreach (int2 chunk in chunks){
                        job.Origin = chunk * ChunkWidth;
                        job.Run();
                    }

                    watch.Stop();
                    if (i >= WarmupCount) Measure.Custom(group, watch.Elapsed.TotalMilliseconds / chunks.Count);
                }
            }
            finally{
                job.Counters.Dispose();
                result.Dispose();
            }
        }

        #endregion

        #region Downstream (W2-PERF-03)

        /// <summary>The class's chunks with their 3 × 3 rings, generated by the density chunk job.</summary>
        private NativeParallelHashMap<int2, ChunkVoxelData> GenerateWithRings(DensityChunkClass c){
            DensityTerrainGraph.TryCreate(_graph, out DensityTerrainGraph terrain, out _);
            NativeList<int2> generate = new(72, Allocator.Persistent);
            NativeParallelHashMap<int2, ChunkVoxelData> chunks = new(72, Allocator.Persistent);
            try{
                HashSet<int2> seen = new();
                foreach (int2 center in DensityFixture.Chunks(c))
                    for (int x = -1; x <= 1; x++)
                    for (int z = -1; z <= 1; z++)
                        if (seen.Add(center + new int2(x, z)))
                            generate.Add(center + new int2(x, z));
                new DensityChunkJob<InterpretedPrograms>{ Jobs = generate, Results = chunks.AsParallelWriter(), Config = _config, Terrain = terrain }.Run(generate.Length);
            }
            finally{
                generate.Dispose();
            }

            return chunks;
        }

        [Test]
        [Performance]
        public void LightJob_Run_Density_MsPerChunk([ValueSource(nameof(Classes))] DensityChunkClass c){
            NativeParallelHashMap<int2, ChunkVoxelData> chunks = GenerateWithRings(c);
            NativeList<int2> jobs = new(8, Allocator.Persistent);
            NativeParallelHashMap<int2, ChunkLightData> results = new(8, Allocator.Persistent);
            VoxelLightTable table = new(BenchmarkFixtures.VoxelTypeCount, Allocator.Persistent);
            try{
                for (int id = 0; id < BenchmarkFixtures.VoxelTypeCount; id++) table.Set(id, 0, 0);
                table.Set(BenchmarkFixtures.Stone, 15, 0);
                table.Set(BenchmarkFixtures.Water, 2, 0);
                foreach (int2 chunk in DensityFixture.Chunks(c)) jobs.Add(chunk);
                LightJob job = new(){ Jobs = jobs, Chunks = chunks.AsReadOnly(), Table = table, Results = results.AsParallelWriter() };
                SampleGroup group = new($"LightJob.Run density {c} ms/chunk", SampleUnit.Millisecond);
                for (int i = 0; i < WarmupCount + MeasurementCount; i++){
                    Stopwatch watch = Stopwatch.StartNew();
                    job.Run(jobs.Length);
                    watch.Stop();
                    foreach (KeyValue<int2, ChunkLightData> kv in results) kv.Value.Dispose();
                    results.Clear();
                    if (i >= WarmupCount) Measure.Custom(group, watch.Elapsed.TotalMilliseconds / jobs.Length);
                }
            }
            finally{
                foreach (KeyValue<int2, ChunkLightData> kv in results) kv.Value.Dispose();
                results.Dispose();
                table.Dispose();
                jobs.Dispose();
                DisposeChunks(chunks);
                chunks.Dispose();
            }
        }

        /// <summary>Collider meshes of every partition of the class's chunks, ms per chunk (all eight partitions).</summary>
        [Test]
        [Performance]
        public void CMeshBuildJob_Run_Density_MsPerChunk([ValueSource(nameof(Classes))] DensityChunkClass c){
            NativeParallelHashMap<int2, ChunkVoxelData> chunks = GenerateWithRings(c);
            NativeArray<UnityEngine.Rendering.VertexAttributeDescriptor> vertexParams = CVertex.CreateLayout(Allocator.Persistent);
            VoxelEngineRenderGenData renderGenData = BenchmarkFixtures.CreateRenderGenData(Allocator.Persistent);
            NativeList<int3> jobs = new(64, Allocator.Persistent);
            NativeParallelHashMap<int3, CMeshBuildJob.PartitionJobResult> results = new(64, Allocator.Persistent);
            try{
                IReadOnlyList<int2> centers = DensityFixture.Chunks(c);
                foreach (int2 chunk in centers)
                    for (int py = 0; py < PartitionsPerChunk; py++)
                        jobs.Add(new int3(chunk.x, py, chunk.y));
                ChunkAccessor accessor = new(chunks.AsReadOnly());
                SampleGroup group = new($"CMeshBuildJob.Run density {c} ms/chunk", SampleUnit.Millisecond);
                for (int i = 0; i < WarmupCount + MeasurementCount; i++){
                    Mesh.MeshDataArray meshData = Mesh.AllocateWritableMeshData(jobs.Length);
                    CMeshBuildJob job = new(){
                        ColliderVertexParams = vertexParams,
                        Accessor = accessor,
                        Jobs = jobs,
                        RenderGenData = renderGenData,
                        Results = results.AsParallelWriter(),
                        ColliderMeshDataArray = meshData
                    };
                    Stopwatch watch = Stopwatch.StartNew();
                    job.Run(jobs.Length);
                    watch.Stop();
                    meshData.Dispose();
                    results.Clear();
                    if (i >= WarmupCount) Measure.Custom(group, watch.Elapsed.TotalMilliseconds / centers.Count);
                }
            }
            finally{
                results.Dispose();
                jobs.Dispose();
                renderGenData.Dispose();
                vertexParams.Dispose();
                DisposeChunks(chunks);
                chunks.Dispose();
            }
        }

        /// <summary>
        ///     Counts per class, printed (no timing): voxel runs per column (the interval store's size); visible faces per
        ///     chunk (a stone face towards air or water, inside the chunk and towards the ring chunks; the GPU mesh has
        ///     four vertices per face before merging); partitions per chunk that hold voxels and that have a visible face
        ///     (the ones the GPU pool gets a slot for).
        /// </summary>
        [Test]
        public void DensityChunks_RunsPerColumn([ValueSource(nameof(Classes))] DensityChunkClass c){
            NativeParallelHashMap<int2, ChunkVoxelData> chunks = GenerateWithRings(c);
            try{
                long runs = 0, faces = 0, occupied = 0, withFaces = 0;
                IReadOnlyList<int2> centers = DensityFixture.Chunks(c);
                Dictionary<int2, ushort[]> expanded = new();

                ushort[] Expand(int2 key){
                    if (expanded.TryGetValue(key, out ushort[] v)) return v;
                    var list = chunks[key].GetData();
                    v = new ushort[list.Length];
                    for (int i = 0; i < v.Length; i++) v[i] = list.Get(i);
                    return expanded[key] = v;
                }

                ushort water = _config.WorldGen.WaterVoxel;
                bool Open(int2 chunk, int x, int y, int z){
                    if (y < 0) return false;
                    if (y >= ChunkHeight) return true;
                    int2 key = chunk + new int2(x < 0 ? -1 : x >= ChunkWidth ? 1 : 0, z < 0 ? -1 : z >= ChunkDepth ? 1 : 0);
                    x = (x + ChunkWidth) % ChunkWidth;
                    z = (z + ChunkDepth) % ChunkDepth;
                    ushort id = Expand(key)[(x * ChunkDepth + z) * ChunkHeight + y];
                    return id == 0 || id == water;
                }

                foreach (int2 chunk in centers){
                    ChunkVoxelData data = chunks[chunk];
                    runs += data.GetData().CompressedLength;
                    ushort[] v = Expand(chunk);
                    for (int p = 0; p < PartitionsPerChunk; p++){
                        if (data.PartitionHasVoxels(p)) occupied++;
                        long before = faces;
                        for (int x = 0; x < ChunkWidth; x++)
                        for (int z = 0; z < ChunkDepth; z++)
                        for (int y = p * PartitionHeight; y < (p + 1) * PartitionHeight; y++){
                            ushort id = v[(x * ChunkDepth + z) * ChunkHeight + y];
                            if (id == 0 || id == water) continue;
                            if (Open(chunk, x + 1, y, z)) faces++;
                            if (Open(chunk, x - 1, y, z)) faces++;
                            if (Open(chunk, x, y + 1, z)) faces++;
                            if (Open(chunk, x, y - 1, z)) faces++;
                            if (Open(chunk, x, y, z + 1)) faces++;
                            if (Open(chunk, x, y, z - 1)) faces++;
                        }

                        if (faces > before) withFaces++;
                    }
                }

                double n = centers.Count;
                Debug.Log($"density {c}: {runs / n:F0} runs per chunk, {runs / n / (ChunkWidth * ChunkDepth):F2} per column; " +
                          $"{faces / n:F0} visible faces per chunk; partitions with voxels {occupied / n:F2}, with visible faces {withFaces / n:F2} per chunk");
            }
            finally{
                DisposeChunks(chunks);
                chunks.Dispose();
            }
        }

        #endregion

        private static void DisposeChunks(NativeParallelHashMap<int2, ChunkVoxelData> chunks){
            foreach (KeyValue<int2, ChunkVoxelData> kv in chunks) kv.Value.Dispose();
            chunks.Clear();
        }
    }
}
