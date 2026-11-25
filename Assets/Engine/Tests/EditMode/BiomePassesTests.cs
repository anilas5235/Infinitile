using System.Collections.Generic;
using System.Linq;
using Engine.Editor.WorldGen;
using Engine.Scripts.Jobs.Chunk;
using Engine.Scripts.WorldGen.Biomes;
using Engine.Scripts.WorldGen.Blending;
using Engine.Scripts.WorldGen.Density;
using Engine.Scripts.WorldGen.Graph;
using Engine.Scripts.WorldGen.Settings;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;

namespace Engine.Tests.EditMode{
    /// <summary>
    ///     The biome passes of WorldGen v2 (§6, §12, phase V3) on the benchmark fixture's graph and the shipped starting
    ///     layout: the padded hard biomes against brute force over the climate P2 read, uniform cells included
    ///     (W2-BIO-03), the wet flags against the terrain at y 123 and the dry-column rule (W2-BIO-04), equal biomes in
    ///     a chunk and in its neighbour's padding (W2-BIO-06), the stored biome ids, and the blend (the port of
    ///     BlendingTests: DistanceBlend against the kernel sum over the padded map, and None).
    /// </summary>
    public class BiomePassesTests{
        private const int Size = DensityLayout.ChunkSize;
        private const int Pad = DensityLayout.BiomePadding;
        private const int P = BiomeBox.Parameters;

        private CompiledNoiseGraph _fixture;
        private GeneratorConfig _config;
        private BiomeLayout _layout;

        [OneTimeSetUp]
        public void SetUp(){
            GraphCompileResult r = DensityFixture.Compile();
            Assert.IsTrue(r.Succeeded, string.Join("\n", r.Messages));
            _fixture = r.Graph;
            _config = TestFixtures.CreateGeneratorConfig(Allocator.Persistent);
            _config.WorldGen.Seed = DensityFixture.Seed;
            _config.WorldGen.DensityStoneVoxel = TestFixtures.Stone;
            _layout = BiomeLayoutTests.Shipped();
            UseLayout(_layout);
            UseBlend(BlendMethod.DistanceBlend, 32f);
        }

        [OneTimeTearDown]
        public void TearDown(){
            _fixture?.Dispose();
            TestFixtures.DisposeGeneratorConfig(ref _config);
        }

        private void UseLayout(BiomeLayout layout){
            _config.WorldGen.Layout.Dispose();
            _config.WorldGen.Layout = BiomeLayoutData.Create(layout, null, Allocator.Persistent);
        }

        private void UseBlend(BlendMethod method, float width){
            if (_config.WorldGen.MaterialTaps.IsCreated) _config.WorldGen.MaterialTaps.Dispose();
            _config.WorldGen.MaterialMethod = method;
            if (method == BlendMethod.DistanceBlend)
                _config.WorldGen.MaterialKernel = KernelSet.Build(width, Allocator.Persistent, out _config.WorldGen.MaterialTaps);
            else{
                _config.WorldGen.MaterialKernel = default;
                _config.WorldGen.MaterialTaps = new NativeArray<KernelTap>(0, Allocator.Persistent);
            }
        }

        private static IEnumerable<int2> SomeChunks(){
            return DensityFixture.Classes.SelectMany(c => DensityFixture.Chunks(c).Take(3));
        }

        /// <summary>The hard biome of a padded column by brute force over the climate the probe's P2 read, with the dry-column rule.</summary>
        private static byte Reference(in DensityProbeResult probe, in BiomeLayoutData layout, int x, int z){
            int px = x + Pad, pz = z + Pad;
            int cx = px / 4, cz = pz / 4;
            float tx = px % 4 * 0.25f, tz = pz % 4 * 0.25f;
            float[] column = new float[P];
            for (int p = 0; p < P; p++){
                float c00 = probe.ClimateAt(p, cx, cz), c10 = probe.ClimateAt(p, cx + 1, cz);
                float c01 = probe.ClimateAt(p, cx, cz + 1), c11 = probe.ClimateAt(p, cx + 1, cz + 1);
                column[p] = GraphInterpolation.Lerp(GraphInterpolation.Lerp(c00, c10, tx), GraphInterpolation.Lerp(c01, c11, tx), tz);
            }

            int box = layout.Main.BruteForce(column);
            byte id = box < 0 ? (byte)255 : layout.Main.BoxBiome[box];
            if (!probe.PaddedWetAt(x, z) && layout.Water.Contains(id) && layout.Land.BoxCount > 0) id = layout.Land.BoxBiome[layout.Land.BruteForce(column)];
            return id;
        }

