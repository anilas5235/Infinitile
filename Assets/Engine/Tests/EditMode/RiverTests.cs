using System.Collections.Generic;
using System.Linq;
using Engine.Editor.WorldGen;
using Engine.Scripts.Jobs.Chunk;
using Engine.Scripts.VoxelConfig.Data.Generation;
using Engine.Scripts.WorldGen.Biomes;
using Engine.Scripts.WorldGen.Density;
using Engine.Scripts.WorldGen.Graph;
using Engine.Scripts.WorldGen.Settings;
using Engine.Scripts.WorldGen.Surface;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;

namespace Engine.Tests.EditMode{
    /// <summary>
    ///     The rivers of WorldGen v2 (§9.2, phase V5) on the shipped graph and rule file: the valleys slice is |W| ≤ 0.04 (DEC-196)
    ///     (the mirrored lowland relief reaches its −4…0 bed there, DEC-192), the river output lies inside the slice and
    ///     in the lowland, river water only below 124 and none above, the river's centre line in the plain lowland is wet,
    ///     the beds take gravel, sand, clay or mud (DEC-195) and the gentle banks the V4 shore sand.
    /// </summary>
    public class RiverTests{
        private const int Size = DensityLayout.ChunkSize;
        private const int Height = DensityLayout.Height;

        /// <summary>Half width of the valleys slice in |W| (§9.2).</summary>
        private const float Slice = 0.04f; // DEC-196 (was 0.05)

        /// <summary>The lowland_pv value at the slice's edge: the lowland rows' bed (−4 … 0) lies at −1 … −0.85.</summary>
        private const float SliceEdgePv = -0.85f;

        /// <summary>C where the river weight is 1 (the plain lowland, DEC-192): −0.11 … 0.03.</summary>
        private const float LowlandCMin = -0.11f, LowlandCMax = 0.03f;

        private CompiledNoiseGraph _graph;
        private GeneratorConfig _config;
        private FixtureVoxelIds _ids;
        private Biome.BiomeDef[] _defs;
        private int _waterLevel;
        private int WetY => DensityLayout.WetnessY(_waterLevel);
        private string[] _names;

        /// <summary>Chunks whose centre lies on a river's centre line in the plain lowland, nearest to the origin first.</summary>
        private List<DensityProbeResult> _chunks;

        /// <summary>The river output and C at each column of <see cref="_chunks" />, [x · 32 + z].</summary>
        private List<float[]> _river, _c;

        [OneTimeSetUp]
        public void SetUp(){
            GraphCompileResult r = ShippedNoiseGraph.Compile();
            Assert.IsTrue(r.Succeeded, string.Join("\n", r.Messages));
            _graph = r.Graph;
            _config = TestFixtures.CreateGeneratorConfig(Allocator.Persistent);
            _config.WorldGen.Seed = DensityFixture.Seed;
            _ids = DensityFixture.UseShippedSurface(ref _config.WorldGen, _graph);
            _defs = _config.WorldGen.Layout.Defs.ToArray();
            _names = BiomeLayoutTests.Shipped().Palette();
            _waterLevel = _config.WorldGen.WaterLevel;
            Assert.AreEqual(WorldGenSettings.DefaultWaterLevel, _waterLevel, "the tests assume the shipped water level");

            _chunks = new List<DensityProbeResult>();
            _river = new List<float[]>();
            _c = new List<float[]>();
            foreach (int2 chunk in RiverChunks(6)){
                DensityProbeResult probe = DensityProbe.Run(_graph, _config.WorldGen, chunk * Size);
                _chunks.Add(probe);
                float[] values = Columns(probe.Origin, out int n);
                _river.Add(values.Skip(Slot(SurfaceRuleSet.RiverOutput) * n).Take(n).ToArray());
                _c.Add(values.Skip(Slot("continentalness") * n).Take(n).ToArray());
            }
        }

        [OneTimeTearDown]
        public void TearDown(){
            if (_chunks != null)
                foreach (DensityProbeResult c in _chunks)
                    c.Dispose();
            _graph?.Dispose();
            TestFixtures.DisposeGeneratorConfig(ref _config);
        }

