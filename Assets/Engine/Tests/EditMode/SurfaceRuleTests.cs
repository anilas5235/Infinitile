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

        /// <summary>The dark rock the rules mix into rock by the rock_mix noise (DEC-182); the oracle cannot see the noise.</summary>
        private static ushort _darkRock;

        /// <summary>
        ///     The rock variety the rules mix into steep and alpine rock by the ground_mix and rock_kind noises (DEC-187, DEC-190):
        ///     granite, diorite, andesite and marble (lichen and mossy stone removed, DEC-201); the oracle cannot see the noises.
        /// </summary>
        private static readonly HashSet<ushort> RockVariety = new();

        /// <summary>The ground-variety tops the rules mix into dry floors below the tree line and into floors 4+ under water by the ground_mix noise (DEC-187, DEC-190).</summary>
        private static readonly HashSet<ushort> GroundVariety = new();
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
            _darkRock = (ushort)_ids.ByName("std:StoneGrey");
            RockVariety.Clear();
            foreach (string name in new[]{ "std:Granite", "std:Diorite", "std:Andesite", "std:Marble" })
                RockVariety.Add((ushort)_ids.ByName(name));
            GroundVariety.Clear();
            foreach (string name in new[]{
                         "std:Litter", "std:LitterLeaves", "std:DirtCoarse", "std:Mud", "std:Peat", "std:Moss",
                         "std:Cobblestone", "std:StoneGravel", "std:Clay"
                     })
                GroundVariety.Add((ushort)_ids.ByName(name));
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
        ///     of a grid of chunks: snow (surface 8 above the centre's snow line), alpine (between its tree line + 4 and snow line − 6), ice (T &lt; −0.68, surface &lt; 112), coast (surface within 2 of
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
                // Snow and alpine from the chunk centre's own lines, so a climate refit keeps the samples.
                int snowLine = Rules.ColdThreshold(t, 0f, Rules.SnowTemperature), treeLine = Rules.TreeLine(t, 0f);
                string kind = s >= snowLine + 8f && s >= 160f ? "snow"
                    : s >= treeLine + 4f && s < snowLine - 6f ? "alpine"
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
            return def.snowTopBlock != 0 ? def.snowTopBlock : (ushort)_ids.ByName("std:Snow");
        }

        private ushort SnowLayer => (ushort)_ids.ByName("std:SnowLayer");

        private ushort SnowHalf => (ushort)_ids.ByName("std:SnowLayerHalf");

        /// <summary>
        ///     The ground under snow (DEC-178, DEC-180, DEC-181, DEC-205): bare rock on steep faces (rise ≥ 4), rock on ledges
        ///     (rise 2–3), a snow block at the foot of a step, above the tree line and on steps (rise 1), else the under block.
        /// </summary>
        private ushort SnowGround(in Biome.BiomeDef def, int y, int treeLine, int rise, bool foot){
            if (rise >= 2 && def.rockBlock != 0) return def.rockBlock;
            return foot || y >= treeLine || rise >= 1 ? SnowBlock(def) : Under(def);
        }

        /// <summary>
        ///     The snow on the snow ground (see <see cref="SnowGround" />): none on steep faces, a half layer at the foot of a step
        ///     (DEC-205), else the thin layer, so every snowy step is one clean voxel high.
        /// </summary>
        private ushort SnowCover(in Biome.BiomeDef def, int rise, bool foot){
            if (rise >= 4 && def.rockBlock != 0) return 0;
            if (rise >= 2 && def.rockBlock != 0) return SnowLayer;
            return foot ? SnowHalf : SnowLayer;
        }

        /// <summary>Whether a direct neighbour column inside the chunk lies higher (the rule's step_foot, DEC-205).</summary>
        private static bool StepFoot(DensityProbeResult r, int x, int z){
            int h = r.Heightmap[x * Size + z];
            if (h < 0) return false;
            return x > 0 && r.Heightmap[(x - 1) * Size + z] > h || x < Size - 1 && r.Heightmap[(x + 1) * Size + z] > h ||
                   z > 0 && r.Heightmap[x * Size + z - 1] > h || z < Size - 1 && r.Heightmap[x * Size + z + 1] > h;
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
            // A cover is a block name too (DEC-178).
            using (SurfaceRuleCompileResult r = Compile(WithDefault("{ 'name': 'a', 'if': 'floor', 'then': { 'block': '@top', 'cover': 'std:NoSuchVoxel' } }")))
                AssertError(r, "block");
            using (SurfaceRuleCompileResult r = Compile(WithDefault("{ 'name': 'a', 'if': 'floor', 'then': { 'block': '@top', 'cover': 'std:SnowLayer' } }"))){
                Assert.IsTrue(r.Succeeded, Errors(r));
                Assert.IsTrue(r.Rules.Actions.Any(a => a.Cover == (ushort)_ids.ByName("std:SnowLayer")), "the cover reaches the compiled action");
            }

            Assert.IsTrue(WithDefault("{ 'name': 'a', 'then': { 'block': '@top', 'cover': 3 } }").Messages.Any(m => m.Severity == LayoutSeverity.Error),
                "a cover that is not a block name");
            using (SurfaceRuleCompileResult r = Compile(WithDefault(
                       "{ 'name': 'a', 'if': { 'noise_threshold': { 'name': 'no_such_output', 'min': 0 } }, 'then': { 'block': '@top' } }")))
                AssertError(r, "output");
        }

        /// <summary>river(min) (§7.2, §9.2) is noise_threshold on the graph's river output, and a non-number is an error.</summary>
        [Test]
        public void RiverCondition_IsANoiseThresholdOnTheRiverOutput(){
            SurfaceRuleSet set = WithDefault("{ 'name': 'a', 'if': { 'river': 0.3 }, 'then': { 'block': '@top' } }");
            Assert.IsFalse(set.Messages.Any(m => m.Severity == LayoutSeverity.Error), string.Join("\n", set.Messages));
            NoiseCondition c = set.Default[0].Condition as NoiseCondition;
            Assert.IsNotNull(c, "river parses into a noise threshold");
            Assert.AreEqual(SurfaceRuleSet.RiverOutput, c.Name);
            Assert.AreEqual(0.3f, c.Min);
            Assert.AreEqual(float.PositiveInfinity, c.Max);
            using (SurfaceRuleCompileResult r = Compile(set))
                Assert.IsTrue(r.Succeeded, "the shipped graph has a river output:\n" + Errors(r));
            Assert.IsTrue(WithDefault("{ 'name': 'a', 'if': { 'river': 'x' }, 'then': { 'block': '@top' } }").Messages.Any(m => m.Severity == LayoutSeverity.Error),
                "river takes a number");
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
        public void Validator_WarnsAboutIceThatCanBeWarmerThanSnow(){
            // A warning since DEC-176: frozen water may meet a snowless shore.
            SurfaceRuleSet set = Parse("{ 'formatVersion': 1, " + Climate.Replace("'iceTemperature': -0.6", "'iceTemperature': -0.45") +
                                       ", 'default': [{ 'name': 'top', 'if': 'floor', 'then': { 'block': '@top' } }] }");
            using SurfaceRuleCompileResult r = Compile(set);
            Assert.IsTrue(r.Succeeded, r.ErrorText());
            Assert.IsTrue(r.Messages.Any(m => m.Severity == LayoutSeverity.Warning && m.Kind == "climate" && m.Text.Contains("W2-CLM-05")), Errors(r));
        }

        [Test]
        public void RiverIce_IsOptional_AndShippedFreezesRiversAtTemperatureLevelZero(){
            // DEC-207: frozen rivers like Minecraft's, a whole sheet where T < −0.45 on the valleys slice; without the
            // setting rivers freeze like the sea.
            using (SurfaceRuleCompileResult shipped = SurfaceRuleCompiler.CompileShipped(_layout, _defs, _graph, _ids.ByName, Allocator.Persistent)){
                Assert.IsTrue(shipped.Succeeded, shipped.ErrorText());
                Assert.GreaterOrEqual(shipped.Rules.RiverIce, 0, "the shipped rules read the river output for the ice");
                Assert.AreEqual(-0.45f, shipped.Rules.RiverIceTemperature, 1e-6f);
                Assert.Greater(shipped.Rules.RiverIceMin, 0f, "a minimum of 0 would freeze the sea too");
            }

            SurfaceRuleSet set = Parse("{ 'formatVersion': 1, " + Climate + ", 'default': [{ 'name': 'top', 'if': 'floor', 'then': { 'block': '@top' } }] }");
            Assert.IsTrue(float.IsNaN(set.Climate.RiverIceTemperature));
            using SurfaceRuleCompileResult r = Compile(set);
            Assert.IsTrue(r.Succeeded, r.ErrorText());
            Assert.AreEqual(-1, r.Rules.RiverIce);
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
                // The floor holds with the wobble too (W2-CLM-04).
                Assert.GreaterOrEqual(rules.TreeLine(t, -8f), (int)rules.TreeLineFloor, $"T {t}, wobble -8");
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
            int snow = 0, aboveLine = 0, halves = 0;
            foreach (DensityProbeResult r in _chunks)
                for (int x = 0; x < Size; x++)
                for (int z = 0; z < Size; z++){
                    int c = x * Size + z, h = r.Heightmap[c];
                    if (h < 0 || h >= Height - 1) continue;
                    Biome.BiomeDef def = _defs[r.Surface[c]];
                    ushort block = SnowBlock(def), top = VoxelAt(r, x, h, z), above = VoxelAt(r, x, h + 1, z);
                    bool dry = h + 1 >= _waterLevel;
                    bool expected = dry && h >= r.SnowLine[c] && def.neverSnows == 0 && block != 0;
                    if (dry && h >= r.SnowLine[c]) aboveLine++;
                    string where = $"chunk {r.Origin} column ({x}, {z}) y {h} {_names[r.Surface[c]]}, snow line {r.SnowLine[c]}, tree line {r.TreeLine[c]}";
                    if (expected){
                        // Rock under a layer on steep slopes, a snow block above the tree line and on steps, else the under block
                        // with a snow layer on top (DEC-178, DEC-180).
                        bool foot = StepFoot(r, x, z);
                        ushort ground = SnowGround(def, h, r.TreeLine[c], r.Rise[c], foot), cover = SnowCover(def, r.Rise[c], foot);
                        Assert.IsTrue(Writes(def, ground, top), $"{where}, rise {r.Rise[c]}, foot {foot}: {Name(top)} instead of {Name(ground)}");
                        if (cover != 0) Assert.AreEqual(cover, above, $"{where}, rise {r.Rise[c]}, foot {foot}: {Name(above)} instead of {Name(cover)}");
                        else Assert.IsTrue(above != SnowLayer && above != SnowHalf, $"{where}, rise {r.Rise[c]}: snow on a steep face");
                        if (cover == SnowHalf) halves++;

                        snow++;
                    }
                    else{
                        if (!IsOwnBlock(def, block)) Assert.AreNotEqual(block, top, $"{where}: snow below the snow line or in a never-snow biome");
                        Assert.IsTrue(above != SnowLayer && above != SnowHalf, $"{where}: a snow layer below the snow line or in a never-snow biome");
                    }
                }

            TestContext.WriteLine($"{snow} snow tops ({halves} step feet under a half layer), {aboveLine} dry tops above the snow line");
            Assert.Greater(snow, 0, "no snow in the sample chunks");
            Assert.Greater(halves, 0, "no step foot took the half snow layer (DEC-205)");
            Assert.IsTrue(Kind("snow").Any(r => Enumerable.Range(0, Size * Size).Any(c => r.Heightmap[c] >= r.SnowLine[c])), "no snow chunk reaches its snow line");
        }

        [Test]
        public void ForestPlainsTaiga_HaveNoGroundPatches(){
            // DEC-205: no moss, leaf litter or coarse dirt patches in these three; Taiga keeps its litter forest floor.
            ushort litter = (ushort)_ids.ByName("std:Litter");
            ushort[] patches = { (ushort)_ids.ByName("std:Moss"), (ushort)_ids.ByName("std:LitterLeaves"), (ushort)_ids.ByName("std:DirtCoarse") };
            int columns = 0;
            foreach (DensityProbeResult r in _chunks)
                for (int x = 0; x < Size; x++)
                for (int z = 0; z < Size; z++){
                    int c = x * Size + z, h = r.Heightmap[c];
                    string name = _names[r.Surface[c]];
                    if (h < 0 || name is not ("Forest" or "Plains" or "Taiga")) continue;
                    columns++;
                    ushort top = VoxelAt(r, x, h, z);
                    string where = $"{name} at chunk {r.Origin} ({x}, {z}) y {h}";
                    Assert.IsFalse(patches.Contains(top), $"{Name(top)} patch in {where}");
                    if (name != "Taiga") Assert.AreNotEqual(litter, top, $"litter patch in {where}");
                }

            TestContext.WriteLine($"{columns} Forest, Plains and Taiga columns");
            Assert.Greater(columns, 0, "no Forest, Plains or Taiga column in the sample chunks");
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
                    if (h + 1 < Height) Assert.IsTrue(VoxelAt(r, x, h + 1, z) != SnowLayer && VoxelAt(r, x, h + 1, z) != SnowHalf, $"a snow layer in {_names[r.Surface[c]]} at chunk {r.Origin} ({x}, {z})");
                    Assert.AreNotEqual(iceId, VoxelAt(r, x, DensityLayout.WetnessY, z), $"ice in {_names[r.Surface[c]]} at chunk {r.Origin} ({x}, {z})");
                }

            TestContext.WriteLine($"{columns} never-snow columns");
            Assert.Greater(columns, 0, "no never-snow biome in the sample chunks");
            Kind("hot");
        }

        /// <summary>A block a rule may write: the block itself, or any band block when it is the biome's banded stone.</summary>
        private static bool Writes(in Biome.BiomeDef def, ushort expected, ushort actual){
            if (expected == actual) return true;
            if (expected == def.rockBlock && (actual == _darkRock || RockVariety.Contains(actual))) return true;
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

        /// <summary>The river_bed rule's threshold on the river output (SurfaceRules.json).</summary>
        internal const float RiverBedMin = 0.3f;

        /// <summary>The blocks the river_bed rule writes in a biome (DEC-195): sand (the beach block), clay, gravel, mud in a swamp.</summary>
        private HashSet<ushort> RiverBed(in Biome.BiomeDef def){
            HashSet<ushort> set = new(){ (ushort)_ids.ByName("std:StoneGravel"), (ushort)_ids.ByName("std:Clay"), (ushort)_ids.ByName("std:Mud") };
            if (def.beachBlock != 0) set.Add(def.beachBlock);
            return set;
        }

        /// <summary>The graph's river output at each column of <paramref name="r" />, [x · 32 + z], sampled at the column.</summary>
        private float[] RiverAt(DensityProbeResult r){
            float2[] points = new float2[Size * Size];
            for (int x = 0; x < Size; x++)
            for (int z = 0; z < Size; z++)
                points[x * Size + z] = new float2(r.Origin.x + x, r.Origin.y + z);
            float[] values = GraphEvaluator.SampleLattice(_graph, _config.WorldGen.Seed, points, out _);
            int slot = _graph.Outputs.First(o => o.Name == SurfaceRuleSet.RiverOutput).LatticeValue;
            float[] river = new float[points.Length];
            Array.Copy(values, slot * points.Length, river, 0, points.Length);
            return river;
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

            foreach (DensityProbeResult r in _chunks){
                float[] river = RiverAt(r);
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
                        bool foot = StepFoot(r, x, z);
                        expected = SnowGround(def, h, r.TreeLine[c], r.Rise[c], foot);
                        ushort cover = SnowCover(def, r.Rise[c], foot);
                        if (cover != 0) Assert.AreEqual(cover, VoxelAt(r, x, h + 1, z), $"{where}: no {Name(cover)} over the snow ground");
                    }
                    // The river bed (DEC-195): mud in a swamp, else sand, clay or gravel by ground_mix. The rule reads the
                    // river output bilinearly from the lattice; the test samples it at the column, so it accepts a bed block
                    // a little either side of the threshold.
                    else if (depth >= 1 && river[c] >= RiverBedMin - 0.1f && RiverBed(def).Contains(top)){
                        rule = "river_bed";
                        expected = top;
                    }
                    else if (depth >= 1 && river[c] >= RiverBedMin + 0.1f){
                        Assert.Fail($"{where}: river {river[c]:0.00}, the river_bed rule wants a bed block, found {Name(top)}");
                        return;
                    }
                    else if (depth >= 1 && depth <= 3 && def.beachBlock != 0){
                        rule = "shallow_floor";
                        expected = def.beachBlock;
                    }
                    else if (depth >= 1){
                        rule = "underwater_top";
                        expected = def.underwaterTopBlock != 0 ? def.underwaterTopBlock : Top(def);
                    }
                    else if (dry && r.Rise[c] >= 3 && h - DensityLayout.WetnessY < 4 && def.rockBlock != 0 && WetWithin(r, x, z, 20) &&
                             Writes(def, def.rockBlock, top)){
                        rule = "shore_cliff";
                        expected = def.rockBlock;
                    }
                    else if (beachPossible && top == def.beachBlock){
                        rule = "beach";
                        expected = def.beachBlock;
                        beachTop = true;
                    }
                    // Steep slopes keep their top (DEC-177): only the layers under it turn to rock.
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

                    // Ground variety (DEC-187, DEC-190) may replace the top, meadow or forest floor of a dry floor below the tree line,
                    // and the floor 4 or more blocks under water.
                    // DEC-204: steep floors of the grass biomes take the biome's rock in patches (ground_steep_rock).
                    if (top != expected && (GroundVariety.Contains(top) || steep && top == def.rockBlock) &&
                        (dry && !alpine && rule is "top" or "meadow" or "forest_floor" || rule == "underwater_top" && depth >= 4))
                        Count(dry ? "ground" : "underwater mix");
                    else{
                        Assert.IsTrue(Writes(def, expected, top), $"{where}: rule {rule} wants {Name(expected)}, found {Name(top)}");
                        Count(rule);
                    }

                    // The four voxels under the floor, while the run goes on.
                    for (int d = 1; d <= 5; d++){
                        int y = h - d;
                        if (!r.IsSolid(x, y, z)) break;
                        ushort under;
                        string underRule;
                        if (dry && r.Rise[c] >= 2 && y >= r.SnowLine[c] && def.neverSnows == 0 && def.rockBlock != 0 && d <= 4){
                            underRule = "snow rock";
                            under = def.rockBlock;
                        }
                        else if (rule == "shore_cliff" && d <= 4){
                            underRule = "shore_cliff";
                            under = def.rockBlock;
                        }
                        else if (dry && beachTop && d <= 2){
                            underRule = "beach";
                            under = def.beachBlock;
                        }
                        else if (rule == "snow" && beachPossible && d <= 2 && VoxelAt(r, x, y, z) == def.beachBlock){
                            underRule = "beach under snow";
                            under = def.beachBlock;
                        }
                        // below_tree_line is a per-voxel test: the rock under an alpine top stops at the tree line.
                        else if (dry && !beachTop && (steep || y >= r.TreeLine[c]) && def.rockBlock != 0 && d <= 4){
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
                        if (d == 1 && underRule == "steep") Count("steep (under)");
                    }
                }
            }

            TestContext.WriteLine(string.Join(", ", seen.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key} {kv.Value}")));
            // No shipped biome sets a MeadowBlock since DEC-204 (Mountain is bare stone); the meadow rule stays for mods.
            foreach (string rule in new[]{ "snow", "shallow_floor", "underwater_top", "beach", "steep (under)", "alpine", "top", "ground" })
                Assert.IsTrue(seen.ContainsKey(rule), $"no column took the {rule} rule in the sample chunks");
            Kind("coast");
            Kind("lowland");
            Kind("alpine");
        }

        #endregion
    }
}
