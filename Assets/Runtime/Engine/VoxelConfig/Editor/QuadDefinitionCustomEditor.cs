using Runtime.Engine.VoxelConfig.Data;
using UnityEditor;
using UnityEngine;

namespace Runtime.Engine.VoxelConfig.Editor
{
    [CustomEditor(typeof(QuadDefinition)), CanEditMultipleObjects]
    public class QuadDefinitionCustomEditor : UnityEditor.Editor
    {
        private const float PreviewHeight = 220f;
        private PreviewRenderUtility _previewUtility;
        private Material _previewMaterial;
        private Material _previewBackMat;
        private Material _previewWireMat;
        private Material _previewAxisXMat;
        private Material _previewAxisYMat;
        private Material _previewAxisZMat;
        private Material _previewNormalMat;
        private Mesh _previewMesh;
        private Mesh _previewWireCubeMesh;
        private Mesh _previewAxisLineMesh;
        private Vector2 _previewDir = new(120f, -20f);
        private float _camDistanceFactor = 4f;

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
            Shader wireShader = Shader.Find("Unlit/Color");
            Shader axisShader = Shader.Find("Unlit/Color");

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

            if (wireShader != null)
            {
                _previewWireMat = new Material(wireShader)
                {
                    hideFlags = HideFlags.HideAndDontSave,
                    color = new Color(0.4f, 0.9f, 1f, 1f)
                };
            }

            if (axisShader != null)
            {
                _previewAxisXMat = new Material(axisShader)
                {
                    hideFlags = HideFlags.HideAndDontSave,
                    color = Color.red
                };
                _previewAxisYMat = new Material(axisShader)
                {
                    hideFlags = HideFlags.HideAndDontSave,
                    color = Color.green
                };
                _previewAxisZMat = new Material(axisShader)
                {
                    hideFlags = HideFlags.HideAndDontSave,
                    color = Color.blue
                };
                _previewNormalMat = new Material(axisShader)
                {
                    hideFlags = HideFlags.HideAndDontSave,
                    color = Color.yellow
                };
            }

            EnsureWireCubeMesh();
            EnsureAxisLineMesh();
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

            if (_previewBackMat != null)
            {
                DestroyImmediate(_previewBackMat);
                _previewBackMat = null;
            }

            if (_previewWireMat != null)
            {
                DestroyImmediate(_previewWireMat);
                _previewWireMat = null;
            }

            if (_previewWireCubeMesh != null)
            {
                DestroyImmediate(_previewWireCubeMesh);
                _previewWireCubeMesh = null;
            }

            if (_previewAxisLineMesh != null)
            {
                DestroyImmediate(_previewAxisLineMesh);
                _previewAxisLineMesh = null;
            }

            if (_previewAxisXMat != null)
            {
                DestroyImmediate(_previewAxisXMat);
                _previewAxisXMat = null;
            }

            if (_previewAxisYMat != null)
            {
                DestroyImmediate(_previewAxisYMat);
                _previewAxisYMat = null;
            }

            if (_previewAxisZMat != null)
            {
                DestroyImmediate(_previewAxisZMat);
                _previewAxisZMat = null;
            }

            if (_previewNormalMat != null)
            {
                DestroyImmediate(_previewNormalMat);
                _previewNormalMat = null;
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
                EditorGUILayout.PropertyField(serializedObject.FindProperty("up"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("right"));
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
            RenderPreview(previewRect, quadDef);
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
                _previewDir.y = Mathf.Clamp(_previewDir.y, -89f, 89f);
                evt.Use();
                Repaint();
                return;
            }

            if (evt.type == EventType.ScrollWheel)
            {
                _camDistanceFactor += evt.delta.y * 0.2f;
                _camDistanceFactor = Mathf.Max(0.1f, _camDistanceFactor);
                evt.Use();
                Repaint();
            }
        }

        private void RenderPreview(Rect previewRect, QuadDefinition quadDef)
        {
            _previewUtility.BeginPreview(previewRect, GUIStyle.none);
            _previewUtility.camera.clearFlags = CameraClearFlags.Color;
            _previewUtility.camera.backgroundColor = new Color(0.12f, 0.12f, 0.12f, 1f);

            Bounds bounds = GetPreviewBounds();
            Vector3 targetPos = bounds.center;
            float radius = Mathf.Max(bounds.extents.magnitude, 0.5f);
            float distance = radius * _camDistanceFactor;

            Quaternion rotation = Quaternion.Euler(_previewDir.y, _previewDir.x, 0f);
            Vector3 cameraPos = targetPos + rotation * (Vector3.back * distance);

            _previewUtility.camera.transform.position = cameraPos;
            _previewUtility.camera.transform.rotation = rotation;
            _previewUtility.camera.transform.LookAt(targetPos);

            _previewUtility.DrawMesh(_previewMesh, Matrix4x4.identity, _previewMaterial, 0);
            _previewUtility.DrawMesh(_previewMesh, Matrix4x4.identity, _previewBackMat, 1);

            _previewUtility.DrawMesh(_previewWireCubeMesh, Matrix4x4.identity, _previewWireMat, 0);

            DrawLines(quadDef);

            _previewUtility.camera.Render();

            Texture result = _previewUtility.EndPreview();
            GUI.DrawTexture(previewRect, result, ScaleMode.StretchToFill, false);
        }

