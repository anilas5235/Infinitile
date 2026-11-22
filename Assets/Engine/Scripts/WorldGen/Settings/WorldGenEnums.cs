namespace Engine.Scripts.WorldGen.Settings{
    /// <summary>Which pipeline produces height and biomes.</summary>
    public enum GenArchitecture : byte{
        /// <summary>Height from nested splines over (C, E, PV); biomes classified in 6D including h.</summary>
        TerrainFirst = 0,

        /// <summary>Biomes classified in 5D; height from blended per-biome parameters.</summary>
        BiomeFirst = 1
    }

    /// <summary>Which generator fills the chunks (WorldGen v2 §2.1): v1 stays the default until V7.</summary>
    public enum WorldGenerator : byte{
        /// <summary>The v1 height-field generator (<see cref="GenArchitecture" /> picks its pipeline).</summary>
        HeightField = 0,

        /// <summary>
        ///     The v2 density generator: terrain from the shipped noise graph's terrain_density (§5); until V3 the fill
        ///     is a placeholder of stone, water and air.
        /// </summary>
        Density = 1
    }

    /// <summary>Blending method of one stage. Every method returns a normalised weight vector over the active biomes.</summary>
    public enum BlendMethod : byte{
        /// <summary>Hard nearest neighbour, one-hot weights.</summary>
        None = 0,

        /// <summary>Spatial kernel blend over a world-aligned lattice, width B.</summary>
        DistanceBlend = 1,

        /// <summary>Domain-warped classification position, amplitude A.</summary>
        DistortedBoundary = 2,

        /// <summary>Soft nearest neighbour in parameter space, width σ.</summary>
        ParameterSpaceWeighting = 3
    }

    /// <summary>The two blend stages. The height stage exists only in Biome-first.</summary>
    public enum BlendStage : byte{
        Material = 0,
        Height = 1
    }
}
