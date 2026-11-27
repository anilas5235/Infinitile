using System;
using System.Collections.Generic;
using System.Linq;
using Engine.Scripts.VoxelConfig.Data.Generation;
using Engine.Scripts.WorldGen.Biomes;
using Engine.Scripts.WorldGen.Graph;
using Unity.Collections;
using Unity.Mathematics;

namespace Engine.Scripts.WorldGen.Surface{
    /// <summary>The compiled rules (when there are no errors) and every message of the merge and the validator.</summary>
    public sealed class SurfaceRuleCompileResult : IDisposable{
        public SurfaceRules Rules;
        public readonly List<LayoutMessage> Messages = new();

        /// <summary>Per biome id: nodes the full graph reaches (0 without rules), for the reports.</summary>
        public int[] FullNodes = Array.Empty<int>();

        public bool HasErrors => Messages.Any(m => m.Severity == LayoutSeverity.Error);
        public bool Succeeded => Rules.IsCreated && !HasErrors;

        public string ErrorText(){
            return string.Join("\n", Messages.Where(m => m.Severity == LayoutSeverity.Error).Select(m => m.ToString()));
        }

        /// <summary>Hands the rules to the caller (who then owns them); this result no longer disposes them.</summary>
        public SurfaceRules TakeRules(){
            SurfaceRules r = Rules;
            Rules = default;
            return r;
        }

        public void Dispose(){
            Rules.Dispose();
            Rules = default;
        }
    }

    /// <summary>
    ///     Compiles merged surface rules (WorldGen v2 §7.5) into one decision graph per biome and checks them (the rule
    ///     validator, §7.7):
    ///     <list type="bullet">
    ///         <item>
    ///             Per biome the list globalBefore + own or default + globalAfter becomes leaf tests with a true and a
    ///             false target; a sequence's rules fall through to the rule after it. Biome and never_snows tests and
    ///             empty slots resolve here, so a rule whose block a biome lacks is dropped for that biome.
    ///         </item>
    ///         <item>
    ///             The interior plan (§7.6): a voxel deeper than every stone depth bound (R_above, R_below) can only
    ///             see y tests and column tests, so a second graph with every depth test false decides it from the
    ///             column's y thresholds alone. A run-level test left in that graph, or more than
    ///             <see cref="SurfaceRules.MaxInteriorThresholds" /> thresholds, is an error.
    ///         </item>
    ///         <item>
    ///             Errors: unknown biomes, blocks, slots and graph outputs, duplicate rule names, stone depth bounds
    ///             above <see cref="SurfaceRules.MaxDepthBound" />, Warnings: a rule after an
    ///             unconditional rule, a never-snow biome whose box reaches snow temperatures, ice warmer than snow
    ///             (W2-CLM-05, DEC-176).
    ///         </item>
    ///     </list>
    /// </summary>
    public static class SurfaceRuleCompiler{
        private static readonly string[] DensityNames ={ "density:trees", "density:undergrowth", "density:flowers", "density:ground_details" };

        /// <summary>
        ///     Compiles <paramref name="set" /> for the biomes <paramref name="names" /> (ids = indices) with their blocks
        ///     <paramref name="defs" />, reading <paramref name="graph" />'s outputs. <paramref name="blockByName" /> maps a
        ///     voxel's full name to its id (−1 unknown); null leaves named blocks at 0 (no registry: tests, preview), which
        ///     drops their rules. <paramref name="layout" /> (optional) feeds the never-snow check.
        /// </summary>
        public static SurfaceRuleCompileResult Compile(SurfaceRuleSet set, IReadOnlyList<string> names, IReadOnlyList<Biome.BiomeDef> defs,
            CompiledNoiseGraph graph, Func<string, int> blockByName, Allocator allocator, BiomeLayout layout = null){
            SurfaceRuleCompileResult result = new();
            result.Messages.AddRange(set.Messages);
            if (set.HasErrors) return result;
            Compiler c = new(set, graph, blockByName, result.Messages);
            c.Run(names, defs, layout);
            if (result.HasErrors) return result;
            result.Rules = c.ToNative(allocator);
            result.FullNodes = c.FullNodes;
            return result;
        }

        /// <summary>The shipped rules for the biomes of <paramref name="layout" />, with ids from <paramref name="blockByName" />.</summary>
        public static SurfaceRuleCompileResult CompileShipped(BiomeLayout layout, IReadOnlyList<Biome.BiomeDef> defs, CompiledNoiseGraph graph,
            Func<string, int> blockByName, Allocator allocator){
            return Compile(SurfaceRuleSet.Shipped(), layout.Palette(), defs, graph, blockByName, allocator, layout);
        }

