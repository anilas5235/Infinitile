using System;
using Engine.Scripts.Jobs.Chunk;
using Engine.Scripts.WorldGen.Graph;
using Engine.Scripts.WorldGen.Settings;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Engine.Scripts.WorldGen.Density{
    /// <summary>One chunk of the density generator with its terrain flags, heightmap, voxels and work counters.</summary>
    internal sealed class DensityProbeResult : IDisposable{
        public int2 Origin;

        /// <summary>[x][z][y] (y contiguous), 1 for terrain.</summary>
        public NativeArray<byte> Solid;

        /// <summary>[x · 32 + z]: the top terrain voxel, −1 for none.</summary>
        public NativeArray<short> Heightmap;

        /// <summary>[x][z][y] voxel ids of the placeholder fill (only with <see cref="DensityChunk.PassFill" />).</summary>
        public NativeArray<ushort> Voxels;

        public DensityWorkCounters Counters;

        public bool IsSolid(int x, int y, int z){
            return Solid[(x * DensityLayout.ChunkSize + z) * DensityLayout.Height + y] != 0;
        }

        public void Dispose(){
            if (Solid.IsCreated) Solid.Dispose();
            if (Heightmap.IsCreated) Heightmap.Dispose();
            if (Voxels.IsCreated) Voxels.Dispose();
        }
    }

    /// <summary>
    ///     Runs the density generator on single chunks, synchronously, for the tests, the benchmarks and the preview:
    ///     the same passes as <see cref="DensityChunkJob{TPrograms}" />.
    /// </summary>
    internal static class DensityProbe{
        /// <summary>
        ///     The chunk whose first column is <paramref name="origin" /> (a multiple of 4), with the generated code where
        ///     it fits <paramref name="graph" /> (unless <paramref name="interpreterOnly" />), else in the interpreter.
        /// </summary>
        public static DensityProbeResult Run(CompiledNoiseGraph graph, in WorldGenConfig worldGen, int2 origin, int passes = DensityChunk.PassFill,
            bool interpreterOnly = false){
            return !interpreterOnly && GraphEvaluator.HasGeneratedCode(graph)
                ? Run<ShippedNoiseGraphCode>(graph, worldGen, origin, passes)
                : Run<InterpretedPrograms>(graph, worldGen, origin, passes);
        }

        /// <summary>The chunk with the programs <typeparamref name="TPrograms" />, which must fit <paramref name="graph" />.</summary>
        public static DensityProbeResult Run<TPrograms>(CompiledNoiseGraph graph, in WorldGenConfig worldGen, int2 origin, int passes = DensityChunk.PassFill)
            where TPrograms : struct, IGraphPrograms{
            DensityProbeJob<TPrograms> job = Create<TPrograms>(graph, worldGen, origin, passes, out DensityProbeResult result);
            NativeArray<DensityWorkCounters> counters = job.Counters;
            try{
                job.Run();
                result.Counters = counters[0];
            }
            catch{
                result.Dispose();
                throw;
            }
            finally{
                counters.Dispose();
            }

            return result;
        }

        /// <summary>
        ///     A probe job with its output buffers (owned by <paramref name="result" />) and a counter store of one the
        ///     caller disposes (<see cref="DensityProbeJob{TPrograms}.Counters" />).
        /// </summary>
        public static DensityProbeJob<TPrograms> Create<TPrograms>(CompiledNoiseGraph graph, in WorldGenConfig worldGen, int2 origin, int passes,
            out DensityProbeResult result) where TPrograms : struct, IGraphPrograms{
            if (origin.x % 4 != 0 || origin.y % 4 != 0) throw new ArgumentException("A chunk origin must be a multiple of 4.");
            // The interpreter (code hash 0) runs any graph.
            if (default(TPrograms).CodeHash != 0 && !GraphEvaluator.Fits<TPrograms>(graph))
                throw new ArgumentException($"{typeof(TPrograms).Name} was generated from another graph; regenerate it.");
            if (!DensityTerrainGraph.TryCreate(graph, out DensityTerrainGraph terrain, out string error)) throw new ArgumentException(error);
            result = new DensityProbeResult{
                Origin = origin,
                Solid = new NativeArray<byte>(DensityLayout.Voxels, Allocator.TempJob),
                Heightmap = new NativeArray<short>(DensityLayout.Columns, Allocator.TempJob),
                Voxels = new NativeArray<ushort>(DensityLayout.Voxels, Allocator.TempJob)
            };
            return new DensityProbeJob<TPrograms>{
                Terrain = terrain,
                WorldGen = worldGen,
                Origin = origin,
                Passes = passes,
                Repeat = 1,
                Solid = result.Solid,
                Heightmap = result.Heightmap,
                Voxels = result.Voxels,
                Counters = new NativeArray<DensityWorkCounters>(1, Allocator.TempJob)
            };
        }
    }
}
