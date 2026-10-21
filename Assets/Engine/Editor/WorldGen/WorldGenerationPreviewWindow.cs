using System;
using System.Collections.Generic;
using Engine.Scripts.Settings;
using Engine.Scripts.VoxelConfig.Data.Generation;
using Engine.Scripts.WorldGen.Classify;
using Engine.Scripts.WorldGen.Noise;
using Engine.Scripts.WorldGen.Settings;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;

namespace Engine.Editor.WorldGen{
    /// <summary>
    ///     World-generation preview. Every view is filled through <c>WorldGenApi.SampleRegion</c> (see
    ///     <see cref="PreviewPass" />), so it shows exactly what <c>WorldSampler</c> computes for the framed region.
    ///     <para>
    ///         Architecture, per-stage method and strength controls (configuration A writes them to the
    ///         <see cref="WorldGenSettings" /> asset with Undo), seed field, views (height with hillshade, hard biome,
    ///         dominant material biome, max material weight, surface source, flora, climate, E, PV and in
    ///         Terrain-first Offset/Amp/J), the clamped share and the biome-pair boundary list. An A/B compare mode
    ///         shows the same view and seed for A and a preview-only configuration B layered on the asset, plus a
    ///         difference view. 1D plots show every spline level at the probe C/E/PV.
    ///     </para>
    /// </summary>
    public partial class WorldGenerationPreviewWindow : EditorWindow{
        private const int ViewResolution = 512;
        private const int DistributionResolution = 256;
        private const int PlotSamples = 96;
        private const double RebuildDebounceSeconds = 0.15; // auto rebuild waits for this pause in the changes
        private static readonly int[] ResolutionOptions ={ 128, 256, 512, 1024, 2048, 4096, 8192, 16384 };
        private static readonly string[] ResolutionLabels = Array.ConvertAll(ResolutionOptions, r => $"{r}x{r}");

        private static readonly PreviewView[] BiomeFirstGrid ={
            PreviewView.Height, PreviewView.HardBiome, PreviewView.DominantBiome,
            PreviewView.SurfaceSource, PreviewView.Flora, PreviewView.MaxMaterialWeight,
            PreviewView.Climate, PreviewView.Erosion, PreviewView.PeaksValleys
        };

        private static readonly PreviewView[] TerrainFirstGrid ={
            PreviewView.Height, PreviewView.HardBiome, PreviewView.DominantBiome, PreviewView.SurfaceSource,
            PreviewView.Flora, PreviewView.MaxMaterialWeight, PreviewView.Climate, PreviewView.Erosion,
            PreviewView.PeaksValleys, PreviewView.Offset, PreviewView.Amp, PreviewView.Jaggedness
        };

        // --- Source -------------------------------------------------------------------------------------------------
        private VoxelEngineSettings _settings;
        private bool _autoRebuild = true;
        private bool _needsRebuild = true;
        private double _lastChangeTime; // EditorApplication.timeSinceStartup of the last RequestRebuild
        private bool _buildInProgress;
        private JobHandle _buildHandle;
        private WorldGenSettings _buildAsset; // the asset of the running build
        private bool _scheduledA;
        private bool _scheduledB;

        // --- Configurations -----------------------------------------------------------------------------------------
        private PreviewPass _passA;
        private PreviewPass _passB;
        [SerializeField] private PreviewConfiguration _configB; // preview-only, never written to the asset implicitly
        [SerializeField] private bool _configBInitialized; // B starts as a copy of A
        [SerializeField] private bool _compare;
        [SerializeField] private PreviewView _compareView = PreviewView.Height;

        // --- Framing ------------------------------------------------------------------------------------------------
        private Vector2Int _worldOffset;
        private int _resolutionIndex = 2;
        private bool _isDragging;
        private Vector2 _dragStartMouse;
        private Vector2Int _dragStartOffset;

        // --- View options -------------------------------------------------------------------------------------------
        private bool _showTemperature = true;
        private bool _showContinental = true;
        private bool _showHumidity = true;
        [SerializeField] private bool _hillshade = true;
        [SerializeField] private float _exaggeration = 2f;

        // --- Difference view ----------------------------------------------------------------------------------------
        private NativeArray<Color32> _diffPixels;
        private Texture2D _diffTexture;
        private PreviewDiff.Stats _diffStats;
        private bool _diffSigned;
        private string _diffNote;

        // --- Distribution plane (classifier over T x F); C/E/PV are also the spline-plot probe ----------------------
        private float _planeContinental = 0.5f;
        private float _planeErosion = 0.5f;
        private float _planePV;
        private float _planeHeight = 0.5f;
        private Texture2D _distributionTexture;
        private NativeArray<Color32> _jobDistributionPixels;
        private NativeArray<Color32> _jobDistributionColors;

        // --- Spline plots -------------------------------------------------------------------------------------------
        private List<SplinePlots.Plot> _plots = new();
        private string _plotsError;

        // --- Editors and foldouts -------------------------------------------------------------------------------------
        private UnityEditor.Editor _settingsEditor;
        private UnityEditor.Editor _biomeEditor;
        private int _selectedBiomeIndex;
        private bool _showSettingsEditor = true;
        private bool _showBiomeEditor = true;
        private bool _showConfiguration = true;
        private bool _showStatistics = true;
        private bool _showPlots;
        private Vector2 _leftScroll;

        [MenuItem("Infinitile/World Generation Preview")]
        private static void OpenWindow(){
            WorldGenerationPreviewWindow window = GetWindow<WorldGenerationPreviewWindow>();
            window.titleContent = new GUIContent("World Gen Preview");
            window.minSize = new Vector2(1400f, 620f);
            window.Show();
        }

