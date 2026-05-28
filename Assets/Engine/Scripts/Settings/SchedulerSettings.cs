using System;
using UnityEngine;
using UnityEngine.Serialization;

namespace Engine.Scripts.Settings
{
    /// <summary>
    ///     Configuration for per-update batch sizes of individual schedulers (meshing, streaming, collider).
    ///     Higher values increase parallel work per frame but may cause frame spikes.
    /// </summary>
    [Serializable]
    public class SchedulerSettings
    {
        /// <summary>
        ///     Number of chunks processed per update for data streaming/generation.
        /// </summary>
        [Tooltip("Number of chunks to process per update for generation")]
        public int chunkGenBatchSize = 8;

        /// <summary>
        ///     Number of chunks processed per update for meshing.
        /// </summary>
        [Tooltip("Number of chunks to process per update for meshing")]
        public int meshingBatchSize = 4;

        /// <summary>
        ///     Number of partitions processed per update for GPU meshing.
        /// </summary>
        [Tooltip("Number of partitions to process on GPU per Frame")]
        public int partitionBuildBatchSize = 4;

        /// <summary>
        ///     Number of chunks processed per update for collider generation.
        /// </summary>
        [Tooltip("Number of chunks to process per update for collider generation")]
        public int colliderBatchSize = 4;
    }
}