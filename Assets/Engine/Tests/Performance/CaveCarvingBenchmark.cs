using System.Collections.Generic;
using System.Diagnostics;
using Engine.Scripts.Jobs.Chunk;
using Engine.Scripts.Utils.Extensions;
using Engine.Scripts.WorldGen.Caves;
using Engine.Scripts.WorldGen.Settings;
using NUnit.Framework;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.PerformanceTesting;
using static Engine.Scripts.Utils.VoxelConstants;

namespace Engine.Tests.Performance{
    /// <summary>
    ///     Cavern carving, ms per chunk on a single worker: the hall and pillar noises on the 4 × 4 × 4 lattice of the
    ///     chunk pass, against sampling both noises at every voxel. The chunks are the scattered chunks of the
    ///     generation benchmark in the cave worst case (cavern mask open everywhere), so every column carves its band;
    ///     the column limits come from the chunk pipeline beforehand and are not timed. The per-voxel row also logs
    ///     how many voxels the two carve differently.
    /// </summary>
    [TestFixture]
    internal class CaveCarvingBenchmark{
        private const int WarmupCount = 3;
        private const int MeasurementCount = 20;
        private const int ChunkCount = 16;
        private const int Columns = ChunkWidth * ChunkDepth;

        private readonly List<UnityEngine.Object> _created = new();
        private GeneratorConfig _config;
        private NativeArray<int3> _origins;
        private NativeArray<int> _cavernTop;
        private NativeArray<ushort> _voxels;

        /// <summary>The hall limit of every column of every chunk, from the chunk pipeline.</summary>
        [BurstCompile(CompileSynchronously = true, FloatMode = FloatMode.Strict)]
        private struct LimitsJob : IJob{
            [ReadOnly] public GeneratorConfig Config;
            [ReadOnly] public NativeArray<int3> Origins;
            public NativeArray<int> CavernTop;

