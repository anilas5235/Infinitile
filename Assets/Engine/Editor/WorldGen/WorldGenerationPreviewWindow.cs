using System;
using System.Collections.Generic;
using Engine.Scripts.Settings;
using Engine.Scripts.VoxelConfig.Data.Generation;
using Engine.Scripts.WorldGen.Classify;
using Engine.Scripts.WorldGen.Core;
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
    ///     World-generation preview on the spec pipeline (Phase 2, INT-40: the only pipeline). Every view is filled
    ///     through <see cref="WorldGenApi.SampleRegion" />, so it shows exactly what <c>WorldSampler</c> computes for
    ///     the framed region (TST-E12): hard biome class, height with the clamped share (GEN-12), climate channels,
    ///     erosion, peaks and valleys and, in Terrain-first, the spline parameters Offset, Amp and J (TRN-06). The
    ///     left column edits the <see cref="WorldGenSettings" /> and its biomes in place and shows the classifier's
    ///     T x F plane at a chosen C, E, PV (and h). Edits rebuild the preview; nothing is written to assets beyond
    ///     the inspector edits the user makes.
    /// </summary>
    public partial class WorldGenerationPreviewWindow : EditorWindow{
        private const int ViewResolution = 512;
        private const int DistributionResolution = 256;
        private static readonly int[] ResolutionOptions ={ 128, 256, 512, 1024, 2048, 4096, 8192, 16384 };
        private static readonly string[] ResolutionLabels = Array.ConvertAll(ResolutionOptions, r => $"{r}x{r}");

        // --- Source -------------------------------------------------------------------------------------------------
        private VoxelEngineSettings _settings;
        private WorldGenConfig _config; // owned by the window (D-05); rebuilt for every build, disposed on close
        private string _configError;
        private bool _autoRebuild = true;
        private bool _needsRebuild = true;
        private bool _buildInProgress;
        private JobHandle _buildHandle;

        // --- Framing ------------------------------------------------------------------------------------------------
        private Vector2Int _worldOffset;
        private int _resolutionIndex = 2;
        private bool _isDragging;
        private Vector2 _dragStartMouse;
        private Vector2Int _dragStartOffset;

        // --- Views --------------------------------------------------------------------------------------------------
        private bool _showTemperature = true;
        private bool _showContinental = true;
        private bool _showHumidity = true;
        private Texture2D _biomeTexture;
        private Texture2D _heightTexture;
        private Texture2D _climateTexture;
        private Texture2D _erosionTexture;
        private Texture2D _pvTexture;
        private Texture2D _offsetTexture;
        private Texture2D _ampTexture;
        private Texture2D _jaggednessTexture;
        private Texture2D _distributionTexture;
        private int _viewSize;
        private bool _terrainFirst;
        private float _clampedShare;
        private float2 _ampRange;
        private float2 _jaggednessRange;

        // --- Distribution plane (classifier over T x F) ---------------------------------------------------------------
        private float _planeContinental = 0.5f;
        private float _planeErosion = 0.5f;
        private float _planePV;
        private float _planeHeight = 0.5f;

        // --- Editors ------------------------------------------------------------------------------------------------
        private UnityEditor.Editor _settingsEditor;
        private UnityEditor.Editor _biomeEditor;
        private int _selectedBiomeIndex;
        private bool _showSettingsEditor = true;
        private bool _showBiomeEditor = true;
        private Vector2 _leftScroll;

        // --- Job data (TempJob, alive for one build) ---------------------------------------------------------------
        private NativeArray<WorldFields> _jobFields;
        private NativeArray<int> _jobHardBiome;
        private NativeArray<float> _jobHeight;
        private NativeArray<byte> _jobClamped;
        private NativeArray<float3> _jobTerrainParams;
        private NativeArray<Color32> _jobBiomeColors;
        private NativeArray<Color32> _jobBiomePixels;
        private NativeArray<Color32> _jobHeightPixels;
        private NativeArray<Color32> _jobClimatePixels;
        private NativeArray<Color32> _jobErosionPixels;
        private NativeArray<Color32> _jobPvPixels;
        private NativeArray<Color32> _jobDistributionPixels;

        [MenuItem("Infinitile/World Generation Preview")]
        private static void OpenWindow(){
            WorldGenerationPreviewWindow window = GetWindow<WorldGenerationPreviewWindow>();
            window.titleContent = new GUIContent("World Gen Preview");
            window.minSize = new Vector2(1400f, 620f);
            window.Show();
        }

        private void OnEnable(){
            EditorApplication.update += OnEditorUpdate;
            Undo.undoRedoPerformed += RequestRebuild;
            ReloadAssets();
        }

        private void OnDisable(){
            EditorApplication.update -= OnEditorUpdate;
            Undo.undoRedoPerformed -= RequestRebuild;
            FinalizeScheduledBuild(false); // a running build still reads the config
            DestroyEditors();
            DestroyTexture(ref _biomeTexture);
            DestroyTexture(ref _heightTexture);
            DestroyTexture(ref _climateTexture);
            DestroyTexture(ref _erosionTexture);
            DestroyTexture(ref _pvTexture);
            DestroyTexture(ref _offsetTexture);
            DestroyTexture(ref _ampTexture);
            DestroyTexture(ref _jaggednessTexture);
            DestroyTexture(ref _distributionTexture);
            DisposeConfig();
        }

        private void OnDestroy(){
            FinalizeScheduledBuild(false);
            DisposeConfig();
        }

        private void DisposeConfig(){
            _config.Dispose(); // safe on a default instance
            _config = default;
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

            if (_autoRebuild && _needsRebuild && !_buildInProgress) ScheduleBuild();
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
            Vector2 windowSize = position.size;
            EditorGUI.BeginChangeCheck();
            EditorGUILayout.BeginHorizontal();
            {
                EditorGUILayout.BeginVertical(GUILayout.Width(windowSize.x * .26f));
                {
                    _leftScroll = EditorGUILayout.BeginScrollView(_leftScroll);
                    DrawToolbar();
                    DrawDistributionPanel();
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

        /// <summary>Hook for additional left-column panels (the BIO-20 calibration section).</summary>
        partial void DrawExtraPanelsImpl();

        private void DrawExtraPanels(){
            DrawExtraPanelsImpl();
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
                    EditorGUI.BeginChangeCheck();
                    int seed = EditorGUILayout.IntField("Seed", _settings.Seed);
                    if (EditorGUI.EndChangeCheck()){
                        Undo.RecordObject(_settings, "Change world seed");
                        _settings.Seed = seed;
                        EditorUtility.SetDirty(_settings);
                    }

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

                if (!string.IsNullOrEmpty(_configError))
                    EditorGUILayout.HelpBox("WorldGenSettings.ToConfig failed:\n" + _configError, MessageType.Error);
            }
            EditorGUILayout.EndVertical();
        }

        /// <summary>
        ///     The classifier's T x F plane (§6.4): every pixel is <see cref="BiomeClassifier.Classify" /> at
        ///     (T, F) with the chosen C, E, PV and, in Terrain-first, the normalised height h (6D).
        /// </summary>
        private void DrawDistributionPanel(){
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            {
                EditorGUILayout.LabelField("Biome distribution (X = Humidity F, Y = Temperature T)", EditorStyles.boldLabel);
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

        private void DrawSettingsPanel(){
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            {
                _showSettingsEditor = EditorGUILayout.Foldout(_showSettingsEditor, "WorldGenSettings", true, EditorStyles.foldoutHeader);
                if (_showSettingsEditor){
                    if (!WorldGenSettings){
                        EditorGUILayout.HelpBox("Assign a WorldGenSettings asset to the VoxelEngineSettings (INT-01).", MessageType.Info);
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
                    EditorGUILayout.HelpBox("The WorldGenSettings has no BiomeSet with biomes (GEN-21).", MessageType.Warning);
                    EditorGUILayout.EndVertical();
                    return;
                }

                // Legend: index, colour swatch and name, in classification order (GEN-23 ties → lower index).
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
                EditorGUILayout.LabelField(
                    $"{(_terrainFirst ? "Terrain-first" : "Biome-first")} · drag a view to pan · 1 sample per block up to 512, then every {Mathf.Max(1, ResolutionOptions[_resolutionIndex] / ViewResolution)}th column",
                    EditorStyles.miniLabel);

                EditorGUILayout.BeginHorizontal();
                {
                    EditorGUILayout.BeginVertical();
                    DrawView(_terrainFirst ? "Biome (6D hard class)" : "Biome (5D hard class)", _biomeTexture, true);
                    DrawView($"Height (clamped {_clampedShare:P1}, red = clamped, blue = below water)", _heightTexture, true);
                    EditorGUILayout.EndVertical();

                    EditorGUILayout.BeginVertical();
                    DrawView("Climate (T, C, F)", _climateTexture, true);
                    DrawView("Erosion", _erosionTexture, true);
                    EditorGUILayout.EndVertical();

                    EditorGUILayout.BeginVertical();
                    DrawView("Peaks & valleys", _pvTexture, true);
                    if (_terrainFirst) DrawView("Offset (Hmin..Hmax, blue below water)", _offsetTexture, true);
                    else GUILayout.FlexibleSpace();
                    EditorGUILayout.EndVertical();

                    if (_terrainFirst){
                        EditorGUILayout.BeginVertical();
                        DrawView($"Amp ({_ampRange.x:0.#}..{_ampRange.y:0.#})", _ampTexture, true);
                        DrawView($"Jaggedness ({_jaggednessRange.x:0.#}..{_jaggednessRange.y:0.#})", _jaggednessTexture, true);
                        EditorGUILayout.EndVertical();
                    }
                }
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.EndVertical();
        }

        private void DrawView(string title, Texture2D texture, bool allowPan, float fixedSize = 0f){
            EditorGUILayout.BeginVertical();
            {
                if (title != null) EditorGUILayout.LabelField(title, EditorStyles.boldLabel);

                Rect rect = fixedSize > 0f
                    ? GUILayoutUtility.GetRect(fixedSize, fixedSize, GUILayout.ExpandWidth(false))
                    : GUILayoutUtility.GetRect(200f, 200f, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));

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

        private void RequestRebuild(){
            _needsRebuild = true;
            if (_autoRebuild && !_buildInProgress) ScheduleBuild();
        }

        /// <summary>
        ///     Rebuilds the Burst mirror from the settings (so inspector edits take effect), samples the framed region
        ///     through <see cref="WorldGenApi.SampleRegion" /> and converts the buffers to textures in jobs. The
        ///     distribution plane runs the classifier directly on the same config.
        /// </summary>
        private void ScheduleBuild(){
            if (_buildInProgress || !_needsRebuild) return;
            _needsRebuild = false;
            if (!_settings || !_settings.WorldGen) return;

            DisposeJobData();
            DisposeConfig(); // D-05: no build is in progress, so the previous mirror is no longer read
            try{
                _config = _settings.WorldGen.ToConfig(_settings.Seed);
                _configError = null;
            }
            catch (Exception e){
                _configError = e.Message;
                Repaint();
                return;
            }

            int resolution = ResolutionOptions[Mathf.Clamp(_resolutionIndex, 0, ResolutionOptions.Length - 1)];
            int viewSize = Mathf.Min(resolution, ViewResolution);
            int stride = Mathf.Max(1, resolution / ViewResolution);
            int count = viewSize * viewSize;
            _viewSize = viewSize;
            _terrainFirst = _config.Architecture == GenArchitecture.TerrainFirst;

            _jobFields = new NativeArray<WorldFields>(count, Allocator.TempJob);
            _jobHardBiome = new NativeArray<int>(count, Allocator.TempJob);
            _jobHeight = new NativeArray<float>(count, Allocator.TempJob);
            _jobClamped = new NativeArray<byte>(count, Allocator.TempJob);
            if (_terrainFirst) _jobTerrainParams = new NativeArray<float3>(count, Allocator.TempJob);
            _jobBiomePixels = new NativeArray<Color32>(count, Allocator.TempJob);
            _jobHeightPixels = new NativeArray<Color32>(count, Allocator.TempJob);
            _jobClimatePixels = new NativeArray<Color32>(count, Allocator.TempJob);
            _jobErosionPixels = new NativeArray<Color32>(count, Allocator.TempJob);
            _jobPvPixels = new NativeArray<Color32>(count, Allocator.TempJob);
            _jobDistributionPixels = new NativeArray<Color32>(DistributionResolution * DistributionResolution, Allocator.TempJob);

            int biomeCount = _config.BiomeCount;
            _jobBiomeColors = new NativeArray<Color32>(Mathf.Max(1, biomeCount), Allocator.TempJob);
            for (int i = 0; i < biomeCount; i++){
                Biome biome = _settings.WorldGen.Biomes.Biomes[i];
                _jobBiomeColors[i] = biome ? (Color32)biome.RepresentativeColor : new Color32(0, 0, 0, 255);
            }

            // The world offset sits at the centre of the view.
            (int2 origin, _) = FramedRegion();
            WorldGenRequest request = new(){
                Config = _config, Origin = origin, Size = new int2(viewSize, viewSize), Stride = stride
            };
            RegionBuffers buffers = new(){
                Fields = _jobFields,
                HardBiome = _jobHardBiome,
                Height = _jobHeight,
                Clamped = _jobClamped,
                TerrainParams = _jobTerrainParams // unconstructed in Biome-first: SampleRegion skips it
            };
            JobHandle sampled = WorldGenApi.SampleRegion(request, ref buffers);

            JobHandle views = new SpecViewJob{
                Fields = _jobFields,
                HardBiome = _jobHardBiome,
                Height = _jobHeight,
                Clamped = _jobClamped,
                BiomeColors = _jobBiomeColors,
                HeightMin = _config.HeightMin,
                HeightMax = _config.HeightMax,
                WaterLevel = _settings.WorldGen.WaterLevel,
                ShowTemperature = _showTemperature ? (byte)1 : (byte)0,
                ShowContinental = _showContinental ? (byte)1 : (byte)0,
                ShowHumidity = _showHumidity ? (byte)1 : (byte)0,
                BiomeView = _jobBiomePixels,
                HeightView = _jobHeightPixels,
                ClimateView = _jobClimatePixels,
                ErosionView = _jobErosionPixels,
                PvView = _jobPvPixels
            }.Schedule(count, 64, sampled);

            JobHandle distribution = new DistributionJob{
                Resolution = DistributionResolution,
                Continental = _planeContinental,
                Erosion = _planeErosion,
                PV = _planePV,
                Height = _planeHeight,
                TerrainFirst = _terrainFirst ? (byte)1 : (byte)0,
                Config = _config,
                BiomeColors = _jobBiomeColors,
                Output = _jobDistributionPixels
            }.Schedule(_jobDistributionPixels.Length, 64);

            _buildHandle = JobHandle.CombineDependencies(views, distribution);
            _buildInProgress = true;
        }

        private void FinalizeScheduledBuild(bool applyResult){
            if (!_buildInProgress){
                DisposeJobData();
                return;
            }

            _buildHandle.Complete();

            if (applyResult){
                Apply(ref _biomeTexture, _jobBiomePixels, _viewSize);
                Apply(ref _heightTexture, _jobHeightPixels, _viewSize);
                Apply(ref _climateTexture, _jobClimatePixels, _viewSize);
                Apply(ref _erosionTexture, _jobErosionPixels, _viewSize);
                Apply(ref _pvTexture, _jobPvPixels, _viewSize);
                Apply(ref _distributionTexture, _jobDistributionPixels, DistributionResolution);

                // GEN-12: share of columns whose height hit the clamp.
                int clamped = 0;
                for (int i = 0; i < _jobClamped.Length; i++) clamped += _jobClamped[i];
                _clampedShare = _jobClamped.Length > 0 ? clamped / (float)_jobClamped.Length : 0f;

                if (_terrainFirst) ApplyTerrainParamViews();
            }

            DisposeJobData();
            _buildInProgress = false;
            Repaint();
        }

        private static void Apply(ref Texture2D texture, NativeArray<Color32> pixels, int size){
            EnsureTexture(ref texture, size);
            texture.SetPixelData(pixels, 0);
            texture.Apply(false);
        }

        /// <summary>
        ///     TRN-06: Offset on the height scale (blue below the water level), Amp and J normalised to the range
        ///     seen in the framed region (shown in the view titles). Main-thread conversion of at most
        ///     ViewResolution x ViewResolution pixels.
        /// </summary>
        private void ApplyTerrainParamViews(){
            int count = _jobTerrainParams.Length;
            float waterLevel = _settings.WorldGen.WaterLevel;
            float2 amp = new(float.MaxValue, float.MinValue), jag = new(float.MaxValue, float.MinValue);
            for (int i = 0; i < count; i++){
                float3 t = _jobTerrainParams[i];
                amp = new float2(math.min(amp.x, t.y), math.max(amp.y, t.y));
                jag = new float2(math.min(jag.x, t.z), math.max(jag.y, t.z));
            }

            if (count == 0) amp = jag = float2.zero;
            _ampRange = amp;
            _jaggednessRange = jag;

            Color32[] offsetPixels = new Color32[count];
            Color32[] ampPixels = new Color32[count];
            Color32[] jagPixels = new Color32[count];
            float heightSpan = math.max(1f, _config.HeightMax - _config.HeightMin);
            float ampSpan = math.max(1e-6f, amp.y - amp.x);
            float jagSpan = math.max(1e-6f, jag.y - jag.x);
            for (int i = 0; i < count; i++){
                float3 t = _jobTerrainParams[i];
                byte o = (byte)math.round(math.saturate((t.x - _config.HeightMin) / heightSpan) * 255f);
                offsetPixels[i] = t.x < waterLevel
                    ? new Color32((byte)(o / 2), (byte)(o / 2), (byte)math.min(255, o + 80), 255)
                    : new Color32(o, o, o, 255);
                byte a = (byte)math.round(math.saturate((t.y - amp.x) / ampSpan) * 255f);
                ampPixels[i] = new Color32(a, a, a, 255);
                byte j = (byte)math.round(math.saturate((t.z - jag.x) / jagSpan) * 255f);
                jagPixels[i] = new Color32(j, j, j, 255);
            }

            EnsureTexture(ref _offsetTexture, _viewSize);
            EnsureTexture(ref _ampTexture, _viewSize);
            EnsureTexture(ref _jaggednessTexture, _viewSize);
            _offsetTexture.SetPixels32(offsetPixels);
            _ampTexture.SetPixels32(ampPixels);
            _jaggednessTexture.SetPixels32(jagPixels);
            _offsetTexture.Apply(false);
            _ampTexture.Apply(false);
            _jaggednessTexture.Apply(false);
        }

        private void DisposeJobData(){
            if (_jobFields.IsCreated) _jobFields.Dispose();
            if (_jobHardBiome.IsCreated) _jobHardBiome.Dispose();
            if (_jobHeight.IsCreated) _jobHeight.Dispose();
            if (_jobClamped.IsCreated) _jobClamped.Dispose();
            if (_jobTerrainParams.IsCreated) _jobTerrainParams.Dispose();
            if (_jobBiomeColors.IsCreated) _jobBiomeColors.Dispose();
            if (_jobBiomePixels.IsCreated) _jobBiomePixels.Dispose();
            if (_jobHeightPixels.IsCreated) _jobHeightPixels.Dispose();
            if (_jobClimatePixels.IsCreated) _jobClimatePixels.Dispose();
            if (_jobErosionPixels.IsCreated) _jobErosionPixels.Dispose();
            if (_jobPvPixels.IsCreated) _jobPvPixels.Dispose();
            if (_jobDistributionPixels.IsCreated) _jobDistributionPixels.Dispose();
        }

        #endregion

        #region TextureHelpers

        private static void EnsureTexture(ref Texture2D texture, int size){
            if (texture && texture.width == size && texture.height == size) return;

            DestroyTexture(ref texture);
            texture = new Texture2D(size, size, TextureFormat.RGBA32, false){
                name = "WorldGenPreviewTexture",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave
            };
        }

        private static void DestroyTexture(ref Texture2D texture){
            if (!texture) return;

            DestroyImmediate(texture);
            texture = null;
        }

        #endregion

        #region Jobs

        /// <summary>Converts the sampled region buffers to the view textures. Reads only; no sampler call.</summary>
        [BurstCompile]
        private struct SpecViewJob : IJobParallelFor{
            [ReadOnly] public NativeArray<WorldFields> Fields;
            [ReadOnly] public NativeArray<int> HardBiome;
            [ReadOnly] public NativeArray<float> Height;
            [ReadOnly] public NativeArray<byte> Clamped;
            [ReadOnly] public NativeArray<Color32> BiomeColors;
            public float HeightMin;
            public float HeightMax;
            public int WaterLevel;
            public byte ShowTemperature;
            public byte ShowContinental;
            public byte ShowHumidity;
            [WriteOnly] public NativeArray<Color32> BiomeView;
            [WriteOnly] public NativeArray<Color32> HeightView;
            [WriteOnly] public NativeArray<Color32> ClimateView;
            [WriteOnly] public NativeArray<Color32> ErosionView;
            [WriteOnly] public NativeArray<Color32> PvView;

            public void Execute(int index){
                int biome = HardBiome[index];
                BiomeView[index] = biome >= 0 && biome < BiomeColors.Length ? BiomeColors[biome] : new Color32(0, 0, 0, 255);

                float h = Height[index];
                byte v = (byte)math.round(math.saturate((h - HeightMin) / math.max(1f, HeightMax - HeightMin)) * 255f);
                Color32 heightColor = h < WaterLevel
                    ? new Color32((byte)(v / 2), (byte)(v / 2), (byte)math.min(255, v + 80), 255) // below water: blue tint
                    : new Color32(v, v, v, 255);
                if (Clamped[index] != 0) heightColor = new Color32(255, 0, 0, 255); // GEN-12
                HeightView[index] = heightColor;

                WorldFields f = Fields[index];
                byte t = ShowTemperature == 1 ? (byte)math.round(math.saturate(f.T) * 255f) : (byte)0;
                byte c = ShowContinental == 1 ? (byte)math.round(math.saturate(f.C) * 255f) : (byte)0;
                byte hum = ShowHumidity == 1 ? (byte)math.round(math.saturate(f.F) * 255f) : (byte)0;
                ClimateView[index] = new Color32(t, c, hum, 255);

                byte e = (byte)math.round(math.saturate(f.E) * 255f);
                ErosionView[index] = new Color32(e, e, e, 255);

                byte pv = (byte)math.round(math.saturate((f.PV + 1f) * 0.5f) * 255f);
                PvView[index] = new Color32(pv, pv, pv, 255);
            }
        }

        /// <summary>
        ///     The classifier's T x F plane: x → F, y → T at fixed C, E, PV (and h in Terrain-first, 6D). Uses the
        ///     same <see cref="BiomeClassifier" /> as the sampler, so the plane is the hard class the world would get
        ///     at those field values (GEN-23 tie rule included).
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
