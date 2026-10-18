using Engine.Scripts.VoxelConfig.Data.Generation;
using Engine.Scripts.WorldGen.Settings;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;

namespace Engine.Editor.WorldGen{
    /// <summary>
    ///     BIO-20 panel of the preview: "Suggest Biome-first height parameters from Terrain-first". Runs
    ///     <see cref="BiomeFirstCalibration" /> over the framed region (same extent and stride as the views), shows
    ///     the suggestions next to the biomes' current values and writes a biome only on its Apply click.
    /// </summary>
    public partial class WorldGenerationPreviewWindow{
        private BiomeFirstCalibration.Suggestion[] _suggestions;
        private string _calibrationError;
        private string _calibrationInfo;
        private bool _calibrationFoldout = true;

        partial void DrawExtraPanelsImpl(){
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            {
                _calibrationFoldout = EditorGUILayout.Foldout(_calibrationFoldout, "Height calibration (BIO-20)", true, EditorStyles.foldoutHeader);
                if (_calibrationFoldout){
                    EditorGUILayout.LabelField(
                        "Samples the Terrain-first pipeline of the assigned settings over the framed area, groups the columns by their Terrain-first biome and suggests Offset = mean H, Amplitude from the detail residual spread and Jaggedness = mean J. Nothing is written until you click Apply.",
                        EditorStyles.wordWrappedMiniLabel);

                    WorldGenSettings settings = WorldGenSettings;
                    using (new EditorGUI.DisabledScope(!settings)){
                        if (GUILayout.Button("Suggest from Terrain-first (framed area)")) RunCalibration();
                    }

                    if (!string.IsNullOrEmpty(_calibrationError)) EditorGUILayout.HelpBox(_calibrationError, MessageType.Warning);
                    if (_suggestions != null && settings) DrawSuggestionTable(settings);
                }
            }
            EditorGUILayout.EndVertical();
        }

        private void RunCalibration(){
            _suggestions = null;
            _calibrationError = null;
            (int2 origin, int extent) = FramedRegion();
            int size = Mathf.Min(extent, ViewResolution);
            int stride = Mathf.Max(1, extent / ViewResolution);
            if (BiomeFirstCalibration.TrySuggest(WorldGenSettings, Seed, origin, size, stride, out BiomeFirstCalibration.Suggestion[] result, out string error)){
                _suggestions = result;
                _calibrationInfo = $"{size}x{size} samples, every {stride}. column, origin ({origin.x}, {origin.y}), seed {Seed}";
            }
            else{
                _calibrationError = error;
            }

            Repaint();
        }

        private void DrawSuggestionTable(WorldGenSettings settings){
            EditorGUILayout.LabelField(_calibrationInfo, EditorStyles.miniLabel);
            EditorGUILayout.BeginHorizontal();
            {
                GUILayout.Label("Biome", EditorStyles.miniBoldLabel, GUILayout.Width(90f));
                GUILayout.Label("n", EditorStyles.miniBoldLabel, GUILayout.Width(50f));
                GUILayout.Label("Offset", EditorStyles.miniBoldLabel, GUILayout.Width(90f));
                GUILayout.Label("Amplitude", EditorStyles.miniBoldLabel, GUILayout.Width(90f));
                GUILayout.Label("Jaggedness", EditorStyles.miniBoldLabel, GUILayout.Width(90f));
                GUILayout.FlexibleSpace();
            }
            EditorGUILayout.EndHorizontal();

            bool anyApplicable = false;
            for (int i = 0; i < _suggestions.Length; i++){
                BiomeFirstCalibration.Suggestion s = _suggestions[i];
                Biome biome = BiomeAt(settings, s.Biome);
                bool applicable = biome && s.Count > 0;
                anyApplicable |= applicable;
                EditorGUILayout.BeginHorizontal();
                {
                    GUILayout.Label(s.Name, EditorStyles.miniLabel, GUILayout.Width(90f));
                    GUILayout.Label(s.Count.ToString(), EditorStyles.miniLabel, GUILayout.Width(50f));
                    if (s.Count > 0){
                        GUILayout.Label(Pair(s.CurrentOffset, s.Offset), EditorStyles.miniLabel, GUILayout.Width(90f));
                        GUILayout.Label(s.AmplitudeAvailable ? Pair(s.CurrentAmplitude, s.Amplitude) : $"{s.CurrentAmplitude:0.##} -> n/a",
                            EditorStyles.miniLabel, GUILayout.Width(90f));
                        GUILayout.Label(Pair(s.CurrentJaggedness, s.Jaggedness), EditorStyles.miniLabel, GUILayout.Width(90f));
                    }
                    else{
                        GUILayout.Label("not in the framed area", EditorStyles.miniLabel, GUILayout.Width(270f));
                    }

                    GUILayout.FlexibleSpace();
                    using (new EditorGUI.DisabledScope(!applicable)){
                        if (GUILayout.Button("Apply", EditorStyles.miniButton, GUILayout.Width(50f))) ApplySuggestion(i, biome);
                    }
                }
                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.BeginHorizontal();
            {
                using (new EditorGUI.DisabledScope(!anyApplicable)){
                    if (GUILayout.Button("Apply all")){
                        for (int i = 0; i < _suggestions.Length; i++){
                            Biome biome = BiomeAt(settings, _suggestions[i].Biome);
                            if (biome && _suggestions[i].Count > 0) ApplySuggestion(i, biome);
                        }
                    }
                }

                if (GUILayout.Button("Clear")){
                    _suggestions = null;
                    _calibrationError = null;
                }
            }
            EditorGUILayout.EndHorizontal();
        }

        private static string Pair(float current, float suggested){
            return $"{current:0.##} -> {suggested:0.##}";
        }

        private static Biome BiomeAt(WorldGenSettings settings, int index){
            if (!settings.Biomes || settings.Biomes.Biomes == null) return null;
            return index >= 0 && index < settings.Biomes.Biomes.Count ? settings.Biomes.Biomes[index] : null;
        }

        /// <summary>The explicit write of BIO-20: one biome, undoable, and the preview rebuilds.</summary>
        private void ApplySuggestion(int index, Biome biome){
            BiomeFirstCalibration.Suggestion s = _suggestions[index];
            Undo.RecordObject(biome, "Apply Biome-first height calibration");
            biome.HeightOffset = s.Offset;
            if (s.AmplitudeAvailable) biome.HeightAmplitude = s.Amplitude;
            biome.Jaggedness = s.Jaggedness;
            EditorUtility.SetDirty(biome);
            s.CurrentOffset = biome.HeightOffset;
            s.CurrentAmplitude = biome.HeightAmplitude;
            s.CurrentJaggedness = biome.Jaggedness;
            _suggestions[index] = s;
            RequestRebuild();
        }
    }
}
