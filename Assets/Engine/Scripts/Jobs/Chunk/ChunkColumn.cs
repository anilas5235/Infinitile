namespace Engine.Scripts.Jobs.Chunk{
    /// <summary>
    ///     Represents a data snapshot for a vertical column within a chunk, including height and biome attributes,
    ///     filled from <c>WorldSampler.SampleColumn</c>.
    /// </summary>
    public struct ChunkColumn{
        /// <summary>Computed terrain height in voxels (h_int = floor(H)).</summary>
        public int Height;

        /// <summary>
        ///     Hard biome class of the column: index into the active BiomeSet, 0 when no biome is active
        ///     (then <see cref="SurfaceSource" /> is -1).
        /// </summary>
        public ushort Biome;

        /// <summary>Surface block voxel ID: the biome's top block, or its underwater top block when water covers it.</summary>
        public ushort TopBlock;

        /// <summary>Sub-surface block voxel ID (just below the top block).</summary>
        public ushort UnderBlock;

        /// <summary>Stone layer block voxel ID.</summary>
        public ushort StoneBlock;

        /// <summary>Sampled temperature factor for this column.</summary>
        public float Temperature;

        /// <summary>Sampled humidity factor for this column.</summary>
        public float Humidity;

        /// <summary>Biome whose top/under/stone blocks the column uses; -1 when no biome is active.</summary>
        public int SurfaceSource;

        /// <summary>
        ///     Flora species id at the column, 0 = none: a single-block plant, or a multi-block species at its
        ///     anchor column, equal to <c>ColumnSample.FloraSpecies</c>.
        /// </summary>
        public ushort FloraSpecies;

        /// <summary>Biome whose flora rule produced <see cref="FloraSpecies" />; -1 when none.</summary>
        public int FloraSource;

        /// <summary>Clamped height H before flooring.</summary>
        public float HeightF;
    }
}