        private int Slot(string output){
            return _graph.Outputs.First(o => o.Name == output).LatticeValue;
        }

        /// <summary>The graph's lattice outputs at the 32 × 32 columns of the chunk at <paramref name="origin" />, [output][x · 32 + z].</summary>
        private float[] Columns(int2 origin, out int n){
            float2[] points = new float2[Size * Size];
            for (int x = 0; x < Size; x++)
            for (int z = 0; z < Size; z++)
                points[x * Size + z] = new float2(origin.x + x, origin.y + z);
            n = points.Length;
            return GraphEvaluator.SampleLattice(_graph, _config.WorldGen.Seed, points, out _);
        }

        /// <summary>Up to <paramref name="count" /> chunks, nearest to the origin first, whose centre has river ≥ 0.9 in the plain lowland.</summary>
        private IEnumerable<int2> RiverChunks(int count){
            const int radius = 200, step = 3;
            List<int2> chunks = new();
            for (int z = -radius; z <= radius; z += step)
            for (int x = -radius; x <= radius; x += step)
                chunks.Add(new int2(x, z));
            chunks = chunks.OrderBy(c => math.lengthsq(c)).ThenBy(c => c.y).ThenBy(c => c.x).ToList();
            float2[] points = chunks.Select(c => new float2(c.x * Size + Size / 2, c.y * Size + Size / 2)).ToArray();
            float[] values = GraphEvaluator.SampleLattice(_graph, _config.WorldGen.Seed, points, out _);
            int river = Slot(SurfaceRuleSet.RiverOutput), cSlot = Slot("continentalness");
            int found = 0;
            for (int i = 0; i < chunks.Count && found < count; i++){
                float c = values[cSlot * points.Length + i];
                if (values[river * points.Length + i] < 0.9f || c < LowlandCMin + 0.01f || c > LowlandCMax - 0.01f) continue;
                found++;
                yield return chunks[i];
            }

            Assert.AreEqual(count, found, "too few river chunks within the search grid");
        }

        /// <summary>Points on a 512 × 512 grid, 7 blocks apart, around the origin.</summary>
        private static float2[] Grid(){
            const int side = 512, spacing = 7;
            float2[] points = new float2[side * side];
            for (int j = 0; j < side; j++)
            for (int i = 0; i < side; i++)
                points[j * side + i] = new float2((i - side / 2) * spacing + 0.5f, (j - side / 2) * spacing + 0.5f);
            return points;
        }

        #region The valleys slice

        /// <summary>
        ///     The valleys slice is |W| ≤ 0.04 (§9.2, DEC-196): the mirrored lowland relief the lowland rows read (lowland_pv, DEC-192)
        ///     is at most −0.85, the rows' −4…0 bed, exactly where |W| ≤ 0.04.
        /// </summary>
        [Test]
        public void ValleysSlice_IsAbsWAtMost0_04(){
            NoiseGraph g = NoiseGraphJson.Parse(ShippedNoiseGraph.LoadJson());
            g.Outputs["lowland_pv_probe"] = "lowland_pv";
            GraphCompileResult r = GraphCompiler.Compile(g, "probe");
            Assert.IsTrue(r.Succeeded, string.Join("\n", r.Messages));
            using CompiledNoiseGraph graph = r.Graph;
            float2[] points = Grid();
            float[] values = GraphEvaluator.SampleLattice(graph, _config.WorldGen.Seed, points, out _);
            int pvSlot = graph.Outputs.First(o => o.Name == "lowland_pv_probe").LatticeValue, wSlot = graph.Outputs.First(o => o.Name == "weirdness").LatticeValue;
            int inside = 0, mismatches = 0;
            string first = null;
            for (int p = 0; p < points.Length; p++){
                float w = math.abs(values[wSlot * points.Length + p]), pv = values[pvSlot * points.Length + p];
                if (math.abs(w - Slice) < 1e-4f) continue; // on the edge itself, float rounding decides
                bool inSlice = w <= Slice;
                if (inSlice) inside++;
                if (inSlice == pv <= SliceEdgePv) continue;
                mismatches++;
                first ??= $"{points[p]}: |W| {w:0.00000}, lowland_pv {pv:0.00000}";
            }

            TestContext.WriteLine($"{inside} of {points.Length} points in the slice");
            Assert.Greater(inside, points.Length / 100, "the slice holds about 5 % of the points");
            Assert.AreEqual(0, mismatches, $"lowland_pv ≤ {SliceEdgePv} must equal |W| ≤ {Slice}; first: {first}");
        }

