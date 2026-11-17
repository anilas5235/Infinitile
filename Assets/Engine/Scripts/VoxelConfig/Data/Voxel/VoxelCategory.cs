namespace Engine.Scripts.VoxelConfig.Data.Voxel{
    /// <summary>
    ///     The kind of block a voxel is, set by hand on each voxel asset. The inventory's category chips filter by
    ///     it and "Sort by kind" orders by it (in this order, then by id). <see cref="None" /> means not set yet; every
    ///     shipped voxel has a category (an EditMode test checks it).
    /// </summary>
    public enum VoxelCategory : byte{
        /// <summary>Not set; the voxel shows only under "All".</summary>
        None,

        /// <summary>Ground blocks: grass, dirt, sand, snow, ice, mud, clay, bedrock.</summary>
        Terrain,

        /// <summary>Stone, sandstone, gravel, rocks and ores.</summary>
        Stone,

        /// <summary>Logs and planks.</summary>
        Wood,

        /// <summary>Leaves, grass tufts, flowers, mushrooms, crops, cactus.</summary>
        Plants,

        /// <summary>Water and lava.</summary>
        Liquids,

        /// <summary>Blocks made to give light. The Light chip lists the voxels of this category.</summary>
        Light,

        /// <summary>Made blocks: bricks, glass, cotton, slabs, workbench, oven.</summary>
        Building
    }
}