        private void OnEnable(){
            _passA ??= new PreviewPass("A");
            _passB ??= new PreviewPass("B");
            EditorApplication.update += OnEditorUpdate;
            Undo.undoRedoPerformed += OnUndoRedo;
            ReloadAssets();
        }

        private void OnDisable(){
            EditorApplication.update -= OnEditorUpdate;
            Undo.undoRedoPerformed -= OnUndoRedo;
            FinalizeScheduledBuild(false); // a running build still reads the configs and buffers
            DestroyEditors();
            _passA?.Dispose();
            _passB?.Dispose();
            _passA = _passB = null;
            if (_diffPixels.IsCreated) _diffPixels.Dispose();
            PreviewPass.DestroyTexture(ref _diffTexture);
            PreviewPass.DestroyTexture(ref _distributionTexture);
        }

        private void DestroyEditors(){
            if (_settingsEditor) DestroyImmediate(_settingsEditor);
            if (_biomeEditor) DestroyImmediate(_biomeEditor);
            _settingsEditor = null;
            _biomeEditor = null;
        }

        private void OnEditorUpdate(){
            if (_buildInProgress && _buildHandle.IsCompleted){
                FinalizeScheduledBuild(true);
                Repaint();
            }

            if (_autoRebuild && _needsRebuild && !_buildInProgress &&
                EditorApplication.timeSinceStartup - _lastChangeTime >= RebuildDebounceSeconds) ScheduleBuild();
        }

        /// <summary>Undo and redo rebuild at once (with auto rebuild on): they are single steps, not a drag.</summary>
        private void OnUndoRedo(){
            _needsRebuild = true;
            if (_autoRebuild && !_buildInProgress) ScheduleBuild();
        }

        /// <summary>The settings asset this preview reads (null until one is assigned).</summary>
        internal WorldGenSettings WorldGenSettings => _settings ? _settings.WorldGen : null;

        /// <summary>World seed of the assigned settings, 0 without settings.</summary>
        internal int Seed => _settings ? _settings.Seed : 0;

        /// <summary>Framed region: origin (bottom-left, blocks) and the world extent per side, like the views.</summary>
        internal (int2 origin, int extent) FramedRegion(){
            int resolution = ResolutionOptions[Mathf.Clamp(_resolutionIndex, 0, ResolutionOptions.Length - 1)];
            int half = resolution / 2;
            return (new int2(_worldOffset.x - half, _worldOffset.y - half), resolution);
        }

        #region GUI

        private void OnGUI(){
            if (_settings && _settings.WorldGen) EnsureConfigB(_settings.WorldGen);

            Vector2 windowSize = position.size;
            EditorGUI.BeginChangeCheck();
            EditorGUILayout.BeginHorizontal();
            {
                EditorGUILayout.BeginVertical(GUILayout.Width(windowSize.x * .26f));
                {
                    _leftScroll = EditorGUILayout.BeginScrollView(_leftScroll);
                    DrawToolbar();
                    DrawConfigurationPanel();
                    DrawStatisticsPanel();
                    DrawDistributionPanel();
                    DrawSplinePlotsPanel();
                    DrawSettingsPanel();
                    DrawBiomePanel();
                    DrawExtraPanels();
                    EditorGUILayout.EndScrollView();
                }
                EditorGUILayout.EndVertical();

                DrawViews();
            }
            EditorGUILayout.EndHorizontal();
            if (EditorGUI.EndChangeCheck()) RequestRebuild();
        }

        /// <summary>Hook for additional left-column panels (the height calibration section).</summary>
        partial void DrawExtraPanelsImpl();

        private void DrawExtraPanels(){
            DrawExtraPanelsImpl();
        }

        /// <summary>Runs a display-only control without marking the GUI changed, so it does not rebuild the preview.</summary>
        private static T WithoutRebuild<T>(Func<T> control){
            bool changed = GUI.changed;
            T value = control();
            GUI.changed = changed;
            return value;
        }

        private void DrawToolbar(){
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            {
                EditorGUILayout.LabelField("Data Source", EditorStyles.boldLabel);
                VoxelEngineSettings settings = (VoxelEngineSettings)EditorGUILayout.ObjectField("VoxelEngineSettings",
                    _settings, typeof(VoxelEngineSettings), false);
                if (settings != _settings){
                    _settings = settings;
                    DestroyEditors();
                    RequestRebuild();
                }

                if (_settings){
                    // Seed field, shared by A and B so the comparison runs at the same seed.
                    EditorGUILayout.BeginHorizontal();
                    EditorGUI.BeginChangeCheck();
                    int seed = EditorGUILayout.IntField("Seed", _settings.Seed);
                    if (GUILayout.Button("Random", GUILayout.Width(60f))) seed = UnityEngine.Random.Range(int.MinValue, int.MaxValue);
                    if (EditorGUI.EndChangeCheck()){
                        Undo.RecordObject(_settings, "Change world seed");
                        _settings.Seed = seed;
                        EditorUtility.SetDirty(_settings);
                    }

                    EditorGUILayout.EndHorizontal();

                    EditorGUI.BeginChangeCheck();
                    WorldGenSettings worldGen = (WorldGenSettings)EditorGUILayout.ObjectField("WorldGenSettings",
                        _settings.WorldGen, typeof(WorldGenSettings), false);
                    if (EditorGUI.EndChangeCheck()){
                        Undo.RecordObject(_settings, "Assign WorldGenSettings");
                        _settings.WorldGen = worldGen;
                        EditorUtility.SetDirty(_settings);
                        DestroyEditors();
                    }
                }

                EditorGUILayout.BeginHorizontal();
                {
                    if (GUILayout.Button("Reload", GUILayout.Width(90f))) ReloadAssets();

                    using (new EditorGUI.DisabledScope(_buildInProgress)){
                        if (GUILayout.Button("Rebuild", GUILayout.Width(90f))){
                            _needsRebuild = true;
                            ScheduleBuild();
                        }
                    }

                    _autoRebuild = EditorGUILayout.ToggleLeft("Auto rebuild", _autoRebuild, GUILayout.Width(100f));
                    if (_buildInProgress) EditorGUILayout.LabelField("building…", GUILayout.Width(70f));
                }
                EditorGUILayout.EndHorizontal();

                if (_passA?.Error != null) EditorGUILayout.HelpBox("A: WorldGenSettings.ToConfig failed:\n" + _passA.Error, MessageType.Error);
                if (_compare && _passB?.Error != null) EditorGUILayout.HelpBox("B: WorldGenSettings.ToConfig failed:\n" + _passB.Error, MessageType.Error);
            }
            EditorGUILayout.EndVertical();
        }