        [Test]
        public void HardBiomes_EqualBruteForceOverTheInterpolatedClimate(){
            long uniform = 0;
            foreach (int2 chunk in SomeChunks()) uniform += CheckHardBiomes(chunk).Uniform;
            Assert.Greater(uniform, 0, "uniform cells were tested");
        }

        /// <summary>
        ///     The shipped ocean bands sit below the coast, so their dry columns are rare; an Ocean box widened up the
        ///     continentalness scale (and of a later source, so it wins the overlap) reaches dry land on the fixture.
        /// </summary>
        [Test]
        public void HardBiomes_DryColumnRule_GivesTheLandLookupsBiome(){
            BiomeLayout wide = BiomeLayoutTests.Shipped();
            int ocean = wide.IdOf("Ocean");
            foreach (LayoutBox box in wide.Boxes)
                if (box.Biome == ocean){
                    box.Max[2] = 0.3f;
                    box.Source = 1;
                }

            UseLayout(wide);
            try{
                long substitutes = 0;
                foreach (int2 chunk in SomeChunks()) substitutes += CheckHardBiomes(chunk).Substitutes;
                Assert.Greater(substitutes, 0, "the dry-column rule was tested");
            }
            finally{
                UseLayout(_layout);
            }
        }

        private (long Uniform, long Substitutes) CheckHardBiomes(int2 chunk){
            using DensityProbeResult probe = DensityProbe.Run(_fixture, _config.WorldGen, chunk * Size, DensityChunk.PassBiomes, true);
            for (int z = -Pad; z < Size + Pad; z++)
            for (int x = -Pad; x < Size + Pad; x++)
                Assert.AreEqual(Reference(probe, _config.WorldGen.Layout, x, z), probe.PaddedHardAt(x, z), $"chunk {chunk} column ({x}, {z})");
            return (probe.Counters.Biomes.UniformCells, probe.Counters.Biomes.DrySubstitutes);
        }

        [Test]
        public void GeneratedCode_GivesTheSameBiomesAsTheInterpreter(){
            // The shipped graph: the fixture graph has no generated code.
            GraphCompileResult r = ShippedNoiseGraph.Compile();
            Assert.IsTrue(r.Succeeded, string.Join("\n", r.Messages));
            using CompiledNoiseGraph shipped = r.Graph;
            Assert.IsTrue(GraphEvaluator.HasGeneratedCode(shipped), "Regenerate the shipped graph code (Infinitile > Generate Noise Graph Code).");
            foreach (int2 chunk in SomeChunks()){
                using DensityProbeResult a = DensityProbe.Run(shipped, _config.WorldGen, chunk * Size, DensityChunk.PassFill, true);
                using DensityProbeResult b = DensityProbe.Run(shipped, _config.WorldGen, chunk * Size);
                CollectionAssert.AreEqual(a.PaddedHard.ToArray(), b.PaddedHard.ToArray(), $"chunk {chunk}");
                CollectionAssert.AreEqual(a.Surface.ToArray(), b.Surface.ToArray(), $"chunk {chunk}");
                CollectionAssert.AreEqual(a.Voxels.ToArray(), b.Voxels.ToArray(), $"chunk {chunk}");
            }
        }

        [Test]
        public void WetFlags_AreTheTerrainAtY123_AndDryColumnsHoldNoWaterBiome(){
            long wet = 0, dry = 0;
            foreach (int2 chunk in SomeChunks()){
                using DensityProbeResult probe = DensityProbe.Run(_fixture, _config.WorldGen, chunk * Size, DensityChunk.PassBiomes, true);
                for (int x = 0; x < Size; x++)
                for (int z = 0; z < Size; z++){
                    bool isWet = probe.PaddedWetAt(x, z);
                    Assert.AreEqual(!probe.IsSolid(x, DensityLayout.WetnessY(_config.WorldGen.WaterLevel), z), isWet, $"chunk {chunk} column ({x}, {z})");
                    if (isWet) wet++;
                    else dry++;
                }

                for (int z = -Pad; z < Size + Pad; z++)
                for (int x = -Pad; x < Size + Pad; x++)
                    if (!probe.PaddedWetAt(x, z))
                        Assert.IsFalse(_config.WorldGen.Layout.Water.Contains(probe.PaddedHardAt(x, z)), $"water biome on a dry column, chunk {chunk} ({x}, {z})");
            }

            Assert.Greater(wet, 0);
            Assert.Greater(dry, 0);
        }

