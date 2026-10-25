using System.Collections.Generic;
using System.Linq;
using Engine.Scripts.Data;
using Engine.Scripts.Jobs.Chunk;
using Engine.Scripts.VoxelConfig.Data.Generation;
using Engine.Scripts.WorldGen.Core;
using Engine.Scripts.WorldGen.Flora;
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
    ///     are complete on both sides, every test and shipped tree variant matches the whole-world stamp across chunk corners, snow,
    ///     strata and ice agree as well, and the same input gives the same output in any chunk order.
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

        private void Use(GenArchitecture architecture, BlendMethod method, bool shippedSpecies = false, bool dense = false){
            _biomes = shippedSpecies ? _fixture.ShippedSpeciesBiomes() : _fixture.DefaultBiomes();
            if (dense){
                // Every rule follows a density noise at full influence; the meadow's probabilities exceed 1 in groves.
                _biomes[0].FloraRules[0].Probability = .7f;
                foreach (Biome b in _biomes)
                    for (int r = 0; r < b.FloraRules.Count; r++){
                        b.FloraRules[r].DensityNoise = (FloraDensity)(r % 4);
                        b.FloraRules[r].DensityInfluence = 1f;
                    }
            }

            WorldGenSettings s = _fixture.Settings(architecture, method, _biomes);
            _config = _fixture.GeneratorConfig(s);
        }

        /// <summary>Runs the chunk job on every chunk of the area, managed copies of the voxels by chunk.</summary>
        private Dictionary<int2, ChunkVoxelData> RunChunks(int2 originChunk, int2 chunks, out NativeParallelHashMap<int2, ChunkVoxelData> results){
            List<int2> order = new();
            for (int cx = 0; cx < chunks.x; cx++)
            for (int cz = 0; cz < chunks.y; cz++)
                order.Add(originChunk + new int2(cx, cz));
            return RunChunks(order, out results);
        }

        /// <summary>Runs the chunk job on <paramref name="order" />, in that order.</summary>
        private Dictionary<int2, ChunkVoxelData> RunChunks(IReadOnlyList<int2> order, out NativeParallelHashMap<int2, ChunkVoxelData> results){
            NativeList<int2> jobs = new(order.Count, Allocator.TempJob);
            foreach (int2 chunk in order) jobs.Add(chunk);
            results = new NativeParallelHashMap<int2, ChunkVoxelData>(jobs.Length, Allocator.TempJob);
            try{
                new ChunkJob{
                    Jobs = jobs, Results = results.AsParallelWriter(), Config = _config
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

        /// <summary>Anchors within two blocks of a chunk corner.</summary>
        private static int CountNearCorner(ReferenceWorld reference){
            static bool NearBorder(int c, int size){
                int m = (c % size + size) % size;
                return m <= 2 || m >= size - 2;
            }

            return reference.Anchors.Count(a => NearBorder(a.column.x, ChunkWidth) && NearBorder(a.column.y, ChunkDepth));
        }

        /// <summary>Anchors whose footprint within the area falls into the given number of chunks or more.</summary>
        private static int CountInChunks(ReferenceWorld reference, int atLeast){
            return reference.Anchors.Count(a => a.footprint.Where(reference.InArea)
                .Select(p => new int2((int)math.floor(p.x / (float)ChunkWidth), (int)math.floor(p.z / (float)ChunkDepth))).Distinct().Count() >= atLeast);
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
            BlendMethod method, [Values(false, true)] bool shippedSpecies){
            // Per-column decisions of PrepareChunkMaps and the stamped voxels of the job equal the pure region API,
            // chunk for chunk, over 4×4 chunks; with the fixture flora and with the shipped species set.
            Use(architecture, method, shippedSpecies);
            AssertChunksMatchTheRegionApi();
        }

        [Test]
        public void ChunkJob_DenseFloraMatchesTheRegionApi(
            [Values(GenArchitecture.TerrainFirst, GenArchitecture.BiomeFirst)] GenArchitecture architecture,
            [Values(false, true)] bool shippedSpecies){
            // The same with every rule on a density noise: per-column intervals and the widened pre-filter.
            Use(architecture, BlendMethod.DistanceBlend, shippedSpecies, true);
            AssertChunksMatchTheRegionApi();
        }

        private void AssertChunksMatchTheRegionApi(){
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
                    ChunkJob.PrepareChunkMaps(ref _config, ref origin, columns);
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
        public void ChunkJob_SnowStrataAndIceMatchTheRegionApi(
            [Values(GenArchitecture.TerrainFirst, GenArchitecture.BiomeFirst)] GenArchitecture architecture){
            // Snow tops, strata bands and ice of the chunk job equal the whole-world stamp, and snowy columns carry
            // no single-block plants. A high freeze temperature brings snow and ice into the test area.
            _biomes = _fixture.DefaultBiomes();
            foreach (Biome b in _biomes){
                b.SnowTopBlock = _fixture.SnowSo;
                b.StrataBlock = _fixture.GraniteSo;
            }

            WorldGenSettings s = _fixture.Settings(architecture, BlendMethod.DistanceBlend, _biomes);
            s.FreezeTemperature = .5f;
            s.StrataPeriod = 3;
            s.StrataThickness = 1;
            _config = _fixture.GeneratorConfig(s);
            int2 originChunk = new(1, 2), chunks = new(4, 4);
            ReferenceWorld reference = new(_fixture, _biomes, _config, originChunk, chunks);

            int waterLevel = _config.WorldGen.WaterLevel;
            int snowy = 0, bare = 0, ice = 0, strata = 0;
            float coldest = float.MaxValue;
            for (int x = reference.Origin.x; x < reference.Origin.x + reference.Size.x; x++)
            for (int z = reference.Origin.y; z < reference.Origin.y + reference.Size.y; z++){
                int h = reference.SurfaceAt(x, z);
                if (reference[new int3(x, h, z)] == FloraFixture.Snow){
                    snowy++;
                    ushort above = reference[new int3(x, h + 1, z)];
                    Assert.IsFalse(above == FloraFixture.Tuft || above == FloraFixture.Flower, $"({x},{z}) plant on snow");
                }
                else if (h + 1 >= waterLevel) bare++;

                if (h + 1 >= waterLevel){
                    int i = reference.Region.Index(x, z);
                    coldest = math.min(coldest, SurfaceCover.EffectiveTemperature(x, z, reference.Region.Fields[i].T, h, _config.WorldGen));
                }

                if (reference[new int3(x, waterLevel - 1, z)] == TestFixtures.Ice) ice++;
                for (int y = math.max(0, h - 5); y <= h; y++)
                    if (reference[new int3(x, y, z)] == FloraFixture.Granite) strata++;
            }

            string counts = $"{architecture}: snowy {snowy}, bare dry {bare}, ice {ice}, strata voxels {strata}, coldest dry {coldest:F3}";
            TestContext.WriteLine(counts);
            Assert.Greater(snowy, 0, "the area has snow; " + counts);
            Assert.Greater(bare, 0, "the area has dry columns without snow");
            Assert.Greater(strata, 0, "the area has strata near the surface");
            if (architecture == GenArchitecture.TerrainFirst) Assert.Greater(ice, 0, "the area has ice");

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
            [Values(BlendMethod.None, BlendMethod.DistanceBlend)] BlendMethod method, [Values(false, true)] bool shippedSpecies){
            // Every voxel of a structure whose footprint spans two or more chunks is present in each chunk it
            // falls into (unless a voxel of a higher class from the whole-world stamp is there), identical on both sides;
            // with the fixture flora and with the shipped species set.
            Use(architecture, method, shippedSpecies);
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
        public void ChunkJob_EveryTreeVariantMatchesTheWholeWorldStamp_AcrossChunkCorners(
            [Values(0, 1, 2, 3, 4)] int variant,
            [Values(GenArchitecture.TerrainFirst, GenArchitecture.BiomeFirst)] GenArchitecture architecture){
            // Each shape variant alone, dense enough that anchors sit next to chunk corners, so their trees fall into
            // up to four chunks: the chunk job equals the whole-world stamp voxel for voxel.
            TreeSpecies single = _fixture.MakeTree("Single", _fixture.ShapeVariants()[variant]);
            _biomes = new[]{
                _fixture.MakeBiome("Wood", .5f, 150f, 10f, _fixture.GrassSo, FloraFixture.Rule(single, .25f), FloraFixture.Rule(_fixture.TuftSo, .3f))
            };
            _config = _fixture.GeneratorConfig(_fixture.Settings(architecture, BlendMethod.DistanceBlend, _biomes));
            int2 originChunk = new(1, 2), chunks = new(3, 3);
            ReferenceWorld reference = new(_fixture, _biomes, _config, originChunk, chunks);

            int nearCorner = CountNearCorner(reference);
            int fourChunks = CountInChunks(reference, 4);
            TestContext.WriteLine($"variant {variant} {architecture}: anchors {reference.Anchors.Count}, near a corner {nearCorner}, " +
                                  $"in four chunks {fourChunks}, narrowed {reference.Narrowed.Count}, reach {_config.WorldGen.FloraReach}");
            Assert.Greater(reference.Anchors.Count, 20, "the area carries trees");
            Assert.Greater(nearCorner, 0, "an anchor lies next to a chunk corner");
            Assert.Greater(fourChunks, 0, "a tree falls into four chunks");
            Assert.IsTrue(reference.TreeVariants.Values.All(v => v == 0), "the species has one variant");
            if (variant == 1 && architecture == GenArchitecture.TerrainFirst){
                // The 2×2 trunk always needs a flat footprint: both outcomes occur on the spline terrain (the
                // Biome-first test hills are flat within 1 block everywhere).
                Assert.Greater(reference.Narrowed.Count, 0, "some wide trunk falls back to width 1");
                Assert.Less(reference.Narrowed.Count, reference.Anchors.Count, "some wide trunk stands");
            }

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

        private static IEnumerable<string> ShippedVariants(){
            return FloraFixture.ShippedVariantNames();
        }

        [Test]
        public void ChunkJob_EveryShippedVariantMatchesTheWholeWorldStamp_AcrossChunkCorners(
            [ValueSource(nameof(ShippedVariants))] string name,
            [Values(GenArchitecture.TerrainFirst, GenArchitecture.BiomeFirst)] GenArchitecture architecture){
            // As above for each variant of the shipped species. Every tree with reach crosses a chunk border; small
            // crowns rarely span a chunk corner, so four chunks are required from reach 4 on (a lying trunk is a line
            // and never spans a corner).
            TreeVariant variant = _fixture.ShippedVariant(name);
            TreeSpecies single = _fixture.MakeTree("Single", variant);
            _biomes = new[]{
                _fixture.MakeBiome("Wood", .5f, 150f, 10f, _fixture.GrassSo, FloraFixture.Rule(single, .25f), FloraFixture.Rule(_fixture.TuftSo, .3f))
            };
            _config = _fixture.GeneratorConfig(_fixture.Settings(architecture, BlendMethod.DistanceBlend, _biomes));
            int2 originChunk = new(1, 2), chunks = new(3, 3);
            ReferenceWorld reference = new(_fixture, _biomes, _config, originChunk, chunks);

            int nearCorner = CountNearCorner(reference);
            int fourChunks = CountInChunks(reference, 4);
            int across = CountInChunks(reference, 2);
            int reach = _config.WorldGen.FloraReach;
            TestContext.WriteLine($"{name} {architecture}: anchors {reference.Anchors.Count}, near a corner {nearCorner}, " +
                                  $"across a border {across}, in four chunks {fourChunks}, reach {reach}");
            Assert.Greater(reference.Anchors.Count, 20, "the area carries trees");
            Assert.Greater(nearCorner, 0, "an anchor lies next to a chunk corner");
            if (reach > 0) Assert.Greater(across, 0, "a tree crosses a chunk border");
            if (reach >= 4 && !variant.Lying) Assert.Greater(fourChunks, 0, "a tree falls into four chunks");
            if (variant.Lying){
                // A log needs the anchor's height under its whole length: both outcomes occur on the test hills.
                TestContext.WriteLine($"shortened to 2: {reference.Narrowed.Count}");
                Assert.Greater(reference.Narrowed.Count, 0, "some log is shortened to 2");
                Assert.Less(reference.Narrowed.Count, reference.Anchors.Count, "some log lies at its rolled length");
            }

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
        public void ChunkJob_TreesDoNotDependOnTheChunkOrder(){
            // Mixed variant radii and trees across chunk borders: generating the chunks in reverse order gives the
            // same voxels in every chunk.
            Use(GenArchitecture.BiomeFirst, BlendMethod.DistanceBlend);
            List<int2> order = new();
            for (int cx = 0; cx < 3; cx++)
            for (int cz = 0; cz < 2; cz++)
                order.Add(new int2(2 + cx, -1 + cz));
            List<int2> reversed = Enumerable.Reverse(order).ToList();
            Dictionary<int2, ChunkVoxelData> forward = RunChunks(order, out NativeParallelHashMap<int2, ChunkVoxelData> r1);
            Dictionary<int2, ChunkVoxelData> backward = RunChunks(reversed, out NativeParallelHashMap<int2, ChunkVoxelData> r2);
            try{
                int logs = 0;
                foreach (int2 chunk in order)
                    for (int x = 0; x < ChunkWidth; x++)
                    for (int z = 0; z < ChunkDepth; z++)
                    for (int y = 0; y < ChunkHeight; y++){
                        int3 p = new(x, y, z);
                        ushort a = forward[chunk].GetVoxel(p);
                        if (a != backward[chunk].GetVoxel(p)) Assert.Fail($"chunk {chunk} voxel {p}: {a} vs {backward[chunk].GetVoxel(p)}");
                        if (a == FloraFixture.Log) logs++;
                    }

                Assert.Greater(logs, 100, "the chunks carry trees");
            }
            finally{
                Release(r1);
                Release(r2);
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