        /// <summary>
        ///     Configuration A (the asset, edited with Undo) and, in compare mode, the preview-only
        ///     configuration B with "Copy A → B" and "Apply B to asset".
        /// </summary>
        private void DrawConfigurationPanel(){
            WorldGenSettings asset = WorldGenSettings;
            if (!asset) return;
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            {
                _showConfiguration = EditorGUILayout.Foldout(_showConfiguration, "Configuration", true, EditorStyles.foldoutHeader);
                if (_showConfiguration){
                    EditorGUILayout.LabelField("A — the WorldGenSettings asset", EditorStyles.boldLabel);
                    PreviewConfiguration a = PreviewConfiguration.Capture(asset);
                    if (DrawConfiguration(a)){
                        Undo.RecordObject(asset, "Change preview configuration");
                        a.ApplyTo(asset);
                        EditorUtility.SetDirty(asset);
                    }

                    EditorGUILayout.Space(4f);
                    _compare = EditorGUILayout.ToggleLeft("Compare with configuration B (A | B | difference)", _compare);
                    if (_compare && _configB != null){
                        EditorGUILayout.LabelField("B — preview only (biomes, splines and climate from the asset)", EditorStyles.boldLabel);
                        DrawConfiguration(_configB);
                        EditorGUILayout.BeginHorizontal();
                        if (GUILayout.Button("Copy A → B")) _configB = PreviewConfiguration.Capture(asset);
                        using (new EditorGUI.DisabledScope(_configB.Matches(asset))){
                            if (GUILayout.Button("Apply B to asset")){
                                Undo.RecordObject(asset, "Apply preview configuration B");
                                _configB.ApplyTo(asset);
                                EditorUtility.SetDirty(asset);
                            }
                        }

                        EditorGUILayout.EndHorizontal();
                    }
                }
            }
            EditorGUILayout.EndVertical();
        }

        /// <summary>Architecture, method and strength controls; only the strengths the methods use are shown.</summary>
        private static bool DrawConfiguration(PreviewConfiguration c){
            EditorGUI.BeginChangeCheck();
            c.Architecture = (GenArchitecture)EditorGUILayout.EnumPopup("Architecture", c.Architecture);
            c.MaterialMethod = (BlendMethod)EditorGUILayout.EnumPopup("Material method", c.MaterialMethod);
            using (new EditorGUI.IndentLevelScope()){
                if (c.MaterialMethod == BlendMethod.DistanceBlend)
                    c.MaterialBlendWidth = EditorGUILayout.Slider("B_mat (blocks)", c.MaterialBlendWidth, 1f, 256f);
                if (c.MaterialMethod == BlendMethod.ParameterSpaceWeighting)
                    c.MaterialSigma = EditorGUILayout.Slider("σ_mat", c.MaterialSigma, 0.001f, 0.5f);
            }

            bool terrainFirst = c.Architecture == GenArchitecture.TerrainFirst;
            using (new EditorGUI.DisabledScope(terrainFirst)){
                c.HeightMethod = (BlendMethod)EditorGUILayout.EnumPopup(
                    terrainFirst ? "Height method (ignored in Terrain-first)" : "Height method", c.HeightMethod);
                if (!terrainFirst)
                    using (new EditorGUI.IndentLevelScope()){
                        if (c.HeightMethod is BlendMethod.DistanceBlend or BlendMethod.DistortedBoundary)
                            c.HeightBlendWidth = EditorGUILayout.Slider("B_h (blocks)", c.HeightBlendWidth, 1f, 256f);
                        if (c.HeightMethod == BlendMethod.ParameterSpaceWeighting)
                            c.HeightSigma = EditorGUILayout.Slider("σ_h", c.HeightSigma, 0.001f, 0.5f);
                    }
            }

            bool warp = c.MaterialMethod == BlendMethod.DistortedBoundary || !terrainFirst && c.HeightMethod == BlendMethod.DistortedBoundary;
            if (warp){
                EditorGUILayout.LabelField("Boundary warp (shared by both stages)", EditorStyles.miniBoldLabel);
                using (new EditorGUI.IndentLevelScope()){
                    c.WarpAmplitude = EditorGUILayout.Slider("A (blocks)", c.WarpAmplitude, 0f, 128f);
                    c.WarpWavelength = EditorGUILayout.Slider("λd (blocks)", c.WarpWavelength, 8f, 1024f);
                    c.WarpOctaves = EditorGUILayout.IntSlider("Octaves", c.WarpOctaves, 1, 4);
                }
            }

            return EditorGUI.EndChangeCheck();
        }