        /// <summary>
        ///     The river output lies inside the valleys slice and is 1 − |W|/0.04 there; it is 1 at that value in the plain lowland
        ///     (C −0.11 … 0.03) and 0 out at sea and in the massif (C &lt; −0.19, or C ≥ 0.12 with E ≤ −0.05).
        /// </summary>
        [Test]
        public void RiverOutput_LiesInTheSliceAndTheLowland(){
            float2[] points = Grid();
            float[] values = GraphEvaluator.SampleLattice(_graph, _config.WorldGen.Seed, points, out _);
            int n = points.Length, river = Slot(SurfaceRuleSet.RiverOutput), w = Slot("weirdness"), c = Slot("continentalness"), e = Slot("erosion");
            int lowland = 0, sea = 0, massif = 0;
            for (int p = 0; p < n; p++){
                float rv = values[river * n + p], aw = math.abs(values[w * n + p]), cv = values[c * n + p], ev = values[e * n + p];
                string where = $"{points[p]}: river {rv}, |W| {aw}, C {cv}, E {ev}";
                Assert.IsTrue(rv >= 0f && rv <= 1f, where);
                if (aw >= Slice) Assert.AreEqual(0f, rv, where + ": river outside the valleys slice");
                if (cv < -0.19f){
                    Assert.AreEqual(0f, rv, where + ": river out at sea");
                    sea++;
                }

                if (cv >= 0.12f && ev <= -0.05f){
                    Assert.AreEqual(0f, rv, where + ": river in the massif");
                    massif++;
                }

                if (cv >= LowlandCMin && cv <= LowlandCMax && aw < Slice){
                    Assert.AreEqual(1f - aw / Slice, rv, 1e-4f, where + ": the plain lowland's river");
                    lowland++;
                }
            }

            TestContext.WriteLine($"lowland slice {lowland}, sea {sea}, massif {massif}");
            Assert.Greater(lowland, 0);
            Assert.Greater(sea, 0);
            Assert.Greater(massif, 0);
        }

        #endregion

        #region Built river chunks

        /// <summary>River water only below 124 (at y 123 and under), and no water anywhere above it.</summary>
        [Test]
        public void RiverWater_OnlyBelow124_AndNoneAbove(){
            ushort water = _config.WorldGen.WaterVoxel;
            Assert.AreNotEqual(0, water);
            int riverWater = 0;
            for (int i = 0; i < _chunks.Count; i++){
                DensityProbeResult r = _chunks[i];
                for (int x = 0; x < Size; x++)
                for (int z = 0; z < Size; z++){
                    int c = x * Size + z;
                    for (int y = 0; y < Height; y++){
                        if (r.Voxels[c * Height + y] != water) continue;
                        Assert.Less(y, _waterLevel, $"chunk {r.Origin} column ({x}, {z}): water at y {y}");
                        if (_river[i][c] > 0f) riverWater++;
                    }
                }
            }

            TestContext.WriteLine($"{riverWater} river water voxels in {_chunks.Count} chunks");
            Assert.Greater(riverWater, 0, "the river chunks hold river water");
        }

