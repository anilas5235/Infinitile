namespace Engine.Scripts.VoxelConfig.Data
{
    /// <summary>
    ///     Identifies the mesh layer used when rendering a voxel (solid, transparent or air).
    /// </summary>
    public enum MeshLayer : byte
    {
        /// <summary>
        ///     Opaque solid geometry rendered in the regular opaque pass.
        /// </summary>
        Solid = 0,

        /// <summary>
        ///     Transparent or alpha-blended geometry rendered in a transparent pass.
        /// </summary>
        Transparent = 1,

        /// <summary>
        ///     Non-solid geometry such as plants or grass, rendered with special handling to allow for alpha testing and correct
        ///     sorting.
        /// </summary>
        Foliage = 2,

        /// <summary>
        ///     Non-rendered air; used as a sentinel value.
        /// </summary>
        Air = byte.MaxValue
    }
}