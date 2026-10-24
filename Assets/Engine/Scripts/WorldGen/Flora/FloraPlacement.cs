using Engine.Scripts.VoxelConfig.Data.Generation;
using Engine.Scripts.WorldGen.Blending;
using Engine.Scripts.WorldGen.Core;
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
    ///         <item>Species: the rule whose cumulative interval holds Hash01(<see cref="HashSalts.FloraSpecies" />); none → no plant.</item>
    ///         <item>
    ///             Conditions: same surface (optional), height above water, not clamped at Hmax, solid ground, and no
    ///             snow cover for single-block plants.
    ///         </item>
    ///         <item>
    ///             Multi-block species only: the column is an anchor if no other candidate within r_tree has a higher
    ///             <see cref="HashSalts.TreePriority" /> hash; otherwise it carries no plant.
    ///         </item>
    ///     </list>
    /// </summary>
    public static class FloraPlacement{
        /// <summary>
        ///     Steps 1–3: the index of the flora rule (into <c>cfg.FloraRules</c>) the column passes, or -1.
        ///     <paramref name="source" /> is the flora source biome of a passing rule, -1 otherwise.
        /// </summary>
        public static int RollRule(int x, int z, in WorldGenConfig cfg, in ColumnSample s, out int source){
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
            int end = def.floraRuleStart + def.floraRuleCount;
            for (int r = def.floraRuleStart; r < end; r++){
                FloraRuleDef candidate = cfg.FloraRules[r];
                if (u >= candidate.CumulativeStart && u < candidate.CumulativeEnd){
                    rule = r;
                    break;
                }
            }

            if (rule < 0) return -1;
            FloraRuleDef d = cfg.FloraRules[rule];
            // Optionally only on ground of the same biome.
            if (d.RequiresSurfaceFromSameBiome != 0 && s.SurfaceSource != biome) return -1;
            if (!GroundAllows(s, d.MinHeightAboveWater, cfg)) return -1;
            // Snow keeps trees and cacti but carries no ground plants.
            if (d.MultiBlock == 0 &&
                SurfaceCover.IsSnowCovered(x, z, s.Fields.T, s.ColumnHeight, cfg.Biomes[s.SurfaceSource], cfg)) return -1;

            source = biome;
            return rule;
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
        ///     candidates within r_tree exactly one outranks the other and at most one becomes an anchor.
        /// </summary>
        public static bool Outranks(int2 a, uint priorityA, int2 b, uint priorityB){
            if (priorityA != priorityB) return priorityA > priorityB;
            return a.x < b.x || a.x == b.x && a.y < b.y;
        }

        /// <summary>True for the offsets of the r_tree disc around a column (dx² + dz² ≤ r², the column excluded).</summary>
        public static bool InSpacingDisc(int dx, int dz, int radius){
            return (dx != 0 || dz != 0) && dx * dx + dz * dz <= radius * radius;
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
            int rule = RollRule(x, z, cfg, s, out int biome);
            if (rule < 0) return;
            FloraRuleDef d = cfg.FloraRules[rule];
            if (d.MultiBlock != 0 && !IsAnchor(x, z, cfg, cache)) return;
            species = d.Species;
            source = biome;
        }

        /// <summary>Anchor test for a column that is a multi-block candidate itself: no outranking multi-block neighbour within r_tree.</summary>
        private static bool IsAnchor(int x, int z, in WorldGenConfig cfg, in BlendCache cache){
            int r = cfg.TreeSpacingRadius;
            int2 self = new(x, z);
            uint own = Priority(x, z, cfg);
            for (int dx = -r; dx <= r; dx++)
            for (int dz = -r; dz <= r; dz++){
                if (!InSpacingDisc(dx, dz, r)) continue;
                int nx = x + dx, nz = z + dz;
                if (!Outranks(new int2(nx, nz), Priority(nx, nz, cfg), self, own)) continue;
                if (!MayBeMultiBlock(nx, nz, cfg)) continue;
                WorldSampler.SampleCore(nx, nz, cfg, cache, out ColumnSample neighbour);
                if (IsMultiBlock(RollRule(nx, nz, cfg, neighbour, out int _), cfg)) return false;
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
