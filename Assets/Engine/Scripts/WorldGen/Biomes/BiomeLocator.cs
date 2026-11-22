using System.Collections.Generic;
using System.Linq;
using Engine.Scripts.WorldGen.Graph;
using Unity.Collections;
using Unity.Mathematics;

namespace Engine.Scripts.WorldGen.Biomes{
    /// <summary>
    ///     Finds places from the 2D program and the biome lookup without generating chunks (§13.6, DEC-160): the nearest
    ///     column of a biome, and the highest peak nearby. Searches square rings on a 16-block grid outwards, then
    ///     refines on the 4-block lattice, where <see cref="BiomeColumnSampler" /> gives the chunk's own values.
    /// </summary>
    public static class BiomeLocator{
        /// <summary>The coarse search grid, and how far the biome search goes.</summary>
        public const int Step = 16, MaxBiomeRadius = 20000;

        /// <summary>How far the peak search looks around the start.</summary>
        public const int PeakRadius = 2048;

        /// <summary>Rings sampled per round of the biome search.</summary>
        private const int RingsPerRound = 32;

        /// <summary>
        ///     The column of biome <paramref name="biome" /> nearest to <paramref name="from" /> (x, z) within
        ///     <see cref="MaxBiomeRadius" />, with its top terrain voxel; false when none is found.
        /// </summary>
        public static bool FindBiome(CompiledNoiseGraph graph, in BiomeLayoutData layout, uint seed, int2 from, byte biome, out int2 at, out int top,
            out bool wet){
            at = default;
            top = -1;
            wet = false;
            int2 origin = (int2)math.floor((float2)from / Step) * Step;
            float bestDistance = float.MaxValue;
            int2 best = default;
            bool found = false;
            int rings = MaxBiomeRadius / Step;
            for (int first = 0; first <= rings; first += RingsPerRound){
                // A ring at Chebyshev distance k·Step holds nothing nearer than k·Step: stop once that passes the best.
                if (found && first * Step > bestDistance + Step) break;
                List<float2> points = new();
                for (int k = first; k < first + RingsPerRound && k <= rings; k++) Ring(origin, k, points);
                using BiomeColumns c = BiomeColumnSampler.Sample(graph, layout, seed, points.ToArray());
                for (int i = 0; i < points.Count; i++){
                    if (c.Biome[i] != biome) continue;
                    float d = math.distance(points[i], from);
                    if (d >= bestDistance) continue;
                    bestDistance = d;
                    best = (int2)points[i];
                    found = true;
                }
            }

            if (!found) return false;

            // Refine on the lattice around the coarse hit: the nearest lattice column of the biome.
            List<float2> fine = new();
            for (int dz = -Step; dz <= Step; dz += 4)
            for (int dx = -Step; dx <= Step; dx += 4)
                fine.Add(best + new int2(dx, dz));
            using BiomeColumns f = BiomeColumnSampler.Sample(graph, layout, seed, fine.ToArray());
            float fineDistance = float.MaxValue;
            int pick = -1;
            for (int i = 0; i < fine.Count; i++){
                if (f.Biome[i] != biome) continue;
                float d = math.distance(fine[i], from);
                if (d < fineDistance){
                    fineDistance = d;
                    pick = i;
                }
            }

            at = pick >= 0 ? (int2)fine[pick] : best;
            Top(graph, layout, seed, at, out top, out wet);
            return true;
        }

        /// <summary>The highest top terrain voxel within <see cref="PeakRadius" /> of <paramref name="from" />.</summary>
        public static bool FindPeak(CompiledNoiseGraph graph, in BiomeLayoutData layout, uint seed, int2 from, out int2 at, out int top){
            int2 origin = (int2)math.floor((float2)from / Step) * Step;
            List<float2> points = new();
            for (int dz = -PeakRadius; dz <= PeakRadius; dz += Step)
            for (int dx = -PeakRadius; dx <= PeakRadius; dx += Step)
                if (dx * dx + dz * dz <= PeakRadius * PeakRadius)
                    points.Add(origin + new int2(dx, dz));
            using BiomeColumns c = BiomeColumnSampler.Sample(graph, layout, seed, points.ToArray());

            // The preliminary surface picks the candidates, the terrain program decides among them on the lattice.
            List<int> candidates = Enumerable.Range(0, points.Count).OrderByDescending(i => c.Surface[i]).Take(24).ToList();
            List<float2> fine = new();
            foreach (int i in candidates)
                for (int dz = -Step; dz <= Step; dz += 4)
                for (int dx = -Step; dx <= Step; dx += 4)
                    fine.Add(points[i] + new float2(dx, dz));
            using BiomeColumns f = BiomeColumnSampler.Sample(graph, layout, seed, fine.ToArray(), true);
            int pick = 0;
            for (int i = 1; i < fine.Count; i++)
                if (f.Top[i] > f.Top[pick])
                    pick = i;
            at = (int2)fine[pick];
            top = f.Top[pick];
            return fine.Count > 0;
        }

        /// <summary>The top terrain voxel and the wet flag of one column.</summary>
        public static void Top(CompiledNoiseGraph graph, in BiomeLayoutData layout, uint seed, int2 at, out int top, out bool wet){
            using BiomeColumns c = BiomeColumnSampler.Sample(graph, layout, seed, new[]{ (float2)at }, true);
            top = c.Top[0];
            wet = c.Wet[0] != 0;
        }

        /// <summary>The grid points at Chebyshev distance k·<see cref="Step" /> around <paramref name="origin" />.</summary>
        private static void Ring(int2 origin, int k, List<float2> into){
            if (k == 0){
                into.Add(origin);
                return;
            }

            int r = k * Step;
            for (int i = -k; i < k; i++){
                int o = i * Step;
                into.Add(origin + new int2(o, -r));
                into.Add(origin + new int2(r, o));
                into.Add(origin + new int2(-o, r));
                into.Add(origin + new int2(-r, -o));
            }
        }
    }
}
