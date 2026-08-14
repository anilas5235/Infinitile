using System.Collections.Generic;
using Engine.Scripts.Jobs.Chunk;
using Engine.Scripts.Noise;
using Engine.Scripts.Settings;
using Engine.Scripts.Utils.Extensions;
using Engine.Scripts.VoxelConfig.Data.Voxel;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;

namespace Engine.Scripts.VoxelConfig.Data.Generation.Editor
{
    public class WorldGenerationPreviewWindow : EditorWindow
    {
        private const int ViewResolution = 512;
        private static readonly int[] ResolutionOptions = { 128, 256, 512, 1024, 2048, 4096, 8192, 16384 };

        private readonly List<VoxelDataPackage> _packages = new();
        private readonly List<Biome> _biomes = new();

        private VoxelEngineSettings _settings;
        private UnityEditor.Editor _biomeEditor;

        private Texture2D _biomeTexture;
        private Texture2D _heightTexture;
        private Texture2D _climateTexture;
        private Texture2D _humidityTemperatureTexture;

        private int _selectedBiomeIndex;
        private int _resolutionIndex;
        private Vector2Int _worldOffset;
        private bool _showHumidity = true;
        private bool _showTemperature = true;
        private bool _showContinental = true;
        private float _phaseContinental = 0.5f;
        private bool _autoRebuild = true;
        private bool _needsRebuild = true;
        private bool _isDragging;
        private Vector2 _dragStartMouse;
        private Vector2Int _dragStartOffset;
        private bool _buildInProgress;
        private JobHandle _buildHandle;

        private GeneratorConfig _generatorConfig;
        private NativeArray<Color32> _jobBiomeColors;
        private NativeArray<Color32> _jobBiomePixels;
        private NativeArray<Color32> _jobHeightPixels;
        private NativeArray<Color32> _jobClimatePixels;
        private NativeArray<Color32> _jobHumidityTemperaturePixels;

        [MenuItem("Infinitile/World Generation Preview")]
        private static void OpenWindow()
        {
            WorldGenerationPreviewWindow window = GetWindow<WorldGenerationPreviewWindow>();
            window.titleContent = new GUIContent("World Gen Preview");
            window.minSize = new Vector2(920f, 620f);
            window.Show();
        }

        private void OnEnable()
        {
            EditorApplication.update += OnEditorUpdate;
            ReloadAssets();
            _needsRebuild = true;
        }

        private void OnDisable()
        {
            EditorApplication.update -= OnEditorUpdate;

            if (_biomeEditor)
            {
                DestroyImmediate(_biomeEditor);
                _biomeEditor = null;
            }

            FinalizeScheduledBuild(false);

            DestroyTexture(ref _biomeTexture);
            DestroyTexture(ref _heightTexture);
            DestroyTexture(ref _climateTexture);
            DestroyTexture(ref _humidityTemperatureTexture);
        }

        private void OnEditorUpdate()
        {
            if (_buildInProgress && _buildHandle.IsCompleted)
            {
                FinalizeScheduledBuild(true);
                Repaint();
            }

            if (_autoRebuild && _needsRebuild && !_buildInProgress)
            {
                ScheduleBuild();
            }
        }

        private void OnGUI()
        {
            EditorGUI.BeginChangeCheck();
            EditorGUILayout.BeginHorizontal();
            {
                EditorGUILayout.BeginVertical();
                {
                    DrawToolbar();
                    DrawBiomeDistributionView();
                }
                EditorGUILayout.EndVertical();

                DrawViews();
                DrawBiomeEditorPanel();

                if (EditorGUI.EndChangeCheck())
                {
                    RequestRebuild();
                }
            }
            EditorGUILayout.EndHorizontal();
        }

        private void OnDestroy()
        {
            _generatorConfig.Dispose();
            DisposeJobData();
        }

