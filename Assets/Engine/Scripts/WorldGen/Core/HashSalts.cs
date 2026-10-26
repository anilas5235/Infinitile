namespace Engine.Scripts.WorldGen.Core{
    /// <summary>
    ///     Every random decision in world generation has its own salt, all declared here so a
    ///     collision between two decisions is visible at a glance. Values are arbitrary distinct 32-bit constants;
    ///     changing one changes every world, so treat them as part of the world format.
    /// </summary>
    public static class HashSalts{
        // --- Noise gradients, one per noise instance -------------------------------------------------------------
        public const uint TemperatureNoise = 0x1F3A9C01u;
        public const uint HumidityNoise = 0x2B7D5E02u;
        public const uint ContinentalNoise = 0x3C8E6F03u;
        public const uint ContinentalWarpX = 0x4D9F7A04u;
        public const uint ContinentalWarpY = 0x5EA08B05u;
        public const uint ErosionNoise = 0x6FB19C06u;
        public const uint WeirdnessNoise = 0x70C2AD07u;
        public const uint PeakNoise = 0x81D3BE08u;

        /// <summary>Noise on the effective temperature of the snow and ice overrides.</summary>
        public const uint FreezeNoise = 0x3C8E6913u;

        /// <summary>Rise and fall of the strata bands.</summary>
        public const uint StrataNoise = 0x4D9F7A14u;

        /// <summary>Base salt of the flora density noises; noise i uses <see cref="FloraDensity" />.</summary>
        private const uint FloraDensityBase = 0x7FA3C519u;

        /// <summary>Salt of flora density noise <paramref name="i" /> (0…3).</summary>
        public static uint FloraDensity(int i){
            return FloraDensityBase + (uint)i;
        }

        /// <summary>Beach height noise of the shore band.</summary>
        public const uint BeachNoise = 0x8E1B4D2Du;

        /// <summary>Shared blend warp field, one salt per axis.</summary>
        public const uint BlendWarpX = 0x92E4CF09u;

        public const uint BlendWarpY = 0xA3F5D00Au;

        /// <summary>Base salt of detail octave 0; octave i uses <see cref="DetailOctave" />.</summary>
        private const uint DetailOctaveBase = 0xB406E10Bu;

        /// <summary>Salt of detail octave <paramref name="i" />. Distinct for every i.</summary>
        public static uint DetailOctave(int i){
            return Hash.Mix(DetailOctaveBase + (uint)i * 0x9E3779B9u);
        }

        /// <summary>
        ///     Salt of octave <paramref name="octave" /> inside an fBm instance with gradient salt
        ///     <paramref name="instanceSalt" />, so the octaves of one profile do not share lattice gradients.
        /// </summary>
        public static uint FbmOctave(uint instanceSalt, int octave){
            return Hash.Mix(instanceSalt ^ ((uint)octave + 1u) * 0x85EBCA77u);
        }

        // --- Per-instance sample offsets ---------------------------------------------------------------------------
        /// <summary>Salt for the x component of an fBm instance's sample offset; combined with the instance salt.</summary>
        public const uint OffsetX = 0xC517F20Cu;

        public const uint OffsetZ = 0xD628030Du;

        // --- Per-column random decisions --------------------------------------------------------------------------
        /// <summary>Surface source pick.</summary>
        public const uint SurfacePick = 0xE739140Eu;

        /// <summary>Which biome's flora rules a column uses.</summary>
        public const uint FloraBiome = 0xF84A250Fu;

        /// <summary>Which species the column's flora roll selects.</summary>
        public const uint FloraSpecies = 0x095B3610u;

        /// <summary>Tree placement priority against neighbours.</summary>
        public const uint TreePriority = 0x1A6C4711u;

        /// <summary>Height of a stacked voxel species such as a cactus 1–3 high.</summary>
        public const uint FloraStackHeight = 0x2B7D5812u;

        /// <summary>Weighted variant pick of a tree species at its anchor column.</summary>
        public const uint TreeVariant = 0x3C8E6915u;

        /// <summary>Base salt of the shape rolls of a tree: roll i (a range or a direction) uses <see cref="TreeShape" />.</summary>
        private const uint TreeShapeBase = 0x4D9F7A16u;

        /// <summary>Salt of shape roll <paramref name="i" /> of a tree at its anchor column. Distinct for every i.</summary>
        public static uint TreeShape(int i){
            return TreeShapeBase + (uint)i;
        }

        // --- Per-voxel random decisions ---------------------------------------------------------------------------
        /// <summary>Hole, bump or hang of a crown voxel at its world position.</summary>
        public const uint Crown = 0x5EA08B17u;

        /// <summary>Main or alternative leaf voxel of a crown voxel at its world position.</summary>
        public const uint CrownLeafMix = 0x6FB19C18u;

        // --- Caves -----------------------------------------------------------------------------------------------
        /// <summary>Bedrock or not at y 1.</summary>
        public const uint BedrockLayer1 = 0xA1C3E519u;

        /// <summary>Bedrock or not at y 2.</summary>
        public const uint BedrockLayer2 = 0xB2D4F61Au;

        /// <summary>2D mask of the halls.</summary>
        public const uint CavernMask = 0xC3E5071Bu;

        /// <summary>2D mask of the surface openings.</summary>
        public const uint EntranceMask = 0xD4F6182Cu;

        /// <summary>2D mask of the lava floor.</summary>
        public const uint LavaMask = 0xE507293Du;

        /// <summary>3D noise of the halls.</summary>
        public const uint CavernNoise = 0xF6183A4Eu;

        /// <summary>3D noise of the stone pillars inside a hall.</summary>
        public const uint PillarNoise = 0x07294B5Fu;

        /// <summary>Number of worms of a start cell (one roll per possible worm).</summary>
        public const uint WormCount = 0x183A5C60u;

        /// <summary>Hash of one worm of a start cell, from which every roll of its path follows.</summary>
        public const uint Worm = 0x294B6D71u;

        /// <summary>Start position, direction, length and branch rolls of a worm.</summary>
        public const uint WormStart = 0x3A5C7E82u;

        /// <summary>Yaw and pitch jitter of one worm step.</summary>
        public const uint WormTurn = 0x4B6D8F93u;

        /// <summary>Radius at one radius knot of a worm.</summary>
        public const uint WormRadius = 0x5C7EA0A4u;

        /// <summary>3D noise of the lava pockets.</summary>
        public const uint LavaPocket = 0x6D8FB1B5u;

        /// <summary>Hash of one 16³ ore cell, from which its vein counts and veins follow.</summary>
        public const uint OreCell = 0x7EA0C2C6u;

        /// <summary>Number of veins of one ore rule in a cell.</summary>
        public const uint OreCount = 0x8FB1D3D7u;

        /// <summary>Hash, seed and size of one ore vein.</summary>
        public const uint OreVein = 0x90C2E4E8u;

        /// <summary>Direction of one step of an ore vein's walk.</summary>
        public const uint OreStep = 0xA1D3F5F9u;
    }
}
