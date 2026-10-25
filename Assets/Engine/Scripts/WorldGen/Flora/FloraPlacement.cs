using System.Runtime.CompilerServices;
using Engine.Scripts.VoxelConfig.Data.Generation;
using Engine.Scripts.WorldGen.Blending;
using Engine.Scripts.WorldGen.Core;
using Engine.Scripts.WorldGen.Noise;
using Engine.Scripts.WorldGen.Settings;
using Unity.Mathematics;

namespace Engine.Scripts.WorldGen.Flora{
    /// <summary>
    ///     The flora decision of one column, a pure function of (x, z, seed, config) on top of the column's core
    ///     sample. <see cref="WorldSampler.SampleColumn(int,int,in WorldGenConfig,in BlendCache,out ColumnSample)" />
    ///     calls <see cref="Decide" />; the chunk job runs the same <see cref="RollRule" /> and <see cref="Outranks" />
    ///     over a padded grid instead of re-sampling neighbours, and gets the same answer.
    ///     <list type="number">
    ///         <item>Flora source biome: dithered pick from w_mat with <see cref="HashSalts.FloraBiome" />.</item>
    ///         <item>
    ///             Species: the rule whose cumulative interval holds Hash01(<see cref="HashSalts.FloraSpecies" />); none →
    ///             no plant. A rule with a density noise has the probability p · lerp(1, m, k) at the column, with the
    ///             noise factor m (<see cref="DensityFactor" />); when these sum above 1 they are scaled to a sum of 1.
    ///         </item>
    ///         <item>
    ///             Conditions: same surface (optional), height above water, not clamped at Hmax, solid ground, beach
    ///             columns for beach-ground rules only, and no snow cover for single-block plants.
    ///         </item>
    ///         <item>
    ///             Multi-block species only: every candidate has a spacing radius (a tree's from its variant, rolled
    ///             first, otherwise r_tree). A column is an anchor if no candidate at distance ≤ max(r_a, r_b) has a
    ///             higher <see cref="HashSalts.TreePriority" /> hash; otherwise it carries no plant.
    ///         </item>
    ///     </list>
    /// </summary>
    public static class FloraPlacement{
        /// <summary>
        ///     Steps 1–3: the index of the flora rule (into <c>cfg.FloraRules</c>) the column passes, or -1.
        ///     <paramref name="source" /> is the flora source biome of a passing rule, -1 otherwise.
        /// </summary>
        public static int RollRule(int x, int z, in WorldGenConfig cfg, in BlendCache cache, in ColumnSample s, out int source){
            source = -1;
            if (!cfg.HasFlora) return -1;

            // Own roll, independent of the surface pick. One-hot weights pick their biome.
            int biome = cfg.MaterialMethod == BlendMethod.None
                ? s.HardBiome
                : SurfacePick.Pick(x, z, cfg.Seed, HashSalts.FloraBiome, s.MaterialWeights, cfg.BiomeCount);
            if (biome < 0) return -1;
            Biome.BiomeDef def = cfg.Biomes[biome];
            if (def.floraRuleCount == 0) return -1;

            float u = Hash.Hash01(x, z, cfg.Seed, HashSalts.FloraSpecies);
            int rule = -1;
            if (def.floraDensity != 0) rule = DenseRule(x, z, u, def, cfg);
            else{
                int end = def.floraRuleStart + def.floraRuleCount;
                for (int r = def.floraRuleStart; r < end; r++){
                    FloraRuleDef candidate = cfg.FloraRules[r];
                    if (u >= candidate.CumulativeStart && u < candidate.CumulativeEnd){
                        rule = r;
                        break;
                    }
                }
            }

            if (rule < 0) return -1;
            FloraRuleDef d = cfg.FloraRules[rule];
            // Optionally only on ground of the same biome.
            if (d.RequiresSurfaceFromSameBiome != 0 && s.SurfaceSource != biome) return -1;
            if (!GroundAllows(s, d.MinHeightAboveWater, cfg)) return -1;
            // Beach columns carry only beach-ground rules, and those grow nowhere else.
            if ((cfg.Biomes[s.SurfaceSource].beachHeight | d.BeachGround) != 0 && !BeachAllows(x, z, d.BeachGround, s, cfg, cache)) return -1;
            // Snow keeps trees and cacti but carries no ground plants.
            if (d.MultiBlock == 0 &&
                SurfaceCover.IsSnowCovered(x, z, s.Fields.T, s.ColumnHeight, cfg.Biomes[s.SurfaceSource], cfg)) return -1;

            source = biome;
            return rule;
        }

