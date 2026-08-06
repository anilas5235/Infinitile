using UnityEditor;
using UnityEngine;

namespace Engine.Scripts.VoxelConfig.Data.Generation.Editor
{
    [CustomPropertyDrawer(typeof(VoxelPlacement))]
    public class VoxelPlacementCustomEditor : PropertyDrawer
    {
        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            // Calculate height dynamically based on visible fields
            int lines = 2; // shape + origin always visible

            PlacementShape shape = (PlacementShape)property.FindPropertyRelative("shape").enumValueIndex;

            switch (shape)
            {
                case PlacementShape.Line:
                    lines += 1; // end
                    break;
                case PlacementShape.Circle:
                    lines += 2; // radius + filled
                    break;

                case PlacementShape.Box:
                    lines += 2; // size + filled
                    break;

                case PlacementShape.Cylinder:
                    lines += 3; // radius + height + filled
                    break;
            }

            return lines * EditorGUIUtility.singleLineHeight;
        }

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            EditorGUI.BeginProperty(position, label, property);

            SerializedProperty shapeProp = property.FindPropertyRelative("shape");
            SerializedProperty originProp = property.FindPropertyRelative("origin");
            SerializedProperty endProp = property.FindPropertyRelative("end");
            SerializedProperty sizeProp = property.FindPropertyRelative("size");
            SerializedProperty heightProp = property.FindPropertyRelative("height");
            SerializedProperty radiusProp = property.FindPropertyRelative("radius");
            SerializedProperty filledProp = property.FindPropertyRelative("filled");

            PlacementShape shape = (PlacementShape)shapeProp.enumValueIndex;

            Rect r = new Rect(position.x, position.y, position.width, EditorGUIUtility.singleLineHeight);

            DrawField(shapeProp);
            DrawField(originProp);

            switch (shape)
            {
                case PlacementShape.Line:
                    DrawField(endProp);
                    break;

                case PlacementShape.Circle:
                    DrawField(radiusProp);
                    DrawField(filledProp);
                    break;

                case PlacementShape.Box:
                    DrawField(sizeProp);
                    DrawField(filledProp);
                    break;

                case PlacementShape.Cylinder:
                    DrawField(radiusProp);
                    DrawField(heightProp);
                    DrawField(filledProp);
                    break;
            }

            r.y -= EditorGUIUtility.singleLineHeight;
            EditorGUI.EndProperty();

            void DrawField(SerializedProperty prop)
            {
                EditorGUI.PropertyField(r, prop);
                r.y += EditorGUIUtility.singleLineHeight;
            }
        }
    }
}