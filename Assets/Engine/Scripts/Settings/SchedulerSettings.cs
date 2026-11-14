using System;
using Unity.Mathematics;
using UnityEngine;

namespace Engine.Scripts.Settings{
    /// <summary>
    ///     Configuration for per-update batch sizes of individual schedulers (meshing, streaming, collider).
    ///     Higher values increase parallel work per frame but may cause frame spikes.
    /// </summary>
    [Serializable]
    public class SchedulerSettings{
        /// <summary>Largest mesh batch that scaling up with the draw distance gives.</summary>
        public const int MaxScaledMeshBatchSize = 32;

        /// <summary>
        ///     Most GPU build slots that scaling up with the draw distance gives; every slot holds its own point
        ///     buffers on the GPU (about 8 MB).
        /// </summary>
        public const int MaxScaledBuildSlots = 16;

        /// <summary>
        ///     Number of chunks processed per update for data streaming/generation.
        /// </summary>
        [Tooltip("Number of chunks to process per update for generation")]
        public int chunkGenBatchSize = 8;

        /// <summary>
        ///     Partitions handed to the mesh job per batch, at <see cref="referenceDrawDistance" />. The engine uses
        ///     <see cref="MeshBatchSizeAt" />.
        /// </summary>
        [Tooltip("Partitions meshed per batch at the reference draw distance; scaled with the draw window")]
        public int meshingBatchSize = 16;

        /// <summary>
        ///     Partitions the renderer builds on the GPU at the same time (build slots), at
        ///     <see cref="referenceDrawDistance" />. The engine uses <see cref="BuildSlotsAt" />.
        /// </summary>
        [Tooltip("GPU build slots at the reference draw distance; scaled with the draw window")]
        public int partitionBuildBatchSize = 8;

        /// <summary>
        ///     Number of chunks processed per update for collider generation.
        /// </summary>
        [Tooltip("Number of chunks to process per update for collider generation")]
        public int colliderBatchSize = 4;

        /// <summary>
        ///     Number of chunks lit per light job batch.
        /// </summary>
        [Tooltip("Number of chunks to light per update")]
        public int lightBatchSize = 8;

        /// <summary>
        ///     Draw distance <see cref="meshingBatchSize" /> and <see cref="partitionBuildBatchSize" /> are given
        ///     for. At another draw distance both are scaled with the side of the draw window (2 x distance + 1):
        ///     flying across a chunk border exposes one row of the window, so that is the work the mesh stage has
        ///     to keep up with. 0 turns the scaling off.
        ///     The sizes are the smallest that kept up with a sprint flight (80 units per second, 2026-11-14): 16 and
        ///     8 at draw distance 12 (with 12 and 6 the mesh queue grew to 1195), 8 and 4 at 6 (with 4 and 2 it
        ///     still held 82 partitions at the end). Larger ones did not shorten a frame, they only take GPU memory.
        /// </summary>
        [Tooltip("Draw distance the mesh batch size and the GPU build slots are given for; 0 = no scaling")]
        public int referenceDrawDistance = 12;

        /// <summary>
        ///     Mesh batch size at <paramref name="drawDistance" />. Read once when the world is built: the mesh
        ///     scheduler sizes its containers with it.
        /// </summary>
        public int MeshBatchSizeAt(int drawDistance){
            return ScaleWithDrawWindow(meshingBatchSize, drawDistance, MaxScaledMeshBatchSize);
        }

        /// <summary>
        ///     GPU build slots at <paramref name="drawDistance" />. Read once when the world is built: the renderer
        ///     creates one point builder (with its GPU buffers) per slot.
        /// </summary>
        public int BuildSlotsAt(int drawDistance){
            return ScaleWithDrawWindow(partitionBuildBatchSize, drawDistance, MaxScaledBuildSlots);
        }

        private int ScaleWithDrawWindow(int value, int drawDistance, int scaledMax){
            value = math.max(1, value);
            if (referenceDrawDistance <= 0 || drawDistance == referenceDrawDistance) return value;

            int side = 2 * math.max(drawDistance, 0) + 1;
            int referenceSide = 2 * referenceDrawDistance + 1;
            int scaled = (2 * value * side + referenceSide) / (2 * referenceSide); // rounded to the nearest

            // Scaling does not lift a value past its cap; a value the asset sets above the cap stays as it is.
            return math.clamp(scaled, 1, math.max(value, scaledMax));
        }
    }
}