        /// <summary>
        ///     The rule of a biome with density noise whose interval holds <paramref name="u" />, or -1: the intervals
        ///     follow from the column's probabilities p' = p · lerp(1, m, k), scaled to a sum of 1 when they exceed it.
        ///     Each density noise is evaluated at most once. A separate method, so the even roll stays small.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int DenseRule(int x, int z, float u, in Biome.BiomeDef def, in WorldGenConfig cfg){
            int start = def.floraRuleStart, end = def.floraRuleStart + def.floraRuleCount;
            float4 factor = 1f;
            int evaluated = 0; // bit i: noise i is in factor
            float total = 0f;
            for (int r = start; r < end; r++){
                FloraRuleDef d = cfg.FloraRules[r];
                if (d.Influence > 0f && (evaluated & (1 << d.DensityNoise)) == 0){
                    factor[d.DensityNoise] = DensityFactor(x, z, d.DensityNoise, cfg);
                    evaluated |= 1 << d.DensityNoise;
                }

                total += ColumnProbability(d, factor);
            }

            float scale = total > 1f ? 1f / total : 1f;
            float cumulative = 0f;
            for (int r = start; r < end; r++){
                cumulative += ColumnProbability(cfg.FloraRules[r], factor);
                if (u < cumulative * scale) return r;
            }

            return -1;
        }

