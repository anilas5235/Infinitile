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
        [Tooltip("Water surface level in world Y coordinates.")]
        public int WaterLevel = 96;

        /// <summary>
        ///     Base noise scale.
        /// </summary>
        [Tooltip("Base noise scale.")]
        public float Scale = 256;

        /// <summary>
        ///     Amplitude reduction per octave.
        /// </summary>
        [Tooltip("Amplitude reduction per octave.")]
        public float Persistance = 0.5f;

        /// <summary>
        ///     Frequency increase per octave.
        /// </summary>
        [Tooltip("Frequency increase per octave.")]
        public float Lacunarity = 2f;

        /// <summary>
        ///     Number of octaves.
        /// </summary>
        [Tooltip("Number of octaves.")]
        public int Octaves = 4;

        [Tooltip("Scale factor for humidity climate noise sampling.")]
        public float HumidityScale = 0.0012f;

        [Tooltip("Scale factor for temperature climate noise sampling.")]
        public float TemperatureScale = 0.0012f;

        [Tooltip("Scale factor for continental climate noise sampling.")]
        public float ContinentalScale = 0.0012f;
    }
}