        /// <summary>Clamped share, biome-pair boundaries and the flora species per pass.</summary>
        private void DrawStatisticsPanel(){
            if (!WorldGenSettings) return;
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            {
                _showStatistics = EditorGUILayout.Foldout(_showStatistics, "Statistics of the view", true, EditorStyles.foldoutHeader);
                if (_showStatistics){
                    DrawPassStatistics(_passA);
                    if (_compare) DrawPassStatistics(_passB);
                }
            }
            EditorGUILayout.EndVertical();
        }

        private void DrawPassStatistics(PreviewPass pass){
            if (pass == null || !pass.HasResult) return;
            EditorGUILayout.LabelField($"{pass.Label}: {pass.Description}", EditorStyles.miniBoldLabel);
            using (new EditorGUI.IndentLevelScope()){
                EditorGUILayout.LabelField("Clamped columns", pass.ClampedShare.ToString("P2"));

                long total = 0, implausible = 0;
                foreach (BiomeBoundaryStats.PairLength p in pass.Boundaries){
                    total += p.Length;
                    if (p.Implausible) implausible += p.Length;
                }

                EditorGUILayout.LabelField("Biome boundaries (dominant material biome)", $"{total:N0} blocks");
                if (implausible > 0){
                    Color previous = GUI.color;
                    GUI.color = new Color(1f, .45f, .4f);
                    EditorGUILayout.LabelField("Implausible neighbours", $"{implausible:N0} blocks ({implausible / (float)math.max(1, total):P1})");
                    GUI.color = previous;
                }

                foreach (BiomeBoundaryStats.PairLength p in pass.Boundaries){
                    Color previous = GUI.color;
                    if (p.Implausible) GUI.color = new Color(1f, .45f, .4f);
                    EditorGUILayout.LabelField($"{p.NameA} – {p.NameB}{(p.Implausible ? "  (implausible)" : "")}",
                        $"{p.Length:N0}  ({p.Share:P1})");
                    GUI.color = previous;
                }

                if (pass.FloraCounts.Count > 0){
                    string[] names = pass.FloraNames;
                    int columns = pass.Size * pass.Size;
                    EditorGUILayout.LabelField("Flora species", EditorStyles.miniBoldLabel);
                    for (int i = 0; i < pass.FloraCounts.Count && i < 10; i++){
                        (ushort species, int count) = pass.FloraCounts[i];
                        EditorGUILayout.BeginHorizontal();
                        Rect swatch = GUILayoutUtility.GetRect(12f, 12f, GUILayout.Width(12f));
                        swatch.x += EditorGUI.indentLevel * 15f;
                        EditorGUI.DrawRect(swatch, PreviewPass.SpeciesColor(species));
                        GUILayout.Space(EditorGUI.indentLevel * 15f);
                        string name = species < names.Length ? names[species] : $"#{species}";
                        EditorGUILayout.LabelField(name, $"{count:N0}  ({count / (float)math.max(1, columns):P2})");
                        EditorGUILayout.EndHorizontal();
                    }
                }
            }
        }

        /// <summary>
        ///     The classifier's T x F plane: every pixel is <see cref="BiomeClassifier.Classify" /> at
        ///     (T, F) with the chosen C, E, PV and, in Terrain-first, the normalised height h (6D). C, E and PV are
        ///     also the probe of the spline plots.
        /// </summary>
        private void DrawDistributionPanel(){
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            {
                EditorGUILayout.LabelField("Biome distribution of A (X = Humidity F, Y = Temperature T)", EditorStyles.boldLabel);
                _planeContinental = EditorGUILayout.Slider("Continental C", _planeContinental, 0f, 1f);
                _planeErosion = EditorGUILayout.Slider("Erosion E", _planeErosion, 0f, 1f);
                _planePV = EditorGUILayout.Slider("Peaks & valleys PV", _planePV, -1f, 1f);
                bool terrainFirst = WorldGenSettings && WorldGenSettings.Architecture == GenArchitecture.TerrainFirst;
                using (new EditorGUI.DisabledScope(!terrainFirst)){
                    _planeHeight = EditorGUILayout.Slider(terrainFirst ? "Height h (6D, normalised)" : "Height h (Terrain-first only)",
                        _planeHeight, 0f, 1f);
                }

                DrawView(null, _distributionTexture, false, 220f);
            }
            EditorGUILayout.EndVertical();
        }

        /// <summary>Every spline level along its axis with the other axes at the probe C/E/PV.</summary>
        private void DrawSplinePlotsPanel(){
            WorldGenSettings asset = WorldGenSettings;
            if (!asset) return;
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            {
                _showPlots = EditorGUILayout.Foldout(_showPlots, "Spline levels", true, EditorStyles.foldoutHeader);
                if (_showPlots){
                    if (!asset.Splines)
                        EditorGUILayout.HelpBox("No TerrainSplineSet assigned.", MessageType.Info);
                    else if (_plotsError != null)
                        EditorGUILayout.HelpBox(_plotsError, MessageType.Warning);
                    else{
                        EditorGUILayout.LabelField($"Probe C = {_planeContinental:0.00}, E = {_planeErosion:0.00}, PV = {_planePV:0.00} " +
                                                   "(distribution sliders) · orange = child point · line = probe", EditorStyles.miniLabel);
                        foreach (SplinePlots.Plot plot in _plots) DrawPlot(plot);
                    }
                }
            }
            EditorGUILayout.EndVertical();
        }

