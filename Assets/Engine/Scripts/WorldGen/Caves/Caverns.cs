using Engine.Scripts.WorldGen.Core;
using Engine.Scripts.WorldGen.Noise;
using Engine.Scripts.WorldGen.Settings;
using Unity.Mathematics;

namespace Engine.Scripts.WorldGen.Caves{
    /// <summary>
    ///     Large halls: a low-frequency 3D noise carves where it exceeds a threshold that rises towards both edges of
    ///     the cavern band, so halls close off at the top and the bottom (the depth fade of the cave threshold in
    ///     Infinitile v0.3.0, ChunkGenerationCavesOres.CarveCaves, applied to both edges). A 2D mask limits halls to part
    ///     of the area, and inside a hall a vertically stretched second noise leaves pillars of stone standing.
    ///     <para>
    ///         The hall noise is not the plain field: two warp noises shift the position it is sampled at along x and
    ///         z, so a hall is twisted and leans into overhangs instead of being an ellipsoid, and a detail octave with
    ///         a short wavelength is added to it, folded at its zero crossings (<see cref="Ridge" />), so the walls have
    ///         bays, crevices and fins instead of smooth bumps. Both are settings and 0 turns each off.
    ///     </para>
    ///     <para>
    ///         Both noises are sampled on a world-aligned lattice every <see cref="LatticeStep" /> blocks and
    ///         interpolated trilinearly, so a voxel's value does not depend on the block that evaluates it.
    ///     </para>
    /// </summary>
    public static class Caverns{
        /// <summary>Spacing in blocks of the lattice the hall and pillar noises are sampled on; 1 samples every voxel.</summary>
        public const int LatticeStep = 4;

        /// <summary>
        ///     Mask values from the mask threshold up to this much above it raise the hall threshold smoothly, so halls
        ///     shrink and close before the edge of the mask instead of ending in a wall.
        /// </summary>
        private const float MaskFade = .1f;

        /// <summary>Raise of the threshold at the mask edge: the full noise range, so nothing is carved there.</summary>
        private const float MaskRise = 2f;

        /// <summary>
        ///     Rise of the hall threshold from the cavern mask value <paramref name="mask" /> of a column; above
        ///     <see cref="MaskRise" /> − 1 the column carves no hall.
        /// </summary>
        public static float MaskRaise(float mask, in CaveConfig c){
            return FadeRaise(mask, c.CavernMaskThreshold);
        }

        /// <summary>
        ///     Threshold rise of a mask value <paramref name="mask" /> that opens above <paramref name="threshold" />:
        ///     <see cref="MaskRise" /> at the threshold, falling smoothly to 0 over <see cref="MaskFade" /> above it.
        /// </summary>
        public static float FadeRaise(float mask, float threshold){
            return MaskRise * (1f - math.saturate((mask - threshold) / MaskFade));
        }

        /// <summary>The column's mask lets halls form at all.</summary>
        public static bool MaskOpen(float mask, in CaveConfig c){
            return mask > c.CavernMaskThreshold;
        }

        /// <summary>The column's mask lets halls form at their full size: <see cref="MaskRaise" /> is 0 there.</summary>
        public static bool MaskFull(float mask, in CaveConfig c){
            return mask >= c.CavernMaskThreshold + MaskFade;
        }

        /// <summary>Hall threshold at height <paramref name="y" /> in a column whose mask raises it by <paramref name="raise" />.</summary>
        public static float Threshold(int y, float raise, in CaveConfig c){
            return BandThreshold(y, c.CavernBandMin, c.CavernBandMax, c.CavernThreshold, c.CavernEdgeRise, c.CavernFloorRise) + raise;
        }

        /// <summary>
        ///     <paramref name="threshold" /> at height <paramref name="y" />, rising by <paramref name="rise" /> times the
        ///     square of the distance from the middle of the band <paramref name="min" />…<paramref name="max" />
        ///     (1 at its edges) in the upper half, and by <paramref name="floorRise" /> times it in the lower half: a
        ///     negative floor rise opens the halls widest at the floor.
        /// </summary>
        public static float BandThreshold(int y, int min, int max, float threshold, float rise, float floorRise){
            float mid = .5f * (min + max);
            float half = math.max(.5f * (max - min), .5f);
            float t = (y - mid) / half;
            return threshold + (t < 0f ? floorRise : rise) * t * t;
        }

        /// <summary>
        ///     Mean of 1 − 2|n| over the 3D simplex field (its mean |n| is 0.35, measured over 200 000 points), which
        ///     <see cref="Ridge" /> subtracts so that the folded detail is centred on 0 like the plain octave.
        /// </summary>
        public const float RidgeMean = .3f;

        /// <summary>
        ///     Detail value <paramref name="n" /> folded at its zero crossings: 1 − 2|n| − <see cref="RidgeMean" />, a
        ///     ridge of 0.7 where n crosses 0 (a crevice cut into the wall) down to −1.3 at its extremes (a fin of stone).
        /// </summary>
        public static float Ridge(float n){
            return 1f - 2f * math.abs(n) - RidgeMean;
        }

