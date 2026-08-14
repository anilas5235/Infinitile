using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Engine.Scripts.VoxelConfig.Data.Voxel.Editor
{
    [CustomEditor(typeof(VoxelDataPackage))]
    public class VoxelDataPackageCustomEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            base.OnInspectorGUI();
            VoxelDataPackage package = (VoxelDataPackage)target;

            if (GUILayout.Button("Add Assets from Folder"))
            {
                string assetPath = AssetDatabase.GetAssetPath(package);
                string folder = Path.GetDirectoryName(assetPath);
                if (string.IsNullOrEmpty(folder)) folder = "Assets";
                folder = folder.Replace("\\", "/");

                AddAssetsOfType(package.voxel, folder);
                AddAssetsOfType(package.biomes, folder);
                AddAssetsOfType(package.structures, folder);
                
                EditorUtility.SetDirty(package);
                AssetDatabase.SaveAssets();
            }
        }

        private void AddAssetsOfType<T>(List<T> list, string folder)
            where T : Object
        {
            if (list == null) return;
            string filter = "t:" + typeof(T).Name;
            string[] guids = AssetDatabase.FindAssets(filter, new[] { folder });
            foreach (string g in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(g);
                T asset = AssetDatabase.LoadAssetAtPath<T>(path);
                if (!asset) continue;
                if (!list.Contains(asset)) list.Add(asset);
            }
        }
    }
}