        private void EnsureAxisLineMesh()
        {
            if (_previewAxisLineMesh != null)
            {
                return;
            }

            _previewAxisLineMesh = new Mesh
            {
                name = "AxisLinePreviewMesh",
                hideFlags = HideFlags.HideAndDontSave,
                vertices = new[]
                {
                    Vector3.zero,
                    Vector3.right
                }
            };

            _previewAxisLineMesh.SetIndices(new[] { 0, 1 }, MeshTopology.Lines, 0);
            _previewAxisLineMesh.RecalculateBounds();
        }

        private void DrawLines(QuadDefinition quadDef)
        {
            if (!_previewAxisLineMesh || !_previewAxisXMat || !_previewAxisYMat ||
                !_previewAxisZMat)
            {
                return;
            }

            Vector3 center = new(0.5f, 0.5f, 0.5f);

            Matrix4x4 xMatrix = Matrix4x4.TRS(center, Quaternion.identity, Vector3.one);
            Matrix4x4 yMatrix = Matrix4x4.TRS(center, Quaternion.Euler(0f, 0f, 90f), Vector3.one);
            Matrix4x4 zMatrix = Matrix4x4.TRS(center, Quaternion.Euler(0f, -90f, 0f), Vector3.one);

            _previewUtility.DrawMesh(_previewAxisLineMesh, xMatrix, _previewAxisXMat, 0);
            _previewUtility.DrawMesh(_previewAxisLineMesh, yMatrix, _previewAxisYMat, 0);
            _previewUtility.DrawMesh(_previewAxisLineMesh, zMatrix, _previewAxisZMat, 0);

            if (!_previewNormalMat) return;
            
            Vector3 quadCenter =
                (quadDef.position00 + quadDef.position01 + quadDef.position02 + quadDef.position03) * 0.25f;
            Vector3 normal = quadDef.normal.sqrMagnitude > 0.0001f
                ? quadDef.normal.normalized
                : Vector3.up;

            Quaternion normalRotation = Quaternion.FromToRotation(Vector3.right, normal);
            Matrix4x4 normalMatrix = Matrix4x4.TRS(quadCenter, normalRotation, Vector3.one);
            _previewUtility.DrawMesh(_previewAxisLineMesh, normalMatrix, _previewNormalMat, 0);
        }

        private void EnsurePreviewMesh(QuadDefinition quadDef)
        {
            if (!_previewMesh)
            {
                _previewMesh = new Mesh
                {
                    name = "QuadPreviewMesh",
                    hideFlags = HideFlags.HideAndDontSave,
                    subMeshCount = 2,
                };
            }

            Vector3[] vertices = {
                quadDef.position00,
                quadDef.position01,
                quadDef.position02,
                quadDef.position03
            };

            Vector2[] uv = {
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

        private void EnsureWireCubeMesh()
        {
            if (_previewWireCubeMesh)
            {
                return;
            }

            _previewWireCubeMesh = new Mesh
            {
                name = "UnitWireCubePreviewMesh",
                hideFlags = HideFlags.HideAndDontSave,
                vertices = new[]
                {
                    new Vector3(0f, 0f, 0f),
                    new Vector3(1f, 0f, 0f),
                    new Vector3(1f, 1f, 0f),
                    new Vector3(0f, 1f, 0f),
                    new Vector3(0f, 0f, 1f),
                    new Vector3(1f, 0f, 1f),
                    new Vector3(1f, 1f, 1f),
                    new Vector3(0f, 1f, 1f)
                }
            };

            _previewWireCubeMesh.SetIndices(new[]
            {
                0, 1, 1, 2, 2, 3, 3, 0,
                4, 5, 5, 6, 6, 7, 7, 4,
                0, 4, 1, 5, 2, 6, 3, 7
            }, MeshTopology.Lines, 0);
            _previewWireCubeMesh.RecalculateBounds();
        }

        private Bounds GetPreviewBounds()
        {
            Bounds bounds = _previewMesh.bounds;
            bounds.Encapsulate(Vector3.zero);
            bounds.Encapsulate(Vector3.one);
            Vector3 center = new(0.5f, 0.5f, 0.5f);
            bounds.Encapsulate(center + Vector3.right);
            bounds.Encapsulate(center + Vector3.up);
            bounds.Encapsulate(center + Vector3.forward);

            if (target is QuadDefinition quadDef)
            {
                Vector3 quadCenter =
                    (quadDef.position00 + quadDef.position01 + quadDef.position02 + quadDef.position03) * 0.25f;
                Vector3 normal = quadDef.normal.sqrMagnitude > 0.0001f
                    ? quadDef.normal.normalized
                    : Vector3.up;
                bounds.Encapsulate(quadCenter);
                bounds.Encapsulate(quadCenter + normal);
            }

            return bounds;
        }
    }
}