        [Test]
        public void PaddedMaps_EqualTheNeighbourChunks([Values(0, 1)] int axis){
            int2 step = axis == 0 ? new int2(1, 0) : new int2(0, 1);
            foreach (int2 chunk in SomeChunks().Take(8)){
                using DensityProbeResult a = DensityProbe.Run(_fixture, _config.WorldGen, chunk * Size, DensityChunk.PassBiomes);
                using DensityProbeResult b = DensityProbe.Run(_fixture, _config.WorldGen, (chunk + step) * Size, DensityChunk.PassBiomes);
                // a's columns [Size − Pad, Size + Pad) are b's [−Pad, Pad).
                for (int u = -Pad; u < Pad; u++)
                for (int v = -Pad; v < Size + Pad; v++){
                    int ax = axis == 0 ? u + Size : v, az = axis == 0 ? v : u + Size;
                    int bx = axis == 0 ? u : v, bz = axis == 0 ? v : u;
                    Assert.AreEqual(a.PaddedHardAt(ax, az), b.PaddedHardAt(bx, bz), $"chunk {chunk} → {chunk + step}, column ({ax}, {az})");
                    Assert.AreEqual(a.PaddedWetAt(ax, az), b.PaddedWetAt(bx, bz), $"wet flag, chunk {chunk} → {chunk + step}, column ({ax}, {az})");
                }
            }
        }

        [Test]
        public void StoredBiomes_AreTheHardBiomesOfTheChunkColumns(){
            foreach (int2 chunk in SomeChunks().Take(6)){
                using DensityProbeResult probe = DensityProbe.Run(_fixture, _config.WorldGen, chunk * Size);
                for (int x = 0; x < Size; x++)
                for (int z = 0; z < Size; z++){
                    byte hard = probe.PaddedHardAt(x, z);
                    Assert.AreEqual(hard == 255 ? 0 : hard, probe.Biomes[x * Size + z], $"chunk {chunk} column ({x}, {z})");
                }
            }
        }

        // --- Blend (port of BlendingTests) ----------------------------------------------------------------------------

        /// <summary>
        ///     DistanceBlend by definition: the kernel's taps of the column's phase summed per hard biome of the padded
        ///     map, the 4 largest kept (ties: the lower id), by id, normalised; the surface is the patchy pick at the
        ///     border threshold (0.5 without the graph output).
        /// </summary>
        private static void ReferenceBlend(in DensityProbeResult probe, in WorldGenConfig worldGen, int x, int z, out int4 ids, out float4 weights, out byte surface,
            float threshold01 = 0.5f){
            KernelSet kernel = worldGen.MaterialKernel;
            int phase = kernel.Phase(probe.Origin.x + x, probe.Origin.y + z);
            Dictionary<int, float> sums = new();
            List<int> order = new();
            for (int t = kernel.PhaseStart[phase]; t < kernel.PhaseStart[phase] + kernel.PhaseCount[phase]; t++){
                KernelTap tap = worldGen.MaterialTaps[t];
                int id = probe.PaddedHardAt(x + tap.Offset.x, z + tap.Offset.y);
                if (!sums.ContainsKey(id)){
                    sums[id] = 0f;
                    order.Add(id);
                }

                sums[id] += tap.K;
            }

            List<int> kept = order.OrderByDescending(id => sums[id]).ThenBy(id => id).Take(4).OrderBy(id => id).ToList();
            float total = 0f;
            foreach (int id in kept) total += sums[id];
            ids = 255;
            weights = 0f;
            for (int k = 0; k < kept.Count; k++){
                ids[k] = kept[k];
                weights[k] = sums[kept[k]] / total;
            }

            if (kept.Count == 1){
                surface = (byte)kept[0];
                return;
            }

            float sum = 0f;
            for (int k = 0; k < kept.Count; k++) sum += weights[k];
            float threshold = threshold01 * sum, cumulative = 0f;
            surface = (byte)kept[^1];
            for (int k = 0; k < kept.Count; k++){
                cumulative += weights[k];
                if (cumulative > threshold){
                    surface = (byte)kept[k];
                    break;
                }
            }
        }

