using Runtime.Engine.VoxelConfig.Data;
using UnityEditor;
using UnityEngine;

namespace Runtime.Engine.VoxelConfig.Editor
{
    [CustomEditor(typeof(QuadDefinition)),  CanEditMultipleObjects]
    public class QuadDefinitionCustomEditor : UnityEditor.Editor
    {
        private const float PreviewHeight = 220f;
        private PreviewRenderUtility _previewUtility;
        private Material _previewMaterial;
        private Material _previewBackMat;
        private Mesh _previewMesh;
        private Vector2 _previewDir = new Vector2(120f, -20f);

        private void OnEnable()
        {
            _previewUtility = new PreviewRenderUtility
            {
                camera =
                {
                    fieldOfView = 30f,
                    nearClipPlane = 0.01f,
                    farClipPlane = 100f
                }
            };

            _previewUtility.lights[0].intensity = 1f;
            _previewUtility.lights[0].transform.rotation = Quaternion.Euler(40f, 40f, 0f);
            _previewUtility.lights[1].intensity = 1f;

            Shader shader = Shader.Find("Unlit/Texture");
            Shader backShader = Shader.Find("Unlit/Color");

            if (shader != null)
            {
                _previewMaterial = new Material(shader)
                {
                    hideFlags = HideFlags.HideAndDontSave,
                    mainTexture = Resources.Load<Texture2D>("Artwork/kenney_voxel-pack/PNG/Tiles/dirt_grass")
                };
            }

            if (backShader != null)
            {
                _previewBackMat = new Material(backShader)
                {
                    hideFlags = HideFlags.HideAndDontSave,
                    color = Color.red,
                };
            }
        }

        private void OnDisable()
        {
            if (_previewMesh != null)
            {
                DestroyImmediate(_previewMesh);
                _previewMesh = null;
            }

            if (_previewMaterial != null)
            {
                DestroyImmediate(_previewMaterial);
                _previewMaterial = null;
            }
            
            if(_previewBackMat != null)
            {
                DestroyImmediate(_previewBackMat);
                _previewBackMat = null;
            }

            if (_previewUtility != null)
            {
                _previewUtility.Cleanup();
                _previewUtility = null;
            }
        }

        public override void OnInspectorGUI()
        {
            QuadDefinition quadDef = (QuadDefinition)target;

            serializedObject.Update();

            SerializedProperty position00Prop = serializedObject.FindProperty("position00");
            SerializedProperty position01Prop = serializedObject.FindProperty("position01");
            SerializedProperty position02Prop = serializedObject.FindProperty("position02");
            SerializedProperty position03Prop = serializedObject.FindProperty("position03");

            EditorGUI.BeginChangeCheck();
            EditorGUILayout.PropertyField(position00Prop);
            EditorGUILayout.PropertyField(position01Prop);
            EditorGUILayout.PropertyField(position02Prop);
            EditorGUILayout.PropertyField(position03Prop);
            bool positionChanged = EditorGUI.EndChangeCheck();

            using (new EditorGUI.DisabledGroupScope(true))
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty("normal"));
            }

            EditorGUILayout.PropertyField(serializedObject.FindProperty("uv00"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("uv01"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("uv02"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("uv03"));

            DrawMeshPreviewSection(quadDef);

            serializedObject.ApplyModifiedProperties();

            if (positionChanged)
            {
                Undo.RecordObject(quadDef, "Recalculate Quad Normal");
                quadDef.RecalculateNormal();
                EditorUtility.SetDirty(quadDef);
            }
        }

        private void DrawMeshPreviewSection(QuadDefinition quadDef)
        {
            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("Quad Preview", EditorStyles.boldLabel);

            if (targets.Length > 1)
            {
                EditorGUILayout.HelpBox("Preview ist bei Multi-Selection deaktiviert.", MessageType.Info);
                return;
            }

            if (_previewUtility == null || _previewMaterial == null)
            {
                EditorGUILayout.HelpBox("PreviewRenderUtility ist nicht verfugbar.", MessageType.Warning);
                return;
            }

            EnsurePreviewMesh(quadDef);
            if (_previewMesh == null)
            {
                EditorGUILayout.HelpBox("Mesh-Preview konnte nicht erstellt werden.", MessageType.Warning);
                return;
            }

            Rect previewRect = GUILayoutUtility.GetRect(10f, PreviewHeight, GUILayout.ExpandWidth(true));
            HandlePreviewInput(previewRect);
            RenderPreview(previewRect);
        }

        private void HandlePreviewInput(Rect previewRect)
        {
            Event evt = Event.current;
            if (!previewRect.Contains(evt.mousePosition))
            {
                return;
            }

            if (evt.type == EventType.MouseDrag && evt.button == 0)
            {
                _previewDir += evt.delta * 0.5f;
                evt.Use();
                Repaint();
                return;
            }

            if (evt.type == EventType.ScrollWheel)
            {
                _previewDir.y = Mathf.Clamp(_previewDir.y + evt.delta.y, -89f, 89f);
                evt.Use();
                Repaint();
            }
        }

        private void RenderPreview(Rect previewRect)
        {
            _previewUtility.BeginPreview(previewRect, GUIStyle.none);
            _previewUtility.camera.clearFlags = CameraClearFlags.Color;
            _previewUtility.camera.backgroundColor = new Color(0.12f, 0.12f, 0.12f, 1f);

            Bounds bounds = _previewMesh.bounds;
            Vector3 targetPos = bounds.center;
            float radius = Mathf.Max(bounds.extents.magnitude, 0.5f);
            float distance = radius * 5f;

            Quaternion rotation = Quaternion.Euler(-_previewDir.y, -_previewDir.x, 0f);
            Vector3 cameraPos = targetPos + rotation * (Vector3.back * distance);

            _previewUtility.camera.transform.position = cameraPos;
            _previewUtility.camera.transform.rotation = rotation;
            _previewUtility.camera.transform.LookAt(targetPos);

            _previewUtility.DrawMesh(_previewMesh, Matrix4x4.identity, _previewMaterial, 0);
            _previewUtility.DrawMesh(_previewMesh, Matrix4x4.identity, _previewBackMat, 1);
            _previewUtility.camera.Render();

            Texture result = _previewUtility.EndPreview();
            GUI.DrawTexture(previewRect, result, ScaleMode.StretchToFill, false);
        }

        private void EnsurePreviewMesh(QuadDefinition quadDef)
        {
            if (_previewMesh == null)
            {
                _previewMesh = new Mesh
                {
                    name = "QuadPreviewMesh",
                    hideFlags = HideFlags.HideAndDontSave,
                    subMeshCount = 2,
                };
            }
            
            Vector3[] vertices = new[]
            {
                quadDef.position00,
                quadDef.position01,
                quadDef.position02,
                quadDef.position03
            };
            
            Vector2[] uv = new[]
            {
                quadDef.uv00,
                quadDef.uv01,
                quadDef.uv02,
                quadDef.uv03
            };

            _previewMesh.Clear();
            
            _previewMesh.vertices = vertices;
            _previewMesh.uv = uv;
            _previewMesh.subMeshCount = 2;
            _previewMesh.SetIndices(new[] { 0, 1, 2, 2, 1, 3 }, MeshTopology.Triangles, 0);
            _previewMesh.SetIndices(new[] { 2, 1, 0, 3, 1, 2 }, MeshTopology.Triangles, 1);
            _previewMesh.RecalculateNormals();
            _previewMesh.RecalculateBounds();
        }
    }
}
