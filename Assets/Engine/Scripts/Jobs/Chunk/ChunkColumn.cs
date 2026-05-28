namespace Engine.Scripts.Jobs.Chunk
{
    /// <summary>
    ///     Represents a data snapshot for a vertical column within a chunk, including height and biome attributes.
    /// </summary>
    public struct ChunkColumn
    {
        /// <summary>Computed terrain height in voxels.</summary>
        public int Height;

        /// <summary>Biome classification for the column.</summary>
        public Biome Biome;

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
    }
}