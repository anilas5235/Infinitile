using Engine.Scripts.WorldGen.Core;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using static Engine.Scripts.Utils.VoxelConstants;
using VoxelSo = Engine.Scripts.VoxelConfig.Data.Voxel.Voxel;

namespace Engine.Editor.WorldGen{
    /// <summary>
    ///     Cross-section mode of the preview: one top view with a chosen layer (any view, including cave volume and
    ///     the cavern, entrance and lava masks) with the slice drawn as a line, and the vertical slice beside it.
    ///     The slice runs through the framed region at one sample per block, at most <see cref="ViewResolution" />
    ///     blocks centred in the view. Moving the slice rebuilds only the slice; redraws are debounced like the views.
    /// </summary>
    public partial class WorldGenerationPreviewWindow{
        [SerializeField] private bool _crossSection;
        [SerializeField] private PreviewView _topLayer = PreviewView.CaveVolume;
        [SerializeField] private SliceAxis _sliceAxis = SliceAxis.X;
        [SerializeField] private int _sliceCoordinate;
        [SerializeField] private int _sliceYMin;
        [SerializeField] private int _sliceYMax = ChunkHeight - 1;
        [SerializeField] private bool _sliceFlora = true;
        private bool _sliceCoordinateSet;

        private PreviewSlice Slice => _builder?.Slice;

        /// <summary>Whether the passes sample the cave volume: only when a shown view needs it.</summary>
        private bool CaveVolumeShown => _crossSection && _topLayer == PreviewView.CaveVolume ||
                                        !_crossSection && _compare && _compareView == PreviewView.CaveVolume;

        /// <summary>The slice of the framed region: start along the axis and width, centred in the view.</summary>
        private (int start, int width) SliceSpan(){
            (int2 origin, int extent) = FramedRegion();
            int width = Mathf.Min(extent, ViewResolution);
            int start = (_sliceAxis == SliceAxis.X ? origin.x : origin.y) + (extent - width) / 2;
            return (start, width);
        }

        private PreviewBuilder.SliceRequest? SliceRequest(){
            if (!_crossSection) return null;
            (int start, int width) = SliceSpan();
            int yMin = Mathf.Clamp(_sliceYMin, 0, ChunkHeight - 1);
            return new PreviewBuilder.SliceRequest{
                Axis = _sliceAxis,
                Coordinate = _sliceCoordinate,
                Start = start,
                Width = width,
                YMin = yMin,
                YMax = Mathf.Clamp(_sliceYMax, yMin, ChunkHeight - 1),
                Flora = _sliceFlora
            };
        }

        /// <summary>The cross-section toggle of the view toolbar.</summary>
        private void DrawCrossSectionToggle(){
            bool on = EditorGUILayout.ToggleLeft("Cross-section", _crossSection, GUILayout.Width(100f));
            if (on == _crossSection) return;
            _crossSection = on;
            if (on && !_sliceCoordinateSet) PlaceSliceAtCentre();
        }

        private void PlaceSliceAtCentre(){
            _sliceCoordinate = _sliceAxis == SliceAxis.X ? _worldOffset.y : _worldOffset.x;
            _sliceCoordinateSet = true;
        }

        private void DrawCrossSectionViews(){
            wantsMouseMove = true; // the voxel readout under the mouse
            DrawCrossSectionControls();
            EditorGUILayout.BeginHorizontal();
            {
                Rect top = DrawView(TopLayerTitle(), PassA?.Texture(_topLayer), true);
                DrawSliceLine(top);
                HandleSlicePlacement(top);
                DrawSliceView();
            }
            EditorGUILayout.EndHorizontal();
        }

        /// <summary>Layer, axis, position, y range and flora. Slice controls rebuild only the slice.</summary>
        private void DrawCrossSectionControls(){
            bool changed = GUI.changed;
            GUI.changed = false;
            EditorGUILayout.BeginHorizontal();
            {
                EditorGUIUtility.labelWidth = 70f;
                PreviewView layer = (PreviewView)EditorGUILayout.EnumPopup("Top layer", _topLayer, GUILayout.Width(220f));
                bool layerChanged = layer != _topLayer;
                _topLayer = layer;
                GUI.changed = false;

                GUILayout.Space(16f);
                EditorGUIUtility.labelWidth = 40f;
                SliceAxis axis = (SliceAxis)EditorGUILayout.EnumPopup(new GUIContent("Slice", "X: along x at a fixed z; Z: along z at a fixed x"),
                    _sliceAxis, GUILayout.Width(110f));
                if (axis != _sliceAxis){
                    _sliceAxis = axis;
                    PlaceSliceAtCentre();
                }

                EditorGUIUtility.labelWidth = 20f;
                _sliceCoordinate = EditorGUILayout.IntField(_sliceAxis == SliceAxis.X ? "z" : "x", _sliceCoordinate, GUILayout.Width(90f));
                if (GUILayout.Button("Centre", GUILayout.Width(55f))) PlaceSliceAtCentre();

                GUILayout.Space(16f);
                EditorGUIUtility.labelWidth = 50f;
                float yMin = _sliceYMin, yMax = _sliceYMax;
                EditorGUILayout.LabelField("y range", GUILayout.Width(50f));
                _sliceYMin = Mathf.Clamp(EditorGUILayout.IntField(_sliceYMin, GUILayout.Width(40f)), 0, ChunkHeight - 1);
                EditorGUILayout.MinMaxSlider(ref yMin, ref yMax, 0f, ChunkHeight - 1, GUILayout.Width(160f));
                if (!Mathf.Approximately(yMin, _sliceYMin) || !Mathf.Approximately(yMax, _sliceYMax)){
                    _sliceYMin = Mathf.RoundToInt(yMin);
                    _sliceYMax = Mathf.RoundToInt(yMax);
                }

                _sliceYMax = Mathf.Clamp(EditorGUILayout.IntField(_sliceYMax, GUILayout.Width(40f)), _sliceYMin, ChunkHeight - 1);

                GUILayout.Space(16f);
                _sliceFlora = EditorGUILayout.ToggleLeft("Flora", _sliceFlora, GUILayout.Width(60f));
                EditorGUIUtility.labelWidth = 0f;
                GUILayout.FlexibleSpace();

                if (GUI.changed) _builder.MarkSliceStale();
                if (layerChanged) RequestRebuild(); // the cave volume is sampled only while it is shown
            }
            EditorGUILayout.EndHorizontal();
            GUI.changed = changed;
        }

