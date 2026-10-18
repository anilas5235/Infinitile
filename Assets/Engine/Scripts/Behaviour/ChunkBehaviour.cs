using UnityEngine;

namespace Engine.Scripts.Behaviour{
    /// <summary>
    ///     MonoBehaviour representation of a chunk with a dedicated render mesh and collider mesh.
    /// </summary>
    public class ChunkBehaviour : MonoBehaviour{
        [SerializeField] private ChunkPartition[] chunkPartitions;

        /// <summary>
        ///     Initializes renderer-specific options (e.g. shadow casting) from settings.
        /// </summary>
        /// <summary>
        ///     PERF-04: cached at pool creation; the first <c>transform</c> access on an instance allocates the
        ///     managed wrapper, which otherwise happened on the first claim during flight.
        /// </summary>
        public Transform Transform{ get; private set; }

        public void Init(){
            Transform = transform;
            for (int pId = 0; pId < chunkPartitions.Length; pId++) chunkPartitions[pId].Init(pId);
        }

        public void ClearData(){
            foreach (ChunkPartition partition in chunkPartitions) partition.Clear();
        }

        public ChunkPartition GetPartition(int y){
            return chunkPartitions[y];
        }
    }
}
