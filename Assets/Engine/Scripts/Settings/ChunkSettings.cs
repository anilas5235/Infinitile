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
        ///     View distance of the settings asset (actively rendered chunks in each direction). Formula: active =
        ///     (2*DrawDistance+1)^2. The world is built with <see cref="ActiveDrawDistance" />, which a player's
        ///     choice can replace at run time without writing the asset.
        /// </summary>
        [Tooltip("Number of active chunks = (2 * draw_distance + 1)^2")]
        public int DrawDistance = 2;

        /// <summary>
        ///     The draw distance the world is built with: the override the world set from the player's choice, else
        ///     <see cref="DrawDistance" />. Every size and window of the engine follows this one.
        /// </summary>
        public int ActiveDrawDistance => DrawDistanceOverride > 0 ? DrawDistanceOverride : DrawDistance;

        /// <summary>
        ///     Chunks kept in memory in each direction: data -> light (needs 3 x 3 data) -> mesh (needs 3 x 3 light),
        ///     so the draw distance plus one ring per stage and one for the light stage. Derived, never stored, so
        ///     the settings asset is not changed at runtime.
        /// </summary>
        public int LoadDistance => LoadDistanceOverride ?? ActiveDrawDistance + 3;

        /// <summary>Distance for collider baking around the focus; at most the draw distance, at least 2.</summary>
        public int UpdateDistance => math.max(ActiveDrawDistance - 2, 2);

        /// <summary>Tests only: a load distance independent of the draw distance, e.g. for a small chunk store.</summary>
        internal int? LoadDistanceOverride{ get; set; }

        /// <summary>
        ///     A draw distance that replaces <see cref="DrawDistance" /> while a world runs; 0 for none. Set by the
        ///     world when it is built or changes its draw distance, cleared when it is destroyed. Not serialized, so
        ///     the asset keeps its value.
        /// </summary>
        internal int DrawDistanceOverride{ get; set; }
    }
}