            public void Execute(){
                GeneratorConfig config = Config;
                for (int i = 0; i < Origins.Length; i++){
                    int3 origin = Origins[i];
                    NativeArray<ushort> vox = new(VoxelsPerChunk, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
                    NativeArray<ChunkColumn> columns = new(Columns, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
                    ChunkJob.GenerateVoxels(ref config, origin, vox, columns, false);
                    CaveColumnData caves = new(0, Allocator.Temp);
                    ChunkJob.PrepareCaveColumns(ref caves, columns, origin, config.WorldGen, default);
                    for (int x = 0; x < ChunkWidth; x++)
                    for (int z = 0; z < ChunkDepth; z++)
                        CavernTop[i * Columns + ChunkJob.GetColumnIdx(x, z, ChunkDepth)] = caves.CavernTop[caves.Index(x, z)];
                    caves.Dispose();
                    columns.Dispose();
                    vox.Dispose();
                }
            }
        }

        /// <summary>Carves the halls of every chunk into a solid block, on the lattice or per voxel.</summary>
        [BurstCompile(CompileSynchronously = true, FloatMode = FloatMode.Strict)]
        private struct CarveJob : IJob{
            [ReadOnly] public GeneratorConfig Config;
            [ReadOnly] public NativeArray<int3> Origins;
            [ReadOnly] public NativeArray<int> CavernTop;
            public bool Lattice;
            public NativeArray<ushort> Voxels;

            public void Execute(){
                ref WorldGenConfig cfg = ref Config.WorldGen;
                for (int i = 0; i < Origins.Length; i++){
                    int3 origin = Origins[i];
                    NativeArray<ushort> vox = Voxels.GetSubArray(i * VoxelsPerChunk, VoxelsPerChunk);
                    for (int v = 0; v < VoxelsPerChunk; v++) vox[v] = 1;
                    if (Lattice){
                        CaveColumnData caves = new(0, Allocator.Temp);
                        for (int x = 0; x < ChunkWidth; x++)
                        for (int z = 0; z < ChunkDepth; z++)
                            caves.CavernTop[caves.Index(x, z)] = CavernTop[i * Columns + ChunkJob.GetColumnIdx(x, z, ChunkDepth)];
                        ChunkJob.CarveCaverns(vox, caves, origin, cfg);
                        caves.Dispose();
                    }
                    else
                        CarvePerVoxel(vox, i, origin, cfg);
                }
            }

            private void CarvePerVoxel(NativeArray<ushort> vox, int chunk, int3 origin, in WorldGenConfig cfg){
                ref readonly CaveConfig c = ref cfg.Caves;
                int yLow = math.max(c.CavernBandMin, CaveColumns.Floor + 1);
                for (int x = 0; x < ChunkWidth; x++)
                for (int z = 0; z < ChunkDepth; z++){
                    int top = math.min(CavernTop[chunk * Columns + ChunkJob.GetColumnIdx(x, z, ChunkDepth)], c.CavernBandMax);
                    if (top < yLow) continue;
                    int wx = origin.x + x, wz = origin.z + z;
                    float mask = CaveMasks.Cavern(wx, wz, cfg);
                    if (!Caverns.MaskOpen(mask, c)) continue;
                    float raise = Caverns.MaskRaise(mask, c);
                    for (int y = yLow; y <= top; y++){
                        float2 noises = new(Caverns.HallNoise(wx, y, wz, cfg), Caverns.PillarNoise(wx, y, wz, cfg));
                        if (Caverns.Carves(y, raise, noises, c)) vox[ChunkSize.Flatten(x, y, z)] = 0;
                    }
                }
            }
        }

        [SetUp]
        public void SetUp(){
            _config = BenchmarkFixtures.CreatePlaceholderGeneratorConfig(GenArchitecture.TerrainFirst, BlendMethod.DistanceBlend,
                Allocator.Persistent, _created);
            BenchmarkFixtures.UseShippedCaves(ref _config, Allocator.Persistent);
            BenchmarkFixtures.MakeCaveWorstCase(ref _config.WorldGen.Caves);
            _origins = new NativeArray<int3>(ChunkCount, Allocator.Persistent);
            Random rng = new(42);
            for (int i = 0; i < ChunkCount; i++){
                int2 chunk = rng.NextInt2(-1000, 1000);
                _origins[i] = new int3(chunk.x * ChunkWidth, 0, chunk.y * ChunkDepth);
            }

            _cavernTop = new NativeArray<int>(ChunkCount * Columns, Allocator.Persistent);
            _voxels = new NativeArray<ushort>(ChunkCount * VoxelsPerChunk, Allocator.Persistent);
            new LimitsJob{ Config = _config, Origins = _origins, CavernTop = _cavernTop }.Run();
        }

        [TearDown]
        public void TearDown(){
            _voxels.Dispose();
            _cavernTop.Dispose();
            _origins.Dispose();
            BenchmarkFixtures.DisposeGeneratorConfig(ref _config);
            foreach (UnityEngine.Object o in _created) UnityEngine.Object.DestroyImmediate(o);
            _created.Clear();
        }

        [Test]
        [Performance]
        public void CarveCaverns_Run_MsPerChunk([Values(true, false)] bool lattice){
            CarveJob job = new(){
                Config = _config, Origins = _origins, CavernTop = _cavernTop, Lattice = lattice, Voxels = _voxels
            };
            if (!lattice) LogDifference(job);

            SampleGroup group = new($"CarveCaverns {(lattice ? "lattice" : "per voxel")} ms/chunk", SampleUnit.Millisecond);
            for (int i = 0; i < WarmupCount + MeasurementCount; i++){
                Stopwatch watch = Stopwatch.StartNew();
                job.Run();
                watch.Stop();
                if (i >= WarmupCount) Measure.Custom(group, watch.Elapsed.TotalMilliseconds / ChunkCount);
            }
        }

        private void LogDifference(CarveJob perVoxel){
            CarveJob lattice = perVoxel;
            lattice.Lattice = true;
            lattice.Run();
            ushort[] onLattice = _voxels.ToArray();
            perVoxel.Run();
            long carvedLattice = 0, carvedPerVoxel = 0, different = 0;
            for (int i = 0; i < onLattice.Length; i++){
                if (onLattice[i] == 0) carvedLattice++;
                if (_voxels[i] == 0) carvedPerVoxel++;
                if (onLattice[i] != _voxels[i]) different++;
            }

            Assert.Greater(carvedLattice, 0, "the chunks hold halls");
            UnityEngine.Debug.Log($"halls: {carvedLattice / ChunkCount} voxels per chunk on the lattice, {carvedPerVoxel / ChunkCount} " +
                                  $"per voxel; {100.0 * different / carvedLattice:F1} % of the lattice's carved count differ");
        }
    }
}