        private sealed class Compiler{
            private readonly SurfaceRuleSet _set;
            private readonly CompiledNoiseGraph _graph;
            private readonly Func<string, int> _blockByName;
            private readonly List<LayoutMessage> _messages;
            private readonly HashSet<string> _reported = new();

            private readonly List<RuleNode> _nodes = new();
            private readonly List<string> _nodeRules = new();
            private readonly List<SurfaceAction> _actions = new(){ new SurfaceAction{ Kind = RuleActionKind.Keep } };
            private readonly List<ColumnFlagDef> _flags = new();
            private readonly List<ColumnIntDef> _ints = new();
            private readonly List<RuleOutput> _outputs = new();
            private readonly Dictionary<string, int> _outputIndex = new();
            private readonly List<int> _thresholds = new();
            private BiomeRules[] _biomes = Array.Empty<BiomeRules>();
            public int[] FullNodes = Array.Empty<int>();

            private string _biomeName;
            private Biome.BiomeDef _def;

            /// <summary>R_above of the biome being compiled: the delta_down margin reaches that much further (§7.4, §7.6).</summary>
            private int _reach;
            private bool _interior;
            private bool _needsTemperature;
            private ushort _iceSnowHalfBlock, _iceSnowFullBlock;

            public Compiler(SurfaceRuleSet set, CompiledNoiseGraph graph, Func<string, int> blockByName, List<LayoutMessage> messages){
                _set = set;
                _graph = graph;
                _blockByName = blockByName;
                _messages = messages;
            }

            public void Run(IReadOnlyList<string> names, IReadOnlyList<Biome.BiomeDef> defs, BiomeLayout layout){
                SurfaceClimate climate = _set.Climate;
                CheckNames(names);
                _biomes = new BiomeRules[names.Count];
                FullNodes = new int[names.Count];
                for (int id = 0; id < names.Count; id++){
                    _biomeName = names[id];
                    _def = id < defs.Count ? defs[id] : default;
                    List<SurfaceRule> list = _set.ListFor(_biomeName);
                    CheckList(list, new HashSet<string>());
                    (int above, int below) = DepthBounds(list);
                    _reach = math.max(0, above);
                    if (above > SurfaceRules.MaxDepthBound || below > SurfaceRules.MaxDepthBound)
                        Error(null, $"A stone depth bound of {math.max(above, below)} exceeds {SurfaceRules.MaxDepthBound} (§7.6: a cavern's cover is at most that thick).",
                            "depth");

                    _interior = false;
                    int root = CompileList(list, -1);
                    _interior = true;
                    int interior = CompileList(list, -1);
                    _interior = false;
                    int start = _thresholds.Count;
                    CheckInterior(interior, start);
                    _biomes[id] = new BiomeRules{
                        Root = root, Interior = interior, RAbove = above, RBelow = below, ThresholdStart = start, ThresholdCount = _thresholds.Count - start,
                        HasRules = 1
                    };
                    FullNodes[id] = Reachable(root).Count;
                }

                _biomeName = null;
                if (_flags.Count > SurfaceRules.MaxColumnFlags) Error(null, $"The rules need {_flags.Count} column tests; at most {SurfaceRules.MaxColumnFlags} are allowed.", "limit");
                if (_ints.Count > SurfaceRules.MaxColumnInts) Error(null, $"The rules need {_ints.Count} column values; at most {SurfaceRules.MaxColumnInts} are allowed.", "limit");
                if (_outputs.Count > SurfaceRules.MaxOutputs) Error(null, $"The rules read {_outputs.Count} graph outputs; at most {SurfaceRules.MaxOutputs} are allowed.", "limit");
                if (climate.NearWaterDistance < 0 || climate.NearWaterDistance > 16)
                    Error(null, $"nearWaterDistance {climate.NearWaterDistance} is outside 0 … 16 (the biome padding).", "climate");
                // River ice reads the river band at the wet probes within nearWaterDistance, which lie on a 4-block grid
                // (AD-48): below 4 most columns have no probe in reach, and frozen rivers and their banks would vanish.
                if (!float.IsNaN(climate.RiverIceTemperature) && climate.NearWaterDistance < 4)
                    Error(null, $"nearWaterDistance {climate.NearWaterDistance} is below 4: river ice needs a wet probe in reach (the probes lie every 4 blocks).", "climate");
                if (!(climate.Lapse > 0f)) Error(null, "The lapse rate must be positive.", "climate");
                if (_set.Strata.Period <= 0 || _set.Strata.Thickness <= 0 || _set.Strata.Thickness > _set.Strata.Period)
                    Error(null, "Strata need 0 < thickness ≤ period.", "climate");

                // Ice is always on: its wobble must exist when named, and T is needed.
                _needsTemperature = true;
                Output(climate.IceWobble);
                if (!float.IsNaN(climate.RiverIceTemperature)) Output(GraphOutputs.River);
                _iceSnowHalfBlock = IceSnowBlock(climate.IceSnowHalf, "snow_half", "iceSnowHalf");
                _iceSnowFullBlock = IceSnowBlock(climate.IceSnowFull, "snow_layer", "iceSnowFull");
                if (_iceSnowHalfBlock != 0 || _iceSnowFullBlock != 0) Output(climate.SnowWobble);
                if (_iceSnowHalfBlock != 0 && _iceSnowFullBlock != 0 && climate.IceSnowFull < climate.IceSnowHalf)
                    Error(null, "iceSnowFull must not be smaller than iceSnowHalf: the full layer lies inside the half one.", "climate");
                float iceWobble = WobbleBound(climate.IceWobble), snowWobble = WobbleBound(climate.SnowWobble);
                if (climate.IceTemperature + iceWobble > climate.SnowTemperature - snowWobble)
                    // A warning since V4 (DEC-176): the snow refit (DEC-175) would otherwise push the ice into rarity.
                    Warning("W2-CLM-05", $"W2-CLM-05: ice ({SurfaceRuleSet.Format(climate.IceTemperature)} ± {SurfaceRuleSet.Format(iceWobble)}) can be warmer than snow " +
                                  $"({SurfaceRuleSet.Format(climate.SnowTemperature)} ± {SurfaceRuleSet.Format(snowWobble)}): water may freeze where the shore has no snow.",
                        "climate");
                if (_needsTemperature && TemperatureSlot() < 0)
                    Error(null, $"The rules read the temperature, but the graph has no lattice output \"{GraphOutputs.Temperature}\".", "output");
                if (PreliminarySlot() < 0) Error(null, $"The rules need the graph's \"{GraphOutputs.PreliminarySurface}\".", "output");
                if (layout != null) CheckNeverSnow(layout, snowWobble);
            }

