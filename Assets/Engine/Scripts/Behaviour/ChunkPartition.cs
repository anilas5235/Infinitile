using Engine.Scripts.Utils.Extensions;
using UnityEngine;
using static Engine.Scripts.Utils.VoxelConstants;

namespace Engine.Scripts.Behaviour{
    public class ChunkPartition : MonoBehaviour{
#if UNITY_EDITOR
        public static bool ShowPartitionGizmos = false;
#endif
        [SerializeField] private MeshCollider _Collider;

        /// <summary>
        ///     Dedicated mesh for collider (not shared with render mesh).
        /// </summary>
        public Mesh ColliderMesh{ get; private set; }

        /// <summary>
        ///     Access to the underlying MeshCollider.
        /// </summary>
        public MeshCollider Collider => _Collider;

        public short PartitionId{ get; private set; }

        /// <summary>
        ///     Incremented whenever <see cref="ColliderMesh" /> is cleared or rewritten, so a collider bake that
        ///     started on an older version can tell that its result is stale. Partition objects are reused per chunk,
        ///     so comparing the object alone does not detect a reclaim followed by a re-claim.
        /// </summary>
        public int MeshVersion{ get; private set; }

        private void Awake(){
            ColliderMesh = new Mesh{ name = "PCollider" };
        }

#if UNITY_EDITOR
        private void OnDrawGizmosSelected(){
            if (!ShowPartitionGizmos) return;

            Gizmos.color = Color.green;
            if (!_Collider.sharedMesh) Gizmos.color = Color.magenta;

            Gizmos.DrawWireCube(
                transform.position + PartitionSize.GetVector3() * 0.5f,
                PartitionSize.GetVector3() * 0.95f
            );
        }
#endif

        public void Init(int pId){
            PartitionId = (short)pId;
            transform.localPosition = new Vector3(0, PartitionHeight * pId, 0);
        }

        public void ApplyColliderMesh(){
            Collider.sharedMesh = ColliderMesh;
        }

        public void Clear(){
            MeshVersion++;
            ColliderMesh.Clear();
            Collider.sharedMesh = null;
        }

        /// <summary>Called before new collider mesh data is applied to <see cref="ColliderMesh" />.</summary>
        public void MarkMeshChanged(){
            MeshVersion++;
        }
    }
}
