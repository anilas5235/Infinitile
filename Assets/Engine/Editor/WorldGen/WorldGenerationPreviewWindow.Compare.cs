using Engine.Scripts.WorldGen.Settings;
using Unity.Collections;
using UnityEditor;
using UnityEngine;

namespace Engine.Editor.WorldGen{
    /// <summary>
    ///     Configuration and compare panel of the preview: configuration A (the asset, edited with Undo), the
    ///     preview-only configuration B, the A | B | difference views and the difference itself.
    /// </summary>
    public partial class WorldGenerationPreviewWindow{
        [SerializeField] private PreviewConfiguration _configB; // preview-only, never written to the asset implicitly
        [SerializeField] private bool _configBInitialized; // B starts as a copy of A
        [SerializeField] private bool _compare;
        [SerializeField] private PreviewView _compareView = PreviewView.Height;

        private NativeArray<Color32> _diffPixels;
        private Texture2D _diffTexture;
        private PreviewDiff.Stats _diffStats;
        private bool _diffSigned;
        private string _diffNote;

        private void DisposeDiff(){
            if (_diffPixels.IsCreated) _diffPixels.Dispose();
            PreviewPass.DestroyTexture(ref _diffTexture);
        }

        private void EnsureConfigB(WorldGenSettings asset){
            if (_configB != null && _configBInitialized) return;
            _configB = PreviewConfiguration.Capture(asset);
            _configBInitialized = true;
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

        /// <summary>The compare toggle of the view toolbar and, in compare mode, the view kind to compare.</summary>
        private void DrawCompareToggle(){
            _compare = EditorGUILayout.ToggleLeft("Compare A/B", _compare, GUILayout.Width(100f));
            if (!_compare) return;
            PreviewView view = WithoutRebuild(() => (PreviewView)EditorGUILayout.EnumPopup(_compareView, GUILayout.Width(170f)));
            if (view != _compareView){
                _compareView = view;
                UpdateDiff();
            }
        }

        /// <summary>The selected view of A and B side by side and their difference.</summary>
        private void DrawCompareViews(){
            EditorGUILayout.BeginHorizontal();
            {
                DrawView("A · " + ViewTitle(PassA, _compareView), PassA?.Texture(_compareView), true);
                DrawView("B · " + ViewTitle(PassB, _compareView), PassB != null && PassB.HasResult ? PassB.Texture(_compareView) : null, true);
                DrawView(DiffTitle(), _diffNote == null ? _diffTexture : null, true);
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.LabelField($"B: {PassB?.Description}", EditorStyles.miniLabel);
            if (_diffNote != null) EditorGUILayout.HelpBox(_diffNote, MessageType.Info);
        }

        private string DiffTitle(){
            if (_diffNote != null) return "Difference";
            if (_diffSigned)
                return $"B − A height (red = B higher): mean |Δ| {_diffStats.MeanAbs:0.##}, max {_diffStats.MaxAbs:0.#}, " +
                       $"≥1 block {_diffStats.Share:P1}";
            return $"Mismatch mask: {_diffStats.Share:P2} of the columns differ";
        }

        /// <summary>
        ///     The difference view of the selected view kind. Height views are signed (B − A); biome classes,
        ///     surface source and flora are mismatch masks; the other views compare the pixels. Recomputed after
        ///     every build and when the compare view changes (no resampling).
        /// </summary>
        private void UpdateDiff(){
            _diffNote = null;
            if (!_compare) return;
            PreviewPass a = PassA, b = PassB;
            if (a == null || b == null || !a.HasResult || !b.HasResult){
                _diffNote = "Waiting for both configurations (see the errors on the left if a config fails).";
                return;
            }

            if (a.Size != b.Size || a.Stride != b.Stride){
                _diffNote = "A and B were sampled at different extents; rebuilding.";
                RequestRebuild();
                return;
            }

            int count = a.Size * a.Size;
            if (!_diffPixels.IsCreated || _diffPixels.Length != count){
                if (_diffPixels.IsCreated) _diffPixels.Dispose();
                _diffPixels = new NativeArray<Color32>(count, Allocator.Persistent);
            }

            _diffSigned = _compareView == PreviewView.Height;
            _diffStats = _compareView switch{
                PreviewView.Height => PreviewDiff.Signed(a.Height, b.Height, _diffPixels),
                PreviewView.HardBiome => PreviewDiff.Mismatch(a.HardBiome, b.HardBiome, a.Pixels(PreviewView.HardBiome), _diffPixels),
                PreviewView.DominantBiome => PreviewDiff.Mismatch(a.DominantBiome, b.DominantBiome, a.Pixels(PreviewView.DominantBiome), _diffPixels),
                PreviewView.SurfaceSource => PreviewDiff.Mismatch(a.SurfaceSource, b.SurfaceSource, a.Pixels(PreviewView.SurfaceSource), _diffPixels),
                PreviewView.Flora => PreviewDiff.Mismatch(a.FloraSpecies, b.FloraSpecies, a.Pixels(PreviewView.Flora), _diffPixels),
                _ => PreviewDiff.Pixels(a.Pixels(_compareView), b.Pixels(_compareView), _diffPixels)
            };
            PreviewPass.EnsureTexture(ref _diffTexture, a.Size);
            _diffTexture.SetPixelData(_diffPixels, 0);
            _diffTexture.Apply(false);
        }
    }
}