        /// <summary>The river's centre line (river ≥ 0.9) in the plain lowland is under water: rivers do not run dry there.</summary>
        [Test]
        public void RiverCentreLine_InThePlainLowland_IsWet(){
            int columns = 0, wet = 0;
            for (int i = 0; i < _chunks.Count; i++){
                DensityProbeResult r = _chunks[i];
                for (int c = 0; c < Size * Size; c++){
                    if (_river[i][c] < 0.9f || _c[i][c] < LowlandCMin || _c[i][c] > LowlandCMax) continue;
                    columns++;
                    if (r.Heightmap[c] < WetY) wet++;
                }
            }

            double share = columns > 0 ? (double)wet / columns : 0;
            TestContext.WriteLine($"{wet} of {columns} centre-line columns wet ({share:P1})");
            Assert.Greater(columns, 100);
            Assert.GreaterOrEqual(share, 0.95, "the plain lowland's river centre line is wet");
        }

        /// <summary>
        ///     The bed of a river (floor under water, river ≥ 0.4) is gravel, sand (the beach block), clay or, in a swamp, mud
        ///     (DEC-195); its gentle dry banks at the water line (top 0 … 2 above it, rise &lt; 3) take the V4 shore sand.
        /// </summary>
        [Test]
        public void RiverBeds_TakeGravelSandClayOrMud_AndGentleBanksTheShoreSand(){
            ushort gravel = (ushort)_ids.ByName("std:StoneGravel"), clay = (ushort)_ids.ByName("std:Clay"), mud = (ushort)_ids.ByName("std:Mud");
            Dictionary<string, int> beds = new();
            int banks = 0;
            for (int i = 0; i < _chunks.Count; i++){
                DensityProbeResult r = _chunks[i];
                for (int x = 0; x < Size; x++)
                for (int z = 0; z < Size; z++){
                    int c = x * Size + z, h = r.Heightmap[c];
                    if (h < 8) continue;
                    Biome.BiomeDef def = _defs[r.Surface[c]];
                    ushort top = r.Voxels[c * Height + h];
                    string where = $"chunk {r.Origin} column ({x}, {z}) y {h} (river {_river[i][c]:0.00}, rise {r.Rise[c]})";
                    if (h < WetY && _river[i][c] >= SurfaceRuleTests.RiverBedMin + 0.1f){
                        string kind = top == gravel ? "gravel" : top == clay ? "clay" : top == mud ? "mud" : def.beachBlock != 0 && top == def.beachBlock ? "sand" : null;
                        Assert.IsNotNull(kind, $"{where}: the river bed is {_ids.Name(top)}");
                        if (kind == "mud") Assert.AreEqual("Swamp", _names[r.Surface[c]], where + ": mud only in a swamp");
                        beds.TryGetValue(kind, out int k);
                        beds[kind] = k + 1;
                        continue;
                    }

                    // A gentle dry bank next to a river column.
                    if (h < WetY || h > WetY + 2 || r.Rise[c] >= 3 || h >= r.SnowLine[c] || def.beachBlock == 0) continue;
                    bool besideRiver = false;
                    for (int d = 0; d < 4 && !besideRiver; d++){
                        int nx = x + (d == 0 ? 1 : d == 1 ? -1 : 0), nz = z + (d == 2 ? 1 : d == 3 ? -1 : 0);
                        if (nx < 0 || nz < 0 || nx >= Size || nz >= Size) continue;
                        int nc = nx * Size + nz;
                        besideRiver = r.Heightmap[nc] < WetY && _river[i][nc] > 0f;
                    }

                    if (!besideRiver) continue;
                    Assert.AreEqual(def.beachBlock, top, $"{where}: a gentle river bank wants {_ids.Name(def.beachBlock)}, found {_ids.Name(top)}");
                    banks++;
                }
            }

            TestContext.WriteLine($"beds: {string.Join(", ", beds.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key} {kv.Value}"))}; gentle banks {banks}");
            Assert.IsTrue(beds.ContainsKey("gravel"), "no gravel bed in the river chunks");
            Assert.GreaterOrEqual(beds.Count, 2, "the beds mix their blocks");
            Assert.Greater(banks, 0, "no gentle river bank in the river chunks");
        }

        #endregion
    }
}
