using System.Collections.Generic;
using Engine.Scripts.Jobs.Chunk;
using Engine.Scripts.Noise;
using Engine.Scripts.Settings;
using Engine.Scripts.Utils.Extensions;
using Engine.Scripts.VoxelConfig.Data.Voxel;
using Engine.Scripts.WorldGen.Core;
using Engine.Scripts.WorldGen.Noise;
using Engine.Scripts.WorldGen.Settings;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;

namespace Engine.Scripts.VoxelConfig.Data.Generation.Editor{
    public class WorldGenerationPreviewWindow : EditorWindow{
        private const int ViewResolution = 512;
        private static readonly int[] ResolutionOptions = { 128, 256, 512, 1024, 2048, 4096, 8192, 16384 };
        private readonly List<Biome> _biomes = new();

        private readonly List<VoxelDataPackage> _packages = new();
        private bool _autoRebuild = true;
        private UnityEditor.Editor _biomeEditor;

        private Texture2D _biomeTexture;
        private JobHandle _buildHandle;
        private bool _buildInProgress;
        private Texture2D _climateTexture;
        private Vector2 _dragStartMouse;
        private Vector2Int _dragStartOffset;

        private GeneratorConfig _generatorConfig;
        private Texture2D _heightTexture;
        private Texture2D _humidityTemperatureTexture;
        private bool _isDragging;
        private NativeArray<Color32> _jobBiomeColors;
        private NativeArray<Color32> _jobBiomePixels;
        private NativeArray<Color32> _jobClimatePixels;
        private NativeArray<Color32> _jobHeightPixels;
        private NativeArray<Color32> _jobHumidityTemperaturePixels;
        private bool _needsRebuild = true;
        private float _phaseContinental = 0.5f;
        private float _phaseElevation = 0.5f;
        private int _resolutionIndex;

        private int _selectedBiomeIndex;

        private VoxelEngineSettings _settings;
        private bool _showContinental = true;
        private bool _showHumidity = true;
        private bool _showTemperature = true;
        private Vector2Int _worldOffset;

        private Vector2 windowSize;

        // Spec pipeline views (Phase 0): filled through WorldGenApi.SampleRegion, so the preview shows exactly what
        // WorldSampler computes (TST-E12).
        private Texture2D _specBiomeTexture;
        private Texture2D _specHeightTexture;
        private Texture2D _erosionTexture;
        private Texture2D _pvTexture;
        private NativeArray<Color32> _jobSpecBiomeColors;
        private NativeArray<Color32> _jobSpecBiomePixels;
        private NativeArray<Color32> _jobSpecHeightPixels;
        private NativeArray<Color32> _jobErosionPixels;
        private NativeArray<Color32> _jobPvPixels;
        private NativeArray<WorldFields> _jobFields;
        private NativeArray<int> _jobHardBiome;
        private NativeArray<float> _jobHeight;
        private NativeArray<byte> _jobClamped;
        private int _specViewSize;
        private bool _specBuildScheduled;
        private float _clampedShare;

        private void OnEnable(){
            EditorApplication.update += OnEditorUpdate;
            ReloadAssets();
            _needsRebuild = true;
        }

        private void OnDisable(){
            EditorApplication.update -= OnEditorUpdate;

            if (_biomeEditor){
                DestroyImmediate(_biomeEditor);
                _biomeEditor = null;
            }

            FinalizeScheduledBuild(false);

            DestroyTexture(ref _biomeTexture);
            DestroyTexture(ref _heightTexture);
            DestroyTexture(ref _climateTexture);
            DestroyTexture(ref _humidityTemperatureTexture);
            DestroyTexture(ref _specBiomeTexture);
            DestroyTexture(ref _specHeightTexture);
            DestroyTexture(ref _erosionTexture);
            DestroyTexture(ref _pvTexture);
        }

        private void OnDestroy(){
            FinalizeScheduledBuild(false); // a running build still reads the config; completes it and frees job data
            DisposeGeneratorConfig();
        }

        /// <summary>
        ///     D-05: the preview allocates its own BiomeDefs and Voxels (no registry owns them), so it releases
        ///     them here; GeneratorConfig.Dispose releases the noise parameters and the WorldGen config.
        /// </summary>
        private void DisposeGeneratorConfig(){
            if (_generatorConfig.BiomeDefs.IsCreated) _generatorConfig.BiomeDefs.Dispose();
            if (_generatorConfig.Voxels.IsCreated) _generatorConfig.Voxels.Dispose();
            _generatorConfig.Dispose();
            _generatorConfig = default;
        }

