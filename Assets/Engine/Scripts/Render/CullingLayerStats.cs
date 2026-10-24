namespace Engine.Scripts.Render{
    /// <summary>
    ///     Page counts of one render layer for the game camera, filled by
    ///     <see cref="VoxelWorldRenderer.GetCullingStats" />. The visible counts are GPU read-backs, a few frames late.
    /// </summary>
    public struct CullingLayerStats{
        /// <summary>Layer name (solid, transparent, foliage).</summary>
        public string Layer;

        /// <summary>Pages the main pass draws.</summary>
        public uint VisiblePages;

        /// <summary>Pages the shadow pass draws; 0 without shadows.</summary>
        public uint ShadowPages;

        /// <summary>Pages allocated over all buffers of the layer.</summary>
        public int AllocatedPages;

        /// <summary>Render buffers of the layer.</summary>
        public int Buffers;
    }
}
