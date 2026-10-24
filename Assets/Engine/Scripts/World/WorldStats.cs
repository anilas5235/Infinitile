namespace Engine.Scripts.World{
    /// <summary>Loaded chunks and job queue lengths of the <see cref="VoxelWorld" />, for debug readouts.</summary>
    public struct WorldStats{
        /// <summary>Chunk columns whose data is generated.</summary>
        public int LoadedChunks;

        /// <summary>Chunks waiting for generation.</summary>
        public int DataQueue;

        /// <summary>Chunks waiting for light.</summary>
        public int LightQueue;

        /// <summary>Partitions waiting for a mesh.</summary>
        public int MeshQueue;

        /// <summary>Partitions waiting for a collider bake.</summary>
        public int BakeQueue;
    }
}