        private static void DrawPlot(SplinePlots.Plot plot){
            float indent = plot.Node.Depth * 14f;
            EditorGUILayout.LabelField(
                $"{new string(' ', plot.Node.Depth * 3)}{plot.Node.Tree} · {plot.Name} over {TerrainSplineSet.AxisName(plot.Axis)} " +
                $"[{plot.ValueRange.x:0.##} … {plot.ValueRange.y:0.##}] · probe {plot.Probe.y:0.##}", EditorStyles.miniLabel);
            Rect rect = GUILayoutUtility.GetRect(100f, 64f, GUILayout.ExpandWidth(true));
            rect.xMin += indent;
            EditorGUI.DrawRect(rect, new Color(0.12f, 0.12f, 0.12f, 1f));
            if (Event.current.type != EventType.Repaint) return;

            Rect inner = new(rect.x + 4f, rect.y + 4f, rect.width - 8f, rect.height - 8f);
            float2 values = plot.ValueRange;
            if (values.y - values.x < 1e-3f) values += new float2(-0.5f, 0.5f);
            float2 axis = plot.AxisRange;

            Vector3 Map(float x, float y){
                return new Vector3(inner.x + (x - axis.x) / (axis.y - axis.x) * inner.width,
                    inner.yMax - (y - values.x) / (values.y - values.x) * inner.height, 0f);
            }

            Vector3[] line = new Vector3[plot.Curve.Length];
            for (int s = 0; s < line.Length; s++) line[s] = Map(math.lerp(axis.x, axis.y, s / (float)(line.Length - 1)), plot.Curve[s]);
            Color previous = Handles.color;
            Handles.color = new Color(0.45f, 0.8f, 1f);
            Handles.DrawAAPolyLine(2f, line);

            Vector3 probe = Map(plot.Probe.x, plot.Probe.y);
            Handles.color = new Color(1f, 1f, 1f, 0.35f);
            Handles.DrawLine(new Vector3(probe.x, inner.y), new Vector3(probe.x, inner.yMax));
            Handles.color = previous;
            EditorGUI.DrawRect(new Rect(probe.x - 3f, probe.y - 3f, 6f, 6f), Color.white);

            for (int k = 0; k < plot.Points.Length; k++){
                Vector3 p = Map(plot.Points[k].x, plot.Points[k].y);
                EditorGUI.DrawRect(new Rect(p.x - 2f, p.y - 2f, 4f, 4f), plot.PointIsChild[k] ? new Color(1f, .6f, .1f) : new Color(.85f, .85f, .85f));
            }
        }

        private void DrawSettingsPanel(){
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            {
                _showSettingsEditor = EditorGUILayout.Foldout(_showSettingsEditor, "WorldGenSettings", true, EditorStyles.foldoutHeader);
                if (_showSettingsEditor){
                    if (!WorldGenSettings){
                        EditorGUILayout.HelpBox("Assign a WorldGenSettings asset to the VoxelEngineSettings.", MessageType.Info);
                    }
                    else{
                        UnityEditor.Editor.CreateCachedEditor(WorldGenSettings, null, ref _settingsEditor);
                        if (_settingsEditor) _settingsEditor.OnInspectorGUI();
                    }
                }
            }
            EditorGUILayout.EndVertical();
        }

        private void DrawBiomePanel(){
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            {
                _showBiomeEditor = EditorGUILayout.Foldout(_showBiomeEditor, "Biomes", true, EditorStyles.foldoutHeader);
                if (!_showBiomeEditor){
                    EditorGUILayout.EndVertical();
                    return;
                }

                BiomeSet set = WorldGenSettings ? WorldGenSettings.Biomes : null;
                if (!set || set.Count == 0){
                    EditorGUILayout.HelpBox("The WorldGenSettings has no BiomeSet with biomes.", MessageType.Warning);
                    EditorGUILayout.EndVertical();
                    return;
                }

                // Legend: index, colour swatch and name, in classification order (ties go to the lower index).
                for (int i = 0; i < set.Count; i++){
                    Biome biome = set.Biomes[i];
                    EditorGUILayout.BeginHorizontal();
                    Rect swatch = GUILayoutUtility.GetRect(14f, 14f, GUILayout.Width(14f));
                    EditorGUI.DrawRect(swatch, biome ? biome.RepresentativeColor : Color.black);
                    if (GUILayout.Button($"{i}: {(biome ? biome.name : "<null>")}", EditorStyles.label))
                        _selectedBiomeIndex = i;
                    EditorGUILayout.EndHorizontal();
                }

                _selectedBiomeIndex = Mathf.Clamp(_selectedBiomeIndex, 0, set.Count - 1);
                Biome selected = set.Biomes[_selectedBiomeIndex];
                EditorGUILayout.Space(4f);
                EditorGUILayout.LabelField($"Editing: {(selected ? selected.name : "<null>")}", EditorStyles.boldLabel);
                if (selected){
                    UnityEditor.Editor.CreateCachedEditor(selected, null, ref _biomeEditor);
                    if (_biomeEditor) _biomeEditor.OnInspectorGUI();
                }
            }
            EditorGUILayout.EndVertical();
        }

