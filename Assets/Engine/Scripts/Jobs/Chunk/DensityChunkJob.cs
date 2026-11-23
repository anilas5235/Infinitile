using Engine.Scripts.Data;
using Engine.Scripts.VoxelConfig.Data.Generation;
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
    ///     The chunk job of the v2 density generator (WorldGen v2 §2), beside <see cref="ChunkJob" />: P1 and P4's terrain
    ///     from the noise graph (<see cref="DensityPasses" />), the biomes and the blend (<see cref="BiomePasses" />, V3),
    ///     then the placeholder fill until the rule lists of V4; the bottom layer as in v1. The chunk keeps its hard
    ///     biome per column (§6.8).
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
            NativeArray<byte> biomes = new(DensityLayout.Columns, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            DensityChunk.Generate(default(TPrograms), Terrain, Config.WorldGen, position * ChunkWidth, voxels, biomes, ref counters);
            ChunkJob.WriteToChunkData(voxels, out ChunkVoxelData data);
            if (Config.WorldGen.Layout.HasBoxes){
                data.CreateBiomes();
                for (int i = 0; i < DensityLayout.Columns; i++) data.Biomes[i] = biomes[i];
            }

            Results.TryAdd(position, data);
            voxels.Dispose();
            biomes.Dispose();
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

        /// <summary>The last pass to run: <see cref="DensityChunk.PassP1" /> … <see cref="DensityChunk.PassFill" />.</summary>
        public int Passes;

        /// <summary>Times the passes run (for timing); the outputs hold the last run.</summary>
        public int Repeat;

        /// <summary>[x][z][y], 1 for terrain.</summary>
        [WriteOnly] public NativeArray<byte> Solid;

        /// <summary>[x · 32 + z]: top terrain voxel, −1 for none.</summary>
        [WriteOnly] public NativeArray<short> Heightmap;

        /// <summary>[x][z][y] voxel ids of the placeholder fill.</summary>
        public NativeArray<ushort> Voxels;

        /// <summary>[x · 32 + z]: the hard biome (§6.8), 0 without a layout.</summary>
        public NativeArray<byte> Biomes;

        /// <summary>[x · 32 + z]: the surface-source biome (§12).</summary>
        public NativeArray<byte> Surface;

        /// <summary>[z][x] over the padded biome map (64², from the chunk − 16): the hard biome and the wet flag.</summary>
        public NativeArray<byte> PaddedHard, PaddedWet;

        /// <summary>[parameter][z][x] over the biome lattice (17², from the chunk − 16, step 4): T, F, C, E, W as P2 read them.</summary>
        public NativeArray<float> BiomeClimate;

        /// <summary>[x · 32 + z]: the blend's 4 largest weights, their biomes (255 unused) and the weights.</summary>
        public NativeArray<int4> BlendBiomes;

        public NativeArray<float4> BlendWeights;

        /// <summary>[z · 32 + x]: the blend's border threshold per column; NaN where the border did not run.</summary>
        public NativeArray<float> Border;

        public NativeArray<DensityWorkCounters> Counters;

        public unsafe void Execute(){
            DensityWorkCounters counters = default;
            DensityChunkBuffers b = DensityChunkBuffers.Allocate(Terrain.Graph.LatticeValueCount, Allocator.Temp);
            for (int r = 0; r < math.max(1, Repeat); r++){
                counters = default;
                DensityChunk.Run(default(TPrograms), Terrain, WorldGen, Origin, Passes, Voxels, Biomes, ref b, ref counters);
            }

            if (Passes >= DensityChunk.PassTerrain){
                UnsafeUtility.MemCpy(Solid.GetUnsafePtr(), b.Solid, DensityLayout.Voxels);
                UnsafeUtility.MemCpy(Heightmap.GetUnsafePtr(), b.Heightmap, DensityLayout.Columns * sizeof(short));
            }

            if (Passes >= DensityChunk.PassBiomes && WorldGen.Layout.HasBoxes){
                const int padded = DensityLayout.BiomeSpan * DensityLayout.BiomeSpan;
                UnsafeUtility.MemCpy(PaddedHard.GetUnsafePtr(), b.Hard, padded);
                UnsafeUtility.MemCpy(PaddedWet.GetUnsafePtr(), b.Wet, padded);
                const int lattice = DensityLayout.BiomeLattice, size = DensityLayout.LatticeSize, offset = DensityLayout.BiomeLatticeOffset;
                for (int p = 0; p < 5; p++){
                    int slot = Terrain.ClimateSlot(p);
                    for (int z = 0; z < lattice; z++)
                    for (int x = 0; x < lattice; x++)
                        BiomeClimate[(p * lattice + z) * lattice + x] = slot < 0 ? 0f : b.Lattice[slot * size * size + (z + offset) * size + x + offset];
                }
            }

            if (Passes >= DensityChunk.PassBlend && WorldGen.Layout.HasBoxes){
                UnsafeUtility.MemCpy(Surface.GetUnsafePtr(), b.Surface, DensityLayout.Columns);
                UnsafeUtility.MemCpy(BlendBiomes.GetUnsafePtr(), b.BlendBiomes, DensityLayout.Columns * sizeof(int4));
                UnsafeUtility.MemCpy(BlendWeights.GetUnsafePtr(), b.BlendWeights, DensityLayout.Columns * sizeof(float4));
                bool border = counters.Biomes.BorderColumns > 0;
                for (int c = 0; c < DensityLayout.Columns; c++) Border[c] = border ? b.Border[c] : float.NaN;
            }

            b.Dispose();
            Counters[0] = counters;
        }
    }

    /// <summary>The density generator's chunk as the jobs run it.</summary>
    internal static unsafe class DensityChunk{
        /// <summary>Last pass to run: P1; P4's terrain; the biomes (P2); the blend (P3); the fill.</summary>
        public const int PassP1 = 1, PassTerrain = 2, PassBiomes = 3, PassBlend = 4, PassFill = 5;

        /// <summary>
        ///     All passes for the chunk whose first column is <paramref name="origin" />, into <paramref name="voxels" />
        ///     and the hard biomes into <paramref name="biomes" /> ([x · 32 + z]).
        /// </summary>
        public static void Generate<TPrograms>(TPrograms programs, in DensityTerrainGraph terrain, in WorldGenConfig worldGen, int2 origin,
            NativeArray<ushort> voxels, NativeArray<byte> biomes, ref DensityWorkCounters counters) where TPrograms : struct, IGraphPrograms{
            DensityChunkBuffers buffers = DensityChunkBuffers.Allocate(terrain.Graph.LatticeValueCount, Allocator.Temp);
            Run(programs, terrain, worldGen, origin, PassFill, voxels, biomes, ref buffers, ref counters);
            buffers.Dispose();
        }

        /// <summary>
        ///     The passes up to <paramref name="passes" />, on buffers the caller allocated. The terrain runs before the
        ///     biomes (they only share P1's lattice), so the terrain rows of the benchmark keep their meaning. Without a
        ///     layout (v1 settings, tests of the terrain alone) the biome passes are skipped and the fill is V2's.
        /// </summary>
        public static void Run<TPrograms>(TPrograms programs, in DensityTerrainGraph terrain, in WorldGenConfig worldGen, int2 origin, int passes,
            NativeArray<ushort> voxels, NativeArray<byte> biomes, ref DensityChunkBuffers b, ref DensityWorkCounters counters)
            where TPrograms : struct, IGraphPrograms{
            GraphCode code = GraphCode.Of(terrain.Graph);
            DensityPasses.Columns(programs, terrain, code, worldGen.Seed, origin - DensityLayout.Padding, DensityLayout.LatticeSize, ref b, ref counters.Graph);
            if (passes < PassTerrain) return;
            DensityPasses.Terrain(programs, terrain, code, worldGen.Seed, origin, ref b, ref counters);
            bool layout = worldGen.Layout.HasBoxes;
            if (passes < PassBiomes) return;
            if (layout) BiomePasses.Biomes(programs, terrain, code, worldGen.Seed, origin, worldGen.Layout, ref b, ref counters);
            if (passes < PassBlend) return;
            if (layout)
                BiomePasses.Blend(programs, terrain, code, worldGen.Seed, origin, worldGen.MaterialMethod == BlendMethod.DistanceBlend, worldGen.MaterialKernel,
                    worldGen.MaterialTaps, ref b, ref counters);
            if (passes < PassFill) return;
            if (layout) FillBiomes(b, voxels, worldGen);
            else Fill(b.Solid, voxels, worldGen);
            ChunkJob.ApplyBottomLayer(voxels, new int3(origin.x, 0, origin.y), worldGen);
            if (biomes.IsCreated) WriteBiomes(b, biomes, layout);
        }

        /// <summary>The hard biome of every chunk column ([x · 32 + z]); 255 (none) is stored as 0, without a layout all 0.</summary>
        private static void WriteBiomes(in DensityChunkBuffers b, NativeArray<byte> biomes, bool layout){
            const int n = DensityLayout.ChunkSize;
            for (int x = 0; x < n; x++)
            for (int z = 0; z < n; z++){
                byte id = layout ? b.Hard[(z + DensityLayout.BiomePadding) * DensityLayout.BiomeSpan + x + DensityLayout.BiomePadding] : (byte)0;
                biomes[x * n + z] = id == 255 ? (byte)0 : id;
            }
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

        /// <summary>Voxels under a surface voxel that take the under block.</summary>
        private const int UnderDepth = 3;

        /// <summary>
        ///     The placeholder fill of V3, until the rule lists of V4: the surface-source biome's blocks (§12, AD-25). A
        ///     terrain voxel with an open voxel above takes the top block (the underwater top block under water), the
        ///     <see cref="UnderDepth" /> voxels below it the under block, the rest the stone block; open voxels below the
        ///     water level are water. A biome without a block keeps the V2 stone.
        /// </summary>
        private static void FillBiomes(in DensityChunkBuffers b, NativeArray<ushort> voxels, in WorldGenConfig worldGen){
            ushort fallback = worldGen.DensityStoneVoxel, water = worldGen.WaterVoxel;
            int waterLevel = math.clamp(worldGen.WaterLevel, 0, ChunkHeight);
            ushort* to = (ushort*)voxels.GetUnsafePtr();
            NativeArray<Biome.BiomeDef> defs = worldGen.Layout.Defs;
            int biomeCount = worldGen.Layout.BiomeCount;
            for (int c = 0; c < DensityLayout.Columns; c++){
                byte* from = b.Solid + c * ChunkHeight;
                ushort* column = to + c * ChunkHeight;
                int id = b.Surface[c];
                ushort top = fallback, wetTop = fallback, under = fallback, stone = fallback;
                if (id < biomeCount){
                    Biome.BiomeDef def = defs[id];
                    stone = def.stoneBlock != 0 ? def.stoneBlock : fallback;
                    under = def.underBlock != 0 ? def.underBlock : stone;
                    top = def.topBlock != 0 ? def.topBlock : under;
                    wetTop = def.underwaterTopBlock != 0 ? def.underwaterTopBlock : top;
                }

                int depth = UnderDepth + 1; // voxels below the last surface voxel; past UnderDepth is stone
                for (int y = ChunkHeight - 1; y >= 0; y--){
                    if (from[y] == 0){
                        column[y] = y < waterLevel ? water : (ushort)0;
                        depth = -1;
                        continue;
                    }

                    depth++;
                    if (depth == 0) column[y] = y + 1 < waterLevel ? wetTop : top;
                    else column[y] = depth <= UnderDepth ? under : stone;
                }
            }
        }
    }
}
