using System.Runtime.InteropServices;
using Engine.Scripts.WorldGen.Blending;
using Engine.Scripts.WorldGen.Noise;
using Unity.Mathematics;

namespace Engine.Scripts.WorldGen.Core{
    /// <summary>
    ///     Everything the generator decides for one column: fields, height, weights of both blend stages,
    ///     the surface source biome and the flora decision. A pure function of (x, z, seed, config).
    /// </summary>
    public struct ColumnSample{
        /// <summary>T, F, C, E, W, PV at the column.</summary>
        public WorldFields Fields;

        /// <summary>H after the clamp to [Hmin, Hmax], before flooring.</summary>
        public float Height;

        /// <summary>h_int = floor(H): the surface block Y.</summary>
        public int ColumnHeight;

        /// <summary>h = saturate((H − Hmin)/(Hmax − Hmin)); the sixth classification dimension in Terrain-first.</summary>
        public float NormalizedHeight;

        /// <summary>Terrain-first spline values (Offset, Amp, J) at the column; zero in Biome-first.</summary>
        public float3 TerrainParams;

        /// <summary>True when the unclamped H lay outside [Hmin, Hmax].</summary>
        [MarshalAs(UnmanagedType.U1)] public bool Clamped; // blittable for the Burst direct call

        /// <summary>Hard classification argmin_b d_b; -1 when no biome is active.</summary>
        public int HardBiome;

        /// <summary>argmax of <see cref="MaterialWeights" />; equals <see cref="HardBiome" /> for one-hot weights.</summary>
        public int DominantMaterialBiome;

        /// <summary>Material-stage weights.</summary>
        public BlendWeights MaterialWeights;

        /// <summary>Height-stage weights; Biome-first only, one-hot otherwise.</summary>
        public BlendWeights HeightWeights;

        /// <summary>Biome whose top/under blocks the column uses: the dithered surface pick when a material method is active, otherwise the hard class.</summary>
        public int SurfaceSource;

        /// <summary>
        ///     Flora species id at the column, 0 = none: a single-block plant, or a multi-block species at its
        ///     anchor column. Ids are 1-based indices into <c>WorldGenSettings.FloraSpeciesNames()</c>.
        /// </summary>
        public ushort FloraSpecies;

        /// <summary>Biome whose flora rule produced <see cref="FloraSpecies" />; -1 when none.</summary>
        public int FloraSource;
    }
}
