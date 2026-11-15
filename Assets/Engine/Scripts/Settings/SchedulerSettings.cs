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
        ///     Main-thread time in milliseconds the pipeline's stages may spend per frame on collecting batches,
        ///     scanning for work and dispatching. A stage's turn is made of items of work (a pool claim, the upload
        ///     of one chunk, one row of a queue scan, the start of a batch): the first item of a frame always
        ///     runs, an item after it only while time is left, and what did not fit continues in the next frame.
        ///     An item that has started is finished, so a frame can exceed the value by one item. 0 switches the
        ///     limit off. Read once when the world is built.
        ///     The value is the budget of an uncapped frame. A frame under a frame cap whose period is longer than
        ///     8.33 ms adds <see cref="cappedFrameSpareShare" /> of the rest of its period (shipped, 60 frames cap:
        ///     6.17 ms), so a capped game builds the world about as fast as without the limit.
        ///     2 ms is the owner's pick of 2026-11-15 from a comparison of 2, 3 and 4 ms in a sprint flight at draw
        ///     distance 12 (Editor): the shortest frames of the three, with the columns around the focus fully
        ///     meshed; the far edge of the draw window and the collider queue fill later than with a larger value.
        /// </summary>
        [Tooltip("Main-thread milliseconds per frame for the pipeline's stages (collect, scan, dispatch); 0 = no limit")]
        public float mainThreadBudgetMs = 2f;

        /// <summary>
        ///     Share (0 to 1) of a capped frame's spare time that is added to <see cref="mainThreadBudgetMs" />. The
        ///     spare time is the part of the frame cap's period above 8.33 ms, the frame of the 120 frames per second
        ///     goal: under the 60 frames cap 8.33 ms, under the 30 frames cap 25 ms, under a cap of 120 frames or more
        ///     none. The cap is the one in force in each frame (vertical sync with the display's refresh rate, else
        ///     <see cref="Application.targetFrameRate" />), so a cap changed at run time is followed. Without a cap
        ///     the budget is <see cref="mainThreadBudgetMs" />. 0 keeps the budget at <see cref="mainThreadBudgetMs" />
        ///     under every cap (the behaviour of the budget as it was introduced on 2026-11-15). Read once when the
        ///     world is built.
        ///     0.5 is the owner's pick of 2026-11-15 from 0, 0.5 and 1, measured under the 60 frames cap at draw
        ///     distance 12 (Editor): budget 6.17 ms, a fresh world in 703 frames as without a budget (799 to 804
        ///     with 0), the draw window in a sprint flight meshed 0.59 on average (0.60 with 1 and without a budget,
        ///     0.40 with 0), complete 5.2 to 5.4 s after the flight (4.6 to 4.7 s with 1), the scheduler's time per
        ///     frame at most 8.8 to 9.0 ms. Not 1: the pipeline could then take all of a capped frame's spare time,
        ///     and since a started item is finished, a 60 frames frame would fit only while the rest of it stays
        ///     within 6.33 ms; with 0.5 about 4.2 ms of the frame stay free, 1.5 ms even after an overshoot.
        /// </summary>
        [Range(0f, 1f)]
        [Tooltip("Share of a capped frame's time above 8.33 ms added to the main-thread budget; 0 = budget only")]
        public float cappedFrameSpareShare = 0.5f;

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