            // --- Compilation --------------------------------------------------------------------------------------------

            private int CompileList(List<SurfaceRule> rules, int fail){
                int next = fail;
                for (int i = rules.Count - 1; i >= 0; i--) next = CompileRule(rules[i], next);
                return next;
            }

            private int CompileRule(SurfaceRule rule, int fail){
                int target;
                switch (rule.Action){
                    case SequenceAction s:
                        target = CompileList(s.Rules, fail);
                        break;
                    default:
                        int action = Action(rule);
                        if (action < 0) return fail; // the biome lacks the block: the rule is dropped
                        target = -action - 1;
                        break;
                }

                return rule.Condition == null ? target : Cond(rule.Condition, target, fail, rule.Name);
            }

            private int Cond(RuleCondition c, int t, int f, string rule){
                if (t == f) return t;
                switch (c){
                    case NotCondition n:
                        return Cond(n.Inner, f, t, rule);
                    case ListCondition{ Any: false } all: {
                        int x = t;
                        for (int i = all.Items.Count - 1; i >= 0; i--) x = Cond(all.Items[i], x, f, rule);
                        return x;
                    }
                    case ListCondition any: {
                        int x = f;
                        for (int i = any.Items.Count - 1; i >= 0; i--) x = Cond(any.Items[i], t, x, rule);
                        return x;
                    }
                    case BiomeCondition b:
                        return b.Names.Contains(_biomeName) ? t : f;
                    case NeverSnowsCondition:
                        return _def.neverSnows != 0 ? t : f;
                    case DepthCondition d: {
                        if (_interior) return f;
                        int limit = d.Noise != null
                            ? Int(new ColumnIntDef{ Kind = ColumnIntKind.DepthNoise, A = d.Depth, Output = Output(d.Noise, rule) })
                            : Int(new ColumnIntDef{ Kind = ColumnIntKind.Const, A = d.Depth });
                        return Leaf(d.Above ? RuleNodeKind.DepthAbove : RuleNodeKind.DepthBelow, limit, 0, t, f, rule);
                    }
                    case YAboveCondition y:
                        return Leaf(RuleNodeKind.YAtLeast, Int(new ColumnIntDef{ Kind = ColumnIntKind.YAbove, A = y.Y, Output = Output(y.Wobble, rule) }), 0, t, f, rule);
                    case AbovePreliminaryCondition p:
                        return Leaf(RuleNodeKind.YAtLeast,
                            Int(new ColumnIntDef{ Kind = ColumnIntKind.AbovePreliminary, A = p.DeltaDown ? _reach : p.Margin, B = p.DeltaDown ? 1f : 0f, Output = -1 }), 0, t, f, rule);
                    case WaterDepthCondition w:
                        return Leaf(RuleNodeKind.WaterDepth, w.Min, w.Max, t, f, rule);
                    case TemperatureCondition temperature: {
                        _needsTemperature = true;
                        float limit = temperature.Snow ? _set.Climate.SnowTemperature : temperature.Below;
                        int wobble = Output(temperature.Snow ? _set.Climate.SnowWobble : temperature.Wobble, rule);
                        return Leaf(RuleNodeKind.YAtLeast, Int(new ColumnIntDef{ Kind = ColumnIntKind.Cold, A = limit, Output = wobble }), 0, t, f, rule);
                    }
                    case TreeLineCondition:
                        // below_tree_line ⟺ not (y ≥ tree line)
                        _needsTemperature = true;
                        return Leaf(RuleNodeKind.YAtLeast, Int(new ColumnIntDef{ Kind = ColumnIntKind.TreeLine, Output = Output(_set.Climate.TreeLineWobble, rule) }), 0,
                            f, t, rule);
                    case SteepCondition s:
                        return Leaf(RuleNodeKind.Flag, Flag(new ColumnFlagDef{ Kind = ColumnFlagKind.Steep, A = s.Rise }), 0, t, f, rule);
                    case StepFootCondition s:
                        return Leaf(RuleNodeKind.Flag, Flag(new ColumnFlagDef{ Kind = ColumnFlagKind.StepFoot, A = s.Rise }), 0, t, f, rule);
                    case NearWaterCondition:
                        return Leaf(RuleNodeKind.Flag, Flag(new ColumnFlagDef{ Kind = ColumnFlagKind.NearWater, A = _set.Climate.NearWaterDistance }), 0, t, f, rule);
                    case SkyOpenCondition:
                        return Leaf(RuleNodeKind.SkyOpen, 0, 0, t, f, rule);
                    case FrozenRiverCondition:
                        // Without river ice (riverIceTemperature unset) no column is in a frozen river.
                        if (float.IsNaN(_set.Climate.RiverIceTemperature)) return f;
                        _needsTemperature = true;
                        Output(GraphOutputs.River);
                        return Leaf(RuleNodeKind.Flag, Flag(new ColumnFlagDef{ Kind = ColumnFlagKind.FrozenRiver }), 0, t, f, rule);
                    case NoiseCondition n: {
                        int density = Array.IndexOf(DensityNames, n.Name);
                        ColumnFlagDef def = density >= 0
                            ? new ColumnFlagDef{ Kind = ColumnFlagKind.Density, A = n.Min, B = n.Max, Output = density }
                            : new ColumnFlagDef{ Kind = ColumnFlagKind.Noise, A = n.Min, B = n.Max, Output = Output(n.Name, rule) };
                        return Leaf(RuleNodeKind.Flag, Flag(def), 0, t, f, rule);
                    }
                    case DenseForestCondition:
                        if (_def.forestFloorBlock == 0) return f;
                        return Leaf(RuleNodeKind.Flag,
                            Flag(new ColumnFlagDef{ Kind = ColumnFlagKind.Density, A = _def.forestFloorDensity, B = float.PositiveInfinity, Output = 0 }), 0, t, f, rule);
                    case TopAboveWaterCondition top: {
                        if (top.BeachBand){
                            if (_def.beachHeight == 0) return f;
                            int bound = Int(new ColumnIntDef{ Kind = ColumnIntKind.BeachBound, A = _def.beachHeight, Output = Output(_set.Climate.BeachShape, rule) });
                            return Leaf(RuleNodeKind.TopAboveWater, 0, bound, t, f, rule);
                        }

                        return Leaf(RuleNodeKind.TopAboveWater, top.Min, Int(new ColumnIntDef{ Kind = ColumnIntKind.Const, A = top.Max }), t, f, rule);
                    }
                    default:
                        Error(rule, $"Rule \"{rule}\": the condition {c.GetType().Name} is not supported.", "condition");
                        return f;
                }
            }

