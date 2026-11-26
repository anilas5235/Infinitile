using System;
using System.Collections.Generic;
using System.Globalization;
using Engine.Scripts.WorldGen.Biomes;
using Engine.Scripts.WorldGen.Settings;
using UnityEngine;

namespace Engine.Scripts.WorldGen.Surface{
    // ---------------------------------------------------------------------------------------------------------------
    // Conditions (§7.2). Each is a test of one voxel of a solid run; the compiler turns them into decision nodes.
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>A condition of a surface rule (WorldGen v2 §7.2).</summary>
    public abstract class RuleCondition{
        public abstract RuleCondition Clone();
    }

    public sealed class NotCondition : RuleCondition{
        public RuleCondition Inner;

        public override RuleCondition Clone(){
            return new NotCondition{ Inner = Inner.Clone() };
        }
    }

    /// <summary>All (<see cref="Any" /> false) or any of the items, tested in order.</summary>
    public sealed class ListCondition : RuleCondition{
        public bool Any;
        public List<RuleCondition> Items = new();

        public override RuleCondition Clone(){
            ListCondition c = new(){ Any = Any };
            foreach (RuleCondition i in Items) c.Items.Add(i.Clone());
            return c;
        }
    }

    /// <summary>The voxel's surface biome is one of <see cref="Names" /> (resolved when the rules compile).</summary>
    public sealed class BiomeCondition : RuleCondition{
        public List<string> Names = new();

        public override RuleCondition Clone(){
            return new BiomeCondition{ Names = new List<string>(Names) };
        }
    }

    /// <summary>The surface biome never takes snow or ice (<c>Biome.NeverSnows</c>; resolved when the rules compile).</summary>
    public sealed class NeverSnowsCondition : RuleCondition{
        public override RuleCondition Clone(){
            return new NeverSnowsCondition();
        }
    }

    /// <summary>
    ///     stone_depth_above (<see cref="Above" />) or stone_depth_below: the voxel lies at most <see cref="Depth" />
    ///     (plus floor(<see cref="Noise" />) when set) voxels below the top of its solid run, or above its bottom.
    /// </summary>
    public sealed class DepthCondition : RuleCondition{
        public bool Above;
        public int Depth;
        public string Noise;

        public override RuleCondition Clone(){
            return (DepthCondition)MemberwiseClone();
        }
    }

    /// <summary>y ≥ floor(<see cref="Y" /> + <see cref="Wobble" />).</summary>
    public sealed class YAboveCondition : RuleCondition{
        public float Y;
        public string Wobble;

        public override RuleCondition Clone(){
            return (YAboveCondition)MemberwiseClone();
        }
    }

    /// <summary>
    ///     y ≥ floor(preliminary_surface − Δdown − R_above) (<see cref="DeltaDown" />: the surface's lowest possible y less
    ///     the rules' largest stone depth bound, §7.6) or y ≥ floor(preliminary_surface − <see cref="Margin" />).
    /// </summary>
    public sealed class AbovePreliminaryCondition : RuleCondition{
        public bool DeltaDown;
        public float Margin;

        public override RuleCondition Clone(){
            return (AbovePreliminaryCondition)MemberwiseClone();
        }
    }

    /// <summary>The open voxels below the water level directly above the voxel's run number <see cref="Min" /> … <see cref="Max" />.</summary>
    public sealed class WaterDepthCondition : RuleCondition{
        public int Min, Max;

        public override RuleCondition Clone(){
            return (WaterDepthCondition)MemberwiseClone();
        }
    }

    /// <summary>
    ///     The effective temperature T − L · max(0, y − lapseFrom) + wobble lies below the snow temperature
    ///     (<see cref="Snow" />, with the climate's snow wobble) or below <see cref="Below" /> (with <see cref="Wobble" />).
    /// </summary>
    public sealed class TemperatureCondition : RuleCondition{
        public bool Snow;
        public float Below;
        public string Wobble;

        public override RuleCondition Clone(){
            return (TemperatureCondition)MemberwiseClone();
        }
    }

    /// <summary>below_tree_line: y &lt; ceil(max(treeLineFloor, y at T_tree) + wobble) (§8.3).</summary>
    public sealed class TreeLineCondition : RuleCondition{
        public override RuleCondition Clone(){
            return new TreeLineCondition();
        }
    }

    /// <summary>steep(rise): the chunk heightmap rises at least <see cref="Rise" /> across the column along x or z.</summary>
    public sealed class SteepCondition : RuleCondition{
        public int Rise;