        [MenuItem("Infinitile/World Generation Preview")]
        private static void OpenWindow(){
            WorldGenerationPreviewWindow window = GetWindow<WorldGenerationPreviewWindow>();
            window.titleContent = new GUIContent("World Gen Preview");
            window.minSize = new Vector2(1400f, 620f);
            window.Show();
        }

        private void OnEditorUpdate(){
            if (_buildInProgress && _buildHandle.IsCompleted){
                FinalizeScheduledBuild(true);
                Repaint();
            }

            if (_autoRebuild && _needsRebuild && !_buildInProgress) ScheduleBuild();
        }

        #region Draw

        private void OnGUI(){
            windowSize = position.size;
            EditorGUI.BeginChangeCheck();
            EditorGUILayout.BeginHorizontal();
            {
                EditorGUILayout.BeginVertical(GUILayout.Width(windowSize.x * .25f));
                {
                    DrawToolbar();
                    DrawBiomeDistributionView();
                    DrawBiomeEditorPanel();
                }
                EditorGUILayout.EndVertical();

                DrawViews();

                if (EditorGUI.EndChangeCheck()) RequestRebuild();
            }
            EditorGUILayout.EndHorizontal();
        }

        private void DrawToolbar(){
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            {
                EditorGUILayout.LabelField("Data Source", EditorStyles.boldLabel);
                _settings = (VoxelEngineSettings)EditorGUILayout.ObjectField("VoxelEngineSettings", _settings,
                    typeof(VoxelEngineSettings), false);

                EditorGUILayout.BeginHorizontal();
                {
                    if (GUILayout.Button("Reload SOs", GUILayout.Width(120f))) ReloadAssets();

                    using (new EditorGUI.DisabledScope(_buildInProgress)){
                        if (GUILayout.Button("Rebuild", GUILayout.Width(120f))){
                            _needsRebuild = true;
                            ScheduleBuild();
                        }
                    }

                    _autoRebuild = EditorGUILayout.ToggleLeft("Auto Rebuild", _autoRebuild, GUILayout.Width(120f));
                }
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.EndVertical();
        }

        private void DrawBiomeDistributionView(){
            EditorGUILayout.BeginVertical(EditorStyles.helpBox, GUILayout.Height(windowSize.y * .55f));
            {
                EditorGUILayout.LabelField("BiomeDistribution", EditorStyles.boldLabel);
                _phaseContinental = EditorGUILayout.Slider("Continental", _phaseContinental, 0f, 1f);
                _phaseElevation = EditorGUILayout.Slider("Elevation", _phaseElevation, 0f, 1f);

                DrawView("Biome Distribution (X=Hum, Y=Temp)", EditorStyles.helpBox, _humidityTemperatureTexture,
                    false);
            }
            EditorGUILayout.EndVertical();
        }

        private void DrawBiomeEditorPanel(){
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Biome Editing", EditorStyles.boldLabel);

            if (_biomes.Count == 0){
                EditorGUILayout.HelpBox("Keine Biome in den geladenen VoxelDataPackages gefunden.",
                    MessageType.Warning);
                EditorGUILayout.EndVertical();
                return;
            }

            string[] biomeNames = new string[_biomes.Count];
            for (int i = 0; i < _biomes.Count; i++) biomeNames[i] = _biomes[i] ? _biomes[i].name : "<null>";

            int newIndex = EditorGUILayout.Popup("Active Biome", _selectedBiomeIndex, biomeNames);
            if (newIndex != _selectedBiomeIndex){
                _selectedBiomeIndex = newIndex;
                CreateBiomeEditor();
            }

            if (_biomeEditor) _biomeEditor.OnInspectorGUI();

            EditorGUILayout.EndVertical();
        }

        private void DrawViews(){
            if (!_settings){
                EditorGUILayout.HelpBox("Bitte ein VoxelEngineSettings-Asset auswählen.", MessageType.Info);
                return;
            }

            EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
            {
                EditorGUILayout.BeginVertical();
                {
                    _worldOffset = EditorGUILayout.Vector2IntField("World Offset (X/Z)", _worldOffset);
                    _resolutionIndex = EditorGUILayout.Popup("Resolution", _resolutionIndex, new[]{
                        $"{ResolutionOptions[0]}x{ResolutionOptions[0]}",
                        $"{ResolutionOptions[1]}x{ResolutionOptions[1]}",
                        $"{ResolutionOptions[2]}x{ResolutionOptions[2]}",
                        $"{ResolutionOptions[3]}x{ResolutionOptions[3]}",
                        $"{ResolutionOptions[4]}x{ResolutionOptions[4]}",
                        $"{ResolutionOptions[5]}x{ResolutionOptions[5]}",
                        $"{ResolutionOptions[6]}x{ResolutionOptions[6]}",
                        $"{ResolutionOptions[7]}x{ResolutionOptions[7]}"
                    });
                    EditorGUILayout.LabelField("Climate RGB Channels", EditorStyles.boldLabel);
                    _showTemperature = EditorGUILayout.ToggleLeft("Temperature (R)", _showTemperature);
                    _showContinental = EditorGUILayout.ToggleLeft("Continental (G)", _showContinental);
                    _showHumidity = EditorGUILayout.ToggleLeft("Humidity (B)", _showHumidity);
                    EditorGUILayout.Separator();
                    DrawView("Biome View", EditorStyles.label, _biomeTexture, true);
                }
                EditorGUILayout.EndVertical();
                EditorGUILayout.BeginVertical();
                {
                    DrawView("Climate", EditorStyles.label, _climateTexture, true);

                    DrawView("HeightMap", EditorStyles.label, _heightTexture, true);
                }
                EditorGUILayout.EndVertical();

                DrawSpecViews();
            }
            EditorGUILayout.EndHorizontal();
        }

        /// <summary>
        ///     Spec-pipeline views (Phase 0): hard 5D biome, common-formula height with the clamped share (GEN-12),
        ///     erosion and peaks-and-valleys. Needs a WorldGenSettings asset on the VoxelEngineSettings.
        /// </summary>
        private void DrawSpecViews(){
            if (!_settings.WorldGen){
                EditorGUILayout.BeginVertical(GUILayout.Width(260f));
                EditorGUILayout.HelpBox(
                    "Assign a WorldGenSettings asset to the VoxelEngineSettings to see the spec views " +
                    "(hard biome, height, erosion, peaks & valleys).", MessageType.Info);
                EditorGUILayout.EndVertical();
                return;
            }

            EditorGUILayout.BeginVertical();
            {
                DrawView("Spec Biome (5D hard class)", EditorStyles.label, _specBiomeTexture, true);
                DrawView($"Spec Height (clamped {_clampedShare:P1})", EditorStyles.label, _specHeightTexture, true);
            }
            EditorGUILayout.EndVertical();
            EditorGUILayout.BeginVertical();
            {
                DrawView("Erosion", EditorStyles.label, _erosionTexture, true);
                DrawView("Peaks & Valleys", EditorStyles.label, _pvTexture, true);
            }
            EditorGUILayout.EndVertical();
        }

        private void DrawView(string viewTitle, GUIStyle style, Texture2D texture, bool allowPan){
            EditorGUILayout.BeginVertical(style);
            {
                EditorGUILayout.LabelField(viewTitle, EditorStyles.boldLabel);

                Rect rect = GUILayoutUtility.GetRect(200f, 200f, GUILayout.ExpandWidth(true),
                    GUILayout.ExpandHeight(true));

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
                    _worldOffset = _dragStartOffset -
                                   new Vector2Int(Mathf.RoundToInt(delta.x), Mathf.RoundToInt(-delta.y));
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
                string[] settingsGuids = AssetDatabase.FindAssets("t:VoxelEngineSettings");
                if (settingsGuids.Length > 0){
                    string settingsPath = AssetDatabase.GUIDToAssetPath(settingsGuids[0]);
                    _settings = AssetDatabase.LoadAssetAtPath<VoxelEngineSettings>(settingsPath);
                }
            }

            _packages.Clear();
            _biomes.Clear();
            FinalizeScheduledBuild(false); // a running build still reads the config
            DisposeGeneratorConfig();

            string[] packageGuids = AssetDatabase.FindAssets("t:VoxelDataPackage");
            HashSet<Biome> uniqueBiomes = new();
            foreach (string guid in packageGuids){
                string path = AssetDatabase.GUIDToAssetPath(guid);
                VoxelDataPackage package = AssetDatabase.LoadAssetAtPath<VoxelDataPackage>(path);
                if (!package) continue;

                _packages.Add(package);
                if (package.biomes == null) continue;

                foreach (Biome biome in package.biomes){
                    if (!biome || !uniqueBiomes.Add(biome)) continue;

                    _biomes.Add(biome);
                }
            }

            _selectedBiomeIndex = Mathf.Clamp(_selectedBiomeIndex, 0, Mathf.Max(0, _biomes.Count - 1));
            // D-05: unlike the runtime config, the preview owns its biome and voxel containers (see DisposeGeneratorConfig).
            _generatorConfig = new GeneratorConfig{
                WaterLevel = _settings.Noise.WaterLevel,
                GlobalSeed = _settings.Seed,
                BiomeDefs = new NativeArray<Biome.BiomeDef>(_biomes.Count, Allocator.Domain),
                Voxels = new NativeHashMap<FixedString32Bytes, Voxel.Voxel.VoxelDef>(0, Allocator.Domain)
            };

            for (int i = 0; i < _biomes.Count; i++) _generatorConfig.BiomeDefs[i] = _biomes[i].ToStruct();

            CreateBiomeEditor();
            RequestRebuild();
            Repaint();
        }

        private void CreateBiomeEditor(){
            if (_biomeEditor){
                DestroyImmediate(_biomeEditor);
                _biomeEditor = null;
            }

            if (_selectedBiomeIndex < 0 || _selectedBiomeIndex >= _biomes.Count) return;

            Biome biome = _biomes[_selectedBiomeIndex];
            if (biome) UnityEditor.Editor.CreateCachedEditor(biome, null, ref _biomeEditor);
        }

        #endregion

        #region JobScheduling

        private void RequestRebuild(){
            _needsRebuild = true;
            if (_autoRebuild && !_buildInProgress) ScheduleBuild();
        }

        private void ScheduleBuild(){
            if (_buildInProgress || !_needsRebuild) return;
            if (!_settings || !_settings.Noise) return;

            int biomeCount = _biomes.Count;
            int resolution = ResolutionOptions[Mathf.Clamp(_resolutionIndex, 0, ResolutionOptions.Length - 1)];
            const int pixelCount = ViewResolution * ViewResolution;

            if (biomeCount == 0){
                EnsureTexture(ref _biomeTexture, ViewResolution);
                EnsureTexture(ref _heightTexture, ViewResolution);
                EnsureTexture(ref _climateTexture, ViewResolution);
                EnsureTexture(ref _humidityTemperatureTexture, ViewResolution);
                FillTexture(_biomeTexture, Color.black);
                FillTexture(_heightTexture, Color.black);
                FillTexture(_climateTexture, Color.black);
                FillTexture(_humidityTemperatureTexture, Color.black);
                _needsRebuild = false;
                return;
            }

            DisposeJobData();

            _jobBiomeColors = new NativeArray<Color32>(biomeCount, Allocator.TempJob);
            _jobBiomePixels = new NativeArray<Color32>(pixelCount, Allocator.TempJob);
            _jobHeightPixels = new NativeArray<Color32>(pixelCount, Allocator.TempJob);
            _jobClimatePixels = new NativeArray<Color32>(pixelCount, Allocator.TempJob);
            _jobHumidityTemperaturePixels = new NativeArray<Color32>(pixelCount, Allocator.TempJob);

            for (int i = 0; i < biomeCount; i++){
                Biome biome = _biomes[i];
                if (!biome){
                    _jobBiomeColors[i] = new Color32(0, 0, 0, 255);
                    continue;
                }

                _jobBiomeColors[i] = biome.RepresentativeColor;
            }

            // D-05: the previous build's noise parameters are no longer read (no build is in progress here)
            _generatorConfig.NoiseParams.Dispose();
            _generatorConfig.NoiseParams = _settings.Noise.ToNoiseParameters(_settings.Seed);
            bool specAvailable = RebuildWorldGenConfig();

            WorldViewJob job = new(){
                Resolution = resolution,
                WorldOffset = _worldOffset.Int2(),
                ShowHumidity = _showHumidity ? (byte)1 : (byte)0,
                ShowTemperature = _showTemperature ? (byte)1 : (byte)0,
                ShowContinental = _showContinental ? (byte)1 : (byte)0,
                Config = _generatorConfig,
                BiomeColors = _jobBiomeColors,
                BiomeWorldView = _jobBiomePixels,
                HeightWorldView = _jobHeightPixels,
                ClimateWorldView = _jobClimatePixels
            };

            BiomeDistributionJob distributionJob = new(){
                Resolution = ViewResolution,
                Continental = _phaseContinental,
                Elevation = _phaseElevation,
                Config = _generatorConfig,
                BiomeColors = _jobBiomeColors,
                Output = _jobHumidityTemperaturePixels
            };

            JobHandle biomeHandle = job.Schedule(pixelCount, 64);
            JobHandle distributionHandle = distributionJob.Schedule(pixelCount, 64);

            JobHandle combined = JobHandle.CombineDependencies(biomeHandle, distributionHandle);
            if (specAvailable) combined = JobHandle.CombineDependencies(combined, ScheduleSpecBuild(resolution));

            _buildHandle = combined;
            _buildInProgress = true;
            _needsRebuild = false;
        }

        /// <summary>
        ///     Rebuilds the WorldGen mirror before any job captures the config (the legacy jobs carry the whole
        ///     GeneratorConfig, so the mirror must be constructed even when no WorldGenSettings is assigned).
        ///     Returns whether the spec views can be built.
        /// </summary>
        private bool RebuildWorldGenConfig(){
            // D-05: the previous build is complete here, so its config is no longer read.
            _generatorConfig.WorldGen.Dispose();
            _specBuildScheduled = false;
            if (!_settings.WorldGen){
                _generatorConfig.WorldGen = WorldGenConfig.CreateEmpty((uint)_settings.Seed);
                return false;
            }

            try{
                _generatorConfig.WorldGen = _settings.WorldGen.ToConfig(_settings.Seed);
                return true;
            }
            catch (System.Exception e){
                _generatorConfig.WorldGen = WorldGenConfig.CreateEmpty((uint)_settings.Seed);
                Debug.LogException(e);
                return false;
            }
        }

        /// <summary>
        ///     Samples the framed region through <see cref="WorldGenApi.SampleRegion" /> (1 sample per block up to the
        ///     view resolution, then every Stride-th column) and converts the buffers to the four spec textures.
        /// </summary>
        private JobHandle ScheduleSpecBuild(int resolution){
            WorldGenConfig cfg = _generatorConfig.WorldGen;
            int viewSize = Mathf.Min(resolution, ViewResolution);
            int stride = Mathf.Max(1, resolution / ViewResolution);
            int count = viewSize * viewSize;
            _specViewSize = viewSize;

            _jobFields = new NativeArray<WorldFields>(count, Allocator.TempJob);
            _jobHardBiome = new NativeArray<int>(count, Allocator.TempJob);
            _jobHeight = new NativeArray<float>(count, Allocator.TempJob);
            _jobClamped = new NativeArray<byte>(count, Allocator.TempJob);
            _jobSpecBiomePixels = new NativeArray<Color32>(count, Allocator.TempJob);
            _jobSpecHeightPixels = new NativeArray<Color32>(count, Allocator.TempJob);
            _jobErosionPixels = new NativeArray<Color32>(count, Allocator.TempJob);
            _jobPvPixels = new NativeArray<Color32>(count, Allocator.TempJob);

            int biomeCount = cfg.BiomeCount;
            _jobSpecBiomeColors = new NativeArray<Color32>(Mathf.Max(1, biomeCount), Allocator.TempJob);
            for (int i = 0; i < biomeCount; i++){
                Biome biome = _settings.WorldGen.Biomes.Biomes[i];
                _jobSpecBiomeColors[i] = biome ? (Color32)biome.RepresentativeColor : new Color32(0, 0, 0, 255);
            }

            // Same framing as WorldViewJob: the world offset sits at the centre of the view.
            int half = resolution / 2;
            WorldGenRequest request = new(){
                Config = cfg,
                Origin = new int2(_worldOffset.x - half, _worldOffset.y - half),
                Size = new int2(viewSize, viewSize),
                Stride = stride
            };
            RegionBuffers buffers = new(){
                Fields = _jobFields,
                HardBiome = _jobHardBiome,
                Height = _jobHeight,
                Clamped = _jobClamped
            };
            JobHandle sampled = WorldGenApi.SampleRegion(request, ref buffers);

            SpecViewJob view = new(){
                Fields = _jobFields,
                HardBiome = _jobHardBiome,
                Height = _jobHeight,
                Clamped = _jobClamped,
                BiomeColors = _jobSpecBiomeColors,
                HeightMin = cfg.HeightMin,
                HeightMax = cfg.HeightMax,
                WaterLevel = _settings.Noise.WaterLevel,
                BiomeView = _jobSpecBiomePixels,
                HeightView = _jobSpecHeightPixels,
                ErosionView = _jobErosionPixels,
                PvView = _jobPvPixels
            };
            _specBuildScheduled = true;
            return view.Schedule(count, 64, sampled);
        }

        private void FinalizeScheduledBuild(bool applyResult){
            if (!_buildInProgress){
                DisposeJobData();
                return;
            }

            _buildHandle.Complete();

            if (applyResult){
                EnsureTexture(ref _biomeTexture, ViewResolution);
                EnsureTexture(ref _heightTexture, ViewResolution);
                EnsureTexture(ref _climateTexture, ViewResolution);
                EnsureTexture(ref _humidityTemperatureTexture, ViewResolution);

                _biomeTexture.SetPixelData(_jobBiomePixels, 0);
                _heightTexture.SetPixelData(_jobHeightPixels, 0);
                _climateTexture.SetPixelData(_jobClimatePixels, 0);
                _humidityTemperatureTexture.SetPixelData(_jobHumidityTemperaturePixels, 0);
                _biomeTexture.Apply(false);
                _heightTexture.Apply(false);
                _climateTexture.Apply(false);
                _humidityTemperatureTexture.Apply(false);

                if (_specBuildScheduled){
                    EnsureTexture(ref _specBiomeTexture, _specViewSize);
                    EnsureTexture(ref _specHeightTexture, _specViewSize);
                    EnsureTexture(ref _erosionTexture, _specViewSize);
                    EnsureTexture(ref _pvTexture, _specViewSize);
                    _specBiomeTexture.SetPixelData(_jobSpecBiomePixels, 0);
                    _specHeightTexture.SetPixelData(_jobSpecHeightPixels, 0);
                    _erosionTexture.SetPixelData(_jobErosionPixels, 0);
                    _pvTexture.SetPixelData(_jobPvPixels, 0);
                    _specBiomeTexture.Apply(false);
                    _specHeightTexture.Apply(false);
                    _erosionTexture.Apply(false);
                    _pvTexture.Apply(false);

                    // GEN-12: share of columns whose height hit the clamp.
                    int clamped = 0;
                    for (int i = 0; i < _jobClamped.Length; i++) clamped += _jobClamped[i];
                    _clampedShare = _jobClamped.Length > 0 ? clamped / (float)_jobClamped.Length : 0f;
                }
            }

            DisposeJobData();
            _buildInProgress = false;
            Repaint();
        }

        private void DisposeJobData(){
            if (_jobBiomeColors.IsCreated) _jobBiomeColors.Dispose();

            if (_jobBiomePixels.IsCreated) _jobBiomePixels.Dispose();

            if (_jobHeightPixels.IsCreated) _jobHeightPixels.Dispose();

            if (_jobClimatePixels.IsCreated) _jobClimatePixels.Dispose();

            if (_jobHumidityTemperaturePixels.IsCreated) _jobHumidityTemperaturePixels.Dispose();

            if (_jobSpecBiomeColors.IsCreated) _jobSpecBiomeColors.Dispose();
            if (_jobSpecBiomePixels.IsCreated) _jobSpecBiomePixels.Dispose();
            if (_jobSpecHeightPixels.IsCreated) _jobSpecHeightPixels.Dispose();
            if (_jobErosionPixels.IsCreated) _jobErosionPixels.Dispose();
            if (_jobPvPixels.IsCreated) _jobPvPixels.Dispose();
            if (_jobFields.IsCreated) _jobFields.Dispose();
            if (_jobHardBiome.IsCreated) _jobHardBiome.Dispose();
            if (_jobHeight.IsCreated) _jobHeight.Dispose();
            if (_jobClamped.IsCreated) _jobClamped.Dispose();
            _specBuildScheduled = false;
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

        private static void FillTexture(Texture2D texture, Color color){
            if (!texture) return;

            Color[] colors = new Color[texture.width * texture.height];
            for (int i = 0; i < colors.Length; i++) colors[i] = color;

            texture.SetPixels(colors);
            texture.Apply(false);
        }

        private static void DestroyTexture(ref Texture2D texture){
            if (!texture) return;

            DestroyImmediate(texture);
            texture = null;
        }

        #endregion

        #region Jobs

        [BurstCompile]
        private struct WorldViewJob : IJobParallelFor{
            public int Resolution;
            public int2 WorldOffset;
            public byte ShowHumidity;
            public byte ShowTemperature;
            public byte ShowContinental;

            [ReadOnly] public GeneratorConfig Config;
            [ReadOnly] public NativeArray<Color32> BiomeColors;
            [WriteOnly] public NativeArray<Color32> BiomeWorldView;
            [WriteOnly] public NativeArray<Color32> HeightWorldView;
            [WriteOnly] public NativeArray<Color32> ClimateWorldView;

            public void Execute(int index){
                int x = index % ViewResolution;
                int z = index / ViewResolution;
                float step = Resolution / (float)ViewResolution;
                float2 centerOffset = new(Resolution * 0.5f, Resolution * 0.5f);
                float2 worldPos = new float2(WorldOffset.x + x * step, WorldOffset.y + z * step) - centerOffset;

                NoiseCalculator.WorldNoiseOutput noise = NoiseCalculator.WorldNoise(worldPos, ref Config.NoiseParams);

                BiomeCalculator.BiomSectionInput input = noise.BiomSectionInput();

                ushort biomeIndex = BiomeCalculator.SelectBiome(ref input, ref Config);

                //BiomeWorldView
                BiomeWorldView[index] = BiomeColors[biomeIndex];

                //HeightWorldView
                byte value = (byte)math.round(math.saturate(noise.Elevation) * 255f);
                HeightWorldView[index] = new Color32(value, value, value, 255);

                //ClimateWorldView
                byte humidity = ShowHumidity == 1 ? (byte)math.round(math.saturate(noise.Humidity) * 255f) : (byte)0;
                byte temperature =
                    ShowTemperature == 1 ? (byte)math.round(math.saturate(noise.Temperature) * 255f) : (byte)0;
                byte continental =
                    ShowContinental == 1 ? (byte)math.round(math.saturate(noise.Continental) * 255f) : (byte)0;

                ClimateWorldView[index] = new Color32(temperature, continental,humidity, 255);
            }
        }

        [BurstCompile]
        private struct BiomeDistributionJob : IJobParallelFor{
            public int Resolution;
            public float Continental;
            public float Elevation;

            [ReadOnly] public GeneratorConfig Config;
            [ReadOnly] public NativeArray<Color32> BiomeColors;
            [WriteOnly] public NativeArray<Color32> Output;

            public void Execute(int index){
                int x = index % Resolution;
                int y = index / Resolution;
                float step = 1f / Resolution;
                float humidity = (x + 0.5f) * step;
                float temperature = (y + 0.5f) * step;
                BiomeCalculator.BiomSectionInput input = new(){
                    Humidity = humidity,
                    Temperature = temperature,
                    Continental = Continental,
                    Elevation = Elevation
                };
                ushort biomeIndex = BiomeCalculator.SelectBiome(ref input, ref Config);
                Output[index] = BiomeColors[biomeIndex];
            }
        }

        /// <summary>Converts the sampled region buffers to the four spec textures. Reads only; no sampler call (GEN-16 n/a).</summary>
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
            [WriteOnly] public NativeArray<Color32> BiomeView;
            [WriteOnly] public NativeArray<Color32> HeightView;
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

                byte e = (byte)math.round(math.saturate(Fields[index].E) * 255f);
                ErosionView[index] = new Color32(e, e, e, 255);

                byte pv = (byte)math.round(math.saturate((Fields[index].PV + 1f) * 0.5f) * 255f);
                PvView[index] = new Color32(pv, pv, pv, 255);
            }
        }

        #endregion
    }
}