            private int Leaf(byte kind, int a, int b, int t, int f, string rule){
                if (t == f) return t;
                _nodes.Add(new RuleNode{ Kind = kind, A = a, B = b, OnTrue = t, OnFalse = f });
                _nodeRules.Add(rule);
                return _nodes.Count - 1;
            }

            /// <summary>The rule's action for the current biome, or −1 when the biome lacks its block.</summary>
            private int Action(SurfaceRule rule){
                switch (rule.Action){
                    case BlockAction b: {
                        ushort id = Block(b.Block, rule.Name);
                        if (id == 0) return -1;
                        if (b.Cover != null){
                            ushort cover = Block(b.Cover, rule.Name);
                            if (cover == 0) return -1;
                            return AddAction(new SurfaceAction{ Kind = RuleActionKind.Block, Block = id, Cover = cover });
                        }

                        // Strata replace the biome's stone block wherever a rule puts it (v1 did so on every stone voxel).
                        if (id == _def.stoneBlock && _def.strataBlock != 0)
                            return AddAction(new SurfaceAction{
                                Kind = RuleActionKind.Bands, Block = id, Period = _set.Strata.Period, Thickness = _set.Strata.Thickness,
                                Shift = _set.Strata.Shift != null ? Int(new ColumnIntDef{ Kind = ColumnIntKind.DepthNoise, A = 0, Output = Output(_set.Strata.Shift, rule.Name) }) : -1,
                                P0 = _def.strataBlock, P1 = _def.strataBlock1, P2 = _def.strataBlock2, P3 = _def.strataBlock3, PaletteCount = math.max(1, (int)_def.strataCount)
                            });
                        return AddAction(new SurfaceAction{ Kind = RuleActionKind.Block, Block = id });
                    }
                    case BandsAction bands: {
                        ushort between = Block(bands.Else ?? "@stone", rule.Name);
                        if (between == 0) return -1;
                        ushort[] palette = new ushort[4];
                        int count = math.min(4, bands.Palette.Count);
                        for (int i = 0; i < count; i++){
                            palette[i] = Block(bands.Palette[i], rule.Name);
                            if (palette[i] == 0) return -1;
                        }

                        if (bands.Palette.Count > 4) Error(rule.Name, $"Rule \"{rule.Name}\": a band palette holds at most 4 blocks.", "action");
                        if (bands.Period <= 0 || bands.Thickness <= 0) Error(rule.Name, $"Rule \"{rule.Name}\": bands need a positive period and thickness.", "action");
                        return AddAction(new SurfaceAction{
                            Kind = RuleActionKind.Bands, Block = between, Period = bands.Period, Thickness = bands.Thickness,
                            Shift = bands.Shift != null ? Int(new ColumnIntDef{ Kind = ColumnIntKind.DepthNoise, A = 0, Output = Output(bands.Shift, rule.Name) }) : -1,
                            P0 = palette[0], P1 = palette[1], P2 = palette[2], P3 = palette[3], PaletteCount = math.max(1, count)
                        });
                    }
                    default:
                        return -1;
                }
            }

