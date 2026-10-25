using Engine.Scripts.VoxelConfig.Data.Generation;
using Engine.Scripts.WorldGen.Noise;
using Engine.Scripts.WorldGen.Settings;
using Unity.Mathematics;

namespace Engine.Scripts.WorldGen.Core{
    /// <summary>
    ///     Snow, ice, strata and beaches of a column, pure functions of the world position. The effective temperature falls with
    ///     the height above the water and carries a little noise, so snow lines and ice edges are ragged. Dry columns and
    ///     the water surface share the freeze temperature, so snow on a shore meets the ice.
    /// </summary>
    public static class SurfaceCover{
        /// <summary>
        ///     Temperature <paramref name="temperature" /> lowered by <c>SnowLapse</c> times the normalised height of the
        ///     top face above the water surface, plus the freeze noise.
        /// </summary>
        public static float EffectiveTemperature(int x, int z, float temperature, int height, in WorldGenConfig cfg){
            return EffectiveTemperature(temperature, height, FreezeNoise(x, z, cfg), cfg);
        }

        /// <summary>
        ///     <see cref="EffectiveTemperature(int,int,float,int,in WorldGenConfig)" /> with the column's
        ///     <see cref="FreezeNoise" /> already evaluated, so snow and ice of one column share it.
        /// </summary>
        public static float EffectiveTemperature(float temperature, int height, float freezeNoise, in WorldGenConfig cfg){
            float above = math.saturate((height + 1 - cfg.WaterLevel) / math.max(1f, cfg.HeightMax - cfg.WaterLevel));
            return temperature - cfg.SnowLapse * above + cfg.FreezeNoiseAmplitude * freezeNoise;
        }

        /// <summary>The unscaled freeze noise of column (<paramref name="x" />, <paramref name="z" />), in −1…1.</summary>
        public static float FreezeNoise(int x, int z, in WorldGenConfig cfg){
            return SeededSimplex.Noise(new float2(x, z) / cfg.FreezeNoiseWavelength, cfg.Seed, HashSalts.FreezeNoise);
        }

        /// <summary>
        ///     The dry column (top block at <paramref name="height" />) takes the snow block of its surface biome
        ///     <paramref name="def" />. Biomes without a snow block never do.
        /// </summary>
        public static bool IsSnowCovered(int x, int z, float temperature, int height, in Biome.BiomeDef def,
            in WorldGenConfig cfg){
            if (!MayBeSnowCovered(height, def, cfg)) return false;
            return EffectiveTemperature(x, z, temperature, height, cfg) < cfg.FreezeTemperature;
        }

        /// <summary>
        ///     <see cref="IsSnowCovered(int,int,float,int,in Biome.BiomeDef,in WorldGenConfig)" /> with the column's
        ///     <see cref="FreezeNoise" />.
        /// </summary>
        public static bool IsSnowCovered(float temperature, int height, float freezeNoise, in Biome.BiomeDef def,
            in WorldGenConfig cfg){
            if (!MayBeSnowCovered(height, def, cfg)) return false;
            return EffectiveTemperature(temperature, height, freezeNoise, cfg) < cfg.FreezeTemperature;
        }

        /// <summary>The water surface (y = WaterLevel − 1) above the column freezes to ice.</summary>
        public static bool WaterFreezes(int x, int z, float temperature, in WorldGenConfig cfg){
            if (!cfg.EnableClimateOverrides) return false;
            return EffectiveTemperature(x, z, temperature, cfg.WaterLevel - 1, cfg) < cfg.FreezeTemperature;
        }

        /// <summary>
        ///     <see cref="WaterFreezes(int,int,float,in WorldGenConfig)" /> with the column's <see cref="FreezeNoise" />.
        /// </summary>
        public static bool WaterFreezes(float temperature, float freezeNoise, in WorldGenConfig cfg){
            if (!cfg.EnableClimateOverrides) return false;
            return EffectiveTemperature(temperature, cfg.WaterLevel - 1, freezeNoise, cfg) < cfg.FreezeTemperature;
        }

        private static bool MayBeSnowCovered(int height, in Biome.BiomeDef def, in WorldGenConfig cfg){
            return cfg.EnableClimateOverrides && def.snowTopBlock != 0 && height + 1 >= cfg.WaterLevel;
        }

        /// <summary>
        ///     The dry column (top block at <paramref name="height" />) is a beach of its surface biome
        ///     <paramref name="def" />: its top block lies 0 … h_b − 1 blocks above the water surface (y = WaterLevel − 1)
        ///     for the column's <see cref="BeachHeight" /> h_b. The height band is tested first, so most columns skip
        ///     the noise.
        /// </summary>
        public static bool IsBeach(int x, int z, int height, in Biome.BiomeDef def, in WorldGenConfig cfg){
            int above = height - (cfg.WaterLevel - 1);
            if (above < 0 || above >= def.beachHeight) return false;
            return above < BeachHeight(x, z, def.beachHeight, cfg);
        }

        /// <summary>
        ///     Beach height h_b = round(h_b,max · s(B)) of the column: s rises smoothly from 0 at the beach threshold to 1
        ///     a ramp above it, for the beach noise B mapped to 0…1. Shores where B stays below the threshold have no
        ///     beach.
        /// </summary>
        public static int BeachHeight(int x, int z, int maxHeight, in WorldGenConfig cfg){
            float b = .5f + .5f * SeededSimplex.Noise(new float2(x, z) / cfg.BeachWavelength, cfg.Seed, HashSalts.BeachNoise);
            float s = math.smoothstep(cfg.BeachThreshold, cfg.BeachThreshold + cfg.BeachRamp, b);
            return (int)math.round(maxHeight * s);
        }

        /// <summary>Vertical shift of the strata bands at the column, in whole blocks.</summary>
        public static int StrataShift(int x, int z, in WorldGenConfig cfg){
            float noise = SeededSimplex.Noise(new float2(x, z) / cfg.StrataWarpWavelength, cfg.Seed, HashSalts.StrataNoise);
            return (int)math.floor(cfg.StrataWarpAmplitude * noise);
        }

        /// <summary>
        ///     Voxel <paramref name="y" /> lies in a strata band: the first <paramref name="thickness" /> blocks of every
        ///     <paramref name="period" />, shifted by <paramref name="shift" />. A period of 0 turns strata off.
        /// </summary>
        public static bool InStrataBand(int y, int shift, int period, int thickness){
            if (period <= 0 || thickness <= 0) return false;
            int phase = (y + shift) % period;
            if (phase < 0) phase += period;
            return phase < thickness;
        }
    }
}