        public override RuleCondition Clone(){
            return (SteepCondition)MemberwiseClone();
        }
    }

    /// <summary>
    ///     step_foot(rise): a direct neighbour column (x ± 1 or z ± 1, inside the chunk) lies at least <see cref="Rise" />
    ///     higher, so the column is the foot of a step (DEC-205: a thicker snow layer smooths the step).
    /// </summary>
    public sealed class StepFootCondition : RuleCondition{
        public int Rise;

        public override RuleCondition Clone(){
            return (StepFootCondition)MemberwiseClone();
        }
    }

    /// <summary>near_water: a water probe (columns at multiples of 4, wet one block below the water level) lies within the climate's distance.</summary>
    public sealed class NearWaterCondition : RuleCondition{
        public override RuleCondition Clone(){
            return new NearWaterCondition();
        }
    }

    /// <summary>
    ///     frozen_river: the column lies in a frozen river's band (§8.3, DEC-229): the river output is at least
    ///     riverIceMin and the water surface is cold enough for river ice. False without river ice.
    /// </summary>
    public sealed class FrozenRiverCondition : RuleCondition{
        public override RuleCondition Clone(){
            return new FrozenRiverCondition();
        }
    }

    /// <summary>sky_open: no terrain lies above the voxel's run (it is the column's top run).</summary>
    public sealed class SkyOpenCondition : RuleCondition{
        public override RuleCondition Clone(){
            return new SkyOpenCondition();
        }
    }

    /// <summary>
    ///     noise_threshold: <see cref="Min" /> ≤ value &lt; <see cref="Max" /> for a 2D graph output, or a flora density
    ///     factor ("density:trees", "density:undergrowth", "density:flowers", "density:ground_details").
    /// </summary>
    public sealed class NoiseCondition : RuleCondition{
        public string Name;
        public float Min = float.NegativeInfinity, Max = float.PositiveInfinity;

        public override RuleCondition Clone(){
            return (NoiseCondition)MemberwiseClone();
        }
    }

    /// <summary>dense_forest: the tree density factor reaches the surface biome's ForestFloorDensity (v1's forest floor).</summary>
    public sealed class DenseForestCondition : RuleCondition{
        public override RuleCondition Clone(){
            return new DenseForestCondition();
        }
    }

    /// <summary>
    ///     The top of the voxel's run lies <see cref="Min" /> … <see cref="Max" /> − 1 blocks above the water surface
    ///     (y = water level − 1); <see cref="BeachBand" /> takes the bound from the beach shape times the biome's
    ///     beach height, as v1's beach band did.
    /// </summary>
    public sealed class TopAboveWaterCondition : RuleCondition{
        public int Min, Max;
        public bool BeachBand;

