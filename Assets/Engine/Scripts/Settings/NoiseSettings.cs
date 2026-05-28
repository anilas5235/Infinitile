using UnityEngine;

namespace Engine.Scripts.Settings
{
    /// <summary>
    ///     Configuration for world noise and height levels (water). Used during generation.
    /// </summary>
    [CreateAssetMenu(fileName = "NoiseSettings2D", menuName = "Voxel/NoiseSettings", order = 0)]
    public class NoiseSettings : ScriptableObject
    {
        /// <summary>
        ///     Water surface level in world Y coordinates.
        /// </summary>
        public int WaterLevel = 96;

        /// <summary>
        ///     Base noise scale.
        /// </summary>
        public float Scale = 256;

        /// <summary>
        ///     Amplitude reduction per octave (persistence).
        /// </summary>
        public float Persistance = 0.5f;

        /// <summary>
        ///     Frequency increase per octave (lacunarity).
        /// </summary>
        public float Lacunarity = 2f;

        /// <summary>
        ///     Number of octaves.
        /// </summary>
        public int Octaves = 4;
    }
}