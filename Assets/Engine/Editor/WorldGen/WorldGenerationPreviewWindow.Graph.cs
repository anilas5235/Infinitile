using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Engine.Scripts.WorldGen.Graph;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;

namespace Engine.Editor.WorldGen{
    /// <summary>
    ///     Graph mode of the preview: compiles the noise graph (the shipped one or a chosen JSON file), lists the
    ///     validator messages, the program sizes and the work counters, and draws every output. 2D outputs are top
    ///     views of the framed region (exact values at every sample); 3D outputs are a vertical x–y slice through the
    ///     centre of the view at one sample per block. Colours run from blue (negative) through grey to red
    ///     (positive), scaled by the output's bound; magenta marks a value outside its bound.
    /// </summary>
    public partial class WorldGenerationPreviewWindow{
        private const double GraphRebuildDelaySeconds = 0.15;

        [SerializeField] private bool _graphView;
        [SerializeField] private TextAsset _graphFile;

        private GraphCompileResult _graphCompile;
        private string _graphSource;
        private Object _graphSourceAsset;
        private readonly List<GraphOutputView> _graphViews = new();
        private GraphViewKey _graphBuiltKey, _graphPendingKey;
        private double _graphPendingSince;
        private GraphWorkCounters _graphCounters;
        private double _graphMilliseconds;
        private Vector2 _graphScroll;

        private sealed class GraphOutputView{
            public GraphOutputInfo Output;
            public Texture2D Texture;
            public float2 Observed;
            public int OutsideBound;
        }

        private struct GraphViewKey{
            public int2 Origin;
            public int Extent, Seed, Compile;

            public bool Equals(GraphViewKey o){
                return Origin.Equals(o.Origin) && Extent == o.Extent && Seed == o.Seed && Compile == o.Compile;
            }
        }

        private int _graphCompileVersion;

        /// <summary>The graph toggle of the view toolbar.</summary>
        private void DrawGraphToggle(){
            _graphView = EditorGUILayout.ToggleLeft("Graph outputs", _graphView, GUILayout.Width(105f));
        }

        private void DrawGraphViews(){
            EditorGUILayout.BeginHorizontal();
            {
                EditorGUIUtility.labelWidth = 70f;
                _graphFile = (TextAsset)EditorGUILayout.ObjectField("Graph file", _graphFile, typeof(TextAsset), false, GUILayout.Width(330f));
                EditorGUIUtility.labelWidth = 0f;
                EditorGUILayout.LabelField(_graphFile ? "" : "(none: the shipped graph)", EditorStyles.miniLabel, GUILayout.Width(170f));
                if (GUILayout.Button("Recompile", GUILayout.Width(90f))) _graphSource = null;
                GUILayout.FlexibleSpace();
            }
            EditorGUILayout.EndHorizontal();

            if (Event.current.type == EventType.Layout) CompileGraphIfChanged();
            if (_graphCompile == null) return;

            foreach (GraphMessage message in _graphCompile.Messages)
                EditorGUILayout.HelpBox(message.ToString(),
                    message.Severity == GraphMessageSeverity.Error ? MessageType.Error : MessageType.Warning);
            CompiledNoiseGraph graph = _graphCompile.Graph;
            if (graph == null) return;

            if (Event.current.type == EventType.Layout) RebuildGraphViewsIfDue(graph);

            EditorGUILayout.LabelField(
                $"{graph.Outputs.Count} outputs · {graph.NoiseCount} noises · {graph.InstructionCount} of {GraphLimits.MaxInstructions} instructions · " +
                (GraphEvaluator.HasGeneratedCode(graph) ? "generated code · " : "interpreter · ") +
                string.Join(" · ", graph.Programs.Select(p => p.ToString())), EditorStyles.miniLabel);
            EditorGUILayout.LabelField($"Last evaluation {_graphMilliseconds:0.0} ms · {_graphCounters}", EditorStyles.miniLabel);

            _graphScroll = EditorGUILayout.BeginScrollView(_graphScroll);
            {
                const int columns = 4;
                List<GraphOutputView> flat = _graphViews.Where(v => !v.Output.Is3D).ToList();
                for (int row = 0; row * columns < flat.Count; row++){
                    EditorGUILayout.BeginHorizontal();
                    for (int col = 0; col < columns; col++){
                        int i = row * columns + col;
                        if (i < flat.Count) DrawView(GraphViewTitle(flat[i]), flat[i].Texture, true, 300f);
                        else GUILayout.Space(304f);
                    }

                    GUILayout.FlexibleSpace();
                    EditorGUILayout.EndHorizontal();
                }

                foreach (GraphOutputView view in _graphViews.Where(v => v.Output.Is3D)){
                    EditorGUILayout.LabelField(GraphViewTitle(view) + $" · x–y slice at z {_worldOffset.y}", EditorStyles.boldLabel);
                    if (!view.Texture) continue;
                    float width = Mathf.Min(position.width - 40f, view.Texture.width * 2f);
                    Rect rect = GUILayoutUtility.GetRect(width, width * view.Texture.height / view.Texture.width, GUILayout.ExpandWidth(false));
                    EditorGUI.DrawRect(rect, new Color(0.1f, 0.1f, 0.1f, 1f));
                    GUI.DrawTexture(rect, view.Texture, ScaleMode.StretchToFill, false);
                }
            }
            EditorGUILayout.EndScrollView();
        }