        /// <summary>
        ///     A rule with <paramref name="beachGround" /> passes on beach columns (<see cref="SurfaceCover.IsBeach" />)
        ///     whose beach block carries plants, other rules only off the beach. A separate method, so the roll stays small.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool BeachAllows(int x, int z, byte beachGround, in ColumnSample s, in WorldGenConfig cfg,
            in BlendCache cache){
            Biome.BiomeDef def = cfg.Biomes[s.SurfaceSource];
            bool beach = SurfaceCover.IsBeach(x, z, s.ColumnHeight, def, cfg, cache);
            if (beachGround == 0) return !beach;
            return beach && def.beachFloraGround != 0;
        }

        private static float ColumnProbability(in FloraRuleDef d, float4 factor){
            return d.Influence > 0f ? d.Probability * math.lerp(1f, factor[d.DensityNoise], d.Influence) : d.Probability;
        }

        /// <summary>
        ///     Factor m of density noise <paramref name="noise" /> at the column: clamp(1 + c · D, 0, 2) for the simplex
        ///     noise D in −1…1 and the noise's contrast c. Symmetric around 1, so its mean is 1 and the long-run density
        ///     of a rule stays its probability.
        /// </summary>
        public static float DensityFactor(int x, int z, int noise, in WorldGenConfig cfg){
            return DensityFactor(x, z, cfg.Seed, noise, cfg.FloraDensityWavelength[noise], cfg.FloraDensityContrast[noise]);
        }

        /// <summary><see cref="DensityFactor(int,int,int,in WorldGenConfig)" /> from the noise's settings alone.</summary>
        public static float DensityFactor(int x, int z, uint seed, int noise, float wavelength, float contrast){
            float d = SeededSimplex.Noise(new float2(x, z) / wavelength, seed, HashSalts.FloraDensity(noise));
            return math.clamp(1f + contrast * d, 0f, 2f);
        }

        /// <summary>
        ///     The column has solid ground (the surface source's top block), is not clamped at Hmax, and its
        ///     top face lies at least <paramref name="minHeightAboveWater" /> blocks above the water surface, never below.
        /// </summary>
        public static bool GroundAllows(in ColumnSample s, int minHeightAboveWater, in WorldGenConfig cfg){
            if (s.SurfaceSource < 0 || cfg.Biomes[s.SurfaceSource].floraGround == 0) return false;
            if (s.Clamped && s.Height >= cfg.HeightMax) return false;
            // Water fills y < WaterLevel, so the top face of the block at h_int is h_int + 1 - WaterLevel above it.
            int aboveWater = s.ColumnHeight + 1 - cfg.WaterLevel;
            return aboveWater >= math.max(minHeightAboveWater, 0);
        }

        /// <summary>True when the rule <paramref name="rule" /> exists and is a multi-block species.</summary>
        public static bool IsMultiBlock(int rule, in WorldGenConfig cfg){
            return rule >= 0 && cfg.FloraRules[rule].MultiBlock != 0;
        }

        /// <summary>
        ///     Necessary condition for a multi-block candidate that needs no sample: the species roll lies in a
        ///     multi-block interval of some biome (pre-filter).
        /// </summary>
        public static bool MayBeMultiBlock(int x, int z, in WorldGenConfig cfg){
            if (cfg.FloraMultiBlockIntervals.Length == 0) return false;
            float u = Hash.Hash01(x, z, cfg.Seed, HashSalts.FloraSpecies);
            for (int i = 0; i < cfg.FloraMultiBlockIntervals.Length; i++){
                float2 interval = cfg.FloraMultiBlockIntervals[i];
                if (u >= interval.x && u < interval.y) return true;
            }

            return false;
        }

        /// <summary>Anchor priority of a column.</summary>
        public static uint Priority(int x, int z, in WorldGenConfig cfg){
            return Hash.Hash32(x, z, cfg.Seed, HashSalts.TreePriority);
        }

        /// <summary>
        ///     Strict total order of candidates: higher priority wins; equal hashes go to the lower (x, z). So of two
        ///     conflicting candidates exactly one outranks the other and at most one becomes an anchor.
        /// </summary>
        public static bool Outranks(int2 a, uint priorityA, int2 b, uint priorityB){
            if (priorityA != priorityB) return priorityA > priorityB;
            return a.x < b.x || a.x == b.x && a.y < b.y;
        }

        /// <summary>True for the offsets of the r_tree disc around a column (dx² + dz² ≤ r², the column excluded).</summary>
        public static bool InSpacingDisc(int dx, int dz, int radius){
            return (dx != 0 || dz != 0) && dx * dx + dz * dz <= radius * radius;
        }

        /// <summary>Two candidates with spacing radii ra and rb at squared distance d² conflict when d ≤ max(ra, rb).</summary>
        public static bool Conflict(int distanceSq, int radiusA, int radiusB){
            int r = math.max(radiusA, radiusB);
            return distanceSq <= r * r;
        }

        /// <summary>The tree variant (index into <c>cfg.TreeVariants</c>) of a candidate of rule <paramref name="rule" />, -1 for other species.</summary>
        public static int Variant(int x, int z, int rule, in WorldGenConfig cfg){
            int tree = cfg.FloraRules[rule].Tree;
            return tree < 0 ? -1 : TreeShape.PickVariant(x, z, cfg, tree);
        }

        /// <summary>Spacing radius of a multi-block candidate: its tree variant's, or r_tree for structures and stacks.</summary>
        public static int SpacingRadius(int x, int z, int rule, in WorldGenConfig cfg){
            int variant = Variant(x, z, rule, cfg);
            return variant < 0 ? cfg.TreeSpacingRadius : cfg.TreeVariants[variant].SpacingRadius;
        }

        /// <summary>
        ///     The flora decision of the column with core sample <paramref name="s" />: species and source at
        ///     an anchor or a single-block plant, (0, -1) otherwise. Neighbours are sampled through
        ///     <paramref name="cache" /> only when they outrank the column and pass the pre-filter.
        /// </summary>
        public static void Decide(int x, int z, in WorldGenConfig cfg, in BlendCache cache, in ColumnSample s,
            out ushort species, out int source){
            species = 0;
            source = -1;
            int rule = RollRule(x, z, cfg, cache, s, out int biome);
            if (rule < 0) return;
            FloraRuleDef d = cfg.FloraRules[rule];
            if (d.MultiBlock != 0){
                if (!IsAnchor(x, z, SpacingRadius(x, z, rule, cfg), cfg, cache)) return;
                if (d.Tree >= 0 && !TreeFits(x, z, rule, cfg, cache)) return;
            }

            species = d.Species;
            source = biome;
        }

        /// <summary>False for an anchor whose lying trunk fits nowhere (<see cref="LyingFit" /> 0); other trees always fit.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool TreeFits(int x, int z, int rule, in WorldGenConfig cfg, in BlendCache cache){
            int variant = Variant(x, z, rule, cfg);
            if (cfg.TreeVariants[variant].Lying == 0) return true;
            TreeRoll roll = TreeShape.Roll(x, z, cfg.Seed, variant, cfg.TreeVariants[variant]);
            return LyingFit(x, z, roll, cfg, cache) != 0;
        }

        /// <summary>
        ///     Fit of the lying trunk <paramref name="roll" /> anchored at (x, z): 1 when every column under its rolled
        ///     length has the anchor's terrain height, 2 when the first two do (it is built 2 long,
        ///     <see cref="TreeRoll.Narrow" />), 0 otherwise (no log). The heights are the sampler's, so every chunk
        ///     and the sampler decide alike.
        /// </summary>
        public static int LyingFit(int x, int z, in TreeRoll roll, in WorldGenConfig cfg, in BlendCache cache){
            WorldSampler.SampleCore(x, z, cfg, cache, out ColumnSample anchor);
            int run = 1;
            for (; run < roll.TrunkHeight; run++){
                int2 c = new int2(x, z) + roll.LeanDirection * run;
                WorldSampler.SampleCore(c.x, c.y, cfg, cache, out ColumnSample s);
                if (s.ColumnHeight != anchor.ColumnHeight) break;
            }

            return run >= roll.TrunkHeight ? 1 : run >= 2 ? 2 : 0;
        }

        /// <summary>
        ///     Anchor test for a column that is a multi-block candidate itself with spacing radius
        ///     <paramref name="radius" />: no outranking multi-block candidate it conflicts with.
        /// </summary>
        private static bool IsAnchor(int x, int z, int radius, in WorldGenConfig cfg, in BlendCache cache){
            int r = cfg.FloraSpacingMax;
            int2 self = new(x, z);
            uint own = Priority(x, z, cfg);
            for (int dx = -r; dx <= r; dx++)
            for (int dz = -r; dz <= r; dz++){
                if (!InSpacingDisc(dx, dz, r)) continue;
                int nx = x + dx, nz = z + dz;
                if (!Outranks(new int2(nx, nz), Priority(nx, nz, cfg), self, own)) continue;
                if (!MayBeMultiBlock(nx, nz, cfg)) continue;
                WorldSampler.SampleCore(nx, nz, cfg, cache, out ColumnSample neighbour);
                int rule = RollRule(nx, nz, cfg, cache, neighbour, out int _);
                if (!IsMultiBlock(rule, cfg)) continue;
                if (Conflict(dx * dx + dz * dz, radius, SpacingRadius(nx, nz, rule, cfg))) return false;
            }

            return true;
        }

        /// <summary>Height of a stacked voxel species at its anchor, in [MinStackHeight, MaxStackHeight].</summary>
        public static int StackHeight(int x, int z, in WorldGenConfig cfg, in FloraRuleDef d){
            int span = math.max(d.MaxStackHeight - d.MinStackHeight, 0) + 1;
            return d.MinStackHeight + (int)(Hash.Hash32(x, z, cfg.Seed, HashSalts.FloraStackHeight) % (uint)span);
        }
    }
}
