using System;
using System.Collections.Generic;
using System.Globalization;
using Engine.Scripts.VoxelConfig.Data.Generation;
using Engine.Scripts.WorldGen.Settings;
using UnityEngine;

namespace Engine.Scripts.WorldGen.Biomes{
    public enum LayoutSeverity{
        Info = 0,
        Warning = 1,
        Error = 2
    }

    /// <summary>A message of the layout merge or the layout validator, naming the data source, the file and the biome.</summary>
    public sealed class LayoutMessage{
        public LayoutSeverity Severity;
        public string Source, File, Biome, Text;

        /// <summary>What kind of check found it (for tests and the Layout tab): "overlap", "corner", "gap", "substitute", …</summary>
        public string Kind;

        public override string ToString(){
            string where = string.Join(", ", new[]{ Source, File, Biome }.FilterEmpty());
            return $"{Severity}: {Text}" + (where.Length > 0 ? $" ({where})" : "");
        }
    }

    internal static class LayoutStringExtensions{
        public static IEnumerable<string> FilterEmpty(this IEnumerable<string> items){
            foreach (string s in items)
                if (!string.IsNullOrEmpty(s))
                    yield return s;
        }
    }

    /// <summary>A biome of a merged layout: its id (the index in the palette), name, water flag and asset.</summary>
    public sealed class LayoutBiome{
        public int Id;
        public string Name;

        /// <summary>A water biome: the dry-column rule replaces it on dry columns (§6.5). Only Ocean in the shipped set.</summary>
        public bool IsWater;

        /// <summary>The data source that last defined it, and its file.</summary>
        public int Source;

        public string File;

        /// <summary>Blocks and colours; a mod biome without its own asset copies those of its base biome.</summary>
        public Biome Asset;
    }

    /// <summary>
    ///     A box of a merged layout: its biome and data source, and its intervals with open ends as infinities
    ///     (min ≤ v &lt; max per parameter).
    /// </summary>
    public sealed class LayoutBox{
        public int Index, Biome, Source;
        public string File;
        public readonly float[] Min = new float[BiomeBox.Parameters], Max = new float[BiomeBox.Parameters];

        public bool Contains(float[] p){
            for (int d = 0; d < BiomeBox.Parameters; d++)
                if (!(p[d] >= Min[d] && p[d] < Max[d]))
                    return false;
            return true;
        }

        /// <summary>Whether the two boxes share a part of positive volume.</summary>
        public bool Overlaps(LayoutBox o){
            for (int d = 0; d < BiomeBox.Parameters; d++)
                if (!(Min[d] < o.Max[d] && o.Min[d] < Max[d]))
                    return false;
            return true;
        }

        public string Describe(){
            System.Text.StringBuilder s = new();
            for (int d = 0; d < BiomeBox.Parameters; d++){
                if (float.IsNegativeInfinity(Min[d]) && float.IsPositiveInfinity(Max[d])) continue;
                if (s.Length > 0) s.Append(' ');
                s.Append(BiomeBox.ShortNames[d]).Append(' ').Append(Format(Min[d])).Append('…').Append(Format(Max[d]));
            }

            return s.Length == 0 ? "everywhere" : s.ToString();
        }

