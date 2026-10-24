using System.Collections.Generic;
using Engine.Scripts.VoxelConfig.Data.Generation;
using Engine.Scripts.WorldGen.Flora;
using Engine.Scripts.WorldGen.Settings;
using UnityEditor;
using UnityEngine;

namespace Engine.Editor.WorldGen{
    /// <summary>
    ///     Species gallery panel of the preview: the variants of a tree species for a row of seeds, as a side view
    ///     and a top view each, with the species' inspector below. It redraws whenever the species changes, so its
    ///     ranges can be tuned by eye.
    /// </summary>
    public partial class WorldGenerationPreviewWindow{
        private const float GalleryPixelsPerVoxel = 4f;

        [SerializeField] private TreeSpecies _gallerySpecies;
        [SerializeField] private int _gallerySeed = 1;
        [SerializeField] private int _galleryCount = 6;
        [SerializeField] private bool _galleryNarrow;
        private bool _showGallery;
        private bool _showGalleryInspector = true;
        private TreeGallery _gallery;
        private UnityEditor.Editor _speciesEditor;
        private (TreeSpecies species, int dirty, int seed, int count, bool narrow) _galleryKey;

        private void DisposeGallery(){
            _gallery?.Dispose();
            _gallery = null;
            if (_speciesEditor) DestroyImmediate(_speciesEditor);
            _speciesEditor = null;
        }

        private void DrawGalleryPanel(){
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            {
                _showGallery = EditorGUILayout.Foldout(_showGallery, "Tree species gallery", true, EditorStyles.foldoutHeader);
                if (_showGallery) DrawGallery();
            }
            EditorGUILayout.EndVertical();
        }

        private void DrawGallery(){
            // The gallery's own controls do not change the world, so they must not rebuild the views.
            bool changed = GUI.changed;
            List<TreeSpecies> used = SpeciesOfTheBiomes();
            if (!_gallerySpecies && used.Count > 0) _gallerySpecies = used[0];
            if (used.Count > 0){
                string[] names = used.ConvertAll(s => s.name).ToArray();
                int index = EditorGUILayout.Popup("In the biomes", used.IndexOf(_gallerySpecies), names);
                if (index >= 0) _gallerySpecies = used[index];
            }

            _gallerySpecies = (TreeSpecies)EditorGUILayout.ObjectField("Species", _gallerySpecies, typeof(TreeSpecies), false);
            EditorGUILayout.BeginHorizontal();
            _gallerySeed = EditorGUILayout.IntField("Seeds from", _gallerySeed);
            if (GUILayout.Button("Next", GUILayout.Width(50f))) _gallerySeed += _galleryCount;
            EditorGUILayout.EndHorizontal();
            _galleryCount = EditorGUILayout.IntSlider("Trees per variant", _galleryCount, 1, 16);
            _galleryNarrow = EditorGUILayout.ToggleLeft("As on uneven ground (wide and leaning trunks built 1 wide, upright)", _galleryNarrow);
            GUI.changed = changed;

            if (!_gallerySpecies){
                EditorGUILayout.HelpBox("Pick a TreeSpecies asset.", MessageType.Info);
                return;
            }

            UpdateGallery();
            if (_gallery.Rows.Count == 0) EditorGUILayout.HelpBox("The species has no variants.", MessageType.Info);
            foreach (TreeGallery.Row row in _gallery.Rows) DrawGalleryRow(row);

            _showGalleryInspector = EditorGUILayout.Foldout(_showGalleryInspector, $"Edit {_gallerySpecies.name}", true);
            if (_showGalleryInspector){
                UnityEditor.Editor.CreateCachedEditor(_gallerySpecies, null, ref _speciesEditor);
                if (_speciesEditor) _speciesEditor.OnInspectorGUI();
            }
        }

        /// <summary>Rebuilds the rows when the species, its contents (dirty count, which undo also changes) or the row options change.</summary>
        private void UpdateGallery(){
            _gallery ??= new TreeGallery();
            var key = (_gallerySpecies, EditorUtility.GetDirtyCount(_gallerySpecies), _gallerySeed, _galleryCount, _galleryNarrow);
            if (key == _galleryKey && _gallery.Rows.Count > 0) return;
            _galleryKey = key;
            _gallery.Build(_gallerySpecies, _gallerySeed, _galleryCount, _galleryNarrow);
        }

        private void DrawGalleryRow(TreeGallery.Row row){
            TreeVariantDef d = row.Def;
            EditorGUILayout.LabelField($"{row.Name} · {row.Share:P0} · reach {d.Reach} · spacing {d.SpacingRadius}", EditorStyles.miniBoldLabel);
            string narrowed = row.Narrowed > 0 ? $" · {row.Narrowed} narrowed" : "";
            EditorGUILayout.LabelField($"height {Range(row.Height)} · voxels {Range(row.VoxelCount)}{narrowed}", EditorStyles.miniLabel);
            DrawGalleryTexture(row.Side);
            DrawGalleryTexture(row.Top);
            EditorGUILayout.Space(4f);
        }

        private static string Range(Unity.Mathematics.int2 r){
            return r.x == r.y ? r.x.ToString() : $"{r.x}–{r.y}";
        }

        /// <summary>Draws a view at up to <see cref="GalleryPixelsPerVoxel" /> per voxel, scaled down to the panel width.</summary>
        private void DrawGalleryTexture(Texture2D texture){
            if (!texture) return;
            float available = position.width * .26f - 40f; // the left column, less the scroll bar and padding
            float scale = Mathf.Min(GalleryPixelsPerVoxel, available / texture.width);
            Rect rect = GUILayoutUtility.GetRect(texture.width * scale, texture.height * scale, GUILayout.ExpandWidth(false));
            GUI.DrawTexture(rect, texture, ScaleMode.ScaleToFit, false);
        }

        /// <summary>Tree species named by the rules of the assigned biomes, in rule order.</summary>
        private List<TreeSpecies> SpeciesOfTheBiomes(){
            List<TreeSpecies> trees = new();
            BiomeSet set = WorldGenSettings ? WorldGenSettings.Biomes : null;
            if (!set || set.Biomes == null) return trees;
            List<Biome> biomes = set.Biomes.FindAll(b => b);
            foreach (Object species in FloraConfigBuilder.CollectSpecies(biomes))
                if (species is TreeSpecies tree)
                    trees.Add(tree);
            return trees;
        }
    }
}