            private int AddAction(SurfaceAction a){
                for (int i = 0; i < _actions.Count; i++)
                    if (_actions[i].Equals(a))
                        return i;
                _actions.Add(a);
                return _actions.Count - 1;
            }

            /// <summary>A slot of the current biome or a named voxel; 0 when empty.</summary>
            private ushort Block(string block, string rule){
                if (block.StartsWith("@")){
                    ushort stone = _def.stoneBlock;
                    ushort under = _def.underBlock != 0 ? _def.underBlock : stone;
                    ushort top = _def.topBlock != 0 ? _def.topBlock : under;
                    switch (block){
                        case "@stone": return stone;
                        case "@under": return under;
                        case "@top": return top;
                        case "@underwater_top": return _def.underwaterTopBlock != 0 ? _def.underwaterTopBlock : top;
                        case "@beach": return _def.beachBlock;
                        case "@meadow": return _def.meadowGround != 0 ? _def.meadowBlock : (ushort)0;
                        case "@forest_floor": return _def.forestFloorBlock;
                        case "@rock": return _def.rockBlock;
                        case "@strata": return _def.strataBlock;
                        case "@snow":
                            if (_def.snowTopBlock != 0) return _def.snowTopBlock;
                            return _set.Blocks.TryGetValue("snow", out string snow) ? Named(snow, rule) : (ushort)0;
                        case "@snow_layer":
                            return _set.Blocks.TryGetValue("snow_layer", out string layer) ? Named(layer, rule) : (ushort)0;
                        default:
                            // Any other slot names a voxel in the rule file's "blocks" table (shipped: @dark_rock).
                            if (_set.Blocks.TryGetValue(block.Substring(1), out string named)) return Named(named, rule);
                            Error(rule, $"Rule \"{rule}\": unknown block slot \"{block}\" (@top, @under, @stone, @underwater_top, @beach, @meadow, @forest_floor, @rock, @snow, @snow_layer, @strata, or a name from \"blocks\").",
                                "block");
                            return 0;
                    }
                }

                return Named(block, rule);
            }