        private string TopLayerTitle(){
            PreviewPass pass = PassA;
            string title = ViewTitle(pass, _topLayer);
            return pass == null || !pass.HasResult ? title : title + " · shift+click places the slice";
        }

        /// <summary>The slice as a line over the top view; only the span the slice covers.</summary>
        private void DrawSliceLine(Rect view){
            if (Event.current.type != EventType.Repaint) return;
            (int2 origin, int extent) = FramedRegion();
            (int start, int width) = SliceSpan();
            float scale = view.width / extent;
            float across = (_sliceCoordinate + 0.5f - (_sliceAxis == SliceAxis.X ? origin.y : origin.x)) * scale;
            float from = (start - (_sliceAxis == SliceAxis.X ? origin.x : origin.y)) * scale;
            float to = from + width * scale;
            if (across < 0f || across > view.width) return; // outside the framed region
            Rect line = _sliceAxis == SliceAxis.X
                ? new Rect(view.x + from, view.yMax - across - 1f, to - from, 2f) // z grows upwards in the view
                : new Rect(view.x + across - 1f, view.yMax - to, 2f, to - from);
            EditorGUI.DrawRect(line, new Color(1f, 0.2f, 0.9f, 0.9f));
        }

        /// <summary>Shift+click on the top view moves the slice through the clicked column.</summary>
        private void HandleSlicePlacement(Rect view){
            Event e = Event.current;
            if (e.type != EventType.MouseDown || e.button != 0 || !e.shift || !view.Contains(e.mousePosition)) return;
            (int2 origin, int extent) = FramedRegion();
            float scale = extent / view.width;
            int x = origin.x + Mathf.FloorToInt((e.mousePosition.x - view.x) * scale);
            int z = origin.y + Mathf.FloorToInt((view.yMax - e.mousePosition.y) * scale);
            _sliceCoordinate = _sliceAxis == SliceAxis.X ? z : x;
            _sliceCoordinateSet = true;
            _builder.MarkSliceStale();
            e.Use();
            Repaint();
        }

        /// <summary>The slice, one pixel per block (scaled to fit), with the voxel under the mouse.</summary>
        private void DrawSliceView(){
            PreviewSlice slice = Slice;
            EditorGUILayout.BeginVertical();
            {
                string axisName = slice != null && slice.Axis == SliceAxis.X ? "z" : "x";
                string title = slice == null || !slice.HasResult
                    ? "Cross-section"
                    : $"Cross-section at {axisName} = {slice.Coordinate}, y {slice.YMin}…{slice.YMax} · {slice.Width} blocks · " +
                      $"{slice.Carved:N0} carved voxels · dark = cave, grey-blue = sky";
                EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
                if (slice?.Error != null) EditorGUILayout.HelpBox("WorldGenSettings.ToConfig failed:\n" + slice.Error, MessageType.Error);

                Rect area = GUILayoutUtility.GetRect(160f, 160f, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
                EditorGUI.DrawRect(area, new Color(0.1f, 0.1f, 0.1f, 1f));
                Texture2D texture = slice?.Texture;
                if (slice != null && slice.HasResult && texture){
                    float scale = Mathf.Min(area.width / texture.width, area.height / texture.height);
                    Rect drawn = new(area.x, area.y, texture.width * scale, texture.height * scale);
                    GUI.DrawTexture(drawn, texture, ScaleMode.StretchToFill, false);
                    EditorGUILayout.LabelField(HoverText(slice, drawn, scale), EditorStyles.miniLabel);
                    if (Event.current.type == EventType.MouseMove && drawn.Contains(Event.current.mousePosition)) Repaint();
                }
                else{
                    EditorGUILayout.LabelField(" ", EditorStyles.miniLabel);
                }
            }
            EditorGUILayout.EndVertical();
        }

        private static string HoverText(PreviewSlice slice, Rect drawn, float scale){
            Vector2 mouse = Event.current.mousePosition;
            if (!drawn.Contains(mouse)) return "Hover the slice to read a voxel.";
            int i = Mathf.FloorToInt((mouse.x - drawn.x) / scale);
            int y = slice.YMin + Mathf.FloorToInt((drawn.yMax - mouse.y) / scale); // row 0 of the texture is at the bottom
            int along = slice.Start + i;
            int x = slice.Axis == SliceAxis.X ? along : slice.Coordinate;
            int z = slice.Axis == SliceAxis.X ? slice.Coordinate : along;
            (VoxelSo voxel, int surface) = slice.VoxelAt(i, y);
            string name = voxel ? voxel.name : y <= surface ? "air (cave)" : "air";
            return $"({x}, {y}, {z}) · {name} · surface before carving {surface}";
        }
    }
}
