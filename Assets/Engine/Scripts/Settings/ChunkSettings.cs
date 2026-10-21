using System;
using Unity.Mathematics;
using UnityEngine;

namespace Engine.Scripts.Settings{
    /// <summary>
    ///     Settings for chunk size and view/load distances. Impacts memory and performance characteristics.
    /// </summary>
    [Serializable]
    public class ChunkSettings{
        /// <summary>
        ///     Prefab containing MeshRenderer + Filter + Collider for a chunk.
        /// </summary>
        public GameObject ChunkPrefab;

        /// <summary>
        ///     View distance (actively rendered chunks in each direction). Formula: active = (2*DrawDistance+1)^2.
        /// </summary>
        [Tooltip("Number of active chunks = (2 * draw_distance + 1)^2")]
        public int DrawDistance = 2;

        /// <summary>
        ///     Chunks kept in memory in each direction: data -> light (needs 3 x 3 data) -> mesh (needs 3 x 3 light),
        ///     so the draw distance plus one ring per stage and one for the light stage. Derived, never stored, so
        ///     the settings asset is not changed at runtime.
        /// </summary>
        public int LoadDistance => LoadDistanceOverride ?? DrawDistance + 3;

        /// <summary>Distance for collider baking around the focus; at most the draw distance, at least 2.</summary>
        public int UpdateDistance => math.max(DrawDistance - 2, 2);

        /// <summary>Tests only: a load distance independent of the draw distance, e.g. for a small chunk store.</summary>
        internal int? LoadDistanceOverride{ get; set; }
    }
}