        private void DrawViews(){
            if (!_settings){
                EditorGUILayout.HelpBox("Select a VoxelEngineSettings asset.", MessageType.Info);
                return;
            }

            if (!_settings.WorldGen){
                EditorGUILayout.HelpBox("Assign a WorldGenSettings asset to the VoxelEngineSettings to see the views.", MessageType.Info);
                return;
            }

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            {
                EditorGUILayout.BeginHorizontal();
                {
                    _worldOffset = EditorGUILayout.Vector2IntField("World offset (X/Z)", _worldOffset, GUILayout.Width(300f));
                    _resolutionIndex = EditorGUILayout.Popup("Extent", _resolutionIndex, ResolutionLabels, GUILayout.Width(220f));
                    GUILayout.FlexibleSpace();
                    EditorGUILayout.LabelField("Climate RGB:", GUILayout.Width(80f));
                    _showTemperature = EditorGUILayout.ToggleLeft("T (R)", _showTemperature, GUILayout.Width(55f));
                    _showContinental = EditorGUILayout.ToggleLeft("C (G)", _showContinental, GUILayout.Width(55f));
                    _showHumidity = EditorGUILayout.ToggleLeft("F (B)", _showHumidity, GUILayout.Width(55f));
                }
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.BeginHorizontal();
                {
                    _hillshade = EditorGUILayout.ToggleLeft("Hillshade", _hillshade, GUILayout.Width(80f));
                    using (new EditorGUI.DisabledScope(!_hillshade)){
                        EditorGUIUtility.labelWidth = 80f;
                        _exaggeration = EditorGUILayout.Slider("Exaggeration", _exaggeration, 1f, 10f, GUILayout.Width(260f));
                        EditorGUIUtility.labelWidth = 0f;
                    }

                    GUILayout.Space(20f);
                    _compare = EditorGUILayout.ToggleLeft("Compare A/B", _compare, GUILayout.Width(100f));
                    if (_compare){
                        PreviewView view = WithoutRebuild(() => (PreviewView)EditorGUILayout.EnumPopup(_compareView, GUILayout.Width(170f)));
                        if (view != _compareView){
                            _compareView = view;
                            UpdateDiff();
                        }
                    }

                    GUILayout.FlexibleSpace();
                }
                EditorGUILayout.EndHorizontal();

                int stride = Mathf.Max(1, ResolutionOptions[_resolutionIndex] / ViewResolution);
                EditorGUILayout.LabelField(
                    $"A: {_passA?.Description} · drag a view to pan · 1 sample per block up to 512, then every {stride}th column",
                    EditorStyles.miniLabel);

                if (_compare) DrawCompareViews();
                else DrawGrid();
            }
            EditorGUILayout.EndVertical();
        }

        private void DrawGrid(){
            bool terrainFirst = _passA != null && _passA.TerrainFirst;
            PreviewView[] views = terrainFirst ? TerrainFirstGrid : BiomeFirstGrid;
            int columns = terrainFirst ? 4 : 3;
            for (int row = 0; row * columns < views.Length; row++){
                EditorGUILayout.BeginHorizontal();
                for (int col = 0; col < columns && row * columns + col < views.Length; col++){
                    PreviewView view = views[row * columns + col];
                    DrawView(ViewTitle(_passA, view), _passA?.Texture(view), true);
                }

                EditorGUILayout.EndHorizontal();
            }
        }

        /// <summary>The selected view of A and B side by side and their difference.</summary>
        private void DrawCompareViews(){
            EditorGUILayout.BeginHorizontal();
            {
                DrawView("A · " + ViewTitle(_passA, _compareView), _passA?.Texture(_compareView), true);
                DrawView("B · " + ViewTitle(_passB, _compareView), _passB != null && _passB.HasResult ? _passB.Texture(_compareView) : null, true);
                DrawView(DiffTitle(), _diffNote == null ? _diffTexture : null, true);
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.LabelField($"B: {_passB?.Description}", EditorStyles.miniLabel);
            if (_diffNote != null) EditorGUILayout.HelpBox(_diffNote, MessageType.Info);
        }

        private string DiffTitle(){
            if (_diffNote != null) return "Difference";
            if (_diffSigned)
                return $"B − A height (red = B higher): mean |Δ| {_diffStats.MeanAbs:0.##}, max {_diffStats.MaxAbs:0.#}, " +
                       $"≥1 block {_diffStats.Share:P1}";
            return $"Mismatch mask: {_diffStats.Share:P2} of the columns differ";
        }

        private string ViewTitle(PreviewPass pass, PreviewView view){
            if (pass == null || !pass.HasResult) return view.ToString();
            int flora = 0;
            if (view == PreviewView.Flora)
                foreach (var (_, count) in pass.FloraCounts)
                    flora += count;
            return view switch{
                PreviewView.Height => $"Height{(_hillshade ? " + hillshade" : "")} (clamped {pass.ClampedShare:P1}, red = clamped, blue = water)",
                PreviewView.HardBiome => pass.TerrainFirst ? "Hard biome (6D)" : "Hard biome (5D)",
                PreviewView.DominantBiome => "Dominant material biome",
                PreviewView.MaxMaterialWeight => $"Max material weight (mean {pass.MeanMaxWeight:0.00})",
                PreviewView.SurfaceSource => "Surface source",
                PreviewView.Flora => $"Flora ({flora:N0} columns, {pass.FloraCounts.Count} species)",
                PreviewView.Climate => "Climate (T, C, F)",
                PreviewView.Erosion => "Erosion E",
                PreviewView.PeaksValleys => "Peaks & valleys PV",
                PreviewView.Offset => pass.TerrainFirst ? "Offset (Hmin..Hmax, blue below water)" : "Offset (Terrain-first only)",
                PreviewView.Amp => pass.TerrainFirst ? $"Amp ({pass.AmpRange.x:0.#}..{pass.AmpRange.y:0.#})" : "Amp (Terrain-first only)",
                PreviewView.Jaggedness => pass.TerrainFirst
                    ? $"Jaggedness ({pass.JaggednessRange.x:0.#}..{pass.JaggednessRange.y:0.#})"
                    : "Jaggedness (Terrain-first only)",
                _ => view.ToString()
            };
        }

        private void DrawView(string title, Texture2D texture, bool allowPan, float fixedSize = 0f){
            EditorGUILayout.BeginVertical();
            {
                if (title != null) EditorGUILayout.LabelField(title, EditorStyles.boldLabel);

                Rect rect = fixedSize > 0f
                    ? GUILayoutUtility.GetRect(fixedSize, fixedSize, GUILayout.ExpandWidth(false))
                    : GUILayoutUtility.GetRect(160f, 160f, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));

                float size = Mathf.Min(rect.width, rect.height);
                rect.width = size;
                rect.height = size;

                EditorGUI.DrawRect(rect, new Color(0.1f, 0.1f, 0.1f, 1f));
                if (texture) GUI.DrawTexture(rect, texture, ScaleMode.ScaleToFit, false);
                if (allowPan) HandlePanInput(rect);
            }
            EditorGUILayout.EndVertical();
        }

