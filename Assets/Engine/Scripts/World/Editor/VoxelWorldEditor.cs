using UnityEditor;
using UnityEngine;
using static Engine.Scripts.Behaviour.ChunkPartition;

namespace Engine.Scripts.World.Editor
{
    [CustomEditor(typeof(VoxelWorld))]
    public class VoxelWorldEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            base.OnInspectorGUI();
            GUILayout.Space(10);

            ShowPartitionGizmos =
                GUILayout.Toggle(ShowPartitionGizmos, "Show Partition Gizmos");
        }
    }
}