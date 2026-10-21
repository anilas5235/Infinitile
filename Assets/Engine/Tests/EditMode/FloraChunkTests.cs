using System.Collections.Generic;
using System.Linq;
using Engine.Scripts.Data;
using Engine.Scripts.Jobs.Chunk;
using Engine.Scripts.VoxelConfig.Data.Generation;
using Engine.Scripts.WorldGen.Settings;
using NUnit.Framework;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using static Engine.Scripts.Utils.VoxelConstants;

namespace Engine.Tests.EditMode{
    /// <summary>
    ///     The chunk job's flora against the pure region API: heights, top block and flora of a 4×4-chunk area equal
    ///     <see cref="Engine.Scripts.WorldGen.Core.WorldGenApi.SampleRegion" /> in all 8 configurations
    ///     (2 architectures × 4 methods; Biome-first uses the method in both stages), trees that cross chunk borders
    ///     are complete on both sides, and the same input gives the same output.
    /// </summary>
    public class FloraChunkTests{
        private FloraFixture _fixture;
        private GeneratorConfig _config;
        private Biome[] _biomes;

        [SetUp]
        public void SetUp(){
            _fixture = new FloraFixture();
        }

        [TearDown]
        public void TearDown(){
            if (_config.Voxels.IsCreated) _config.Voxels.Dispose();
            _config.Dispose();
            _config = default;
            _fixture.Dispose();
        }

        private void Use(GenArchitecture architecture, BlendMethod method){
            _biomes = _fixture.DefaultBiomes();
            WorldGenSettings s = _fixture.Settings(architecture, method, _biomes);
            _config = _fixture.GeneratorConfig(s);
        }

        /// <summary>Runs the chunk job on every chunk of the area, managed copies of the voxels by chunk.</summary>
        private Dictionary<int2, ChunkVoxelData> RunChunks(int2 originChunk, int2 chunks, out NativeParallelHashMap<int2, ChunkVoxelData> results){
            NativeList<int2> jobs = new(chunks.x * chunks.y, Allocator.TempJob);
            for (int cx = 0; cx < chunks.x; cx++)
            for (int cz = 0; cz < chunks.y; cz++)
                jobs.Add(originChunk + new int2(cx, cz));
            results = new NativeParallelHashMap<int2, ChunkVoxelData>(jobs.Length, Allocator.TempJob);
            try{
                new ChunkJob{
                    Jobs = jobs, Results = results.AsParallelWriter(), RandomSeed = _config.GlobalSeed, Config = _config
                }.Run(jobs.Length);
            }
            finally{
                jobs.Dispose();
            }

            Dictionary<int2, ChunkVoxelData> byChunk = new();
            foreach (KeyValue<int2, ChunkVoxelData> kv in results) byChunk.Add(kv.Key, kv.Value);
            return byChunk;
        }

        private static void Release(NativeParallelHashMap<int2, ChunkVoxelData> results){
            foreach (KeyValue<int2, ChunkVoxelData> kv in results) kv.Value.Dispose();
            results.Dispose();
        }

        private static ushort VoxelAt(Dictionary<int2, ChunkVoxelData> chunks, int3 world){
            int2 chunk = new((int)math.floor(world.x / (float)ChunkWidth), (int)math.floor(world.z / (float)ChunkDepth));
            int3 local = new(world.x - chunk.x * ChunkWidth, world.y, world.z - chunk.y * ChunkDepth);
            return chunks[chunk].GetVoxel(local);
        }

        /// <summary>
        ///     Compares the voxels around every surface (h − 5 … h + 14) and at every position the reference stamped a
        ///     plant on. Returns the mismatch count; the first few are listed in <paramref name="report" />.
        /// </summary>
        private static int CompareVoxels(ReferenceWorld reference, Dictionary<int2, ChunkVoxelData> chunks, List<string> report){
            int mismatches = 0;
            void Check(int3 p){
                ushort expected = reference[p], actual = VoxelAt(chunks, p);
                if (expected == actual) return;
                if (mismatches++ < 12) report.Add($"{p}: expected {expected}, got {actual}");
            }

            for (int x = reference.Origin.x; x < reference.Origin.x + reference.Size.x; x++)
            for (int z = reference.Origin.y; z < reference.Origin.y + reference.Size.y; z++){
                int h = reference.SurfaceAt(x, z);
                for (int y = math.max(0, h - 5); y <= math.min(MaxTerrainHeight, h + 14); y++) Check(new int3(x, y, z));
            }

            foreach (int3 p in reference.Stamped) Check(p);
            return mismatches;
        }