        public override RuleCondition Clone(){
            return (TopAboveWaterCondition)MemberwiseClone();
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Actions (§7.3).
    // ---------------------------------------------------------------------------------------------------------------

    public abstract class RuleAction{
        public abstract RuleAction Clone();
    }

    /// <summary>
    ///     A block: a biome slot ("@top", …) or a voxel's full name ("std:Stone"). <see cref="Cover" /> (optional) goes
    ///     on the air voxel above a run's top, e.g. a snow layer on the ground (DEC-178).
    /// </summary>
    public sealed class BlockAction : RuleAction{
        public string Block, Cover;

        public override RuleAction Clone(){
            return new BlockAction{ Block = Block, Cover = Cover };
        }
    }

    /// <summary>Strata bands: <see cref="Palette" /> in bands of <see cref="Thickness" /> every <see cref="Period" /> blocks, else <see cref="Else" />.</summary>
    public sealed class BandsAction : RuleAction{
        public int Period, Thickness;
        public string Shift;
        public List<string> Palette = new();
        public string Else;

        public override RuleAction Clone(){
            BandsAction a = (BandsAction)MemberwiseClone();
            a.Palette = new List<string>(Palette);
            return a;
        }
    }

    /// <summary>A sequence: its rules in order; when none matches, the list goes on after the sequence.</summary>
    public sealed class SequenceAction : RuleAction{
        public List<SurfaceRule> Rules = new();

        public override RuleAction Clone(){
            SequenceAction a = new();
            foreach (SurfaceRule r in Rules) a.Rules.Add(r.Clone());
            return a;
        }
    }

    /// <summary>A named rule: when <see cref="Condition" /> holds (or is null), <see cref="Action" />.</summary>
    public sealed class SurfaceRule{
        public string Name;
        public RuleCondition Condition;
        public RuleAction Action;

        /// <summary>Data source and file that last defined the rule (messages).</summary>
        public string Source, File;

        public SurfaceRule Clone(){
            return new SurfaceRule{ Name = Name, Condition = Condition?.Clone(), Action = Action.Clone(), Source = Source, File = File };
        }
    }

    /// <summary>The climate of snow, ice and the tree line (§8), and the beach shape and water probes of the beach rules.</summary>
    public sealed class SurfaceClimate{
        /// <summary>L: temperature lost per block above <see cref="LapseFrom" />.</summary>
        public float Lapse = 0.0055f;

        public float LapseFrom = WorldGenSettings.DefaultWaterLevel;
        public float SnowTemperature = -0.638f;
        public string SnowWobble;
        public float IceTemperature = -0.6f;
        public string IceWobble;

        /// <summary>
        ///     Frozen rivers (§8.3, DEC-207): water where the graph's <c>river</c> output is at least
        ///     <see cref="RiverIceMin" /> freezes where T &lt; this, without the wobble (Minecraft's temperature level 0),
        ///     so a cold river is one ice sheet. NaN (the default): rivers freeze like the sea.
        /// </summary>
        public float RiverIceTemperature = float.NaN;

        public float RiverIceMin = 0.3f;

        /// <summary>T_tree = T_snow + this · L: the tree line lies this many blocks below the snow line.</summary>
        public float TreeLineBelowSnow = 15f;

        public float TreeLineFloor = 170f;
        public string TreeLineWobble;
        public int NearWaterDistance = 16;
        public string BeachShape;

        public float TreeTemperature => SnowTemperature + TreeLineBelowSnow * Lapse;
    }

    /// <summary>The strata bands every biome with a strata block lays over its stone block.</summary>
    public sealed class SurfaceStrata{
        public int Period = 7, Thickness = 2;
        public string Shift;
    }

    /// <summary>
    ///     The surface rules of every data source merged in load order (WorldGen v2 §7, §14): the shipped file, then each
    ///     mod's rule file. A list holds rules (appended) and operations on named rules: <c>{"before": name, "rule":
    ///     {…}}</c>, <c>"after"</c> and <c>"replace"</c>. The anchor may be a rule inside a sequence. A mod's list for a
    ///     biome without its own list starts from a copy of the default list. The merge only collects; the
    ///     <see cref="SurfaceRuleCompiler" /> checks and compiles the result.
    /// </summary>
    public sealed class SurfaceRuleSet{
        /// <summary>Format version of the rule files this code writes and the newest it reads.</summary>
        public const int CurrentFormatVersion = 1;

        public const string ResourcePath = "WorldGen/SurfaceRules";
        public const string FileName = "Resources/WorldGen/SurfaceRules.json";

        /// <summary>The graph output the river(min) condition reads (§9.2): 1 on a river's centre line, 0 outside the valleys slice.</summary>
        public const string RiverOutput = global::Engine.Scripts.WorldGen.Graph.GraphOutputs.River;

        public readonly List<string> Sources = new();
        public SurfaceClimate Climate = new();
        public SurfaceStrata Strata = new();

        /// <summary>Default blocks of slots a biome leaves empty ("snow").</summary>
        public readonly Dictionary<string, string> Blocks = new();

        public readonly List<SurfaceRule> GlobalBefore = new(), Default = new(), GlobalAfter = new();

        /// <summary>Biomes with their own list, by name (in place of <see cref="Default" />).</summary>
        public readonly Dictionary<string, List<SurfaceRule>> Biomes = new();

        public readonly List<LayoutMessage> Messages = new();

        public bool HasErrors{
            get{
                foreach (LayoutMessage m in Messages)
                    if (m.Severity == LayoutSeverity.Error)
                        return true;
                return false;
            }
        }

        /// <summary>The shipped rule file's text, or null when the asset is missing.</summary>
        public static string LoadShippedJson(){
            TextAsset asset = Resources.Load<TextAsset>(ResourcePath);
            return asset != null ? asset.text : null;
        }

        /// <summary>The shipped rules; a missing asset is an error.</summary>
        public static SurfaceRuleSet Shipped(){
            string json = LoadShippedJson();
            if (json != null) return Parse(json, BiomeLayout.BaseSourceName, FileName);
            SurfaceRuleSet set = new();
            set.Sources.Add(BiomeLayout.BaseSourceName);
            set.Error(BiomeLayout.BaseSourceName, FileName, null, "The surface rule asset is missing.", "file");
            return set;
        }

        /// <summary>The base rule file (the shipped one, or a test's): climate, strata, blocks and the lists.</summary>
        public static SurfaceRuleSet Parse(string json, string source, string file){
            SurfaceRuleSet set = new();
            set.Sources.Add(source);
            Dictionary<string, object> root = set.ReadRoot(json, source, file);
            if (root == null) return set;
            foreach (string key in root.Keys)
                switch (key){
                    case "formatVersion": break;
                    case "climate":
                        set.ReadClimate(root[key], source, file);
                        break;
                    case "strata":
                        set.ReadStrata(root[key], source, file);
                        break;
                    case "blocks":
                        if (root[key] is Dictionary<string, object> blocks)
                            foreach (KeyValuePair<string, object> b in blocks)
                                if (b.Value is string s) set.Blocks[b.Key] = s;
                                else set.Error(source, file, null, $"Block \"{b.Key}\" must be a voxel name.", "file");
                        else set.Error(source, file, null, "\"blocks\" must be an object of slot names and voxel names.", "file");
                        break;
                    case "globalBefore":
                    case "default":
                    case "globalAfter":
                    case "biomes":
                        break;
                    default:
                        set.Error(source, file, null, $"Unknown top-level field \"{key}\".", "file");
                        break;
                }

            set.MergeLists(root, source, file);
            return set;
        }

        /// <summary>
        ///     Merges a mod's rule file (§14):
        ///     <code>
        ///     { "formatVersion": 1,
        ///       "globalBefore": [ … ], "default": [ … ], "globalAfter": [ … ],
        ///       "biomes": { "Heath": [ { "before": "top", "rule": { "name": "heath_moss", "if": …, "then": … } } ] } }
        ///     </code>
        ///     A file with errors adds nothing.
        /// </summary>
        public void AddModJson(string modName, string file, string json){
            Sources.Add(modName);
            Dictionary<string, object> root = ReadRoot(json, modName, file);
            if (root == null) return;
            foreach (string key in root.Keys)
                if (key != "formatVersion" && key != "globalBefore" && key != "default" && key != "globalAfter" && key != "biomes")
                    Error(modName, file, null, $"Unknown top-level field \"{key}\" (a mod's rule file holds lists only).", "file");
            MergeLists(root, modName, file);
        }

        /// <summary>
        ///     The list of biome <paramref name="biome" /> with the global lists around it: globalBefore, its own list or
        ///     the default list, globalAfter.
        /// </summary>
        public List<SurfaceRule> ListFor(string biome){
            List<SurfaceRule> list = new(GlobalBefore);
            list.AddRange(biome != null && Biomes.TryGetValue(biome, out List<SurfaceRule> own) ? own : Default);
            list.AddRange(GlobalAfter);
            return list;
        }

        // --- Merge ------------------------------------------------------------------------------------------------------

        private void MergeLists(Dictionary<string, object> root, string source, string file){
            int errors = CountErrors();
            // Parse everything first, so a file with errors leaves the lists as they were.
            List<(List<SurfaceRule> target, string biome, List<ListEntry> entries)> work = new();
            foreach (string scope in new[]{ "globalBefore", "default", "globalAfter" }){
                if (!root.TryGetValue(scope, out object value)) continue;
                List<ListEntry> entries = ReadEntries(value, source, file, null, scope);
                if (entries != null) work.Add((ScopeList(scope), null, entries));
            }

            if (root.TryGetValue("biomes", out object biomesValue)){
                if (biomesValue is not Dictionary<string, object> biomes) Error(source, file, null, "\"biomes\" must be an object of biome names and rule lists.", "file");
                else
                    foreach (KeyValuePair<string, object> pair in biomes){
                        List<ListEntry> entries = ReadEntries(pair.Value, source, file, pair.Key, pair.Key);
                        if (entries != null) work.Add((null, pair.Key, entries));
                    }
            }

            if (CountErrors() > errors) return;

            // Apply on copies, then swap in, so an unresolved anchor leaves nothing half merged.
            Dictionary<List<SurfaceRule>, List<SurfaceRule>> copies = new();
            Dictionary<string, List<SurfaceRule>> biomeCopies = new();
            foreach ((List<SurfaceRule> target, string biome, List<ListEntry> entries) in work){
                List<SurfaceRule> list;
                if (biome != null){
                    if (!biomeCopies.TryGetValue(biome, out list)){
                        List<SurfaceRule> from = Biomes.TryGetValue(biome, out List<SurfaceRule> own) ? own : copies.TryGetValue(Default, out List<SurfaceRule> d) ? d : Default;
                        list = CloneList(from);
                        biomeCopies[biome] = list;
                    }
                }
                else if (!copies.TryGetValue(target, out list)){
                    list = CloneList(target);
                    copies[target] = list;
                }

                foreach (ListEntry e in entries) Apply(list, e, source, file, biome);
            }

            if (CountErrors() > errors) return;
            foreach (KeyValuePair<List<SurfaceRule>, List<SurfaceRule>> c in copies){
                c.Key.Clear();
                c.Key.AddRange(c.Value);
            }

            foreach (KeyValuePair<string, List<SurfaceRule>> c in biomeCopies) Biomes[c.Key] = c.Value;
        }

        private List<SurfaceRule> ScopeList(string scope){
            return scope switch{
                "globalBefore" => GlobalBefore,
                "globalAfter" => GlobalAfter,
                _ => Default
            };
        }

        private static List<SurfaceRule> CloneList(List<SurfaceRule> list){
            List<SurfaceRule> copy = new(list.Count);
            foreach (SurfaceRule r in list) copy.Add(r.Clone());
            return copy;
        }

        private sealed class ListEntry{
            public string Op, Anchor;
            public SurfaceRule Rule;
        }

        private void Apply(List<SurfaceRule> list, ListEntry e, string source, string file, string biome){
            if (e.Op == null){
                if (Find(list, e.Rule.Name, out _, out _)){
                    Error(source, file, biome, $"Rule \"{e.Rule.Name}\" already exists; use \"replace\" to change it.", "rule");
                    return;
                }

                list.Add(e.Rule);
                return;
            }

            if (!Find(list, e.Anchor, out List<SurfaceRule> container, out int index)){
                Error(source, file, biome, $"\"{e.Op}\": no rule \"{e.Anchor}\" in the list.", "anchor");
                return;
            }

            if (e.Op != "replace" && Find(list, e.Rule.Name, out _, out _)){
                Error(source, file, biome, $"Rule \"{e.Rule.Name}\" already exists; use \"replace\" to change it.", "rule");
                return;
            }

            switch (e.Op){
                case "before":
                    container.Insert(index, e.Rule);
                    break;
                case "after":
                    container.Insert(index + 1, e.Rule);
                    break;
                default:
                    container[index] = e.Rule;
                    break;
            }
        }

        /// <summary>The rule named <paramref name="name" /> in <paramref name="list" /> or in a sequence inside it.</summary>
        public static bool Find(List<SurfaceRule> list, string name, out List<SurfaceRule> container, out int index){
            for (int i = 0; i < list.Count; i++){
                if (list[i].Name == name){
                    container = list;
                    index = i;
                    return true;
                }

                if (list[i].Action is SequenceAction s && Find(s.Rules, name, out container, out index)) return true;
            }

            container = null;
            index = -1;
            return false;
        }

        // --- Reading ----------------------------------------------------------------------------------------------------

        private Dictionary<string, object> ReadRoot(string json, string source, string file){
            Dictionary<string, object> root;
            try{
                root = JsonReader.Parse(json) as Dictionary<string, object>;
            }
            catch (Exception e){
                Error(source, file, null, $"The rule file is not valid JSON: {e.Message}", "file");
                return null;
            }

            if (root == null){
                Error(source, file, null, "A rule file must be a JSON object.", "file");
                return null;
            }

            if (!root.TryGetValue("formatVersion", out object version) || version is not double v || v != Math.Floor(v)){
                Error(source, file, null, "The rule file needs an integer \"formatVersion\".", "file");
                return null;
            }

            if (v > CurrentFormatVersion){
                Error(source, file, null, $"formatVersion {v} is newer than this game reads ({CurrentFormatVersion}).", "file");
                return null;
            }

            return root;
        }

        private void ReadClimate(object value, string source, string file){
            if (value is not Dictionary<string, object> o){
                Error(source, file, null, "\"climate\" must be an object.", "file");
                return;
            }

            foreach (KeyValuePair<string, object> p in o){
                switch (p.Key){
                    case "lapse": Climate.Lapse = Number(p, source, file); break;
                    case "lapseFrom": Climate.LapseFrom = Number(p, source, file); break;
                    case "snowTemperature": Climate.SnowTemperature = Number(p, source, file); break;
                    case "snowWobble": Climate.SnowWobble = Text(p, source, file); break;
                    case "iceTemperature": Climate.IceTemperature = Number(p, source, file); break;
                    case "iceWobble": Climate.IceWobble = Text(p, source, file); break;
                    case "riverIceTemperature": Climate.RiverIceTemperature = Number(p, source, file); break;
                    case "riverIceMin": Climate.RiverIceMin = Number(p, source, file); break;
                    case "treeLineBelowSnow": Climate.TreeLineBelowSnow = Number(p, source, file); break;
                    case "treeLineFloor": Climate.TreeLineFloor = Number(p, source, file); break;
                    case "treeLineWobble": Climate.TreeLineWobble = Text(p, source, file); break;
                    case "nearWaterDistance": Climate.NearWaterDistance = (int)Number(p, source, file); break;
                    case "beachShape": Climate.BeachShape = Text(p, source, file); break;
                    default:
                        Error(source, file, null, $"Unknown climate field \"{p.Key}\".", "file");
                        break;
                }
            }
        }

        private void ReadStrata(object value, string source, string file){
            if (value is not Dictionary<string, object> o){
                Error(source, file, null, "\"strata\" must be an object.", "file");
                return;
            }

            foreach (KeyValuePair<string, object> p in o){
                switch (p.Key){
                    case "period": Strata.Period = (int)Number(p, source, file); break;
                    case "thickness": Strata.Thickness = (int)Number(p, source, file); break;
                    case "shift": Strata.Shift = Text(p, source, file); break;
                    default:
                        Error(source, file, null, $"Unknown strata field \"{p.Key}\".", "file");
                        break;
                }
            }
        }

        private float Number(KeyValuePair<string, object> p, string source, string file){
            if (p.Value is double d) return (float)d;
            Error(source, file, null, $"\"{p.Key}\" must be a number.", "file");
            return 0f;
        }

        private string Text(KeyValuePair<string, object> p, string source, string file){
            if (p.Value is string s) return s;
            Error(source, file, null, $"\"{p.Key}\" must be a name.", "file");
            return null;
        }

        private List<ListEntry> ReadEntries(object value, string source, string file, string biome, string scope){
            if (value is not List<object> items){
                Error(source, file, biome, $"\"{scope}\" must be a list of rules.", "file");
                return null;
            }

            List<ListEntry> entries = new();
            foreach (object item in items){
                if (item is not Dictionary<string, object> o){
                    Error(source, file, biome, "A list entry must be a rule or an operation object.", "file");
                    continue;
                }

                string op = o.ContainsKey("before") ? "before" : o.ContainsKey("after") ? "after" : o.ContainsKey("replace") ? "replace" : null;
                if (op == null){
                    SurfaceRule r = ReadRule(o, source, file, biome);
                    if (r != null) entries.Add(new ListEntry{ Rule = r });
                    continue;
                }

                if (o[op] is not string anchor || !o.TryGetValue("rule", out object ruleValue) || ruleValue is not Dictionary<string, object> ruleObject || o.Count != 2){
                    Error(source, file, biome, $"An operation is {{\"{op}\": \"rule name\", \"rule\": {{…}}}}.", "file");
                    continue;
                }

                SurfaceRule rule = ReadRule(ruleObject, source, file, biome);
                if (rule != null) entries.Add(new ListEntry{ Op = op, Anchor = anchor, Rule = rule });
            }

            return entries;
        }

        private SurfaceRule ReadRule(Dictionary<string, object> o, string source, string file, string biome){
            if (!o.TryGetValue("name", out object nameValue) || nameValue is not string name || name.Length == 0){
                Error(source, file, biome, "Every rule needs a \"name\".", "file");
                return null;
            }

            foreach (string key in o.Keys)
                if (key != "name" && key != "if" && key != "then"){
                    Error(source, file, biome, $"Rule \"{name}\": unknown field \"{key}\" (allowed: name, if, then).", "file");
                    return null;
                }

            if (!o.TryGetValue("then", out object thenValue)){
                Error(source, file, biome, $"Rule \"{name}\" needs \"then\".", "file");
                return null;
            }

            int errors = CountErrors();
            RuleCondition condition = o.TryGetValue("if", out object ifValue) ? ReadCondition(ifValue, source, file, biome, name) : null;
            RuleAction action = ReadAction(thenValue, source, file, biome, name);
            if (CountErrors() > errors) return null;
            return new SurfaceRule{ Name = name, Condition = condition, Action = action, Source = source, File = file };
        }

        private RuleAction ReadAction(object value, string source, string file, string biome, string rule){
            // A block may carry a cover: {"block": …, "cover": …}.
            if (value is Dictionary<string, object>{ Count: 2 } bc && bc.TryGetValue("block", out object bb) && bc.TryGetValue("cover", out object cv)){
                if (bb is string blockName && cv is string coverName) return new BlockAction{ Block = blockName, Cover = coverName };
                Error(source, file, biome, $"Rule \"{rule}\": \"block\" and \"cover\" are block names.", "file");
                return null;
            }

            if (value is not Dictionary<string, object>{ Count: 1 } o){
                Error(source, file, biome, $"Rule \"{rule}\": \"then\" is one of {{\"block\": …}} (with an optional \"cover\"), {{\"bands\": …}}, {{\"sequence\": […]}}.", "file");
                return null;
            }

            foreach (KeyValuePair<string, object> p in o)
                switch (p.Key){
                    case "block":
                        if (p.Value is string block) return new BlockAction{ Block = block };
                        break;
                    case "sequence":
                        if (p.Value is List<object> items){
                            SequenceAction s = new();
                            foreach (object item in items)
                                if (item is Dictionary<string, object> ro){
                                    SurfaceRule r = ReadRule(ro, source, file, biome);
                                    if (r != null) s.Rules.Add(r);
                                }
                                else Error(source, file, biome, $"Rule \"{rule}\": a sequence holds rules.", "file");

                            return s;
                        }

                        break;
                    case "bands":
                        if (p.Value is Dictionary<string, object> b){
                            BandsAction a = new(){ Period = Strata.Period, Thickness = Strata.Thickness, Shift = Strata.Shift };
                            foreach (KeyValuePair<string, object> f in b)
                                switch (f.Key){
                                    case "period" when f.Value is double d: a.Period = (int)d; break;
                                    case "thickness" when f.Value is double d: a.Thickness = (int)d; break;
                                    case "shift" when f.Value is string s: a.Shift = s; break;
                                    case "else" when f.Value is string s: a.Else = s; break;
                                    case "palette" when f.Value is List<object> list:
                                        foreach (object x in list)
                                            if (x is string s) a.Palette.Add(s);
                                            else Error(source, file, biome, $"Rule \"{rule}\": the palette holds block names.", "file");
                                        break;
                                    default:
                                        Error(source, file, biome, $"Rule \"{rule}\": unknown or malformed bands field \"{f.Key}\".", "file");
                                        break;
                                }

                            if (a.Palette.Count == 0) Error(source, file, biome, $"Rule \"{rule}\": bands need a palette.", "file");
                            return a;
                        }

                        break;
                }

            Error(source, file, biome, $"Rule \"{rule}\": malformed \"then\".", "file");
            return null;
        }

        /// <summary>Shorthand conditions by name.</summary>
        private static RuleCondition Shorthand(string s){
            return s switch{
                "floor" => new DepthCondition{ Above = true, Depth = 0 },
                "ceiling" => new DepthCondition{ Above = false, Depth = 0 },
                "dry" => new WaterDepthCondition{ Min = 0, Max = 0 },
                "sky_open" => new SkyOpenCondition(),
                "near_water" => new NearWaterCondition(),
                "frozen_river" => new FrozenRiverCondition(),
                "below_tree_line" => new TreeLineCondition(),
                "never_snows" => new NeverSnowsCondition(),
                "steep" => new SteepCondition{ Rise = 4 },
                "step_foot" => new StepFootCondition{ Rise = 1 },
                "beach_band" => new TopAboveWaterCondition{ BeachBand = true },
                "dense_forest" => new DenseForestCondition(),
                _ => null
            };
        }

        private RuleCondition ReadCondition(object value, string source, string file, string biome, string rule){
            if (value is string s){
                RuleCondition c = Shorthand(s);
                if (c == null) Error(source, file, biome, $"Rule \"{rule}\": unknown condition \"{s}\".", "condition");
                return c;
            }

            if (value is not Dictionary<string, object>{ Count: 1 } o){
                Error(source, file, biome, $"Rule \"{rule}\": a condition is a name or an object with one field.", "condition");
                return null;
            }

            foreach (KeyValuePair<string, object> p in o){
                object v = p.Value;
                switch (p.Key){
                    case "not": {
                        RuleCondition inner = ReadCondition(v, source, file, biome, rule);
                        return inner == null ? null : new NotCondition{ Inner = inner };
                    }
                    case "all":
                    case "any": {
                        if (v is not List<object> items) break;
                        ListCondition l = new(){ Any = p.Key == "any" };
                        foreach (object item in items){
                            RuleCondition c = ReadCondition(item, source, file, biome, rule);
                            if (c != null) l.Items.Add(c);
                        }

                        return l;
                    }
                    case "biome": {
                        BiomeCondition b = new();
                        bool ok = true;
                        if (v is string one) b.Names.Add(one);
                        else if (v is List<object> names)
                            foreach (object n in names)
                                if (n is string name) b.Names.Add(name);
                                else ok = false;
                        else ok = false;
                        if (!ok || b.Names.Count == 0) break;
                        return b;
                    }
                    case "stone_depth_above":
                    case "stone_depth_below": {
                        DepthCondition d = new(){ Above = p.Key == "stone_depth_above" };
                        if (v is double n) d.Depth = (int)n;
                        else if (v is Dictionary<string, object> f && f.TryGetValue("depth", out object dv) && dv is double dn){
                            d.Depth = (int)dn;
                            if (f.TryGetValue("noise", out object nv)){
                                if (nv is string name) d.Noise = name;
                                else break;
                            }
                        }
                        else break;

                        if (d.Depth < 0) break;
                        return d;
                    }
                    case "y_above": {
                        YAboveCondition y = new();
                        if (v is double n) y.Y = (float)n;
                        else if (v is Dictionary<string, object> f && f.TryGetValue("y", out object yv) && yv is double yn){
                            y.Y = (float)yn;
                            if (f.TryGetValue("wobble", out object w)){
                                if (w is string name) y.Wobble = name;
                                else break;
                            }
                        }
                        else break;

                        return y;
                    }
                    case "above_preliminary_surface": {
                        if (v is "delta_down") return new AbovePreliminaryCondition{ DeltaDown = true };
                        if (v is double m) return new AbovePreliminaryCondition{ Margin = (float)m };
                        break;
                    }
                    case "water_depth": {
                        if (v is List<object>{ Count: 2 } r && r[0] is double min && r[1] is double max)
                            return new WaterDepthCondition{ Min = (int)min, Max = (int)max };
                        break;
                    }
                    case "temperature": {
                        if (v is "snow") return new TemperatureCondition{ Snow = true };
                        if (v is Dictionary<string, object> f && f.TryGetValue("below", out object bv) && bv is double below){
                            TemperatureCondition t = new(){ Below = (float)below };
                            if (f.TryGetValue("wobble", out object w)){
                                if (w is string name) t.Wobble = name;
                                else break;
                            }

                            return t;
                        }

                        break;
                    }
                    case "steep": {
                        if (v is double rise && rise >= 1) return new SteepCondition{ Rise = (int)rise };
                        break;
                    }
                    case "step_foot": {
                        if (v is double rise && rise >= 1) return new StepFootCondition{ Rise = (int)rise };
                        break;
                    }
                    case "noise_threshold": {
                        if (v is not Dictionary<string, object> f || !f.TryGetValue("name", out object nv) || nv is not string name) break;
                        NoiseCondition c = new(){ Name = name };
                        if (f.TryGetValue("min", out object min)){
                            if (min is double d) c.Min = (float)d;
                            else break;
                        }

                        if (f.TryGetValue("max", out object max)){
                            if (max is double d) c.Max = (float)d;
                            else break;
                        }

                        return c;
                    }
                    case "river": {
                        // river(min) (§7.2, §9.2): the graph's river output is at least min; sugar for noise_threshold.
                        if (v is double min) return new NoiseCondition{ Name = RiverOutput, Min = (float)min };
                        break;
                    }
                    case "top_above_water": {
                        if (v is List<object>{ Count: 2 } r && r[0] is double min && r[1] is double max)
                            return new TopAboveWaterCondition{ Min = (int)min, Max = (int)max };
                        break;
                    }
                    default:
                        Error(source, file, biome, $"Rule \"{rule}\": unknown condition \"{p.Key}\".", "condition");
                        return null;
                }

                Error(source, file, biome, $"Rule \"{rule}\": malformed condition \"{p.Key}\".", "condition");
            }

            return null;
        }

        private int CountErrors(){
            int n = 0;
            foreach (LayoutMessage m in Messages)
                if (m.Severity == LayoutSeverity.Error)
                    n++;
            return n;
        }

        internal void Error(string source, string file, string biome, string text, string kind){
            Messages.Add(new LayoutMessage{ Severity = LayoutSeverity.Error, Source = source, File = file, Biome = biome, Text = text, Kind = kind });
        }

        internal static string Format(float v){
            return v.ToString("0.###", CultureInfo.InvariantCulture);
        }
    }
}
