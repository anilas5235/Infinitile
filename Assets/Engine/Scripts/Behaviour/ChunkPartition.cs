using Engine.Scripts.Jobs.ColliderBake;
using Engine.Scripts.Utils;
using Engine.Scripts.Utils.Extensions;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering;
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

        /// <summary>
        ///     True while the collider mesh is empty and the collider has no mesh, as <see cref="Clear" /> leaves them:
        ///     clearing again changes nothing, so the claim of a partition without a voxel (most of the world above
        ///     the generation top) skips the Unity calls (DEC-232).
        /// </summary>
        public bool IsClear{ get; private set; }

        private void Awake(){
            ColliderMesh = new Mesh{ name = "PCollider" };
        }

        private void OnDestroy(){
            CoreUtils.Destroy(ColliderMesh); // DestroyImmediate outside play mode (EditMode tests)
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
            // Unity reuses the job's Physics.BakeMesh result only if the options match; otherwise assigning the mesh
            // cooks it again on the main thread
            _Collider.cookingOptions = ColliderBuildJob.CookingOptions;
        }

        public void ApplyColliderMesh(){
            using ProfilerMarker.AutoScope _ = VoxelProfilerMarkers.ColliderAssign.Auto();
            // An empty mesh has no collider; the partition may still hold the one from before its last block went
            Collider.sharedMesh = ColliderMesh.vertexCount > 0 ? ColliderMesh : null;
        }

        public void Clear(){
            MeshVersion++;
            if (IsClear) return;

            using ProfilerMarker.AutoScope _ = VoxelProfilerMarkers.PartitionClear.Auto();
            ColliderMesh.Clear();
            Collider.sharedMesh = null;
            IsClear = true;
        }

        /// <summary>Called before new collider mesh data is applied to <see cref="ColliderMesh" />.</summary>
        public void MarkMeshChanged(){
            MeshVersion++;
            IsClear = false;
        }
    }
}
