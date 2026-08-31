using Engine.Scripts.Behaviour;
using UnityEditor;

namespace Engine.Scripts.World.Editor
{
    [CustomEditor(typeof(VoxelWorld))]
    public class VoxelWorldEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            base.OnInspectorGUI();

            VoxelWorld voxelWorld = (VoxelWorld)target;
            ChunkPartition.ShowPartitionGizmos = voxelWorld.ShowGizmos;
        }
    }
}