        [Test]
        public void DistanceBlend_IsTheKernelSumOverThePaddedHardBiomes(){
            int blended = 0;
            foreach (int2 chunk in SomeChunks()){
                using DensityProbeResult probe = DensityProbe.Run(_fixture, _config.WorldGen, chunk * Size, DensityChunk.PassBlend, true);
                for (int x = 0; x < Size; x++)
                for (int z = 0; z < Size; z++){
                    ReferenceBlend(probe, _config.WorldGen, x, z, out int4 ids, out float4 w, out byte surface);
                    int c = x * Size + z;
                    Assert.AreEqual(ids, probe.BlendBiomes[c], $"chunk {chunk} column ({x}, {z})");
                    for (int k = 0; k < 4; k++) Assert.AreEqual(w[k], probe.BlendWeights[c][k], 1e-6f, $"weight {k}, chunk {chunk} column ({x}, {z})");
                    Assert.AreEqual(surface, probe.Surface[c], $"surface, chunk {chunk} column ({x}, {z})");
                    Assert.AreEqual(1f, math.csum(probe.BlendWeights[c]), 1e-5f);
                    if (ids.y != 255) blended++;
                }
            }

            Assert.Greater(blended, 0, "some columns lie in a blend zone");
        }

        /// <summary>
        ///     The shipped graph's border noise is a per-column output, so the blend picks each column's surface at its
        ///     own threshold: the pick equals the reference at the probe's threshold, and some columns differ from the
        ///     straight cut at 0.5 (the patchy, wavy border of DEC-87).
        /// </summary>
        [Test]
        public void DistanceBlend_ShippedBorder_IsPerColumnAndPatchy(){
            GraphCompileResult r = ShippedNoiseGraph.Compile();
            Assert.IsTrue(r.Succeeded, string.Join("\n", r.Messages));
            using CompiledNoiseGraph shipped = r.Graph;
            Assert.IsTrue(DensityTerrainGraph.TryCreate(shipped, out DensityTerrainGraph terrain, out string error), error);
            Assert.GreaterOrEqual(terrain.BorderColumn, 0, "blend_border must be a per-column output (\"perColumn\": true)");

            int mixed = 0, blended = 0, patchy = 0;
            for (int i = 0; i < 64 && mixed < 6; i++){
                int2 chunk = new(i * 7 - 224, i * 5 - 160);
                using DensityProbeResult probe = DensityProbe.Run(shipped, _config.WorldGen, chunk * Size, DensityChunk.PassBlend);
                if (probe.Counters.Biomes.BlendKernelColumns == 0) continue;
                mixed++;
                Assert.Greater(probe.Counters.Biomes.BorderColumns, 0, $"the border ran, chunk {chunk}");
                for (int x = 0; x < Size; x++)
                for (int z = 0; z < Size; z++){
                    float threshold = probe.Border[z * Size + x];
                    Assert.IsTrue(threshold >= 0.04f && threshold <= 0.96f, $"threshold {threshold}, chunk {chunk} column ({x}, {z})");
                    ReferenceBlend(probe, _config.WorldGen, x, z, out int4 ids, out _, out byte surface, threshold);
                    int c = x * Size + z;
                    Assert.AreEqual(surface, probe.Surface[c], $"surface, chunk {chunk} column ({x}, {z}), threshold {threshold}");
                    if (ids.y == 255) continue;
                    blended++;
                    ReferenceBlend(probe, _config.WorldGen, x, z, out _, out _, out byte straight);
                    if (straight != surface) patchy++;
                }
            }

            Assert.Greater(mixed, 0, "some chunks hold a biome border");
            Assert.Greater(blended, 0);
            Assert.Greater(patchy, 0, "the border noise moves the pick away from the straight cut");
            UnityEngine.Debug.Log($"[BiomePasses] shipped border: {mixed} chunks, {blended} blended columns, {patchy} off the straight cut");
        }

        [Test]
        public void DistanceBlend_SkipsOneBiomeChunksAndUniformWindowsExactly(){
            long skipped = 0, uniform = 0, kernel = 0;
            foreach (int2 chunk in SomeChunks()){
                using DensityProbeResult probe = DensityProbe.Run(_fixture, _config.WorldGen, chunk * Size, DensityChunk.PassBlend, true);
                skipped += probe.Counters.Biomes.BlendSkippedChunks;
                uniform += probe.Counters.Biomes.BlendUniformColumns;
                kernel += probe.Counters.Biomes.BlendKernelColumns;
            }

            Assert.Greater(skipped + uniform, 0, "the exact shortcuts ran (the reference test checks their results)");
            Assert.Greater(kernel, 0);
        }

