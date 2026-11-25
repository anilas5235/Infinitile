using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Engine.Scripts.VoxelConfig.Data.Generation;
using Engine.Scripts.WorldGen.Biomes;
using Engine.Scripts.WorldGen.Graph;
using Engine.Scripts.WorldGen.Settings;
using Unity.Collections;
using UnityEditor;
using UnityEngine;

namespace Engine.Editor.WorldGen{
    /// <summary>
    ///     The Layout tab of WorldGen v2 (§13.2): the merged biome boxes of the WorldGenSettings' BiomeSet as a T × F grid
    ///     for one region of C, E and W (DEC-156). Each cell is the biome the compiled lookup gives at its centre; a
    ///     hatched cell is filled by the nearest box (a gap). Lines mark neighbour pairs, red lines the implausible ones.
    ///     The validator runs live (whenever the set, a biome or the graph changes) with the graph's ε and range, and
    ///     its messages are listed. The tab is read-only: the boxes are edited in the biome assets (V3 deviation from
    ///     §13.2, which edits through the B overrides). The sweep and the world report run from here too.
    /// </summary>
    public partial class WorldGenerationPreviewWindow{
        [SerializeField] private bool _layoutView;
        [SerializeField] private int[] _layoutRegion = new int[3];

        private BiomeLayout _layout;
        private LayoutValidation _layoutValidation;
        private BiomeLayoutData _layoutData;
        private List<Biome> _layoutAssets;
        private int _layoutKey = int.MinValue;
        private Vector2 _layoutScroll;
        private string _layoutTable;

        private void DrawLayoutToggle(){
            bool on = EditorGUILayout.ToggleLeft("Layout", _layoutView, GUILayout.Width(65f));
            if (on != _layoutView){
                _layoutView = on;
                if (on) _graphView = false;
            }
        }

        private void DisposeLayout(){
            if (_layoutData.IsCreated) _layoutData.Dispose();
            _layoutData = default;
            _layout = null;
            _layoutValidation = null;
            _layoutKey = int.MinValue;
        }

        /// <summary>Rebuilds the layout and the validation when the set, a biome asset or the compiled graph changed.</summary>
        private void RefreshLayout(BiomeSet set, CompiledNoiseGraph graph){
            int key = set.GetInstanceID() * 31 + _graphCompileVersion;
            key = key * 31 + EditorUtility.GetDirtyCount(set);
            foreach (Biome b in set.Biomes)
                if (b)
                    key = key * 31 + EditorUtility.GetDirtyCount(b) + b.GetInstanceID();
            if (key == _layoutKey && _layout != null) return;
            DisposeLayout();
            _layoutKey = key;
            _layout = BiomeLayout.FromBiomeSet(set);
            _layoutAssets = _layout.Assets();
            _layoutValidation = graph != null ? BiomeLayoutValidator.Validate(_layout, graph) : BiomeLayoutValidator.Validate(_layout);
            if (!BiomeLayoutData.TryCreate(_layout, null, Allocator.Persistent, out _layoutData, out _)) _layoutData = default;
        }

        private void DrawLayoutView(){
            BiomeSet set = WorldGenSettings ? WorldGenSettings.Biomes : null;
            if (!set){
                EditorGUILayout.HelpBox("Assign a BiomeSet to the WorldGenSettings to see the layout.", MessageType.Info);
                return;
            }

            if (Event.current.type == EventType.Layout){
                CompileGraphIfChanged();
                RefreshLayout(set, _graphCompile?.Graph);
            }

            if (_layout == null) return;
            _layoutScroll = EditorGUILayout.BeginScrollView(_layoutScroll);
            {
                LayoutValidation v = _layoutValidation;
                int errors = v.Messages.Count(m => m.Severity == LayoutSeverity.Error), warnings = v.Messages.Count(m => m.Severity == LayoutSeverity.Warning);
                EditorGUILayout.LabelField(
                    $"{_layout.Biomes.Count} biomes · {_layout.Boxes.Count} boxes · {_layout.Sources.Count} source(s) · validator: {errors} errors, {warnings} warnings · " +
                    $"ε T {v.Epsilon[0]:0.####} F {v.Epsilon[1]:0.####} C {v.Epsilon[2]:0.####} E {v.Epsilon[3]:0.####} W {v.Epsilon[4]:0.####}",
                    EditorStyles.miniLabel);

                // The region of C, E and W: the intervals between the box edges inside the graph's range.
                float[][] regionEdges = new float[3][];
                EditorGUILayout.BeginHorizontal();
                for (int r = 0; r < 3; r++){
                    int d = r + 2;
                    regionEdges[r] = Edges(d, v.RangeMin[d], v.RangeMax[d]);
                    string[] labels = new string[regionEdges[r].Length - 1];
                    for (int i = 0; i < labels.Length; i++) labels[i] = $"{regionEdges[r][i]:0.###} … {regionEdges[r][i + 1]:0.###}";
                    EditorGUIUtility.labelWidth = 20f;
                    _layoutRegion[r] = Mathf.Clamp(EditorGUILayout.Popup(BiomeBox.ShortNames[d], _layoutRegion[r], labels, GUILayout.Width(170f)), 0, labels.Length - 1);
                    EditorGUIUtility.labelWidth = 0f;
                }

                GUILayout.FlexibleSpace();
                EditorGUILayout.EndHorizontal();

                DrawLayoutGrid(regionEdges);
                DrawLayoutMessages();
                DrawLayoutTools(set);
            }
            EditorGUILayout.EndScrollView();
        }

