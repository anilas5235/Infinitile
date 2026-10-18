namespace Engine.Scripts.WorldGen.Settings{
    /// <summary>Which pipeline produces height and biomes (§4, §7, §8). Legacy is the pre-spec hybrid generator (INT-40).</summary>
    public enum GenArchitecture : byte{
        /// <summary>Height from nested splines over (C, E, PV); biomes classified in 6D including h (§7).</summary>
        TerrainFirst = 0,

        /// <summary>Biomes classified in 5D; height from blended per-biome parameters (§8).</summary>
        BiomeFirst = 1,

        /// <summary>Current hybrid generator (NoiseCalculator + BiomeCalculator). Removed after Phase 2.</summary>
        Legacy = 2
    }

    /// <summary>Blending method of one stage (§9). Every method returns a normalised weight vector over the active biomes.</summary>
    public enum BlendMethod : byte{
        /// <summary>Hard nearest neighbour, one-hot weights (§9.2).</summary>
        None = 0,

        /// <summary>Spatial kernel blend over a world-aligned lattice, width B (§9.3).</summary>
        DistanceBlend = 1,

        /// <summary>Domain-warped classification position, amplitude A (§9.4).</summary>
        DistortedBoundary = 2,

        /// <summary>Soft nearest neighbour in parameter space, width σ (§9.5).</summary>
        ParameterSpaceWeighting = 3
    }

    /// <summary>The two blend stages (§9.1). The height stage exists only in Biome-first.</summary>
    public enum BlendStage : byte{
        Material = 0,
        Height = 1
    }
}