            private ushort Named(string name, string rule){
                if (_blockByName == null) return 0;
                int id = _blockByName(name);
                if (id > 0 && id <= ushort.MaxValue) return (ushort)id;
                Error(rule, $"Rule \"{rule}\": unknown block \"{name}\".", "block");
                return 0;
            }

            private int Flag(ColumnFlagDef d){
                for (int i = 0; i < _flags.Count; i++)
                    if (_flags[i].Equals(d))
                        return i;
                _flags.Add(d);
                return _flags.Count - 1;
            }

            private int Int(ColumnIntDef d){
                for (int i = 0; i < _ints.Count; i++)
                    if (_ints[i].Equals(d))
                        return i;
                _ints.Add(d);
                return _ints.Count - 1;
            }

            /// <summary>Index of a 2D graph output among the rules' outputs; −1 for no name, or (with an error) an unusable one.</summary>
            private int Output(string name, string rule = null){
                if (name == null) return -1;
                if (_outputIndex.TryGetValue(name, out int known)) return known;
                GraphOutputInfo o = _graph?.Output(name);
                if (o == null || o.Is3D || o.BoundOnly || o.LatticeValue < 0){
                    Error(rule, $"{(rule != null ? $"Rule \"{rule}\": " : "")}\"{name}\" is no 2D lattice output of the noise graph.", "output");
                    _outputIndex[name] = -1;
                    return -1;
                }

                _outputs.Add(new RuleOutput{ Column = o.PerColumn ? o.ColumnOutput : -1, Lattice = o.LatticeValue });
                _outputIndex[name] = _outputs.Count - 1;
                return _outputs.Count - 1;
            }

            private float WobbleBound(string name){
                GraphOutputInfo o = name != null ? _graph?.Output(name) : null;
                return o == null ? 0f : math.max(math.abs(o.Bounds.Min), math.abs(o.Bounds.Max));
            }

            private int TemperatureSlot(){
                GraphOutputInfo o = _graph?.Output(GraphOutputs.Temperature);
                return o is{ BoundOnly: false, Is3D: false } ? o.LatticeValue : -1;
            }

            private int PreliminarySlot(){
                GraphOutputInfo o = _graph?.Output(GraphOutputs.PreliminarySurface);
                return o is{ BoundOnly: false, Is3D: false } ? o.LatticeValue : -1;
            }

            // --- Checks -------------------------------------------------------------------------------------------------

            private void CheckNames(IReadOnlyList<string> names){
                void Visit(List<SurfaceRule> list){
                    foreach (SurfaceRule r in list){
                        VisitCondition(r.Condition, r.Name);
                        if (r.Action is SequenceAction s) Visit(s.Rules);
                    }
                }

                void VisitCondition(RuleCondition c, string rule){
                    switch (c){
                        case NotCondition n:
                            VisitCondition(n.Inner, rule);
                            break;
                        case ListCondition l:
                            foreach (RuleCondition i in l.Items) VisitCondition(i, rule);
                            break;
                        case BiomeCondition b:
                            foreach (string name in b.Names)
                                if (!names.Contains(name))
                                    Error(rule, $"Rule \"{rule}\": unknown biome \"{name}\".", "biome");
                            break;
                    }
                }

                Visit(_set.GlobalBefore);
                Visit(_set.Default);
                Visit(_set.GlobalAfter);
                foreach (KeyValuePair<string, List<SurfaceRule>> pair in _set.Biomes){
                    if (!names.Contains(pair.Key)) Error(null, $"A rule list for the unknown biome \"{pair.Key}\".", "biome");
                    Visit(pair.Value);
                }
            }

