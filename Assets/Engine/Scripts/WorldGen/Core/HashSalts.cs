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
    }
}