        [Test]
        public void None_IsOneHotOnTheHardBiome(){
            UseBlend(BlendMethod.None, 32f);
            try{
                foreach (int2 chunk in SomeChunks().Take(8)){
                    using DensityProbeResult probe = DensityProbe.Run(_fixture, _config.WorldGen, chunk * Size, DensityChunk.PassBlend, true);
                    for (int x = 0; x < Size; x++)
                    for (int z = 0; z < Size; z++){
                        int c = x * Size + z;
                        byte hard = probe.PaddedHardAt(x, z);
                        Assert.AreEqual(new int4(hard, 255, 255, 255), probe.BlendBiomes[c]);
                        Assert.AreEqual(new float4(1f, 0f, 0f, 0f), probe.BlendWeights[c]);
                        Assert.AreEqual(hard, probe.Surface[c]);
                    }
                }
            }
            finally{
                UseBlend(BlendMethod.DistanceBlend, 32f);
            }
        }

        [Test]
        public void Scheduler_RefusesABlendWidthPastThePadding(){
            KernelSet wide = KernelSet.Build(40f, Allocator.Temp, out NativeArray<KernelTap> taps);
            taps.Dispose();
            Assert.Greater(wide.Reach, Pad - 1, "a width of 40 does not fit the padding");
            StringAssert.Contains("biome padding allows", ChunkScheduler.BlendWidthError(BlendMethod.DistanceBlend, wide.Reach, 40f));
            Assert.IsNull(ChunkScheduler.BlendWidthError(BlendMethod.None, wide.Reach, 40f), "None has no kernel");
            KernelSet shipped = KernelSet.Build(32f, Allocator.Temp, out taps);
            taps.Dispose();
            Assert.LessOrEqual(shipped.Reach, Pad - 1, "the shipped width 32 fits");
            Assert.IsNull(ChunkScheduler.BlendWidthError(BlendMethod.DistanceBlend, shipped.Reach, 32f));
        }

        // --- Test mod -------------------------------------------------------------------------------------------------

        [Test]
        public void TestMod_Generates(){
            BiomeLayout layout = BiomeLayoutTests.WithTestMod();
            GraphCompileResult r = BiomeLayoutTests.TestModGraph();
            Assert.IsTrue(r.Succeeded, string.Join("\n", r.Messages));
            using CompiledNoiseGraph graph = r.Graph;
            Assert.IsFalse(BiomeLayoutValidator.Validate(layout, graph).HasErrors);
            int heath = layout.IdOf("Heath");
            UseLayout(layout);
            try{
                // Find a Heath column from the lattice values alone, then generate its chunk.
                int2? found = null;
                using BiomeLayoutData data = BiomeLayoutData.Create(layout, null, Allocator.Persistent);
                int[] slots = Enumerable.Range(0, P).Select(p => graph.Output(BiomeLayoutValidator.ParameterOutput(p)).LatticeValue).ToArray();
                int surface = graph.Output(GraphOutputs.PreliminarySurface).LatticeValue;
                List<float2> points = new();
                for (int i = -64; i < 64; i++)
                for (int j = -64; j < 64; j++)
                    points.Add(new float2(i * 64, j * 64));
                float[] values = GraphEvaluator.SampleLattice(graph, _config.WorldGen.Seed, points.ToArray(), out _);
                for (int k = 0; k < points.Count && found == null; k++){
                    float[] column = slots.Select(s => values[s * points.Count + k]).ToArray();
                    if (values[surface * points.Count + k] < 140f) continue;
                    if (data.Main.BoxBiome[data.Main.Lookup(column)] == heath) found = (int2)points[k];
                }

                Assert.IsNotNull(found, "a Heath column within 4 km");
                int2 chunk = (int2)math.floor((float2)found.Value / Size);
                using DensityProbeResult probe = DensityProbe.Run(graph, _config.WorldGen, chunk * Size, DensityChunk.PassFill, true);
                Assert.IsTrue(probe.Biomes.Contains((byte)heath), $"Heath is stored in chunk {chunk}");
            }
            finally{
                UseLayout(_layout);
            }
        }
    }
}