            /// <summary>Duplicate names and rules after an unconditional rule, recursively.</summary>
            private void CheckList(List<SurfaceRule> list, HashSet<string> seen){
                for (int i = 0; i < list.Count; i++){
                    SurfaceRule r = list[i];
                    if (!seen.Add(r.Name)) Error(r.Name, $"The rule name \"{r.Name}\" appears twice in the list.", "duplicate");
                    if (r.Condition == null && r.Action is not SequenceAction && i + 1 < list.Count)
                        Warning($"after:{r.Name}:{list[i + 1].Name}", $"Rule \"{list[i + 1].Name}\" follows the unconditional rule \"{r.Name}\" and is reached only where that rule's block is empty.",
                            "unreachable");
                    if (r.Action is SequenceAction s) CheckList(s.Rules, seen);
                }
            }

            /// <summary>R_above and R_below: the largest bound of any stone depth test in the list (with the noise's bound), −1 for none.</summary>
            private (int above, int below) DepthBounds(List<SurfaceRule> list){
                int above = -1, below = -1;

                void VisitCondition(RuleCondition c){
                    switch (c){
                        case NotCondition n:
                            VisitCondition(n.Inner);
                            break;
                        case ListCondition l:
                            foreach (RuleCondition i in l.Items) VisitCondition(i);
                            break;
                        case DepthCondition d: {
                            int bound = d.Depth;
                            if (d.Noise != null){
                                GraphOutputInfo o = _graph?.Output(d.Noise);
                                if (o != null) bound += (int)math.floor(o.Bounds.Max);
                            }

                            if (d.Above) above = math.max(above, bound);
                            else below = math.max(below, bound);
                            break;
                        }
                    }
                }

                void Visit(List<SurfaceRule> rules){
                    foreach (SurfaceRule r in rules){
                        VisitCondition(r.Condition);
                        if (r.Action is SequenceAction s) Visit(s.Rules);
                    }
                }

                Visit(list);
                return (above, below);
            }

            /// <summary>The interior graph may hold column tests and y tests only, and few distinct thresholds (§7.6).</summary>
            private void CheckInterior(int root, int thresholdStart){
                foreach (int n in Reachable(root)){
                    RuleNode node = _nodes[n];
                    if (RuleNodeKind.IsRunLevel(node.Kind) || node.Kind == RuleNodeKind.DepthAbove || node.Kind == RuleNodeKind.DepthBelow){
                        string what = node.Kind switch{
                            RuleNodeKind.WaterDepth => "water_depth / dry",
                            RuleNodeKind.SkyOpen => "sky_open",
                            RuleNodeKind.TopAboveWater => "top_above_water / beach_band",
                            _ => "a stone depth"
                        };
                        Error(_nodeRules[n], $"Rule \"{_nodeRules[n]}\" tests {what} on voxels deeper than every stone depth bound; give it a stone_depth condition first (§7.6).",
                            "interior");
                        continue;
                    }

                    if (node.Kind == RuleNodeKind.YAtLeast && !_thresholds.Skip(thresholdStart).Contains(node.A)) _thresholds.Add(node.A);
                }

                int count = _thresholds.Count - thresholdStart;
                if (count > SurfaceRules.MaxInteriorThresholds)
                    Error(null, $"The interior plan needs {count} y thresholds; at most {SurfaceRules.MaxInteriorThresholds} are allowed (§7.6).", "interior");
            }

            private List<int> Reachable(int root){
                List<int> found = new();
                HashSet<int> seen = new();
                Stack<int> stack = new();
                if (root >= 0) stack.Push(root);
                while (stack.Count > 0){
                    int n = stack.Pop();
                    if (!seen.Add(n)) continue;
                    found.Add(n);
                    RuleNode node = _nodes[n];
                    if (node.OnTrue >= 0) stack.Push(node.OnTrue);
                    if (node.OnFalse >= 0) stack.Push(node.OnFalse);
                }

                return found;
            }

            /// <summary>A never-snow biome whose coldest box edge would take snow below the highest surface: its snow stops at its border.</summary>
            private void CheckNeverSnow(BiomeLayout layout, float snowWobble){
                GraphOutputInfo surface = _graph?.Output(GraphOutputs.PreliminarySurface);
                GraphOutputInfo temperature = _graph?.Output(GraphOutputs.Temperature);
                if (surface == null || temperature == null) return;
                int top = (int)math.min(GraphLimits.WorldHeight - 1, math.floor(surface.Bounds.Max));
                SurfaceRules probe = Climate();
                foreach (LayoutBiome b in layout.Biomes){
                    if (b.Asset == null || !b.Asset.NeverSnows) continue;
                    float coldest = float.PositiveInfinity;
                    foreach (LayoutBox box in layout.Boxes)
                        if (box.Biome == b.Id)
                            coldest = math.min(coldest, box.Min[0]);
                    if (float.IsPositiveInfinity(coldest)) continue;
                    if (float.IsNegativeInfinity(coldest)) coldest = temperature.Bounds.Min;
                    int line = probe.ColdThreshold(coldest, snowWobble, _set.Climate.SnowTemperature);
                    if (line <= top)
                        Messages(LayoutSeverity.Warning, b.Name,
                            $"{b.Name} never snows, but its box reaches T {SurfaceRuleSet.Format(coldest)}, where snow lies from y {line} (surfaces reach y {top}): its snow stops at its border.",
                            "never-snow");
                }
            }

