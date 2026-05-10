using System;
using System.Collections.Generic;
using Engine.Scripts.VoxelConfig.Data;
using UnityEditor;
using UnityEngine;

namespace Engine.Scripts.VoxelConfig.Editor
{
    /// <summary>
    /// Custom inspector for <see cref="VoxelDefinition" /> that shows texture fields based on the selected texture mode.
    /// </summary>
    [CustomEditor(typeof(VoxelDefinition))]
    [CanEditMultipleObjects]
    public class VoxelDefinitionCustomEditor : UnityEditor.Editor
    {
        /// <summary>
        /// Draws the custom inspector UI for voxel definitions.
        /// </summary>
        public override void OnInspectorGUI()
        {
            VoxelDefinition voxelDef = (VoxelDefinition)target;

            serializedObject.Update();

            EditorGUILayout.PropertyField(serializedObject.FindProperty("meshLayer"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("collision"));

            EditorGUILayout.PropertyField(serializedObject.FindProperty("alwaysRenderAllFaces"));

            EditorGUILayout.PropertyField(serializedObject.FindProperty("glow"));

            if (voxelDef.meshLayer == MeshLayer.Transparent)
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty("depthFadeDistance"));
                if (voxelDef.usePostProcess)
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("postProcess"));
            }

            EditorGUILayout.PropertyField(serializedObject.FindProperty("shape"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("textureMode"));
            switch (voxelDef.TextureMode)
            {
                case VoxelDefinition.VoxelTexMode.AllSame:
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("all"));
                    break;

                case VoxelDefinition.VoxelTexMode.TopBottomSides:
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("top"));
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("bottom"));
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("side"));
                    break;

                case VoxelDefinition.VoxelTexMode.SixSidesUnique:
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("top"));
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("bottom"));
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("front"));
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("back"));
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("left"));
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("right"));
                    break;
                case VoxelDefinition.VoxelTexMode.AllUnique:
                    int quadCount = voxelDef.shape.quads.Length;
                    if (voxelDef.allUnique.Count != quadCount)
                    {
                        Dictionary<QuadDefinition, Texture2D> temp = new(quadCount);
                        foreach (VoxelQuad q in voxelDef.shape.quads)
                            temp[q.quadDef] = voxelDef.allUnique.GetValueOrDefault(q.quadDef);

                        voxelDef.allUnique = temp;
                    }

                    EditorGUILayout.BeginVertical("Box");
                    foreach (VoxelQuad q in voxelDef.shape.quads)
                        voxelDef.allUnique[q.quadDef] = (Texture2D)EditorGUILayout.ObjectField(
                            $"Face {q.quadDef.name}", voxelDef.allUnique[q.quadDef], typeof(Texture2D),
                            false);

                    EditorGUILayout.EndVertical();
                    break;

                default:
                    throw new ArgumentOutOfRangeException();
            }

            serializedObject.ApplyModifiedProperties();
        }
    }
}