        private static string Format(float v){
            return float.IsInfinity(v) ? v < 0 ? "−∞" : "∞" : v.ToString("0.###", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    ///     The biome boxes of every data source merged in load order (§6.2, §14): the base game's BiomeSet first, then
    ///     each mod's biome file. Biomes merge by name: a later source's biome of the same name replaces the earlier
    ///     one's boxes (and, when it says so, its water flag), a new name gets the next id. The ids are the per-world
    ///     palette of biome names (§6.8): at most 255 (AD-20). The merge itself only collects; the
    ///     <see cref="BiomeLayoutValidator" /> checks the result.
    /// </summary>
    public sealed class BiomeLayout{
        /// <summary>Largest number of biomes: ids are bytes (AD-20, DEC-144); 255 is reserved for "none".</summary>
        public const int MaxBiomes = 255;

        /// <summary>Format version of the biome files this code writes and the newest it reads.</summary>
        public const int CurrentFormatVersion = 1;

        public const string BaseSourceName = "base game";

        public readonly List<string> Sources = new();
        public readonly List<LayoutBiome> Biomes = new();
        public readonly List<LayoutBox> Boxes = new();

        /// <summary>Implausible neighbour pairs by biome id (smaller id first).</summary>
        public readonly List<Vector2Int> ImplausiblePairs = new();

        /// <summary>Errors of the merge (unreadable files, unknown base biomes); the validator adds its own.</summary>
        public readonly List<LayoutMessage> MergeMessages = new();

        public int BiomeCount => Biomes.Count;

        /// <summary>The palette: biome names by id.</summary>
        public string[] Palette(){
            string[] names = new string[Biomes.Count];
            for (int i = 0; i < names.Length; i++) names[i] = Biomes[i].Name;
            return names;
        }

        public int IdOf(string name){
            for (int i = 0; i < Biomes.Count; i++)
                if (string.Equals(Biomes[i].Name, name, StringComparison.Ordinal))
                    return i;
            return -1;
        }

        public string NameOf(int id){
            return id >= 0 && id < Biomes.Count ? Biomes[id].Name : $"<{id}>";
        }

        /// <summary>The biome assets by id, for the tint palette and the blocks (a mod biome's is its base's copy).</summary>
        public List<Biome> Assets(){
            List<Biome> list = new(Biomes.Count);
            foreach (LayoutBiome b in Biomes) list.Add(b.Asset);
            return list;
        }

        public bool IsImplausible(int a, int b){
            if (a == b) return false;
            Vector2Int key = new(Math.Min(a, b), Math.Max(a, b));
            return ImplausiblePairs.Contains(key);
        }

        /// <summary>
        ///     The layout of the base game: the biomes of <paramref name="set" /> in its order (so the ids are the
        ///     BiomeSet indices), their boxes and the set's implausible pairs. Empty slots get a placeholder name and no
        ///     boxes, so the ids stay the set's indices.
        /// </summary>
        public static BiomeLayout FromBiomeSet(BiomeSet set, string file = "BiomeSet"){
            BiomeLayout layout = new();
            layout.Sources.Add(BaseSourceName);
            if (set == null || set.Biomes == null){
                layout.Error(0, file, null, "The layout has no BiomeSet.", "merge");
                return layout;
            }

            for (int i = 0; i < set.Biomes.Count; i++){
                Biome asset = set.Biomes[i];
                string name = asset ? asset.name : $"<empty {i}>";
                LayoutBiome biome = new(){ Id = i, Name = name, IsWater = asset && asset.IsUnderwaterBiome, Source = 0, File = asset ? asset.name + ".asset" : file, Asset = asset };
                layout.Biomes.Add(biome);
                if (!asset){
                    layout.Error(0, file, name, $"BiomeSet slot {i} is empty.", "merge");
                    continue;
                }

                if (asset.Boxes == null) continue;
                foreach (BiomeBox box in asset.Boxes)
                    if (box != null && box.AtSurface)
                        layout.AddBox(i, 0, biome.File, box);
            }

            if (set.ImplausibleNeighbours != null)
                foreach (BiomePair pair in set.ImplausibleNeighbours){
                    if (pair == null || !pair.First || !pair.Second) continue;
                    layout.AddImplausible(set.IndexOf(pair.First), set.IndexOf(pair.Second));
                }

            return layout;
        }

        /// <summary>Adds a box of biome <paramref name="biome" /> from source <paramref name="source" />.</summary>
        public LayoutBox AddBox(int biome, int source, string file, BiomeBox box){
            LayoutBox b = new(){ Index = Boxes.Count, Biome = biome, Source = source, File = file };
            for (int d = 0; d < BiomeBox.Parameters; d++){
                Vector2 r = box.Range(d);
                b.Min[d] = r.x <= -1f ? float.NegativeInfinity : r.x;
                b.Max[d] = r.y >= 1f ? float.PositiveInfinity : r.y;
                if (!(b.Min[d] < b.Max[d]))
                    Error(source, file, NameOf(biome), $"Box {box} has an empty {BiomeBox.ParameterNames[d]} interval.", "box");
            }

            Boxes.Add(b);
            return b;
        }

        public void AddImplausible(int a, int b){
            if (a < 0 || b < 0 || a == b) return;
            Vector2Int key = new(Math.Min(a, b), Math.Max(a, b));
            if (!ImplausiblePairs.Contains(key)) ImplausiblePairs.Add(key);
        }

        /// <summary>
        ///     Merges a mod's biome file (§14):
        ///     <code>
        ///     { "formatVersion": 1,
        ///       "biomes": [ { "name": "Heath", "base": "Plains", "water": false,
        ///                     "boxes": [ { "temperature": [-1, -0.28], "humidity": [0.087, 1] } ] } ],
        ///       "implausible": [ ["Heath", "Desert"] ] }
        ///     </code>
        ///     A parameter left out of a box spans its whole range. A biome with a known name replaces that biome's boxes
        ///     with its own (they count as this source's); a new name needs "base", the biome whose blocks and colours it
        ///     copies until the rule lists of V4. Errors name the mod and the file; a file with errors adds nothing.
        /// </summary>
        public void AddModJson(string modName, string file, string json){
            int source = Sources.Count;
            Sources.Add(modName);
            Dictionary<string, object> root;
            try{
                root = JsonReader.Parse(json) as Dictionary<string, object>;
            }
            catch (Exception e){
                Error(source, file, null, $"The biome file is not valid JSON: {e.Message}", "file");
                return;
            }

            if (root == null){
                Error(source, file, null, "A biome file must be a JSON object.", "file");
                return;
            }

            if (!root.TryGetValue("formatVersion", out object version) || version is not double v || v != Math.Floor(v)){
                Error(source, file, null, "The biome file needs an integer \"formatVersion\".", "file");
                return;
            }

            if (v > CurrentFormatVersion){
                Error(source, file, null, $"formatVersion {v} is newer than this game reads ({CurrentFormatVersion}).", "file");
                return;
            }

            int errors = CountErrors();
            List<(LayoutBiome biome, bool isNew, Biome baseAsset, List<BiomeBox> boxes, object water)> parsed = new();
            if (root.TryGetValue("biomes", out object biomesValue)){
                if (biomesValue is not List<object> list){
                    Error(source, file, null, "\"biomes\" must be a list.", "file");
                    return;
                }

                foreach (object item in list){
                    if (item is not Dictionary<string, object> o || !o.TryGetValue("name", out object nameValue) || nameValue is not string name || name.Length == 0){
                        Error(source, file, null, "Every biome needs a \"name\".", "file");
                        continue;
                    }

                    int existing = IdOf(name);
                    LayoutBiome biome;
                    Biome baseAsset = null;
                    bool isNew = existing < 0;
                    if (isNew){
                        if (!o.TryGetValue("base", out object baseValue) || baseValue is not string baseName || IdOf(baseName) < 0){
                            Error(source, file, name, "A new biome needs \"base\", the name of a biome whose blocks and colours it copies.", "file");
                            continue;
                        }

                        // The base asset is copied only once the whole file is accepted, so a file with errors leaves no copy behind.
                        LayoutBiome b = Biomes[IdOf(baseName)];
                        baseAsset = b.Asset;
                        biome = new LayoutBiome{ Name = name, IsWater = b.IsWater, Source = source, File = file };
                    }
                    else biome = Biomes[existing];

                    o.TryGetValue("water", out object water);
                    if (water != null && water is not bool) Error(source, file, name, "\"water\" must be true or false.", "file");
                    List<BiomeBox> boxes = new();
                    if (o.TryGetValue("boxes", out object boxesValue)){
                        if (boxesValue is not List<object> boxList){
                            Error(source, file, name, "\"boxes\" must be a list.", "file");
                            continue;
                        }

                        foreach (object boxValue in boxList){
                            BiomeBox box = ReadBox(boxValue, source, file, name);
                            if (box != null) boxes.Add(box);
                        }
                    }

                    parsed.Add((biome, isNew, baseAsset, boxes, water));
                }
            }

            List<(string, string)> pairs = new();
            if (root.TryGetValue("implausible", out object pairsValue)){
                if (pairsValue is not List<object> pairList) Error(source, file, null, "\"implausible\" must be a list of name pairs.", "file");
                else
                    foreach (object p in pairList)
                        if (p is List<object>{ Count: 2 } two && two[0] is string a && two[1] is string b) pairs.Add((a, b));
                        else Error(source, file, null, "Each implausible pair is a list of two biome names.", "file");
            }

            foreach (string key in root.Keys)
                if (key != "formatVersion" && key != "biomes" && key != "implausible")
                    Error(source, file, null, $"Unknown top-level field \"{key}\".", "file");

            if (CountErrors() > errors) return;

            foreach ((LayoutBiome biome, bool isNew, Biome baseAsset, List<BiomeBox> boxes, object water) in parsed){
                if (isNew){
                    if (baseAsset){
                        biome.Asset = UnityEngine.Object.Instantiate(baseAsset);
                        biome.Asset.name = biome.Name;
                    }

                    biome.Id = Biomes.Count;
                    Biomes.Add(biome);
                }
                else{
                    Boxes.RemoveAll(b => b.Biome == biome.Id);
                    for (int i = 0; i < Boxes.Count; i++) Boxes[i].Index = i;
                    biome.Source = source;
                    biome.File = file;
                }

                if (water is bool w) biome.IsWater = w;
                foreach (BiomeBox box in boxes)
                    if (box.AtSurface)
                        AddBox(biome.Id, source, file, box);
            }

            foreach ((string a, string b) in pairs){
                int ia = IdOf(a), ib = IdOf(b);
                if (ia < 0 || ib < 0) Error(source, file, null, $"Implausible pair {a}–{b} names an unknown biome.", "file");
                else AddImplausible(ia, ib);
            }
        }

        private BiomeBox ReadBox(object value, int source, string file, string biome){
            if (value is not Dictionary<string, object> o){
                Error(source, file, biome, "A box must be an object of parameter intervals.", "file");
                return null;
            }

            BiomeBox box = new();
            foreach (KeyValuePair<string, object> pair in o){
                int p = Array.IndexOf(BiomeBox.ParameterNames, pair.Key);
                bool depth = pair.Key == "depth";
                if (p < 0 && !depth){
                    Error(source, file, biome, $"Unknown box parameter \"{pair.Key}\" (allowed: {string.Join(", ", BiomeBox.ParameterNames)}, depth).", "file");
                    return null;
                }

                if (pair.Value is not List<object>{ Count: 2 } range || range[0] is not double min || range[1] is not double max){
                    Error(source, file, biome, $"\"{pair.Key}\" must be a list [min, max].", "file");
                    return null;
                }

                Vector2 r = new((float)min, (float)max);
                if (depth) box.DepthRange = r;
                else box.SetRange(p, r);
            }

            return box;
        }

        private int CountErrors(){
            int n = 0;
            foreach (LayoutMessage m in MergeMessages)
                if (m.Severity == LayoutSeverity.Error)
                    n++;
            return n;
        }

        private void Error(int source, string file, string biome, string text, string kind){
            MergeMessages.Add(new LayoutMessage{
                Severity = LayoutSeverity.Error, Source = source < Sources.Count ? Sources[source] : null, File = file, Biome = biome, Text = text, Kind = kind
            });
        }
    }
}