        /// <summary>
        ///     Hall noise at lattice point (x, y, z), a multiple of <see cref="LatticeStep" />: the cavern simplex sampled
        ///     at the position the warp noises shift along x and z by up to <see cref="CaveConfig.CavernWarpAmplitude" />
        ///     blocks, plus the detail octave at the same shifted position, scaled by
        ///     <see cref="CaveConfig.CavernDetailAmplitude" /> and folded by <see cref="Ridge" /> when ridged. The vertical
        ///     wavelengths of the warp and the detail follow the ratio of the cavern wavelengths.
        /// </summary>
        public static float HallNoise(int x, int y, int z, in WorldGenConfig cfg){
            ref readonly CaveConfig c = ref cfg.Caves;
            float2 w = c.CavernWavelength;
            float vertical = w.y / w.x;
            float3 p = new(x, y, z);
            if (c.CavernWarpAmplitude > 0f){
                float3 q = p / new float3(c.CavernWarpWavelength, c.CavernWarpWavelength * vertical, c.CavernWarpWavelength);
                p.x += c.CavernWarpAmplitude * SeededSimplex.Noise(q, cfg.Seed, HashSalts.CavernWarpX);
                p.z += c.CavernWarpAmplitude * SeededSimplex.Noise(q, cfg.Seed, HashSalts.CavernWarpZ);
            }

            float hall = SeededSimplex.Noise(p / new float3(w.x, w.y, w.x), cfg.Seed, HashSalts.CavernNoise);
            if (c.CavernDetailAmplitude > 0f){
                float d = c.CavernDetailWavelength;
                float detail = SeededSimplex.Noise(p / new float3(d, d * vertical, d), cfg.Seed, HashSalts.CavernDetail);
                hall += c.CavernDetailAmplitude * (c.CavernDetailRidged ? Ridge(detail) : detail);
            }

            return hall;
        }

        /// <summary>Pillar noise at lattice point (x, y, z), a multiple of <see cref="LatticeStep" />.</summary>
        public static float PillarNoise(int x, int y, int z, in WorldGenConfig cfg){
            float2 w = cfg.Caves.PillarWavelength;
            return SeededSimplex.Noise(new float3(x / w.x, y / w.y, z / w.x), cfg.Seed, HashSalts.PillarNoise);
        }

        /// <summary>
        ///     Trilinear interpolation of the corner values c[x][y][z] at fraction <paramref name="f" />: x first, then z,
        ///     then y. Every evaluation of a lattice cell uses this order, so all give the same bits.
        /// </summary>
        public static float Trilinear(float c000, float c100, float c001, float c101, float c010, float c110,
            float c011, float c111, float3 f){
            float bottom = math.lerp(math.lerp(c000, c100, f.x), math.lerp(c001, c101, f.x), f.z);
            float top = math.lerp(math.lerp(c010, c110, f.x), math.lerp(c011, c111, f.x), f.z);
            return math.lerp(bottom, top, f.y);
        }

        /// <summary>Lattice point at or below <paramref name="v" />.</summary>
        public static int LatticeFloor(int v){
            int q = v / LatticeStep;
            return (q * LatticeStep > v ? q - 1 : q) * LatticeStep;
        }

        /// <summary>Fraction of <paramref name="v" /> between its lattice point and the next.</summary>
        public static float LatticeFraction(int v){
            return (v - LatticeFloor(v)) * (1f / LatticeStep);
        }

        /// <summary>
        ///     The interpolated hall and pillar noise at voxel (x, y, z), evaluating the eight lattice corners of its
        ///     cell. The chunk pass reads the same corners from its lattice.
        /// </summary>
        public static float2 Noises(int x, int y, int z, in WorldGenConfig cfg){
            int x0 = LatticeFloor(x), y0 = LatticeFloor(y), z0 = LatticeFloor(z);
            int x1 = x0 + LatticeStep, y1 = y0 + LatticeStep, z1 = z0 + LatticeStep;
            float3 f = new(LatticeFraction(x), LatticeFraction(y), LatticeFraction(z));
            float hall = Trilinear(HallNoise(x0, y0, z0, cfg), HallNoise(x1, y0, z0, cfg), HallNoise(x0, y0, z1, cfg),
                HallNoise(x1, y0, z1, cfg), HallNoise(x0, y1, z0, cfg), HallNoise(x1, y1, z0, cfg),
                HallNoise(x0, y1, z1, cfg), HallNoise(x1, y1, z1, cfg), f);
            float pillar = Trilinear(PillarNoise(x0, y0, z0, cfg), PillarNoise(x1, y0, z0, cfg), PillarNoise(x0, y0, z1, cfg),
                PillarNoise(x1, y0, z1, cfg), PillarNoise(x0, y1, z0, cfg), PillarNoise(x1, y1, z0, cfg),
                PillarNoise(x0, y1, z1, cfg), PillarNoise(x1, y1, z1, cfg), f);
            return new float2(hall, pillar);
        }

        /// <summary>A hall carves voxel y of a column with the interpolated noises <paramref name="noises" /> (hall, pillar).</summary>
        public static bool Carves(int y, float raise, float2 noises, in CaveConfig c){
            return noises.x > Threshold(y, raise, c) && noises.y <= c.PillarThreshold;
        }

        /// <summary>
        ///     A hall carves voxel (x, y, z) in a column whose halls may carve up to <paramref name="top" />
        ///     (<see cref="CaveLimits.Cavern" />).
        /// </summary>
        public static bool Carves(int x, int y, int z, int top, in WorldGenConfig cfg){
            ref readonly CaveConfig c = ref cfg.Caves;
            if (y <= CaveColumns.Floor || y < c.CavernBandMin || y > c.CavernBandMax || y > top) return false;
            float mask = CaveMasks.Cavern(x, z, cfg);
            if (!MaskOpen(mask, c)) return false;
            return Carves(y, MaskRaise(mask, c), Noises(x, y, z, cfg), c);
        }
    }
}