        /// <summary>The distinct box edges of parameter <paramref name="d" /> inside [lo, hi], with lo and hi.</summary>
        private float[] Edges(int d, float lo, float hi){
            SortedSet<float> edges = new(){ lo, hi };
            foreach (LayoutBox b in _layout.Boxes){
                if (b.Min[d] > lo && b.Min[d] < hi) edges.Add(b.Min[d]);
                if (b.Max[d] > lo && b.Max[d] < hi) edges.Add(b.Max[d]);
            }

            return edges.ToArray();
        }

        private void DrawLayoutGrid(float[][] regionEdges){
            LayoutValidation v = _layoutValidation;
            float[] t = Edges(0, v.RangeMin[0], v.RangeMax[0]), f = Edges(1, v.RangeMin[1], v.RangeMax[1]);
            float size = Mathf.Clamp(position.width * 0.74f - 40f, 200f, 720f);
            Rect area = GUILayoutUtility.GetRect(size, size, GUILayout.Width(size), GUILayout.Height(size));
            EditorGUILayout.LabelField($"x: temperature T {t[0]:0.##} → {t[^1]:0.##} · y: humidity F {f[0]:0.##} → {f[^1]:0.##} (up) · darker: gap filled by the nearest box · " +
                                       "red line: implausible pair", EditorStyles.miniLabel);
            if (Event.current.type != EventType.Repaint) return;

            float[] p = new float[BiomeBox.Parameters];
            for (int r = 0; r < 3; r++){
                float[] e = regionEdges[r];
                int i = Mathf.Clamp(_layoutRegion[r], 0, e.Length - 2);
                p[r + 2] = (e[i] + e[i + 1]) * 0.5f;
            }

            float tSpan = t[^1] - t[0], fSpan = f[^1] - f[0];
            float X(float value) => area.x + (value - t[0]) / tSpan * area.width;
            float Y(float value) => area.yMax - (value - f[0]) / fSpan * area.height; // F up

            int nt = t.Length - 1, nf = f.Length - 1;
            int[,] biome = new int[nt, nf];
            bool[,] gap = new bool[nt, nf];
            for (int i = 0; i < nt; i++)
            for (int j = 0; j < nf; j++){
                p[0] = (t[i] + t[i + 1]) * 0.5f;
                p[1] = (f[j] + f[j + 1]) * 0.5f;
                int box = _layoutData.IsCreated ? _layoutData.Main.Lookup(p) : -1;
                biome[i, j] = box < 0 ? -1 : _layout.Boxes[box].Biome;
                gap[i, j] = box >= 0 && !_layout.Boxes[box].Contains(p);
                Rect cell = Rect.MinMaxRect(X(t[i]), Y(f[j + 1]), X(t[i + 1]), Y(f[j]));
                Color c = biome[i, j] < 0 ? Color.black : BiomeColor(biome[i, j]);
                EditorGUI.DrawRect(cell, gap[i, j] ? Color.Lerp(c, Color.black, 0.45f) : c);
                if (gap[i, j]) EditorGUI.DrawRect(new Rect(cell.x, cell.center.y, cell.width, 1f), new Color(0f, 0f, 0f, 0.5f));
            }

            // Borders between cells of different biomes: neighbour pairs, implausible ones in red.
            HashSet<(int, int)> implausible = new(_layout.ImplausiblePairs.Select(q => (Math.Min(q.x, q.y), Math.Max(q.x, q.y))));
            void Border(int a, int b, Rect line){
                if (a == b || a < 0 || b < 0) return;
                bool bad = implausible.Contains((Math.Min(a, b), Math.Max(a, b)));
                EditorGUI.DrawRect(line, bad ? Color.red : new Color(0f, 0f, 0f, 0.8f));
            }

            for (int i = 0; i < nt; i++)
            for (int j = 0; j < nf; j++){
                if (i + 1 < nt) Border(biome[i, j], biome[i + 1, j], new Rect(X(t[i + 1]) - 1f, Y(f[j + 1]), 2f, Y(f[j]) - Y(f[j + 1])));
                if (j + 1 < nf) Border(biome[i, j], biome[i, j + 1], new Rect(X(t[i]), Y(f[j + 1]) - 1f, X(t[i + 1]) - X(t[i]), 2f));
            }

            // One name per biome, on its largest cell, and the axes.
            GUIStyle label = new(EditorStyles.miniBoldLabel){ alignment = TextAnchor.MiddleCenter, normal ={ textColor = Color.white } };
            Dictionary<int, Rect> largest = new();
            for (int i = 0; i < nt; i++)
            for (int j = 0; j < nf; j++){
                if (biome[i, j] < 0) continue;
                Rect cell = Rect.MinMaxRect(X(t[i]), Y(f[j + 1]), X(t[i + 1]), Y(f[j]));
                if (!largest.TryGetValue(biome[i, j], out Rect best) || cell.width * cell.height > best.width * best.height) largest[biome[i, j]] = cell;
            }

            foreach (KeyValuePair<int, Rect> kv in largest) GUI.Label(kv.Value, _layout.NameOf(kv.Key), label);
        }

