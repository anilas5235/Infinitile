using Engine.Scripts.VoxelConfig.Data.Generation;
using Engine.Scripts.WorldGen.Noise;
using Engine.Scripts.WorldGen.Settings;
using Unity.Mathematics;

namespace Engine.Scripts.WorldGen.Core{
    /// <summary>
    ///     Snow, ice and strata of a column, pure functions of the world position. The effective temperature falls with
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
