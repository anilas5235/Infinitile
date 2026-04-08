using System.Threading.Tasks;
using Runtime.Engine.Utils.Extensions;
using UnityEditor;
using UnityEngine;
using static Runtime.Engine.Utils.VoxelConstants;

namespace Runtime.Engine.Behaviour
{
    public class ChunkPartition : MonoBehaviour
    {
#if UNITY_EDITOR
        public static bool ShowPartitionGizmos = false;
#endif
        [SerializeField] private MeshCollider _Collider;

        private Task _handel;

        /// <summary>
        /// Dedicated mesh for collider (not shared with render mesh).
        /// </summary>
        public Mesh ColliderMesh { get; private set; }

        /// <summary>
        /// Access to the underlying MeshCollider.
        /// </summary>
        public MeshCollider Collider => _Collider;

        public short PartitionId { get; private set; }

        private void Awake()
        {
            ColliderMesh = new Mesh { name = "PCollider" };
        }

        public void Init(int pId)
        {
            PartitionId = (short)pId;
            transform.localPosition = new Vector3(0, PartitionHeight * pId, 0);
        }


        public void ApplyColliderMesh()
        {
            Collider.sharedMesh = ColliderMesh;
        }

        public void Clear()
        {
            ColliderMesh.Clear();
            Collider.sharedMesh = null;
        }

#if UNITY_EDITOR
        private static readonly Color[] FaceColors =
            { Color.red, Color.blue, Color.green, Color.yellow, Color.cyan, Color.magenta };

        private static readonly Vector3[] FaceNormals =
            { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back };

        private void OnDrawGizmosSelected()
        {
            if (!ShowPartitionGizmos) return;

            Gizmos.color = Color.green;
            if (!_Collider.sharedMesh) Gizmos.color = Color.magenta;

            Gizmos.DrawWireCube(
                transform.position + PartitionSize.GetVector3() * 0.5f,
                PartitionSize.GetVector3() * 0.95f
            );

            // Draw partition ID
            Gizmos.color = Color.white;
            Handles.Label(
                transform.position + PartitionSize.GetVector3() * 0.5f,
                $"Id:{PartitionId}",
                new GUIStyle()
                {
                    fontSize = 20,
                    normal = new GUIStyleState() { textColor = Color.white }
                }
            );
        }
#endif
    }
}