        [Test]
        public void ChunkJob_FloraAndSurfaceMatchTheRegionApi(
            [Values(GenArchitecture.TerrainFirst, GenArchitecture.BiomeFirst)] GenArchitecture architecture,
            [Values(BlendMethod.None, BlendMethod.DistanceBlend, BlendMethod.DistortedBoundary, BlendMethod.ParameterSpaceWeighting)]
            BlendMethod method){
            // Per-column decisions of PrepareChunkMaps and the stamped voxels of the job equal the pure region API,
            // chunk for chunk, over 4×4 chunks.
            Use(architecture, method);
            int2 originChunk = new(1, 2), chunks = new(4, 4); // ~80 % land with shores in Terrain-first
            ReferenceWorld reference = new(_fixture, _biomes, _config, originChunk, chunks);
            RegionSample region = reference.Region;

            NativeArray<ChunkColumn> columns = new(ChunkWidth * ChunkDepth, Allocator.Temp);
            int planted = 0, anchors = 0;
            HashSet<ushort> speciesSeen = new();
            try{
                for (int cx = 0; cx < chunks.x; cx++)
                for (int cz = 0; cz < chunks.y; cz++){
                    int3 origin = new((originChunk.x + cx) * ChunkWidth, 0, (originChunk.y + cz) * ChunkDepth);
                    ChunkJob.PrepareChunkMaps(_config.GlobalSeed, ref _config, ref origin, columns);
                    for (int x = 0; x < ChunkWidth; x++)
                    for (int z = 0; z < ChunkDepth; z++){
                        int wx = origin.x + x, wz = origin.z + z;
                        int i = region.Index(wx, wz);
                        ChunkColumn col = columns[ChunkJob.GetColumnIdx(x, z, ChunkDepth)];
                        string at = $"({wx},{wz})";
                        Assert.AreEqual(math.clamp(region.ColumnHeight[i], 0, MaxTerrainHeight), col.Height, at + " Height");
                        Assert.AreEqual(region.SurfaceSource[i], col.SurfaceSource, at + " SurfaceSource");
                        Assert.AreEqual(region.FloraSpecies[i], col.FloraSpecies, at + " FloraSpecies");
                        Assert.AreEqual(region.FloraSource[i], col.FloraSource, at + " FloraSource");
                        if (col.FloraSpecies != 0){
                            planted++;
                            speciesSeen.Add(col.FloraSpecies);
                        }
                    }
                }
            }
            finally{
                columns.Dispose();
            }

            anchors = reference.Anchors.Count;
            Assert.Greater(planted, 500, "the area carries flora");
            Assert.Greater(anchors, 5, "the area carries multi-block flora");

            Dictionary<int2, ChunkVoxelData> chunkData = RunChunks(originChunk, chunks, out NativeParallelHashMap<int2, ChunkVoxelData> results);
            try{
                List<string> report = new();
                int mismatches = CompareVoxels(reference, chunkData, report);
                Assert.AreEqual(0, mismatches, "chunk voxels differ from the whole-world stamp:\n" + string.Join("\n", report));
            }
            finally{
                Release(results);
            }
        }

