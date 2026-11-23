using System;
using System.Collections.Generic;
using System.IO;
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
    ///     The surface rules of WorldGen v2 (§7, §8, phase V4) on the shipped graph, layout and rule file with real voxel
    ///     ids: the validator's errors and warnings, the merge of the test mod's rule file, the interior plan against full
    ///     evaluation on every voxel, the snow line and tree line formulas, ice only at y 123 below T_ice, no snow or ice
    ///     in never-snow biomes, and the v1 surface behaviour on built columns (the ports of BeachTests,
    ///     SurfaceCoverTests and UnderwaterTopBlockTests: beach band, shallow sand, underwater top, steep and alpine rock,
    ///     meadow below the tree line, the under layers, snow over all).
    /// </summary>
    public class SurfaceRuleTests{
        private const int Size = DensityLayout.ChunkSize;
        private const int Height = DensityLayout.Height;

        private CompiledNoiseGraph _graph;
        private GeneratorConfig _config;
        private FixtureVoxelIds _ids;
        private BiomeLayout _layout;
        private string[] _names;
        private Biome.BiomeDef[] _defs;
        private int _waterLevel;

        /// <summary>Sample chunks by kind (snow, ice, coast, lowland, hot), found from the climate at chunk centres.</summary>
        private Dictionary<string, List<DensityProbeResult>> _kinds;

        private List<DensityProbeResult> _chunks;

        /// <summary>The lowest T at a sampled chunk centre under 12 blocks of water or more (for the ice search's message).</summary>
        private float _coldestWater;

        [OneTimeSetUp]
        public void SetUp(){
            GraphCompileResult r = ShippedNoiseGraph.Compile();
            Assert.IsTrue(r.Succeeded, string.Join("\n", r.Messages));
            _graph = r.Graph;
            _config = TestFixtures.CreateGeneratorConfig(Allocator.Persistent);
            _config.WorldGen.Seed = DensityFixture.Seed;
            _ids = DensityFixture.UseShippedSurface(ref _config.WorldGen, _graph);
            _layout = BiomeLayoutTests.Shipped();
            _names = _layout.Palette();
            _defs = _config.WorldGen.Layout.Defs.ToArray();
            _waterLevel = _config.WorldGen.WaterLevel;
            Assert.AreEqual(DensityLayout.WetnessY + 1, _waterLevel, "the tests assume the water level 124");

            _kinds = new Dictionary<string, List<DensityProbeResult>>();
            HashSet<int2> done = new();
            _chunks = new List<DensityProbeResult>();
            foreach ((string kind, int2 chunk) in SampleChunks()){
                if (!_kinds.TryGetValue(kind, out List<DensityProbeResult> list)) _kinds[kind] = list = new List<DensityProbeResult>();
                if (!done.Add(chunk)){
                    list.Add(_chunks.First(c => c.Origin.Equals(chunk * Size)));
                    continue;
                }

                DensityProbeResult probe = DensityProbe.Run(_graph, _config.WorldGen, chunk * Size);
                _chunks.Add(probe);
                list.Add(probe);
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

        private ref SurfaceRules Rules => ref _config.WorldGen.SurfaceRules;

        /// <summary>
        ///     Up to four chunks per kind, nearest to the origin first, from T and the preliminary surface at the centres
        ///     of a grid of chunks: snow (surface ≥ 215), alpine (surface 180 … 205), ice (T &lt; −0.68, surface &lt; 112), coast (surface within 2 of
        ///     the water), lowland (surface 135 … 165) and hot (T &gt; 0.45, dry).
        /// </summary>
        private IEnumerable<(string Kind, int2 Chunk)> SampleChunks(){
            const int radius = 240, step = 4, perKind = 4;
            List<int2> chunks = new();
            for (int z = -radius; z <= radius; z += step)
            for (int x = -radius; x <= radius; x += step)
                chunks.Add(new int2(x, z));
            chunks = chunks.OrderBy(c => math.lengthsq(c)).ThenBy(c => c.y).ThenBy(c => c.x).ToList();
            float2[] points = chunks.Select(c => new float2(c.x * Size + Size / 2, c.y * Size + Size / 2)).ToArray();
            float[] values = GraphEvaluator.SampleLattice(_graph, _config.WorldGen.Seed, points, out _);
            int tSlot = Rules.TemperatureSlot, sSlot = Rules.PreliminarySurfaceSlot;
            Assert.GreaterOrEqual(tSlot, 0);
            Assert.GreaterOrEqual(sSlot, 0);
            Dictionary<string, int> found = new();
            _coldestWater = float.PositiveInfinity;
            for (int i = 0; i < chunks.Count; i++){
                float t = values[tSlot * points.Length + i], s = values[sSlot * points.Length + i];
                if (s < 112f) _coldestWater = math.min(_coldestWater, t);
                string kind = s >= 215f ? "snow"
                    : s >= 180f && s < 205f ? "alpine"
                    : t < -0.68f && s < 112f ? "ice"
                    : math.abs(s - _waterLevel) < 2f ? "coast"
                    : s >= 135f && s <= 165f && t <= 0.45f ? "lowland"
                    : t > 0.45f && s > _waterLevel + 4 ? "hot"
                    : null;
                if (kind == null) continue;
                found.TryGetValue(kind, out int n);
                if (n >= perKind) continue;
                found[kind] = n + 1;
                yield return (kind, chunks[i]);
            }
        }

        private List<DensityProbeResult> Kind(string kind){
            Assert.IsTrue(_kinds.TryGetValue(kind, out List<DensityProbeResult> list) && list.Count > 0,
                $"no {kind} chunk within the search grid (kinds found: {string.Join(", ", _kinds.Select(k => $"{k.Key} {k.Value.Count}"))}; coldest deep water T {_coldestWater})");
            return list;
        }

        private static ushort VoxelAt(DensityProbeResult r, int x, int y, int z){
            return r.Voxels[(x * Size + z) * Height + y];
        }

        private string Name(int id){
            return _ids.Name(id);
        }

        private ushort SnowBlock(in Biome.BiomeDef def){
            return def.snowTopBlock != 0 ? def.snowTopBlock : (ushort)_ids.ByName("std:StoneSnowy");
        }

        /// <summary>Whether <paramref name="block" /> is one of the biome's own surface blocks (then it may lie there without snow).</summary>
        private static bool IsOwnBlock(in Biome.BiomeDef def, ushort block){
            return block == def.topBlock || block == def.underBlock || block == def.stoneBlock || block == def.underwaterTopBlock ||
                   block == def.beachBlock || block == def.meadowBlock || block == def.forestFloorBlock || block == def.rockBlock ||
                   block == def.strataBlock || block == def.strataBlock1 || block == def.strataBlock2 || block == def.strataBlock3;
        }

        #region Validator and merge

        private SurfaceRuleCompileResult Compile(SurfaceRuleSet set, BiomeLayout layout = null, Biome.BiomeDef[] defs = null, CompiledNoiseGraph graph = null){
            layout ??= _layout;
            return SurfaceRuleCompiler.Compile(set, layout.Palette(), defs ?? _defs, graph ?? _graph, _ids.ByName, Allocator.Persistent, layout);
        }

        /// <summary>A rule file from <paramref name="json" /> with ' for ".</summary>
        private static SurfaceRuleSet Parse(string json){
            return SurfaceRuleSet.Parse(json.Replace('\'', '"'), BiomeLayout.BaseSourceName, "test.json");
        }

        private const string Climate =
            "'climate': { 'lapse': 0.0055, 'lapseFrom': 124, 'snowTemperature': -0.473, 'snowWobble': 'snow_wobble', 'iceTemperature': -0.6, " +
            "'iceWobble': 'ice_wobble', 'treeLineBelowSnow': 15, 'treeLineFloor': 170, 'nearWaterDistance': 16, 'beachShape': 'beach_shape' }";

        /// <summary>A rule file with the shipped climate and the given default list.</summary>
        private static SurfaceRuleSet WithDefault(string rules){
            return Parse("{ 'formatVersion': 1, " + Climate + ", 'default': [" + rules + "] }");
        }

        private static string Errors(SurfaceRuleCompileResult r){
            return string.Join("\n", r.Messages);
        }

        private static void AssertError(SurfaceRuleCompileResult r, string kind){
            Assert.IsTrue(r.Messages.Any(m => m.Severity == LayoutSeverity.Error && m.Kind == kind), $"expected a \"{kind}\" error:\n{Errors(r)}");
        }

        [Test]
        public void ShippedRules_CompileWithoutErrors_AndEveryBiomeHasRules(){
            using SurfaceRuleCompileResult r = SurfaceRuleCompiler.CompileShipped(_layout, _defs, _graph, _ids.ByName, Allocator.Persistent);
            Assert.IsTrue(r.Succeeded, r.ErrorText());
            foreach (LayoutMessage m in r.Messages) TestContext.WriteLine(m);
            for (int b = 0; b < _names.Length; b++) Assert.IsTrue(r.Rules.HasRulesFor(b), $"{_names[b]} has no rules");
            Assert.LessOrEqual(r.Rules.Biomes.Max(x => math.max(x.RAbove, x.RBelow)), SurfaceRules.MaxDepthBound);
            Assert.AreEqual(1, r.Rules.Ice, "some biome snows, so water freezes");
        }

        [Test]
        public void Validator_ReportsUnknownSlotsBlocksAndOutputs(){
            using (SurfaceRuleCompileResult r = Compile(WithDefault("{ 'name': 'a', 'if': 'floor', 'then': { 'block': '@nope' } }")))
                AssertError(r, "block");
            using (SurfaceRuleCompileResult r = Compile(WithDefault("{ 'name': 'a', 'if': 'floor', 'then': { 'block': 'std:NoSuchVoxel' } }")))
                AssertError(r, "block");
            using (SurfaceRuleCompileResult r = Compile(WithDefault(
                       "{ 'name': 'a', 'if': { 'noise_threshold': { 'name': 'no_such_output', 'min': 0 } }, 'then': { 'block': '@top' } }")))
                AssertError(r, "output");
        }

        [Test]
        public void Validator_ReportsDuplicateNamesAndUnknownBiomes(){
            // Twice in one list: the merge refuses the second; the same name in globalBefore and a biome list: the compiler.
            SurfaceRuleSet twice = WithDefault("{ 'name': 'a', 'if': 'floor', 'then': { 'block': '@top' } }, { 'name': 'a', 'then': { 'block': '@stone' } }");
            Assert.IsTrue(twice.Messages.Any(m => m.Severity == LayoutSeverity.Error && m.Kind == "rule"), string.Join("\n", twice.Messages));
            SurfaceRuleSet across = Parse("{ 'formatVersion': 1, " + Climate + ", 'globalBefore': [{ 'name': 'a', 'if': 'floor', 'then': { 'block': '@top' } }], " +
                                          "'default': [{ 'name': 'a', 'then': { 'block': '@stone' } }] }");
            using (SurfaceRuleCompileResult r = Compile(across))
                AssertError(r, "duplicate");
            using (SurfaceRuleCompileResult r = Compile(WithDefault(
                       "{ 'name': 'a', 'if': { 'biome': ['Atlantis'] }, 'then': { 'block': '@top' } }")))
                AssertError(r, "biome");
        }

        [Test]
        public void Validator_ReportsDepthBoundsAboveEightAndTooManyInteriorThresholds(){
            using (SurfaceRuleCompileResult r = Compile(WithDefault("{ 'name': 'a', 'if': { 'stone_depth_above': 9 }, 'then': { 'block': '@under' } }")))
                AssertError(r, "depth");
            // Four y thresholds in the interior: one more than the plan allows.
            using (SurfaceRuleCompileResult r = Compile(WithDefault(string.Join(", ",
                       Enumerable.Range(0, 4).Select(i => $"{{ 'name': 'y{i}', 'if': {{ 'y_above': {140 + 10 * i} }}, 'then': {{ 'block': '@stone' }} }}")))))
                AssertError(r, "interior");
            // A run-level test on voxels deeper than every stone depth bound.
            using (SurfaceRuleCompileResult r = Compile(WithDefault("{ 'name': 'a', 'if': 'dry', 'then': { 'block': '@under' } }")))
                AssertError(r, "interior");
        }

        [Test]
        public void Validator_ReportsIceThatCanBeWarmerThanSnow(){
            SurfaceRuleSet set = Parse("{ 'formatVersion': 1, " + Climate.Replace("'iceTemperature': -0.6", "'iceTemperature': -0.45") +
                                       ", 'default': [{ 'name': 'top', 'if': 'floor', 'then': { 'block': '@top' } }] }");
            using SurfaceRuleCompileResult r = Compile(set);
            AssertError(r, "climate");
            StringAssert.Contains("W2-CLM-05", Errors(r));
        }

        [Test]
        public void Validator_WarnsWhenANeverSnowBiomeReachesSnowTemperatures(){
            using (SurfaceRuleCompileResult shipped = SurfaceRuleCompiler.CompileShipped(_layout, _defs, _graph, _ids.ByName, Allocator.Persistent))
                foreach (LayoutMessage m in shipped.Messages.Where(m => m.Kind == "never-snow"))
                    TestContext.WriteLine("shipped: " + m);

            BiomeLayout cold = BiomeLayoutTests.Shipped();
            int desert = Array.IndexOf(cold.Palette(), "Desert");
            Assert.GreaterOrEqual(desert, 0);
            foreach (LayoutBox box in cold.Boxes)
                if (box.Biome == desert)
                    box.Min[0] = -1f;
            using SurfaceRuleCompileResult r = SurfaceRuleCompiler.CompileShipped(cold, _defs, _graph, _ids.ByName, Allocator.Persistent);
            Assert.IsTrue(r.Succeeded, r.ErrorText());
            Assert.IsTrue(r.Messages.Any(m => m.Severity == LayoutSeverity.Warning && m.Kind == "never-snow" && m.Biome == "Desert"), Errors(r));
        }

        [Test]
        public void ModMerge_InsertsBeforeTheAnchorInsideTheSequence_ForItsBiomeOnly(){
            SurfaceRuleSet set = SurfaceRuleSet.Shipped();
            set.AddModJson("TestMod", "Rules.json", File.ReadAllText(BiomeLayoutTests.TestModPath + "Rules.json"));
            Assert.IsFalse(set.HasErrors, string.Join("\n", set.Messages));

            List<SurfaceRule> heath = set.ListFor("Heath");
            Assert.IsTrue(SurfaceRuleSet.Find(heath, "heath_moss", out List<SurfaceRule> container, out int index), "heath_moss is missing");
            Assert.AreEqual("top", container[index + 1].Name, "heath_moss goes right before top");
            Assert.IsFalse(SurfaceRuleSet.Find(set.ListFor("Plains"), "heath_moss", out _, out _), "Plains keeps the default list");
            Assert.IsFalse(SurfaceRuleSet.Find(set.Default, "heath_moss", out _, out _), "the default list is unchanged");

            BiomeLayout layout = BiomeLayoutTests.WithTestMod();
            GraphCompileResult graph = BiomeLayoutTests.TestModGraph();
            Assert.IsTrue(graph.Succeeded, string.Join("\n", graph.Messages));
            using CompiledNoiseGraph g = graph.Graph;
            BiomeLayoutData data = BiomeLayoutData.Create(layout, _ids.Id, Allocator.Persistent);
            try{
                using SurfaceRuleCompileResult r = Compile(set, layout, data.Defs.ToArray(), g);
                Assert.IsTrue(r.Succeeded, r.ErrorText());
                Assert.IsTrue(r.Rules.HasRulesFor(Array.IndexOf(layout.Palette(), "Heath")));
            }
            finally{
                data.Dispose();
            }
        }

        [Test]
        public void ModMerge_UnknownAnchorAndDuplicateAppend_AreErrorsThatAddNothing(){
            SurfaceRuleSet set = SurfaceRuleSet.Shipped();
            int before = set.Default.Count;
            set.AddModJson("Bad", "Rules.json", "{ 'formatVersion': 1, 'default': [ { 'name': 'x', 'then': { 'block': '@stone' } }, { 'before': 'no_such_rule', 'rule': { 'name': 'y', 'then': { 'block': '@stone' } } } ] }".Replace('\'', '"'));
            Assert.IsTrue(set.Messages.Any(m => m.Kind == "anchor"), string.Join("\n", set.Messages));
            Assert.AreEqual(before, set.Default.Count, "a file with errors adds nothing");

            SurfaceRuleSet dup = SurfaceRuleSet.Shipped();
            dup.AddModJson("Dup", "Rules.json", "{ 'formatVersion': 1, 'default': [ { 'name': 'stone', 'then': { 'block': '@stone' } } ] }".Replace('\'', '"'));
            Assert.IsTrue(dup.Messages.Any(m => m.Kind == "rule"), string.Join("\n", dup.Messages));
        }

        #endregion

        #region Formulas

        [Test]
        public void ColdThreshold_IsTheLowestColdY_AndFollowsTheSnowLineFormula(){
            SurfaceRules rules = Rules;
            float[] wobbles ={ -0.033f, 0f, 0.021f };
            for (float t = -1.2f; t <= 1.2f; t += 0.0137f)
                foreach (float w in wobbles){
                    int y = rules.ColdThreshold(t, w, rules.SnowTemperature);
                    Assert.That(y, Is.InRange(0, 256));
                    if (y < 256) Assert.IsTrue(rules.ColdAt(y, t, w, rules.SnowTemperature), $"T {t} w {w}: y {y} is not cold");
                    if (y > 0) Assert.IsFalse(rules.ColdAt(y - 1, t, w, rules.SnowTemperature), $"T {t} w {w}: y {y - 1} is cold already");
                    if (y > 0 && y < 256){
                        float formula = rules.LapseFrom + (t + w - rules.SnowTemperature) / rules.Lapse;
                        Assert.That(y, Is.InRange((int)math.floor(formula), (int)math.ceil(formula) + 1), $"T {t} w {w}");
                    }
                }

            // DEC-175: the snow line lies at y 210 at T_snow + 86 L (the median Mountain T of the calibration).
            int median = rules.ColdThreshold(rules.SnowTemperature + 86f * rules.Lapse, 0f, rules.SnowTemperature);
            Assert.That(median, Is.InRange(210, 211));
        }

        [Test]
        public void TreeLine_NeverLiesBelowItsFloor_AndFifteenBlocksBelowTheSnowLine(){
            SurfaceRules rules = Rules;
            for (float t = -1.2f; t <= 1.2f; t += 0.0137f){
                int tree = rules.TreeLine(t, 0f);
                Assert.GreaterOrEqual(tree, (int)rules.TreeLineFloor);
                float free = rules.LapseFrom + (t - rules.TreeTemperature) / rules.Lapse;
                if (free <= rules.TreeLineFloor + 1f || free >= 240f) continue;
                int snow = rules.ColdThreshold(t, 0f, rules.SnowTemperature);
                Assert.That(snow - tree, Is.InRange(14, 16), $"T {t}");
            }
        }

        #endregion

        #region Built chunks

        [Test]
        public void InteriorPlan_EqualsFullEvaluation_OnEveryVoxel(){
            WorldGenConfig full = _config.WorldGen; // a copy that shares the arrays
            full.SurfaceRules.FullEvaluation = true;
            long interior = 0, voxels = 0;
            foreach (DensityProbeResult plan in _chunks){
                using DensityProbeResult reference = DensityProbe.Run(_graph, full, plan.Origin);
                interior += plan.Counters.Surface.InteriorVoxels;
                for (int i = 0; i < plan.Voxels.Length; i++){
                    voxels++;
                    if (plan.Voxels[i] == reference.Voxels[i]) continue;
                    int c = i / Height, y = i % Height;
                    Assert.Fail($"chunk {plan.Origin}, column ({c / Size}, {c % Size}), y {y}: plan {Name(plan.Voxels[i])}, full {Name(reference.Voxels[i])}");
                }
            }

            TestContext.WriteLine($"{voxels} voxels in {_chunks.Count} chunks, {interior} from the interior plan");
            Assert.Greater(interior, 0, "the plan filled no interior voxel");
        }

        [Test]
        public void Snow_CoversDryTopsFromTheSnowLine_OutsideNeverSnowBiomes(){
            int snow = 0, aboveLine = 0;
            foreach (DensityProbeResult r in _chunks)
                for (int x = 0; x < Size; x++)
                for (int z = 0; z < Size; z++){
                    int c = x * Size + z, h = r.Heightmap[c];
                    if (h < 0 || h >= Height - 1) continue;
                    Biome.BiomeDef def = _defs[r.Surface[c]];
                    ushort block = SnowBlock(def), top = VoxelAt(r, x, h, z);
                    bool dry = h + 1 >= _waterLevel;
                    bool expected = dry && h >= r.SnowLine[c] && def.neverSnows == 0 && block != 0;
                    if (dry && h >= r.SnowLine[c]) aboveLine++;
                    string where = $"chunk {r.Origin} column ({x}, {z}) y {h} {_names[r.Surface[c]]}, snow line {r.SnowLine[c]}";
                    if (expected){
                        Assert.AreEqual(block, top, $"{where}: {Name(top)} instead of snow");
                        snow++;
                    }
                    else if (!IsOwnBlock(def, block))
                        Assert.AreNotEqual(block, top, $"{where}: snow below the snow line or in a never-snow biome");
                }

            TestContext.WriteLine($"{snow} snow tops, {aboveLine} dry tops above the snow line");
            Assert.Greater(snow, 0, "no snow in the sample chunks");
            Assert.IsTrue(Kind("snow").Any(r => Enumerable.Range(0, Size * Size).Any(c => r.Heightmap[c] >= r.SnowLine[c])), "no snow chunk reaches its snow line");
        }

        [Test]
        public void Ice_FormsOnlyAtY123_OverOpenColdWater_OutsideNeverSnowBiomes(){
            int ice = 0;
            ushort iceId = _config.WorldGen.IceVoxel;
            Assert.AreNotEqual(0, iceId);
            foreach (DensityProbeResult r in _chunks)
                for (int x = 0; x < Size; x++)
                for (int z = 0; z < Size; z++){
                    int c = x * Size + z, h = r.Heightmap[c];
                    for (int y = 0; y < Height; y++)
                        if (y != DensityLayout.WetnessY && VoxelAt(r, x, y, z) == iceId)
                            Assert.Fail($"chunk {r.Origin} column ({x}, {z}): ice at y {y}");
                    Biome.BiomeDef def = _defs[r.Surface[c]];
                    bool expected = h < DensityLayout.WetnessY && r.IceCold[c] != 0 && def.neverSnows == 0;
                    bool found = VoxelAt(r, x, DensityLayout.WetnessY, z) == iceId;
                    Assert.AreEqual(expected, found, $"chunk {r.Origin} column ({x}, {z}) {_names[r.Surface[c]]}, top {h}, T {r.ColumnTemperature[c]}");
                    if (found) ice++;
                }

            TestContext.WriteLine($"{ice} ice voxels");
            Kind("ice");
            Assert.Greater(ice, 0, "no ice in the sample chunks");
        }

        [Test]
        public void NeverSnowBiomes_HaveNoSnowAndNoIce(){
            int columns = 0;
            ushort iceId = _config.WorldGen.IceVoxel;
            foreach (DensityProbeResult r in _chunks)
                for (int x = 0; x < Size; x++)
                for (int z = 0; z < Size; z++){
                    int c = x * Size + z, h = r.Heightmap[c];
                    Biome.BiomeDef def = _defs[r.Surface[c]];
                    if (def.neverSnows == 0 || h < 0) continue;
                    columns++;
                    ushort block = SnowBlock(def);
                    if (!IsOwnBlock(def, block)) Assert.AreNotEqual(block, VoxelAt(r, x, h, z), $"snow in {_names[r.Surface[c]]} at chunk {r.Origin} ({x}, {z})");
                    Assert.AreNotEqual(iceId, VoxelAt(r, x, DensityLayout.WetnessY, z), $"ice in {_names[r.Surface[c]]} at chunk {r.Origin} ({x}, {z})");
                }

            TestContext.WriteLine($"{columns} never-snow columns");
            Assert.Greater(columns, 0, "no never-snow biome in the sample chunks");
            Kind("hot");
        }

        /// <summary>A block a rule may write: the block itself, or any band block when it is the biome's banded stone.</summary>
        private static bool Writes(in Biome.BiomeDef def, ushort expected, ushort actual){
            if (expected == actual) return true;
            if (expected != def.stoneBlock || def.strataCount == 0) return false;
            return actual == def.stoneBlock || actual == def.strataBlock || actual == def.strataBlock1 || actual == def.strataBlock2 || actual == def.strataBlock3;
        }

        private static ushort Top(in Biome.BiomeDef def){
            return def.topBlock != 0 ? def.topBlock : def.underBlock != 0 ? def.underBlock : def.stoneBlock;
        }

        private static ushort Under(in Biome.BiomeDef def){
            return def.underBlock != 0 ? def.underBlock : def.stoneBlock;
        }

        /// <summary>A wet probe of the padded map within <paramref name="r" /> blocks (a little wider than the rule's probes, for acceptance only).</summary>
        private static bool WetWithin(DensityProbeResult p, int x, int z, int r){
            for (int dz = -r; dz <= r; dz++)
            for (int dx = -r; dx <= r; dx++){
                int px = x + dx, pz = z + dz;
                if (dx * dx + dz * dz > r * r || px < -16 || pz < -16 || px > 47 || pz > 47) continue;
                if (p.PaddedWetAt(px, pz)) return true;
            }

            return false;
        }

        /// <summary>
        ///     The shipped rule order on a column's top run, written out (§7.4, the v1 surfaces as rules): what the top
        ///     voxel and the four under it must be. Beach band and dense forest depend on noise the test does not see,
        ///     so a beach or forest-floor top is accepted where the rule could fire, and otherwise the order goes on.
        /// </summary>
        [Test]
        public void TopRuns_FollowTheV1SurfaceOrder(){
            Dictionary<string, int> seen = new();
            void Count(string what){
                seen.TryGetValue(what, out int n);
                seen[what] = n + 1;
            }

            foreach (DensityProbeResult r in _chunks)
                for (int x = 0; x < Size; x++)
                for (int z = 0; z < Size; z++){
                    int c = x * Size + z, h = r.Heightmap[c];
                    if (h < 8 || h >= Height - 1) continue;
                    int id = r.Surface[c];
                    Biome.BiomeDef def = _defs[id];
                    int depth = math.max(0, _waterLevel - (h + 1));
                    bool dry = depth == 0;
                    bool steep = r.Rise[c] >= 4, alpine = h >= r.TreeLine[c];
                    ushort top = VoxelAt(r, x, h, z);
                    string where = $"chunk {r.Origin} column ({x}, {z}) y {h} {_names[id]} (water {depth}, rise {r.Rise[c]}, tree line {r.TreeLine[c]}, snow line {r.SnowLine[c]})";

                    // The beach rule could fire here (its band noise is not visible to the test).
                    bool beachPossible = dry && def.beachBlock != 0 && h - DensityLayout.WetnessY < math.max(1, (int)def.beachHeight) && WetWithin(r, x, z, 20);

                    // The floor.
                    string rule;
                    ushort expected;
                    bool beachTop = false;
                    if (dry && h >= r.SnowLine[c] && def.neverSnows == 0){
                        rule = "snow";
                        expected = SnowBlock(def);
                    }
                    else if (depth >= 1 && depth <= 3 && def.beachBlock != 0){
                        rule = "shallow_floor";
                        expected = def.beachBlock;
                    }
                    else if (depth >= 1){
                        rule = "underwater_top";
                        expected = def.underwaterTopBlock != 0 ? def.underwaterTopBlock : Top(def);
                    }
                    else if (beachPossible && top == def.beachBlock){
                        rule = "beach";
                        expected = def.beachBlock;
                        beachTop = true;
                    }
                    else if (steep && def.rockBlock != 0){
                        rule = "steep";
                        expected = def.rockBlock;
                    }
                    else if (alpine && def.rockBlock != 0){
                        rule = "alpine";
                        expected = def.rockBlock;
                    }
                    else if (def.forestFloorBlock != 0 && top == def.forestFloorBlock){
                        rule = "forest_floor";
                        expected = def.forestFloorBlock;
                    }
                    else if (!alpine && def.meadowGround != 0 && def.meadowBlock != 0){
                        rule = "meadow";
                        expected = def.meadowBlock;
                    }
                    else{
                        rule = "top";
                        expected = Top(def);
                    }

                    Assert.IsTrue(Writes(def, expected, top), $"{where}: rule {rule} wants {Name(expected)}, found {Name(top)}");
                    Count(rule);

                    // The four voxels under the floor, while the run goes on.
                    for (int d = 1; d <= 5; d++){
                        int y = h - d;
                        if (!r.IsSolid(x, y, z)) break;
                        ushort under;
                        string underRule;
                        if (dry && beachTop && d <= 2){
                            underRule = "beach";
                            under = def.beachBlock;
                        }
                        else if (rule == "snow" && beachPossible && d <= 2 && VoxelAt(r, x, y, z) == def.beachBlock){
                            underRule = "beach under snow";
                            under = def.beachBlock;
                        }
                        else if (dry && !beachTop && (steep || alpine) && def.rockBlock != 0 && d <= 4){
                            underRule = steep ? "steep" : "alpine";
                            under = def.rockBlock;
                        }
                        else if (d <= 4){
                            underRule = "under";
                            under = Under(def);
                        }
                        else{
                            underRule = "stone";
                            under = def.stoneBlock;
                        }

                        ushort actual = VoxelAt(r, x, y, z);
                        Assert.IsTrue(Writes(def, under, actual), $"{where}, depth {d}: rule {underRule} wants {Name(under)}, found {Name(actual)}");
                    }
                }

            TestContext.WriteLine(string.Join(", ", seen.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key} {kv.Value}")));
            foreach (string rule in new[]{ "snow", "shallow_floor", "underwater_top", "beach", "steep", "alpine", "meadow", "top" })
                Assert.IsTrue(seen.ContainsKey(rule), $"no column took the {rule} rule in the sample chunks");
            Kind("coast");
            Kind("lowland");
            Kind("alpine");
        }

        #endregion
    }
}
