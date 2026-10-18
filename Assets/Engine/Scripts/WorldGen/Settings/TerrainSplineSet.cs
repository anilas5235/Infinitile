using UnityEngine;

namespace Engine.Scripts.WorldGen.Settings{
    /// <summary>
    ///     Terrain-first spline set (TRN-05): Offset, Amp and J as nested splines over (C, E, PV) with JSON
    ///     import/export. Phase 0 only introduces the asset type so <see cref="WorldGenSettings" /> can reference
    ///     it and inline it into the canonical JSON; the control points and the Burst mirror follow in Phase 1.
    /// </summary>
    [CreateAssetMenu(fileName = "TerrainSplineSet", menuName = "Voxel/TerrainSplineSet", order = 2)]
    public class TerrainSplineSet : ScriptableObject{
        [TextArea]
        [Tooltip("Free text; the spline control points are added in Phase 1 (TRN-01…05).")]
        public string Notes;
    }
}