            /// <summary>The voxel of a snow layer on the sea ice: the rule file's block <paramref name="block" />, 0 when the margin is unset.</summary>
            private ushort IceSnowBlock(float margin, string block, string field){
                if (float.IsNaN(margin)) return 0;
                if (!(margin >= 0f)) Error(null, $"{field} must be a margin of at least 0 below iceTemperature.", "climate");
                if (_set.Blocks.TryGetValue(block, out string name)) return Named(name, field);
                Error(null, $"{field} needs the block \"{block}\" in \"blocks\".", "climate");
                return 0;
            }

            private SurfaceRules Climate(){
                SurfaceClimate c = _set.Climate;
                return new SurfaceRules{
                    Lapse = c.Lapse, LapseFrom = c.LapseFrom, SnowTemperature = c.SnowTemperature, IceTemperature = c.IceTemperature, TreeTemperature = c.TreeTemperature,
                    TreeLineFloor = c.TreeLineFloor
                };
            }

            private void Error(string rule, string text, string kind){
                Messages(LayoutSeverity.Error, _biomeName, text, kind, rule);
            }

            private void Warning(string key, string text, string kind){
                if (!_reported.Add(key)) return;
                Messages(LayoutSeverity.Warning, null, text, kind);
            }

            private void Messages(LayoutSeverity severity, string biome, string text, string kind, string rule = null){
                // A rule's error is the same for every biome that shares the list: report it once.
                string key = $"{severity}|{text}";
                if (!_reported.Add(key)) return;
                _messages.Add(new LayoutMessage{ Severity = severity, Source = null, File = SurfaceRuleSet.FileName, Biome = rule != null ? null : biome, Text = text, Kind = kind });
            }

            // --- Native ------------------------------------------------------------------------------------------------

            public SurfaceRules ToNative(Allocator allocator){
                SurfaceRules r = Climate();
                r.Biomes = new NativeArray<BiomeRules>(math.max(1, _biomes.Length), allocator);
                for (int i = 0; i < _biomes.Length; i++) r.Biomes[i] = _biomes[i];
                r.Nodes = ToArray(_nodes, allocator);
                r.Actions = ToArray(_actions, allocator);
                r.Flags = ToArray(_flags, allocator);
                r.Ints = ToArray(_ints, allocator);
                r.Outputs = ToArray(_outputs, allocator);
                r.Thresholds = ToArray(_thresholds, allocator);
                r.TemperatureSlot = TemperatureSlot();
                r.PreliminarySurfaceSlot = PreliminarySlot();
                r.IceWobble = Output(_set.Climate.IceWobble);
                bool riverIce = !float.IsNaN(_set.Climate.RiverIceTemperature);
                r.RiverIce = riverIce ? Output(GraphOutputs.River) : -1;
                r.RiverIceMin = _set.Climate.RiverIceMin;
                r.RiverIceTemperature = riverIce ? _set.Climate.RiverIceTemperature : 0f;
                r.RiverIceReach = _set.Climate.NearWaterDistance;
                r.IceSnowHalf = _iceSnowHalfBlock != 0 ? _set.Climate.IceSnowHalf : 0f;
                r.IceSnowFull = _iceSnowFullBlock != 0 ? _set.Climate.IceSnowFull : 0f;
                r.IceSnowHalfBlock = _iceSnowHalfBlock;
                r.IceSnowFullBlock = _iceSnowFullBlock;
                r.SnowWobble = Output(_set.Climate.SnowWobble);
                r.TreeLineWobble = Output(_set.Climate.TreeLineWobble);
                r.Ice = 1;
                return r;
            }

            private static NativeArray<T> ToArray<T>(List<T> list, Allocator allocator) where T : struct{
                NativeArray<T> a = new(math.max(1, list.Count), allocator);
                for (int i = 0; i < list.Count; i++) a[i] = list[i];
                return a;
            }
        }
    }
}
