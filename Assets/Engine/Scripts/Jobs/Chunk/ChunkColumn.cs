namespace Engine.Scripts.Jobs.Chunk{
    /// <summary>
    ///     Represents a data snapshot for a vertical column within a chunk, including height and biome attributes,
    ///     filled from <c>WorldSampler.SampleColumn</c> (INT-10).
    /// </summary>
    public struct ChunkColumn{
        /// <summary>Computed terrain height in voxels (h_int = floor(H)).</summary>
        public int Height;

        /// <summary>
        ///     Hard biome class of the column: index into the active BiomeSet (GEN-21), 0 when no biome is active
        ///     (then <see cref="SurfaceSource" /> is -1).
        /// </summary>
        public ushort Biome;

        /// <summary>Surface block voxel ID.</summary>
        public ushort TopBlock;

        /// <summary>Sub-surface block voxel ID (just below the top block).</summary>
        public ushort UnderBlock;

        /// <summary>Stone layer block voxel ID.</summary>
        public ushort StoneBlock;

        /// <summary>Sampled temperature factor for this column.</summary>
        public float Temperature;

        /// <summary>Sampled humidity factor for this column.</summary>
        public float Humidity;

        /// <summary>Biome whose top/under/stone blocks the column uses (§9.6); -1 when no biome is active. INT-10.</summary>
        public int SurfaceSource;

        /// <summary>Flora species id at the column, 0 = none (VEG-06). Assigned in Phase 4. INT-10.</summary>
        public ushort FloraSpecies;

        /// <summary>Biome whose flora rule produced <see cref="FloraSpecies" />; -1 when none. INT-10.</summary>
        public int FloraSource;

        /// <summary>Clamped height H before flooring (§5.7). INT-10.</summary>
        public float HeightF;
    }
}
