using UnityEngine;
using static Engine.Scripts.Utils.VoxelConstants;

namespace Engine.Scripts.Behaviour{
    /// <summary>
    ///     MonoBehaviour representation of a chunk with a dedicated render mesh and collider mesh.
    /// </summary>
    public class ChunkBehaviour : MonoBehaviour{
        /// <summary>
        ///     The partition object; <see cref="Init" /> builds <see cref="PartitionsPerChunk" /> of them under the chunk
        ///     (AD-37), so a change of the build height cannot leave the prefab behind.
        /// </summary>
        [SerializeField] private ChunkPartition partitionPrefab;

        private ChunkPartition[] chunkPartitions;

        /// <summary>
        ///     Cached at pool creation; the first <c>transform</c> access on an instance allocates the managed
        ///     wrapper, which would otherwise happen on the first claim during flight.
        /// </summary>
        public Transform Transform{ get; private set; }

        /// <summary>
        ///     Bit p: partition p is active in the pool (<see cref="World.ChunkPool.IsPartitionActive" />), and its
        ///     collider is ready (<see cref="World.ChunkPool.IsCollidable" />). Kept by the pool while the chunk is
        ///     claimed, so the scans test a whole column at once (DEC-232).
        /// </summary>
        internal uint ActivePartitions, CollidablePartitions;

        public void Init(){
            Transform = transform;
            chunkPartitions = new ChunkPartition[PartitionsPerChunk];
            for (int pId = 0; pId < PartitionsPerChunk; pId++){
                ChunkPartition partition = Instantiate(partitionPrefab, Transform);
                partition.name = "ChunkPartition";
                partition.Init(pId);
                chunkPartitions[pId] = partition;
            }
        }

        public void ClearData(){
            foreach (ChunkPartition partition in chunkPartitions) partition.Clear();
        }

        public ChunkPartition GetPartition(int y){
            return chunkPartitions[y];
        }
    }
}