        private void HandlePanInput(Rect viewRect){
            Event e = Event.current;
            if (e == null) return;

            switch (e.type){
                case EventType.MouseDown when e.button == 0 && viewRect.Contains(e.mousePosition):
                    _isDragging = true;
                    _dragStartMouse = e.mousePosition;
                    _dragStartOffset = _worldOffset;
                    e.Use();
                    return;
                case EventType.MouseDrag when _isDragging:{
                    Vector2 delta = e.mousePosition - _dragStartMouse;
                    // One view pixel is one sample; views are drawn at most ViewResolution wide.
                    delta *= ResolutionOptions[_resolutionIndex] / (float)ViewResolution;
                    _worldOffset = _dragStartOffset - new Vector2Int(Mathf.RoundToInt(delta.x), Mathf.RoundToInt(-delta.y));
                    RequestRebuild();
                    Repaint();
                    e.Use();
                    return;
                }
                case EventType.MouseUp when _isDragging:
                    _isDragging = false;
                    e.Use();
                    break;
            }
        }

        private void ReloadAssets(){
            if (!_settings){
                string[] guids = AssetDatabase.FindAssets("t:VoxelEngineSettings");
                if (guids.Length > 0)
                    _settings = AssetDatabase.LoadAssetAtPath<VoxelEngineSettings>(AssetDatabase.GUIDToAssetPath(guids[0]));
            }

            DestroyEditors();
            RequestRebuild();
            Repaint();
        }

        #endregion

        #region Build

        /// <summary>
        ///     Marks the views stale. With auto rebuild on, <see cref="OnEditorUpdate" /> builds once no change has
        ///     come in for <see cref="RebuildDebounceSeconds" />, so dragging a slider or panning starts one build
        ///     per pause instead of one after another. The Rebuild button and undo/redo build at once.
        /// </summary>
        private void RequestRebuild(){
            _needsRebuild = true;
            _lastChangeTime = EditorApplication.timeSinceStartup;
        }

        /// <summary>
        ///     Rebuilds the Burst configs (so edits take effect) and schedules pass A and, in compare mode, pass B
        ///     over the same region and seed, plus the distribution plane of A. The spline plots are recomputed
        ///     synchronously (a few thousand spline evaluations).
        /// </summary>
        private void ScheduleBuild(){
            if (_buildInProgress || !_needsRebuild) return;
            _needsRebuild = false;
            if (!_settings || !_settings.WorldGen || _passA == null) return;
            WorldGenSettings asset = _settings.WorldGen;
            EnsureConfigB(asset);

            int resolution = ResolutionOptions[Mathf.Clamp(_resolutionIndex, 0, ResolutionOptions.Length - 1)];
            int viewSize = Mathf.Min(resolution, ViewResolution);
            int stride = Mathf.Max(1, resolution / ViewResolution);
            (int2 origin, _) = FramedRegion(); // the world offset sits at the centre of the view
            PreviewViewOptions options = new(){
                ShowTemperature = _showTemperature,
                ShowContinental = _showContinental,
                ShowHumidity = _showHumidity,
                Hillshade = _hillshade,
                Exaggeration = _exaggeration
            };

            ComputePlots(asset);

            JobHandle handle = default;
            _scheduledA = _passA.Schedule(asset, null, _settings.Seed, origin, viewSize, stride, options, out JobHandle a);
            if (_scheduledA){
                handle = JobHandle.CombineDependencies(handle, a, ScheduleDistribution(asset));
            }

            _scheduledB = false;
            if (_compare && _passB.Schedule(asset, _configB, _settings.Seed, origin, viewSize, stride, options, out JobHandle b)){
                _scheduledB = true;
                handle = JobHandle.CombineDependencies(handle, b);
            }

            if (!_scheduledA && !_scheduledB){
                UpdateDiff();
                Repaint();
                return;
            }

            _buildAsset = asset;
            _buildHandle = handle;
            _buildInProgress = true;
        }

        private void EnsureConfigB(WorldGenSettings asset){
            if (_configB != null && _configBInitialized) return;
            _configB = PreviewConfiguration.Capture(asset);
            _configBInitialized = true;
        }