        [Test]
        public void ChunkJob_TreesCrossingChunkBordersAreComplete(
            [Values(GenArchitecture.TerrainFirst, GenArchitecture.BiomeFirst)] GenArchitecture architecture,
            [Values(BlendMethod.None, BlendMethod.DistanceBlend)] BlendMethod method){
            // Every voxel of a structure whose footprint spans two or more chunks is present in each chunk it
            // falls into (unless a voxel of a higher class from the whole-world stamp is there), identical on both sides.
            Use(architecture, method);
            int2 originChunk = new(5, -1), chunks = new(2, 2);
            ReferenceWorld reference = new(_fixture, _biomes, _config, originChunk, chunks);

            Dictionary<int2, ChunkVoxelData> chunkData = RunChunks(originChunk, chunks, out NativeParallelHashMap<int2, ChunkVoxelData> results);
            try{
                int crossing = 0, enteringFromOutside = 0;
                List<string> report = new();
                int missing = 0;
                foreach ((int2 column, List<int3> footprint) in reference.Anchors){
                    HashSet<int2> touched = new(footprint.Where(reference.InArea).Select(p => new int2(
                        (int)math.floor(p.x / (float)ChunkWidth), (int)math.floor(p.z / (float)ChunkDepth))));
                    int2 own = new((int)math.floor(column.x / (float)ChunkWidth), (int)math.floor(column.y / (float)ChunkDepth));
                    if (touched.Count > 1) crossing++;
                    if (!touched.Contains(own)) enteringFromOutside++;
                    if (touched.Count == 1 && touched.Contains(own)) continue;
                    foreach (int3 p in footprint.Where(reference.InArea)){
                        ushort expected = reference[p], actual = VoxelAt(chunkData, p);
                        if (expected != actual && missing++ < 12) report.Add($"anchor {column} voxel {p}: expected {expected}, got {actual}");
                    }
                }

                Assert.Greater(crossing, 0, "some structure spans a chunk border");
                Assert.AreEqual(0, missing, "tree parts missing or different at chunk borders:\n" + string.Join("\n", report));
                // Not asserted > 0: whether an outside anchor reaches in depends on the seed; the voxel test covers it.
                TestContext.WriteLine($"crossing {crossing}, entering from outside {enteringFromOutside}");
            }
            finally{
                Release(results);
            }
        }

        [Test]
        public void SampleRegion_SameRegionTwice_IsIdentical(
            [Values(GenArchitecture.TerrainFirst, GenArchitecture.BiomeFirst)] GenArchitecture architecture){
            // The flora decision is a pure function; two runs give the same hashes.
            Use(architecture, BlendMethod.DistanceBlend);
            RegionSample a = RegionSample.Sample(_config.WorldGen, new int2(-70, 40), new int2(150, 90));
            RegionSample b = RegionSample.Sample(_config.WorldGen, new int2(-70, 40), new int2(150, 90));
            Assert.AreEqual(Hash(a), Hash(b));
            CollectionAssert.AreEqual(a.FloraSpecies, b.FloraSpecies);
            CollectionAssert.AreEqual(a.FloraSource, b.FloraSource);
            Assert.Greater(a.FloraSpecies.Count(s => s != 0), 0);
        }

        [Test]
        public void ChunkJob_SameChunksTwice_IsIdentical(
            [Values(GenArchitecture.TerrainFirst, GenArchitecture.BiomeFirst)] GenArchitecture architecture){
            // Determinism of the stamped voxels.
            Use(architecture, BlendMethod.DistanceBlend);
            int2 originChunk = new(1, 1), chunks = new(2, 1);
            Dictionary<int2, ChunkVoxelData> first = RunChunks(originChunk, chunks, out NativeParallelHashMap<int2, ChunkVoxelData> r1);
            Dictionary<int2, ChunkVoxelData> second = RunChunks(originChunk, chunks, out NativeParallelHashMap<int2, ChunkVoxelData> r2);
            try{
                foreach (int2 chunk in first.Keys){
                    ulong ha = 1469598103934665603UL, hb = ha;
                    for (int x = 0; x < ChunkWidth; x++)
                    for (int z = 0; z < ChunkDepth; z++)
                    for (int y = 0; y < ChunkHeight; y++){
                        ha = (ha ^ first[chunk].GetVoxel(new int3(x, y, z))) * 1099511628211UL;
                        hb = (hb ^ second[chunk].GetVoxel(new int3(x, y, z))) * 1099511628211UL;
                    }

                    Assert.AreEqual(ha, hb, $"chunk {chunk}");
                }
            }
            finally{
                Release(r1);
                Release(r2);
            }
        }

        private static ulong Hash(RegionSample r){
            ulong h = 1469598103934665603UL;
            void Mix(ulong v){
                h = (h ^ v) * 1099511628211UL;
            }

            for (int i = 0; i < r.FloraSpecies.Length; i++){
                Mix(r.FloraSpecies[i]);
                Mix((ulong)(uint)r.FloraSource[i]);
                Mix((ulong)(uint)r.ColumnHeight[i]);
                Mix((ulong)(uint)r.SurfaceSource[i]);
                Mix(math.asuint(r.Height[i]));
            }

            return h;
        }
    }
}