        private Color BiomeColor(int id){
            Biome asset = id < _layoutAssets.Count ? _layoutAssets[id] : null;
            if (asset) return asset.RepresentativeColor;
            UnityEngine.Random.State s = UnityEngine.Random.state;
            UnityEngine.Random.InitState(_layout.NameOf(id)?.GetHashCode() ?? id);
            Color c = Color.HSVToRGB(UnityEngine.Random.value, 0.55f, 0.8f);
            UnityEngine.Random.state = s;
            return c;
        }

        private void DrawLayoutMessages(){
            foreach (LayoutMessage m in _layoutValidation.Messages.Where(m => m.Severity != LayoutSeverity.Info).Take(40))
                EditorGUILayout.HelpBox(m.ToString(), m.Severity == LayoutSeverity.Error ? MessageType.Error : MessageType.Warning);
            int infos = _layoutValidation.Messages.Count(m => m.Severity == LayoutSeverity.Info);
            if (infos > 0) EditorGUILayout.LabelField($"{infos} info message(s)", EditorStyles.miniLabel);
        }

        /// <summary>The sweep (§6.7) and the world report (§13.4) on the five seeds; both take minutes and block the Editor.</summary>
        private void DrawLayoutTools(BiomeSet set){
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Run sweep (minutes)", GUILayout.Width(150f))) _layoutTable = RunSweep(set);
            if (GUILayout.Button("World report (minutes)", GUILayout.Width(160f))) _layoutTable = RunReport();
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
            if (_layoutTable != null)
                EditorGUILayout.TextArea(_layoutTable, new GUIStyle(EditorStyles.textArea){ font = EditorStyles.miniFont, wordWrap = false });
        }

        private static string RunSweep(BiomeSet set){
            List<SweepRow> rows = new();
            try{
                List<SweepCandidate> candidates = BiomeSweep.Candidates();
                for (int i = 0; i < candidates.Count; i++){
                    int index = i;
                    rows.Add(BiomeSweep.Run(candidates[i], set, progress: s => {
                        if (EditorUtility.DisplayCancelableProgressBar("Biome sweep", s, (float)index / candidates.Count)) throw new OperationCanceledException();
                    }));
                }
            }
            catch (OperationCanceledException){ }
            finally{
                EditorUtility.ClearProgressBar();
            }

            string table = BiomeSweep.Table(rows);
            WriteReport("sweep.txt", table);
            return table;
        }

        private string RunReport(){
            GraphCompileResult compile = _graphCompile ?? ShippedNoiseGraph.Compile();
            if (compile.Graph == null || _layout == null || !_layoutData.IsCreated) return "The graph or the layout does not compile.";
            List<string> parts = new();
            WorldReportOptions options = new(){
                WaterLevel = WorldGenSettings ? WorldGenSettings.WaterLevel : global::Engine.Scripts.WorldGen.Settings.WorldGenSettings.DefaultWaterLevel
            };
            try{
                foreach (uint seed in BiomeWorldReport.Seeds){
                    if (EditorUtility.DisplayCancelableProgressBar("World report", $"seed {seed}", parts.Count / (float)BiomeWorldReport.Seeds.Length)) break;
                    parts.Add(BiomeWorldReport.Text(BiomeWorldReport.Run(compile.Graph, _layout, _layoutData, seed, options), "preview"));
                }
            }
            finally{
                EditorUtility.ClearProgressBar();
                if (compile != _graphCompile) compile.Graph?.Dispose();
            }

            string text = string.Join("\n", parts);
            WriteReport("report.txt", text);
            return text;
        }

        private static void WriteReport(string file, string text){
            string dir = Path.Combine(Directory.GetCurrentDirectory(), "Temp", "world-report");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, file), text);
        }
    }
}
