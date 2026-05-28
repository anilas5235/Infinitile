using UnityEngine;

namespace Engine.Scripts.Behaviour
{
    /// <summary>
    ///     MonoBehaviour representation of a chunk with a dedicated render mesh and collider mesh.
    /// </summary>
    public class ChunkBehaviour : MonoBehaviour
    {
        [SerializeField] private ChunkPartition[] chunkPartitions;

        /// <summary>
        ///     Initializes renderer-specific options (e.g. shadow casting) from settings.
        /// </summary>
        public void Init()
        {
            for (int pId = 0; pId < chunkPartitions.Length; pId++) chunkPartitions[pId].Init(pId);
        }

        public void ClearData()
        {
            foreach (ChunkPartition partition in chunkPartitions) partition.Clear();
        }

        public ChunkPartition GetPartition(int y)
        {
            return chunkPartitions[y];
        }
    }
}