        private static string GraphViewTitle(GraphOutputView view){
            GraphOutputInfo o = view.Output;
            string outside = view.OutsideBound > 0 ? $" · {view.OutsideBound} outside the bound!" : "";
            return $"{o.Name}{(o.PerColumn ? " (per column)" : "")} · bound {o.Bounds.Min:0.###}..{o.Bounds.Max:0.###} · " +
                   $"seen {view.Observed.x:0.###}..{view.Observed.y:0.###}{outside}";
        }

        private void CompileGraphIfChanged(){
            Object asset = _graphFile;
            string source = _graphFile ? _graphFile.text : ShippedNoiseGraph.LoadJson();
            if (_graphCompile != null && asset == _graphSourceAsset && source == _graphSource) return;

            DisposeGraphCompile();
            _graphSourceAsset = asset;
            _graphSource = source;
            if (source == null){
                _graphCompile = ShippedNoiseGraph.Compile(); // reports the missing asset
            }
            else{
                string file = _graphFile ? AssetDatabase.GetAssetPath(_graphFile) : ShippedNoiseGraph.FileName;
                _graphCompile = GraphCompiler.Compile(source, file);
            }

            _graphCompileVersion++;
        }

        /// <summary>Rebuilds the textures once the framed region has stayed put for a moment (panning is a drag).</summary>
        private void RebuildGraphViewsIfDue(CompiledNoiseGraph graph){
            (int2 origin, int extent) = FramedRegion();
            GraphViewKey key = new(){ Origin = origin, Extent = extent, Seed = Seed, Compile = _graphCompileVersion };
            if (_graphViews.Count > 0 && key.Equals(_graphBuiltKey)) return;

            double now = EditorApplication.timeSinceStartup;
            if (!key.Equals(_graphPendingKey)){
                _graphPendingKey = key;
                _graphPendingSince = now;
            }

            if (_graphViews.Count > 0 && now - _graphPendingSince < GraphRebuildDelaySeconds){
                Repaint();
                return;
            }

            BuildGraphViews(graph, origin, extent);
            _graphBuiltKey = key;
        }