        private JobHandle ScheduleDistribution(WorldGenSettings asset){
            WorldGenConfig config = _passA.Config;
            _jobDistributionPixels = new NativeArray<Color32>(DistributionResolution * DistributionResolution, Allocator.Persistent);
            _jobDistributionColors = new NativeArray<Color32>(Mathf.Max(1, config.BiomeCount), Allocator.Persistent);
            for (int i = 0; i < config.BiomeCount; i++){
                Biome biome = asset.Biomes.Biomes[i];
                _jobDistributionColors[i] = biome ? (Color32)biome.RepresentativeColor : new Color32(0, 0, 0, 255);
            }

            return new DistributionJob{
                Resolution = DistributionResolution,
                Continental = _planeContinental,
                Erosion = _planeErosion,
                PV = _planePV,
                Height = _planeHeight,
                TerrainFirst = config.Architecture == GenArchitecture.TerrainFirst ? (byte)1 : (byte)0,
                Config = config,
                BiomeColors = _jobDistributionColors,
                Output = _jobDistributionPixels
            }.Schedule(_jobDistributionPixels.Length, 64);
        }

        private void ComputePlots(WorldGenSettings asset){
            _plots = SplinePlots.Compute(asset.Splines, new float3(_planeContinental, _planeErosion, _planePV), PlotSamples, out _plotsError);
        }

        private void FinalizeScheduledBuild(bool applyResult){
            if (_buildInProgress){
                _buildHandle.Complete();
                _buildInProgress = false;
                if (applyResult && _buildAsset){
                    if (_scheduledA){
                        _passA.Finish(_buildAsset);
                        PreviewPass.EnsureTexture(ref _distributionTexture, DistributionResolution);
                        _distributionTexture.SetPixelData(_jobDistributionPixels, 0);
                        _distributionTexture.Apply(false);
                    }

                    if (_scheduledB) _passB.Finish(_buildAsset);
                    UpdateDiff();
                }
            }

            if (_jobDistributionPixels.IsCreated) _jobDistributionPixels.Dispose();
            if (_jobDistributionColors.IsCreated) _jobDistributionColors.Dispose();
            Repaint();
        }

        /// <summary>
        ///     The difference view of the selected view kind. Height views are signed (B − A); biome classes,
        ///     surface source and flora are mismatch masks; the other views compare the pixels. Recomputed after
        ///     every build and when the compare view changes (no resampling).
        /// </summary>
        private void UpdateDiff(){
            _diffNote = null;
            if (!_compare) return;
            if (_passA == null || _passB == null || !_passA.HasResult || !_passB.HasResult){
                _diffNote = "Waiting for both configurations (see the errors on the left if a config fails).";
                return;
            }

            if (_passA.Size != _passB.Size || _passA.Stride != _passB.Stride){
                _diffNote = "A and B were sampled at different extents; rebuilding.";
                RequestRebuild();
                return;
            }

            int count = _passA.Size * _passA.Size;
            if (!_diffPixels.IsCreated || _diffPixels.Length != count){
                if (_diffPixels.IsCreated) _diffPixels.Dispose();
                _diffPixels = new NativeArray<Color32>(count, Allocator.Persistent);
            }

            _diffSigned = _compareView == PreviewView.Height;
            _diffStats = _compareView switch{
                PreviewView.Height => PreviewDiff.Signed(_passA.Height, _passB.Height, _diffPixels),
                PreviewView.HardBiome => PreviewDiff.Mismatch(_passA.HardBiome, _passB.HardBiome, _passA.Pixels(PreviewView.HardBiome), _diffPixels),
                PreviewView.DominantBiome => PreviewDiff.Mismatch(_passA.DominantBiome, _passB.DominantBiome, _passA.Pixels(PreviewView.DominantBiome), _diffPixels),
                PreviewView.SurfaceSource => PreviewDiff.Mismatch(_passA.SurfaceSource, _passB.SurfaceSource, _passA.Pixels(PreviewView.SurfaceSource), _diffPixels),
                PreviewView.Flora => PreviewDiff.Mismatch(_passA.FloraSpecies, _passB.FloraSpecies, _passA.Pixels(PreviewView.Flora), _diffPixels),
                _ => PreviewDiff.Pixels(_passA.Pixels(_compareView), _passB.Pixels(_compareView), _diffPixels)
            };
            PreviewPass.EnsureTexture(ref _diffTexture, _passA.Size);
            _diffTexture.SetPixelData(_diffPixels, 0);
            _diffTexture.Apply(false);
        }

        #endregion

        #region Jobs

        /// <summary>
        ///     The classifier's T x F plane: x → F, y → T at fixed C, E, PV (and h in Terrain-first, 6D). Uses the
        ///     same <see cref="BiomeClassifier" /> as the sampler, so the plane is the hard class the world would get
        ///     at those field values, tie rule included.
        /// </summary>
        [BurstCompile(CompileSynchronously = true, FloatMode = FloatMode.Strict)]
        private struct DistributionJob : IJobParallelFor{
            public int Resolution;
            public float Continental;
            public float Erosion;
            public float PV;
            public float Height;
            public byte TerrainFirst;
            [ReadOnly] public WorldGenConfig Config;
            [ReadOnly] public NativeArray<Color32> BiomeColors;
            [WriteOnly] public NativeArray<Color32> Output;

            public void Execute(int index){
                int x = index % Resolution;
                int y = index / Resolution;
                float step = 1f / Resolution;
                WorldFields f = new(){
                    F = (x + 0.5f) * step,
                    T = (y + 0.5f) * step,
                    C = Continental,
                    E = Erosion,
                    W = 0f,
                    PV = PV
                };
                int biome = TerrainFirst == 1
                    ? BiomeClassifier.Classify(f, Height, Config)
                    : BiomeClassifier.Classify(f, Config);
                Output[index] = biome >= 0 && biome < BiomeColors.Length ? BiomeColors[biome] : new Color32(0, 0, 0, 255);
            }
        }

        #endregion
    }
}
