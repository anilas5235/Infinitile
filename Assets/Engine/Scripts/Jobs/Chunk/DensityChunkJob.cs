using Engine.Scripts.Data;
using Engine.Scripts.Jobs.Chunk;
using Engine.Scripts.Jobs.Core;
using Engine.Scripts.WorldGen.Density;
using Engine.Scripts.WorldGen.Graph;
using Engine.Scripts.WorldGen.Settings;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using static Engine.Scripts.Utils.VoxelConstants;

// The interpreter's instances; generated code registers its own (NoiseGraphCodeGenerator).
[assembly: RegisterGenericJobType(typeof(DensityChunkJob<InterpretedPrograms>))]
[assembly: RegisterGenericJobType(typeof(DensityProbeJob<InterpretedPrograms>))]

namespace Engine.Scripts.Jobs.Chunk{
    /// <summary>
    ///     The chunk job of the v2 density generator (WorldGen v2 §2, phase V2), beside <see cref="ChunkJob" />: P1 and
    ///     P4's terrain from the noise graph (<see cref="DensityPasses" />), then the placeholder fill until the rule
    ///     lists arrive in V3: terrain is stone, open voxels below the water level are water, the rest is air; the
    ///     bottom layer as in v1. The chunk keeps no biomes yet.
    /// </summary>
    [BurstCompile(CompileSynchronously = true, FloatMode = FloatMode.Strict)]
    internal struct DensityChunkJob<TPrograms> : ISlicedParallelFor where TPrograms : struct, IGraphPrograms{
        [ReadOnly] public NativeList<int2> Jobs;
        [WriteOnly] public NativeParallelHashMap<int2, ChunkVoxelData>.ParallelWriter Results;
        [ReadOnly] public GeneratorConfig Config;
        public DensityTerrainGraph Terrain;

        public int Offset{ get; set; }

        public void Execute(int index){
            int2 position = Jobs[Offset + index];
            NativeArray<ushort> voxels = new(VoxelsPerChunk, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            DensityWorkCounters counters = default;
            DensityChunk.Generate(default(TPrograms), Terrain, Config.WorldGen, position * ChunkWidth, voxels, ref counters);
            ChunkJob.WriteToChunkData(voxels, out ChunkVoxelData data);
            Results.TryAdd(position, data);
            voxels.Dispose();
        }
    }

    /// <summary>
    ///     One chunk of the density generator with everything the tests and benchmarks look at: the terrain flags, the
    ///     heightmap, the voxel ids and the work counters. <see cref="Passes" /> stops early for timing a single pass.
    /// </summary>
    [BurstCompile(CompileSynchronously = true, FloatMode = FloatMode.Strict)]
    internal struct DensityProbeJob<TPrograms> : IJob where TPrograms : struct, IGraphPrograms{
        public DensityTerrainGraph Terrain;
        [ReadOnly] public WorldGenConfig WorldGen;

        /// <summary>First column of the chunk (multiples of 32 in the engine; any multiple of 4 works).</summary>
        public int2 Origin;

        /// <summary><see cref="DensityChunk.PassP1" />, <see cref="DensityChunk.PassTerrain" /> or <see cref="DensityChunk.PassFill" />.</summary>
        public int Passes;

        /// <summary>Times the passes run (for timing); the outputs hold the last run.</summary>
        public int Repeat;

        /// <summary>[x][z][y], 1 for terrain.</summary>
        [WriteOnly] public NativeArray<byte> Solid;

        /// <summary>[x · 32 + z]: top terrain voxel, −1 for none.</summary>
        [WriteOnly] public NativeArray<short> Heightmap;

        /// <summary>[x][z][y] voxel ids of the placeholder fill.</summary>
        public NativeArray<ushort> Voxels;

        public NativeArray<DensityWorkCounters> Counters;

        public unsafe void Execute(){
            DensityWorkCounters counters = default;
            DensityChunkBuffers b = DensityChunkBuffers.Allocate(Terrain.Graph.LatticeValueCount, Allocator.Temp);
            for (int r = 0; r < math.max(1, Repeat); r++){
                counters = default;
                DensityChunk.Run(default(TPrograms), Terrain, WorldGen, Origin, Passes, Voxels, ref b, ref counters);
            }

            if (Passes >= DensityChunk.PassTerrain){
                UnsafeUtility.MemCpy(Solid.GetUnsafePtr(), b.Solid, DensityLayout.Voxels);
                UnsafeUtility.MemCpy(Heightmap.GetUnsafePtr(), b.Heightmap, DensityLayout.Columns * sizeof(short));
            }

            b.Dispose();
            Counters[0] = counters;
        }
    }

    /// <summary>The density generator's chunk as the jobs run it.</summary>
    internal static unsafe class DensityChunk{
        public const int PassP1 = 1, PassTerrain = 2, PassFill = 3;

        /// <summary>All passes for the chunk whose first column is <paramref name="origin" />, into <paramref name="voxels" />.</summary>
        public static void Generate<TPrograms>(TPrograms programs, in DensityTerrainGraph terrain, in WorldGenConfig worldGen, int2 origin,
            NativeArray<ushort> voxels, ref DensityWorkCounters counters) where TPrograms : struct, IGraphPrograms{
            DensityChunkBuffers buffers = DensityChunkBuffers.Allocate(terrain.Graph.LatticeValueCount, Allocator.Temp);
            Run(programs, terrain, worldGen, origin, PassFill, voxels, ref buffers, ref counters);
            buffers.Dispose();
        }

        /// <summary>The passes up to <paramref name="passes" />, on buffers the caller allocated.</summary>
        public static void Run<TPrograms>(TPrograms programs, in DensityTerrainGraph terrain, in WorldGenConfig worldGen, int2 origin, int passes,
            NativeArray<ushort> voxels, ref DensityChunkBuffers b, ref DensityWorkCounters counters) where TPrograms : struct, IGraphPrograms{
            GraphCode code = GraphCode.Of(terrain.Graph);
            DensityPasses.Columns(programs, terrain, code, worldGen.Seed, origin - DensityLayout.Padding, DensityLayout.LatticeSize, ref b, ref counters.Graph);
            if (passes < PassTerrain) return;
            DensityPasses.Terrain(programs, terrain, code, worldGen.Seed, origin, ref b, ref counters);
            if (passes < PassFill) return;
            Fill(b.Solid, voxels, worldGen);
            ChunkJob.ApplyBottomLayer(voxels, new int3(origin.x, 0, origin.y), worldGen);
        }

        /// <summary>
        ///     The placeholder fill of V2: terrain is <see cref="WorldGenConfig.DensityStoneVoxel" />, an open voxel below
        ///     the water level is water, the rest air. Same [x][z][y] layout as the voxel buffer.
        /// </summary>
        private static void Fill(byte* solid, NativeArray<ushort> voxels, in WorldGenConfig worldGen){
            ushort stone = worldGen.DensityStoneVoxel, water = worldGen.WaterVoxel;
            int waterLevel = math.clamp(worldGen.WaterLevel, 0, ChunkHeight);
            ushort* to = (ushort*)voxels.GetUnsafePtr();
            for (int c = 0; c < DensityLayout.Columns; c++){
                byte* from = solid + c * ChunkHeight;
                ushort* column = to + c * ChunkHeight;
                for (int y = 0; y < waterLevel; y++) column[y] = from[y] != 0 ? stone : water;
                for (int y = waterLevel; y < ChunkHeight; y++) column[y] = from[y] != 0 ? stone : (ushort)0;
            }
        }
    }
}