        private void BuildGraphViews(CompiledNoiseGraph graph, int2 origin, int extent){
            DestroyGraphTextures();
            uint seed = unchecked((uint)Seed);
            Stopwatch watch = Stopwatch.StartNew();
            _graphCounters = default;

            // 2D outputs: exact values at the view's samples.
            int size = Mathf.Min(extent, ViewResolution);
            int stride = Mathf.Max(1, extent / ViewResolution);
            float2[] points = new float2[size * size];
            for (int j = 0; j < size; j++)
            for (int i = 0; i < size; i++)
                points[j * size + i] = new float2(origin.x + i * stride, origin.y + j * stride);
            float[] lattice = null;
            if (graph.Tables.LatticeValueCount > 0){
                lattice = GraphEvaluator.SampleLattice(graph, seed, points, out GraphWorkCounters latticeCounters);
                _graphCounters.Add(latticeCounters);
            }

            foreach (GraphOutputInfo output in graph.Outputs.Where(o => !o.Is3D)){
                GraphOutputView view = new(){ Output = output };
                view.Texture = Paint(size, size, output.Bounds, k => lattice[output.LatticeValue * points.Length + k], view);
                _graphViews.Add(view);
            }

            // 3D outputs: an x–y slice through the centre of the view, one sample per block.
            if (graph.Outputs.Any(o => o.Is3D)){
                int width = Mathf.Min(extent, ViewResolution) & ~3;
                int startX = (origin.x + (extent - width) / 2) & ~3;
                int z = _worldOffset.y;
                int regionZ = z & ~3;
                using GraphRegionResult region = GraphEvaluator.EvaluateRegion(graph, seed, new int2(startX, regionZ), new int2(width, 4), true);
                _graphCounters.Add(region.Counters);
                foreach (GraphOutputInfo output in graph.Outputs.Where(o => o.Is3D)){
                    GraphOutputView view = new(){ Output = output };
                    view.Texture = Paint(width, GraphLimits.WorldHeight, output.Bounds,
                        k => region.Voxel(output.VoxelOutput, k % width, k / width, z - regionZ), view);
                    _graphViews.Add(view);
                }
            }

            _graphMilliseconds = watch.Elapsed.TotalMilliseconds;
        }

        /// <summary>Paints a diverging ramp: row 0 is the bottom (smallest z or y).</summary>
        private static Texture2D Paint(int width, int height, GraphBounds bounds, System.Func<int, float> value, GraphOutputView view){
            float scale = math.max(math.max(math.abs(bounds.Min), math.abs(bounds.Max)), 1e-6f);
            Color32[] pixels = new Color32[width * height];
            Color32 negative = new(40, 100, 215, 255), middle = new(235, 235, 235, 255), positive = new(215, 70, 40, 255);
            Color32 outside = new(255, 0, 255, 255);
            float2 observed = new(float.MaxValue, float.MinValue);
            int outsideCount = 0;
            for (int k = 0; k < pixels.Length; k++){
                float v = value(k);
                observed = new float2(math.min(observed.x, v), math.max(observed.y, v));
                if (!bounds.Contains(v)){
                    outsideCount++;
                    pixels[k] = outside;
                    continue;
                }

                float t = math.clamp(v / scale, -1f, 1f);
                pixels[k] = t < 0f ? Color32.Lerp(middle, negative, -t) : Color32.Lerp(middle, positive, t);
            }

            view.Observed = observed;
            view.OutsideBound = outsideCount;
            Texture2D texture = new(width, height, TextureFormat.RGBA32, false){
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave
            };
            texture.SetPixels32(pixels);
            texture.Apply(false);
            return texture;
        }

        private void DestroyGraphTextures(){
            foreach (GraphOutputView view in _graphViews)
                if (view.Texture)
                    DestroyImmediate(view.Texture);
            _graphViews.Clear();
        }

        private void DisposeGraphCompile(){
            _graphCompile?.Graph?.Dispose();
            _graphCompile = null;
            DestroyGraphTextures();
        }

        private void DisposeGraphView(){
            DisposeGraphCompile();
            _graphSource = null;
            _graphSourceAsset = null;
        }
    }
}
