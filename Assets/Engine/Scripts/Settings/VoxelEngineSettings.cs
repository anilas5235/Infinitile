using UnityEngine;

namespace Engine.Scripts.Settings
{
    /// <summary>
    ///     Central ScriptableObject configuration for the voxel engine (seed, noise, chunk, renderer, scheduler).
    /// </summary>
    [CreateAssetMenu(fileName = "VoxelEngineSettings", menuName = "Voxel/EngineSettings", order = 0)]
    public class VoxelEngineSettings : ScriptableObject
    {
        /// <summary>
        ///     Global seed for deterministic world generation.
        /// </summary>
        public int Seed = 1337;

        /// <summary>
        ///     Noise parameters (scale, persistence, etc.).
        /// </summary>
        public NoiseSettings Noise;

        /// <summary>
        ///     Chunk related settings (dimensions, distances, prefabs).
        /// </summary>
        public ChunkSettings Chunk;

        /// <summary>
        ///     Renderer options (shadows, materials).
        /// </summary>
        public RendererSettings Renderer;

        /// <summary>
        ///     Scheduler batch sizes.
        /// </summary>
        public SchedulerSettings Scheduler;
    }
}