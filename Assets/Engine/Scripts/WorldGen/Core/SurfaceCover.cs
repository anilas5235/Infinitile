using System.Runtime.CompilerServices;
using Engine.Scripts.VoxelConfig.Data.Generation;
using Engine.Scripts.WorldGen.Blending;
using Engine.Scripts.WorldGen.Noise;
using Unity.Collections;
using Engine.Scripts.WorldGen.Settings;
using Unity.Mathematics;

namespace Engine.Scripts.WorldGen.Core{
    /// <summary>
    ///     Snow, ice, strata and beaches of a column, pure functions of the world position. The effective temperature falls with
    ///     the height above the water and carries a little noise, so snow lines and ice edges are ragged. Dry columns and
    ///     the water surface share the freeze temperature, so snow on a shore meets the ice.
    /// </summary>
    public static class SurfaceCover{
        /// <summary>Spacing in blocks of the world-aligned probe lattice on which beaches look for water.</summary>
        public const int WaterProbeStride = 4;

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

        /// <summary>
        ///     The water surface (y = WaterLevel − 1) above the column of surface biome <paramref name="def" /> freezes to
        ///     ice. Biomes without <c>freezesWater</c> never do.
        /// </summary>
        public static bool WaterFreezes(int x, int z, float temperature, in Biome.BiomeDef def, in WorldGenConfig cfg){
            if (!cfg.EnableClimateOverrides || def.freezesWater == 0) return false;
            return EffectiveTemperature(x, z, temperature, cfg.WaterLevel - 1, cfg) < cfg.FreezeTemperature;
        }

        /// <summary>
        ///     <see cref="WaterFreezes(int,int,float,in Biome.BiomeDef,in WorldGenConfig)" /> with the column's
        ///     <see cref="FreezeNoise" />.
        /// </summary>
        public static bool WaterFreezes(float temperature, float freezeNoise, in Biome.BiomeDef def,
            in WorldGenConfig cfg){
            if (!cfg.EnableClimateOverrides || def.freezesWater == 0) return false;
            return EffectiveTemperature(temperature, cfg.WaterLevel - 1, freezeNoise, cfg) < cfg.FreezeTemperature;
        }

        private static bool MayBeSnowCovered(int height, in Biome.BiomeDef def, in WorldGenConfig cfg){
            return cfg.EnableClimateOverrides && def.snowTopBlock != 0 && height + 1 >= cfg.WaterLevel;
        }

        /// <summary>
        ///     The dry column (top block at <paramref name="height" />) is a beach of its surface biome
        ///     <paramref name="def" />: its top block lies 0 … h_b − 1 blocks above the water surface (y = WaterLevel − 1)
        ///     for the column's <see cref="BeachHeight" /> h_b, and water lies near it (<see cref="NearWater" />). The
        ///     height band is tested first, so most columns skip the noise, and the water probes come last.
        /// </summary>
        public static bool IsBeach(int x, int z, int height, in Biome.BiomeDef def, in WorldGenConfig cfg){
            return IsBeach(x, z, height, def, cfg, default);
        }

        /// <summary>
        ///     <see cref="IsBeach(int,int,int,in Biome.BiomeDef,in WorldGenConfig)" /> with the water probes remembered in
        ///     <paramref name="cache" />; the same result.
        /// </summary>
        public static bool IsBeach(int x, int z, int height, in Biome.BiomeDef def, in WorldGenConfig cfg, in BlendCache cache){
            int above = height - (cfg.WaterLevel - 1);
            if (above < 0 || above >= def.beachHeight) return false;
            if (above >= BeachHeight(x, z, def.beachHeight, cfg)) return false;
            return cfg.BeachWaterDistance <= 0 || NearWater(x, z, cfg, cache);
        }

        /// <summary>
        ///     Some probe within <c>BeachWaterDistance</c> blocks (Euclidean) of the column lies under water: probes are
        ///     the columns at multiples of <see cref="WaterProbeStride" />, tested by their terrain height alone, nearest
        ///     rings first. Water narrower than the stride may go unseen.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool NearWater(int x, int z, in WorldGenConfig cfg, in BlendCache cache){
            int r = cfg.BeachWaterDistance;
            int2 p = new(x, z);
            int2 center = ProbeCell(p);
            // The column lies in cell center … center + 1, so probes within r lie at most r / stride + 1 cells away.
            int rings = r / WaterProbeStride + 1;
            for (int ring = 0; ring <= rings; ring++)
            for (int dx = -ring; dx <= ring; dx++){
                int step = math.abs(dx) == ring ? 1 : 2 * ring;
                for (int dz = -ring; dz <= ring; dz += step){
                    int2 cell = center + new int2(dx, dz);
                    int2 d = cell * WaterProbeStride - p;
                    if (d.x * d.x + d.y * d.y > r * r) continue;
                    if (ProbeUnderWater(cell, cfg, cache)) return true;
                }
            }

            return false;
        }

        /// <summary>Probe cell of column <paramref name="column" />: floor(column / stride) per axis.</summary>
        public static int2 ProbeCell(int2 column){
            int2 q = column / WaterProbeStride;
            return q - math.select(int2.zero, new int2(1), (column % WaterProbeStride != 0) & (column < 0));
        }

        private static bool ProbeUnderWater(int2 cell, in WorldGenConfig cfg, in BlendCache cache){
            int2 local = cell - cache.WaterProbeOrigin;
            bool remembered = cache.WaterProbes.IsCreated && math.all(local >= 0) && math.all(local < cache.WaterProbeSize);
            int index = local.y * cache.WaterProbeSize.x + local.x;
            if (remembered){
                byte known = cache.WaterProbes[index];
                if (known != 0) return known == 2;
            }

            int2 q = cell * WaterProbeStride;
            WorldSampler.SampleHeight(q.x, q.y, cfg, cache, out float height);
            // Water fills y < WaterLevel: the voxel above the probe's top block is water.
            bool wet = (int)math.floor(height) + 1 < cfg.WaterLevel;
            if (remembered){
                NativeArray<byte> probes = cache.WaterProbes;
                probes[index] = wet ? (byte)2 : (byte)1;
            }

            return wet;
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
