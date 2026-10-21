using Engine.Scripts.Behaviour;
using Engine.Scripts.World;
using UnityEditor;

namespace Engine.Editor.World{
    [CustomEditor(typeof(VoxelWorld))]
    public class VoxelWorldEditor : UnityEditor.Editor{
        public override void OnInspectorGUI(){
            base.OnInspectorGUI();

            VoxelWorld voxelWorld = (VoxelWorld)target;
            ChunkPartition.ShowPartitionGizmos = voxelWorld.ShowGizmos;
        }
    }
}
