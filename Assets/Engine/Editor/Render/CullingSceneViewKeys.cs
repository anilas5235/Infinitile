using Engine.Scripts.Render;
using UnityEditor;
using UnityEngine;

namespace Engine.Editor.Render{
    /// <summary>
    ///     CUL-06 in the Scene view: the game-side debug keys go through the Input System, which in the Editor only
    ///     receives the keyboard while the Game view has focus. While playing, F7 (overlay), F8 (culling) and F9
    ///     (freeze) therefore also work with the Scene view focused, and a status line shows the switches.
    /// </summary>
    [InitializeOnLoad]
    internal static class CullingSceneViewKeys{
        static CullingSceneViewKeys(){
            SceneView.duringSceneGui += OnSceneGui;
        }

        private static void OnSceneGui(SceneView view){
            if (!EditorApplication.isPlaying) return;
            VoxelWorldRenderer renderer = VoxelWorldRenderer.Instance;
            if (renderer == null) return;

            Event e = Event.current;
            if (e.type == EventType.KeyDown && !e.alt && !e.control && !e.shift && !e.command){
                bool handled = true;
                switch (e.keyCode){
                    case KeyCode.F7: renderer.ShowCullingOverlay = !renderer.ShowCullingOverlay; break;
                    case KeyCode.F8: renderer.GpuCulling = !renderer.GpuCulling; break;
                    case KeyCode.F9: renderer.FreezeCulling = !renderer.FreezeCulling; break;
                    default: handled = false; break;
                }

                if (handled){
                    e.Use();
                    SceneView.RepaintAll();
                }
            }

            Handles.BeginGUI();
            string freeze = renderer.FreezeCulling
                ? "on (Scene view shows what the game camera kept)"
                : "off (Scene view culls with its own frustum, F9 to freeze)";
            GUI.Label(new Rect(10, view.position.height - 60, 600, 20),
                $"GPU culling (F8): {(renderer.GpuCulling ? "on" : "off")}   freeze (F9): {freeze}",
                EditorStyles.whiteLabel);
            Handles.EndGUI();
        }
    }
}