        private void DrawToolbar()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            {
                EditorGUILayout.LabelField("Data Source", EditorStyles.boldLabel);
                _settings = (VoxelEngineSettings)EditorGUILayout.ObjectField("VoxelEngineSettings", _settings,
                    typeof(VoxelEngineSettings), false);

                EditorGUILayout.BeginHorizontal();
                {
                    if (GUILayout.Button("Reload SOs", GUILayout.Width(120f)))
                    {
                        ReloadAssets();
                    }

                    using (new EditorGUI.DisabledScope(_buildInProgress))
                    {
                        if (GUILayout.Button("Rebuild", GUILayout.Width(120f)))
                        {
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

        private void DrawBiomeDistributionView()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            {
                EditorGUILayout.LabelField("BiomeDistribution", EditorStyles.boldLabel);
                EditorGUILayout.LabelField("Humidity/Temperature Phase View", EditorStyles.boldLabel);
                _phaseContinental = EditorGUILayout.Slider("Continental", _phaseContinental, 0f, 1f);
                DrawView("Biome Distribution (X=Hum, Y=Temp)", EditorStyles.helpBox, _humidityTemperatureTexture,
                    false);
            }
            EditorGUILayout.EndVertical();
        }

        private void DrawBiomeEditorPanel()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Biome Editing", EditorStyles.boldLabel);

            if (_biomes.Count == 0)
            {
                EditorGUILayout.HelpBox("Keine Biome in den geladenen VoxelDataPackages gefunden.",
                    MessageType.Warning);
                EditorGUILayout.EndVertical();
                return;
            }

            string[] biomeNames = new string[_biomes.Count];
            for (int i = 0; i < _biomes.Count; i++)
            {
                biomeNames[i] = _biomes[i] ? _biomes[i].name : "<null>";
            }

            int newIndex = EditorGUILayout.Popup("Active Biome", _selectedBiomeIndex, biomeNames);
            if (newIndex != _selectedBiomeIndex)
            {
                _selectedBiomeIndex = newIndex;
                CreateBiomeEditor();
            }

            if (_biomeEditor)
            {
                _biomeEditor.OnInspectorGUI();
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawViews()
        {
            if (!_settings)
            {
                EditorGUILayout.HelpBox("Bitte ein VoxelEngineSettings-Asset auswählen.", MessageType.Info);
                return;
            }

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            {
                _worldOffset = EditorGUILayout.Vector2IntField("World Offset (X/Z)", _worldOffset);
                _resolutionIndex = EditorGUILayout.Popup("Resolution", _resolutionIndex, new[]
                {
                    $"{ResolutionOptions[0]}x{ResolutionOptions[0]}",
                    $"{ResolutionOptions[1]}x{ResolutionOptions[1]}",
                    $"{ResolutionOptions[2]}x{ResolutionOptions[2]}",
                    $"{ResolutionOptions[3]}x{ResolutionOptions[3]}",
                    $"{ResolutionOptions[4]}x{ResolutionOptions[4]}",
                    $"{ResolutionOptions[5]}x{ResolutionOptions[5]}",
                    $"{ResolutionOptions[6]}x{ResolutionOptions[6]}",
                    $"{ResolutionOptions[7]}x{ResolutionOptions[7]}",
                });

                EditorGUILayout.Space();
                EditorGUILayout.Separator();

                EditorGUILayout.BeginHorizontal();
                {
                    EditorGUILayout.BeginVertical();
                    {
                        EditorGUILayout.LabelField("Climate RGB Channels", EditorStyles.boldLabel);
                        _showHumidity = EditorGUILayout.ToggleLeft("Humidity (R)", _showHumidity);
                        _showTemperature = EditorGUILayout.ToggleLeft("Temperature (G)", _showTemperature);
                        _showContinental = EditorGUILayout.ToggleLeft("Continental (B)", _showContinental);

                        DrawView("Climate", EditorStyles.helpBox, _climateTexture, true);
                    }
                    EditorGUILayout.EndVertical();
                    EditorGUILayout.BeginVertical();
                    {
                        DrawView("Biome View", EditorStyles.label, _biomeTexture, true);
                        DrawView("HeightMap", EditorStyles.helpBox, _heightTexture, true);
                    }
                    EditorGUILayout.EndVertical();
                }
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.EndVertical();
        }

        private void DrawView(string viewTitle, GUIStyle style, Texture2D texture, bool allowPan)
        {
            EditorGUILayout.BeginVertical(style, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            {
                EditorGUILayout.LabelField(viewTitle, EditorStyles.boldLabel);

                Rect rect = GUILayoutUtility.GetRect(200f, 280f, GUILayout.ExpandWidth(true),
                    GUILayout.ExpandHeight(true));
                EditorGUI.DrawRect(rect, new Color(0.1f, 0.1f, 0.1f, 1f));
                if (texture)
                {
                    GUI.DrawTexture(rect, texture, ScaleMode.ScaleToFit, false);
                }

                if (allowPan)
                {
                    HandlePanInput(rect);
                }
            }
            EditorGUILayout.EndVertical();
        }

        private void HandlePanInput(Rect viewRect)
        {
            Event e = Event.current;
            if (e == null)
            {
                return;
            }

            if (e.type == EventType.MouseDown && e.button == 0 && viewRect.Contains(e.mousePosition))
            {
                _isDragging = true;
                _dragStartMouse = e.mousePosition;
                _dragStartOffset = _worldOffset;
                e.Use();
                return;
            }

            if (e.type == EventType.MouseDrag && _isDragging)
            {
                Vector2 delta = e.mousePosition - _dragStartMouse;
                _worldOffset = _dragStartOffset - new Vector2Int(Mathf.RoundToInt(delta.x), Mathf.RoundToInt(-delta.y));
                RequestRebuild();
                Repaint();
                e.Use();
                return;
            }

            if (e.type == EventType.MouseUp && _isDragging)
            {
                _isDragging = false;
                e.Use();
            }
        }

        private void ReloadAssets()
        {
            if (!_settings)
            {
                string[] settingsGuids = AssetDatabase.FindAssets("t:VoxelEngineSettings");
                if (settingsGuids.Length > 0)
                {
                    string settingsPath = AssetDatabase.GUIDToAssetPath(settingsGuids[0]);
                    _settings = AssetDatabase.LoadAssetAtPath<VoxelEngineSettings>(settingsPath);
                }
            }

            _packages.Clear();
            _biomes.Clear();
            _generatorConfig.Dispose();

            string[] packageGuids = AssetDatabase.FindAssets("t:VoxelDataPackage");
            HashSet<Biome> uniqueBiomes = new();
            foreach (string guid in packageGuids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                VoxelDataPackage package = AssetDatabase.LoadAssetAtPath<VoxelDataPackage>(path);
                if (!package) continue;

                _packages.Add(package);
                if (package.biomes == null) continue;

                foreach (Biome biome in package.biomes)
                {
                    if (!biome || !uniqueBiomes.Add(biome)) continue;

                    _biomes.Add(biome);
                }
            }

            _selectedBiomeIndex = Mathf.Clamp(_selectedBiomeIndex, 0, Mathf.Max(0, _biomes.Count - 1));
            _generatorConfig = new GeneratorConfig
            {
                WaterLevel = _settings.Noise.WaterLevel,
                GlobalSeed = _settings.Seed,
                BiomeDefs = new NativeArray<Biome.BiomeDef>(_biomes.Count, Allocator.Domain),
                Voxels = new NativeHashMap<FixedString32Bytes, Voxel.Voxel.VoxelDef>(0, Allocator.Domain),
            };
            for (int i = 0; i < _biomes.Count; i++)
            {
                Biome biome = _biomes[i];
                _generatorConfig.BiomeDefs[i] = new Biome.BiomeDef()
                {
                    targetHumidity = biome.TargetHumidity,
                    targetTemperature = biome.TargetTemperature,
                    targetContinental = biome.TargetContinental,
                };
            }

            CreateBiomeEditor();
            RequestRebuild();
            Repaint();
        }

        private void CreateBiomeEditor()
        {
            if (_biomeEditor)
            {
                DestroyImmediate(_biomeEditor);
                _biomeEditor = null;
            }

            if (_selectedBiomeIndex < 0 || _selectedBiomeIndex >= _biomes.Count)
            {
                return;
            }

            Biome biome = _biomes[_selectedBiomeIndex];
            if (biome)
            {
                UnityEditor.Editor.CreateCachedEditor(biome, null, ref _biomeEditor);
            }
        }

        private void RequestRebuild()
        {
            _needsRebuild = true;
            if (_autoRebuild && !_buildInProgress)
            {
                ScheduleBuild();
            }
        }

        private void ScheduleBuild()
        {
            if (_buildInProgress || !_needsRebuild) return;
            if (!_settings || !_settings.Noise) return;

            int biomeCount = _biomes.Count;
            int resolution = ResolutionOptions[Mathf.Clamp(_resolutionIndex, 0, ResolutionOptions.Length - 1)];
            const int pixelCount = ViewResolution * ViewResolution;

            if (biomeCount == 0)
            {
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

            for (int i = 0; i < biomeCount; i++)
            {
                Biome biome = _biomes[i];
                if (!biome)
                {
                    _jobBiomeColors[i] = new Color32(0, 0, 0, 255);
                    continue;
                }

                _jobBiomeColors[i] = biome.RepresentativeColor;
            }

            _generatorConfig.NoiseProfile = new NoiseProfile(new NoiseProfile.Settings
            {
                Seed = _settings.Seed,
                Scale = _settings.Noise.Scale,
                Persistance = _settings.Noise.Persistance,
                Lacunarity = _settings.Noise.Lacunarity,
                Octaves = _settings.Noise.Octaves
            });

            _generatorConfig.NoiseParams = new NoiseCalculator.NoiseParameters
            {
                Seed = _settings.Seed,
                HumidityScale = _settings.Noise.HumidityScale,
                TemperatureScale = _settings.Noise.TemperatureScale,
                ContinentalScale = _settings.Noise.ContinentalScale
            };

            BiomeWorldViewJob biomeJob = new()
            {
                Resolution = resolution,
                WorldOffset = _worldOffset.Int2(),
                Config = _generatorConfig,
                BiomeColors = _jobBiomeColors,
                Output = _jobBiomePixels
            };

            HeightWorldViewJob heightJob = new()
            {
                Resolution = resolution,
                WorldOffset = _worldOffset.Int2(),
                Config = _generatorConfig,
                Output = _jobHeightPixels
            };

            ClimateWorldViewJob climateJob = new()
            {
                Resolution = resolution,
                WorldOffset = _worldOffset.Int2(),
                Config = _generatorConfig,
                ShowHumidity = _showHumidity ? (byte)1 : (byte)0,
                ShowTemperature = _showTemperature ? (byte)1 : (byte)0,
                ShowContinental = _showContinental ? (byte)1 : (byte)0,
                Output = _jobClimatePixels
            };

            BiomeDistributionJob distributionJob = new()
            {
                Resolution = ViewResolution,
                Continental = _phaseContinental,
                Config = _generatorConfig,
                BiomeColors = _jobBiomeColors,
                Output = _jobHumidityTemperaturePixels
            };

            JobHandle biomeHandle = biomeJob.Schedule(pixelCount, 64);
            JobHandle heightHandle = heightJob.Schedule(pixelCount, 64);
            JobHandle climateHandle = climateJob.Schedule(pixelCount, 64);
            JobHandle phaseHandle = distributionJob.Schedule(pixelCount, 64);

            JobHandle combined = JobHandle.CombineDependencies(biomeHandle, heightHandle);
            combined = JobHandle.CombineDependencies(combined, climateHandle);
            combined = JobHandle.CombineDependencies(combined, phaseHandle);

            _buildHandle = combined;
            _buildInProgress = true;
            _needsRebuild = false;
        }

        private void FinalizeScheduledBuild(bool applyResult)
        {
            if (!_buildInProgress)
            {
                DisposeJobData();
                return;
            }

            _buildHandle.Complete();

            if (applyResult)
            {
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
            }

            DisposeJobData();
            _buildInProgress = false;
        }

        private void DisposeJobData()
        {
            if (_jobBiomeColors.IsCreated) _jobBiomeColors.Dispose();

            if (_jobBiomePixels.IsCreated) _jobBiomePixels.Dispose();

            if (_jobHeightPixels.IsCreated) _jobHeightPixels.Dispose();

            if (_jobClimatePixels.IsCreated) _jobClimatePixels.Dispose();

            if (_jobHumidityTemperaturePixels.IsCreated) _jobHumidityTemperaturePixels.Dispose();
        }

        private static void EnsureTexture(ref Texture2D texture, int size)
        {
            if (texture && texture.width == size && texture.height == size)
            {
                return;
            }

            DestroyTexture(ref texture);
            texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = "WorldGenPreviewTexture",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave
            };
        }

        private static void FillTexture(Texture2D texture, Color color)
        {
            if (!texture)
            {
                return;
            }

            Color[] colors = new Color[texture.width * texture.height];
            for (int i = 0; i < colors.Length; i++)
            {
                colors[i] = color;
            }

            texture.SetPixels(colors);
            texture.Apply(false);
        }

        private static void DestroyTexture(ref Texture2D texture)
        {
            if (!texture)
            {
                return;
            }

            DestroyImmediate(texture);
            texture = null;
        }

        private static float2 CalcWorldPos(int index, int resolution, int2 worldOffset)
        {
            int x = index % ViewResolution;
            int z = index / ViewResolution;
            float step = resolution / (float)ViewResolution;
            float2 worldPos = new(worldOffset.x + x * step, worldOffset.y + z * step);
            return worldPos;
        }

        [BurstCompile]
        private struct BiomeWorldViewJob : IJobParallelFor
        {
            public int Resolution;
            public int2 WorldOffset;

            [ReadOnly] public GeneratorConfig Config;
            [ReadOnly] public NativeArray<Color32> BiomeColors;
            [WriteOnly] public NativeArray<Color32> Output;

            public void Execute(int index)
            {
                float2 worldPos = CalcWorldPos(index, Resolution, WorldOffset);
                NoiseCalculator.WorldNoiseOutput noise = NoiseCalculator.WorldNoise(worldPos, ref Config.NoiseParams,
                    ref Config.NoiseProfile);
                BiomeCalculator.BiomSectionInput input = noise.BiomSectionInput();
                ushort biomeIndex = BiomeCalculator.SelectBiome(ref input, ref Config);
                Output[index] = BiomeColors[biomeIndex];
            }
        }

        [BurstCompile]
        private struct HeightWorldViewJob : IJobParallelFor
        {
            public int Resolution;
            public int2 WorldOffset;

            [ReadOnly] public GeneratorConfig Config;
            [WriteOnly] public NativeArray<Color32> Output;

            public void Execute(int index)
            {
                float2 worldPos = CalcWorldPos(index, Resolution, WorldOffset);
                NoiseCalculator.WorldNoiseOutput noise = NoiseCalculator.WorldNoise(worldPos, ref Config.NoiseParams,
                    ref Config.NoiseProfile);
                byte value = (byte)math.round(math.saturate(noise.Height) * 255f);
                Output[index] = new Color32(value, value, value, 255);
            }
        }

        [BurstCompile]
        private struct ClimateWorldViewJob : IJobParallelFor
        {
            public int Resolution;
            public int2 WorldOffset;
            public byte ShowHumidity;
            public byte ShowTemperature;
            public byte ShowContinental;

            [ReadOnly] public GeneratorConfig Config;
            [WriteOnly] public NativeArray<Color32> Output;

            public void Execute(int index)
            {
                float2 worldPos = CalcWorldPos(index, Resolution, WorldOffset);
                NoiseCalculator.WorldNoiseOutput noise = NoiseCalculator.WorldNoise(worldPos, ref Config.NoiseParams,
                    ref Config.NoiseProfile);

                byte humidity = ShowHumidity == 1 ? (byte)math.round(math.saturate(noise.Humidity) * 255f) : (byte)0;
                byte temperature =
                    ShowTemperature == 1 ? (byte)math.round(math.saturate(noise.Temperature) * 255f) : (byte)0;
                byte continental =
                    ShowContinental == 1 ? (byte)math.round(math.saturate(noise.Continental) * 255f) : (byte)0;

                Output[index] = new Color32(humidity, temperature, continental, 255);
            }
        }

        [BurstCompile]
        private struct BiomeDistributionJob : IJobParallelFor
        {
            public int Resolution;
            public float Continental;
            public float Height;

            [ReadOnly] public GeneratorConfig Config;
            [ReadOnly] public NativeArray<Color32> BiomeColors;
            [WriteOnly] public NativeArray<Color32> Output;

            public void Execute(int index)
            {
                int x = index % Resolution;
                int y = index / Resolution;
                float step = 1f / Resolution;
                float humidity = (x + 0.5f) * step;
                float temperature = (y + 0.5f) * step;
                BiomeCalculator.BiomSectionInput input = new()
                {
                    Humidity = humidity,
                    Temperature = temperature,
                    Continental = Continental,
                    Height = Height
                };
                ushort biomeIndex = BiomeCalculator.SelectBiome(ref input, ref Config);
                Output[index] = BiomeColors[biomeIndex];
            }
        }
    }
}