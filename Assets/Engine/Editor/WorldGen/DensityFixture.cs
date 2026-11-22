using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Engine.Scripts.WorldGen.Density;
using Engine.Scripts.WorldGen.Graph;
using Unity.Mathematics;

namespace Engine.Editor.WorldGen{
    /// <summary>Chunk classes of the benchmark fixture (§17.5); the cavern and lava mask classes come in V6a.</summary>
    public enum DensityChunkClass{
        Ocean,
        Coast,
        Lowland,
        Massif
    }

    /// <summary>
    ///     The benchmark fixture of WorldGen v2 (DEC-147, §17.5): a frozen copy of the noise graph under Tests/ that does
    ///     not move with tuning, a seed, and a fixed list of chunks per class. The budget rows, the work-counter tests
    ///     and the voxel hash read it; the chunk lists come from <see cref="Classify" /> (DensityFixtureTests'
    ///     explicit finder) and are not recomputed.
    /// </summary>
    public static class DensityFixture{
        public const string GraphPath = "Assets/Engine/Tests/Fixtures/DensityFixture/NoiseGraph.json";
        public const uint Seed = 1337u;

        public static readonly DensityChunkClass[] Classes = (DensityChunkClass[])Enum.GetValues(typeof(DensityChunkClass));

        // Chunk coordinates (in chunks), eight per class, from the finder on the fixture graph with Seed.
        private static readonly int2[] Ocean ={ };
        private static readonly int2[] Coast ={ };
        private static readonly int2[] Lowland ={ };
        private static readonly int2[] Massif ={ };

        /// <summary>The chunks of <paramref name="c" />, in chunk coordinates.</summary>
        public static IReadOnlyList<int2> Chunks(DensityChunkClass c){
            return c switch{
                DensityChunkClass.Ocean => Ocean,
                DensityChunkClass.Coast => Coast,
                DensityChunkClass.Lowland => Lowland,
                DensityChunkClass.Massif => Massif,
                _ => throw new ArgumentOutOfRangeException(nameof(c))
            };
        }

        public static IEnumerable<(DensityChunkClass Class, int2 Chunk)> AllChunks(){
            return Classes.SelectMany(c => Chunks(c).Select(p => (c, p)));
        }

        /// <summary>Compiles the fixture graph; a missing file is an error.</summary>
        public static GraphCompileResult Compile(){
            if (File.Exists(GraphPath)) return GraphCompiler.Compile(File.ReadAllText(GraphPath), GraphPath);
            GraphCompileResult missing = new();
            missing.Messages.Add(new GraphMessage(GraphMessageSeverity.Error, GraphPath, null, "The fixture graph is missing."));
            return missing;
        }

        /// <summary>
        ///     The class of a chunk from the preliminary surface at its corner columns (lowest, highest), or null when it
        ///     fits none: ocean lies wholly 12 blocks or more under the water, a coast crosses the water level, a lowland
        ///     stays between 2 and 26 blocks above it, a massif reaches y 190.
        /// </summary>
        public static DensityChunkClass? Classify(float lowest, float highest, int waterLevel){
            if (highest < waterLevel - 12) return DensityChunkClass.Ocean;
            if (lowest < waterLevel - 2 && highest > waterLevel + 2) return DensityChunkClass.Coast;
            if (lowest >= waterLevel + 2 && highest < waterLevel + 26) return DensityChunkClass.Lowland;
            if (highest >= 190f) return DensityChunkClass.Massif;
            return null;
        }

        /// <summary>
        ///     Finds <paramref name="perClass" /> chunks of each class on <paramref name="graph" />: chunks on a grid of
        ///     <paramref name="step" /> chunks out to <paramref name="radius" />, nearest to the origin first, classified
        ///     from the preliminary surface at their 9 × 9 corner columns. For the explicit finder test.
        /// </summary>
        public static Dictionary<DensityChunkClass, List<int2>> Find(CompiledNoiseGraph graph, uint seed, int waterLevel, int perClass, int radius, int step){
            if (!DensityTerrainGraph.TryCreate(graph, out DensityTerrainGraph terrain, out string error)) throw new ArgumentException(error);
            List<int2> candidates = new();
            for (int z = -radius; z <= radius; z += step)
            for (int x = -radius; x <= radius; x += step)
                candidates.Add(new int2(x, z));
            candidates = candidates.OrderBy(c => math.lengthsq(c)).ThenBy(c => c.y).ThenBy(c => c.x).ToList();

            const int corners = DensityLayout.CornerColumns;
            float2[] points = new float2[candidates.Count * corners * corners];
            for (int i = 0; i < candidates.Count; i++)
            for (int k = 0; k < corners * corners; k++)
                points[i * corners * corners + k] = new float2(candidates[i].x * 32 + 4 * (k % corners), candidates[i].y * 32 + 4 * (k / corners));
            float[] values = GraphEvaluator.SampleLattice(graph, seed, points, out _);

            Dictionary<DensityChunkClass, List<int2>> found = Classes.ToDictionary(c => c, _ => new List<int2>());
            for (int i = 0; i < candidates.Count; i++){
                float lowest = float.MaxValue, highest = float.MinValue;
                for (int k = 0; k < corners * corners; k++){
                    float v = values[terrain.PreliminarySurface * points.Length + i * corners * corners + k];
                    lowest = math.min(lowest, v);
                    highest = math.max(highest, v);
                }

                DensityChunkClass? c = Classify(lowest, highest, waterLevel);
                if (c.HasValue && found[c.Value].Count < perClass) found[c.Value].Add(candidates[i]);
            }

            return found;
        }
    }
}
