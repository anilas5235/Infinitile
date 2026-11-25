using System;
using Engine.Scripts.Jobs.Core;
using Engine.Scripts.WorldGen.Density;
using Engine.Scripts.WorldGen.Graph;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;

namespace Engine.Scripts.WorldGen.Biomes{
    /// <summary>What <see cref="BiomeColumnSampler" /> gives per point.</summary>
    public sealed class BiomeColumns : IDisposable{
        public int Count;

        /// <summary>The hard biome after the dry-column rule (255: the layout has no box).</summary>
        public NativeArray<byte> Biome;

        /// <summary>1 when the terrain is air at y 123 (§6.5).</summary>
        public NativeArray<byte> Wet;

        /// <summary>The preliminary surface (graph output).</summary>
        public NativeArray<float> Surface;

        /// <summary>The top terrain voxel (−1: none), when asked for; else one element.</summary>
        public NativeArray<short> Top;

        /// <summary>The five climate parameters, [parameter][point], when asked for; else one element.</summary>
        public NativeArray<float> Climate;

        /// <summary>1 where the dry-column rule replaced a water biome.</summary>
        public NativeArray<byte> Substituted;

        /// <summary>The graph's <c>river</c> output (§9.2), 0 when the graph has none.</summary>
        public NativeArray<float> River;

        public void Dispose(){
            if (Biome.IsCreated) Biome.Dispose();
            if (Wet.IsCreated) Wet.Dispose();
            if (Surface.IsCreated) Surface.Dispose();
            if (Top.IsCreated) Top.Dispose();
            if (Climate.IsCreated) Climate.Dispose();
            if (Substituted.IsCreated) Substituted.Dispose();
            if (River.IsCreated) River.Dispose();
        }
    }

    /// <summary>
    ///     The biome facts of single columns without generating chunks, for the world report, the sweep and
    ///     <c>locate</c> (§13.4, §13.6): the 2D program at each point, the wetness test with the chunk's early-out and
    ///     the corner program on the two layers around y 123, the lookup and the dry-column rule, and optionally the top
    ///     terrain voxel from the corner program inside the column's Δ window (§5.4). On a point of the 4-block lattice
    ///     (x and z multiples of 4) every value equals the chunk's, which reads that point's lattice values without
    ///     interpolation; elsewhere the point is evaluated exactly where a chunk interpolates.
    /// </summary>
    public static class BiomeColumnSampler{
        /// <summary>Points per job index: two lanes each for the wetness layers.</summary>
        private const int Batch = GraphLimits.MaxLanes / 2;

        /// <summary>Job indices per slice (§13.4, W2-ARC-03): the Temp memory of a slice is freed when it completes.</summary>
        private const int BatchesPerSlice = 256;

        /// <summary>
        ///     Samples <paramref name="points" />. The caller disposes the result. <paramref name="graph" /> must have
        ///     the terrain outputs.
        /// </summary>
        public static BiomeColumns Sample(CompiledNoiseGraph graph, in BiomeLayoutData layout, uint seed, NativeArray<float2> points, bool heights = false,
            bool climate = false, Allocator allocator = Allocator.TempJob){
            if (!DensityTerrainGraph.TryCreate(graph, out DensityTerrainGraph terrain, out string error)) throw new ArgumentException(error);
            int n = points.Length;
            BiomeColumns r = new(){
                Count = n,
                Biome = new NativeArray<byte>(math.max(1, n), allocator),
                Wet = new NativeArray<byte>(math.max(1, n), allocator),
                Surface = new NativeArray<float>(math.max(1, n), allocator),
                Top = new NativeArray<short>(heights ? math.max(1, n) : 1, allocator),
                Climate = new NativeArray<float>(climate ? math.max(1, n * BiomeBox.Parameters) : 1, allocator),
                Substituted = new NativeArray<byte>(math.max(1, n), allocator),
                River = new NativeArray<float>(math.max(1, n), allocator)
            };
            if (n == 0) return r;
            BiomeColumnJob job = new(){
                Terrain = terrain,
                Layout = layout,
                Seed = seed,
                Points = points,
                Heights = heights,
                WithClimate = climate,
                Biome = r.Biome,
                Wet = r.Wet,
                Surface = r.Surface,
                Top = r.Top,
                Climate = r.Climate,
                Substituted = r.Substituted,
                River = r.River,
                RiverSlot = graph.Output(GraphOutputs.River) is{ BoundOnly: false, LatticeValue: >= 0 } river ? river.LatticeValue : -1
            };
            int batches = (n + Batch - 1) / Batch;
            SlicedParallelFor.Schedule(job, batches, BatchesPerSlice).Complete();
            return r;
        }

        /// <summary><see cref="Sample(CompiledNoiseGraph, in BiomeLayoutData, uint, NativeArray{float2}, bool, bool, Allocator)" /> for managed points.</summary>
        public static BiomeColumns Sample(CompiledNoiseGraph graph, in BiomeLayoutData layout, uint seed, float2[] points, bool heights = false, bool climate = false){
            using NativeArray<float2> p = new(points, Allocator.TempJob);
            return Sample(graph, layout, seed, p, heights, climate);
        }

        [BurstCompile(FloatMode = FloatMode.Strict, CompileSynchronously = true)]
        private unsafe struct BiomeColumnJob : ISlicedParallelFor{
            public DensityTerrainGraph Terrain;
            public BiomeLayoutData Layout;
            public uint Seed;
            public bool Heights, WithClimate;
            public int RiverSlot;
            [ReadOnly] public NativeArray<float2> Points;

            [NativeDisableParallelForRestriction] [WriteOnly] public NativeArray<byte> Biome, Wet, Substituted;
            [NativeDisableParallelForRestriction] [WriteOnly] public NativeArray<float> Surface, Climate, River;
            [NativeDisableParallelForRestriction] [WriteOnly] public NativeArray<short> Top;

            public int Offset{ get; set; }

            private const int Lanes = GraphLimits.MaxLanes;
            private const int MaxLayers = GraphLimits.WorldHeight / 4 + 1;

            public void Execute(int index){
                int start = (Offset + index) * Batch;
                int m = math.min(Batch, Points.Length - start);
                if (m <= 0) return;
                GraphData g = Terrain.Graph;
                GraphCode code = GraphCode.Of(g);
                GraphWorkCounters counters = default;
                int slots = g.LatticeValueCount;
                float* registers = (float*)UnsafeUtility.Malloc(GraphInterpreter.RegisterFloats * sizeof(float), 64, Allocator.Temp);
                float* scratch = (float*)UnsafeUtility.Malloc(GraphInterpreter.ScratchFloats * sizeof(float), 64, Allocator.Temp);
                float* lattice = (float*)UnsafeUtility.Malloc(math.max(1, slots) * Batch * sizeof(float), 64, Allocator.Temp);
                float* layers = (float*)UnsafeUtility.Malloc(Batch * MaxLayers * sizeof(float), 64, Allocator.Temp);
                int* lanePoint = (int*)UnsafeUtility.Malloc(Lanes * sizeof(int), 16, Allocator.Temp);
                int* laneLayer = (int*)UnsafeUtility.Malloc(Lanes * sizeof(int), 16, Allocator.Temp);
                UnsafeUtility.MemClear(registers, GraphInterpreter.RegisterFloats * sizeof(float));

                // The 2D program at the batch's points.
                GraphProgram lp = code.Programs[g.LatticeProgram];
                int padded = (m + 3) & ~3;
                for (int i = 0; i < padded; i++){
                    float2 p = Points[start + math.min(i, m - 1)];
                    registers[i] = p.x;
                    registers[Lanes + i] = 0f;
                    registers[2 * Lanes + i] = p.y;
                }

                code.Run(lp, registers, scratch, m, Seed, ref counters);
                for (int o = 0; o < lp.OutputCount; o++){
                    GraphSlot s = code.Slots[lp.OutputStart + o];
                    float* row = registers + s.Register * Lanes;
                    for (int i = 0; i < m; i++) lattice[s.Slot * Batch + i] = row[i];
                }

                GraphProgram cp = code.Programs[Terrain.CornerProgram];
                int markerRegister = -1;
                for (int o = 0; o < cp.OutputCount; o++){
                    GraphSlot s = code.Slots[cp.OutputStart + o];
                    if (s.Slot == Terrain.Marker) markerRegister = s.Register;
                }

                int h = Terrain.CellHeight;
                int cy = DensityLayout.WetnessY / h;
                float yLo = cy * h, yHi = (cy + 1) * h;
                float inverse = GraphInterpolation.InverseCellHeight(h);
                float ty = (DensityLayout.WetnessY - cy * h) * inverse;
                float scale = 64f * Terrain.NoiseBound;
                int maxLayer = GraphLimits.WorldHeight / h;

                // Per point the layers to compute: the two around y 123 when the early-out cannot decide, and with
                // heights every layer of the Δ window. Lanes fill across points; a full set of lanes runs at once.
                byte* states = stackalloc byte[Batch];
                int* firstLayer = stackalloc int[Batch];
                int* lastLayer = stackalloc int[Batch];
                int lanes = 0;
                for (int i = 0; i < m; i++){
                    float surface = lattice[Terrain.PreliminarySurface * Batch + i];
                    float up = scale / lattice[Terrain.Factor * Batch + i], down = math.min(32f, up);
                    byte state = surface - yHi > down ? (byte)0 : yLo - surface > up ? (byte)1 : (byte)2;
                    int first, last;
                    if (Heights){
                        first = math.clamp((int)math.floor((surface - down) / h), 0, maxLayer);
                        last = math.clamp((int)math.floor((surface + up) / h) + 1, 0, maxLayer);
                        if (state == 2){
                            first = math.min(first, cy);
                            last = math.max(last, cy + 1);
                        }
                    }
                    else if (state == 2){
                        first = cy;
                        last = cy + 1;
                    }
                    else{
                        first = 1;
                        last = 0;
                    }

                    states[i] = state;
                    firstLayer[i] = first;
                    lastLayer[i] = last;
                    for (int l = first; l <= last; l++){
                        lanePoint[lanes] = i;
                        laneLayer[lanes] = l;
                        if (++lanes == Lanes){
                            RunCorners(code, cp, markerRegister, h, start, registers, scratch, lattice, layers, lanePoint, laneLayer, lanes, ref counters);
                            lanes = 0;
                        }
                    }
                }

                if (lanes > 0) RunCorners(code, cp, markerRegister, h, start, registers, scratch, lattice, layers, lanePoint, laneLayer, lanes, ref counters);

                float* column = stackalloc float[BiomeBox.Parameters];
                bool hasLand = Layout.Land.BoxCount > 0;
                for (int i = 0; i < m; i++){
                    float surface = lattice[Terrain.PreliminarySurface * Batch + i];
                    float* d = layers + i * MaxLayers;
                    byte state = states[i];
                    byte wet = state == 2 ? GraphInterpolation.Lerp(d[cy], d[cy + 1], ty) > 0f ? (byte)0 : (byte)1 : state;
                    Wet[start + i] = wet;
                    Surface[start + i] = surface;
                    River[start + i] = RiverSlot < 0 ? 0f : lattice[RiverSlot * Batch + i];

                    if (Heights){
                        // The top voxel: down from the window's top layer, as P4 interpolates between layers; above the
                        // window the terrain is surely air, below it surely solid.
                        int first = firstLayer[i], last = lastLayer[i];
                        int top = first * h - 1;
                        for (int y = math.min(last * h, GraphLimits.WorldHeight) - 1; y >= first * h; y--){
                            int c = y / h;
                            if (GraphInterpolation.Lerp(d[c], d[c + 1], (y - c * h) * inverse) > 0f){
                                top = y;
                                break;
                            }
                        }

                        Top[start + i] = (short)math.min(top, GraphLimits.WorldHeight - 1);
                    }

                    for (int p = 0; p < BiomeBox.Parameters; p++){
                        int s = Terrain.ClimateSlot(p);
                        column[p] = s < 0 ? 0f : lattice[s * Batch + i];
                        if (WithClimate) Climate[p * Points.Length + start + i] = column[p];
                    }

                    byte id = Layout.Main.BiomeAt(column);
                    byte substituted = 0;
                    if (hasLand && wet == 0 && Layout.Water.Contains(id)){
                        id = Layout.Land.BiomeAt(column);
                        substituted = 1;
                    }

                    Biome[start + i] = id;
                    Substituted[start + i] = substituted;
                }

                UnsafeUtility.Free(registers, Allocator.Temp);
                UnsafeUtility.Free(scratch, Allocator.Temp);
                UnsafeUtility.Free(lattice, Allocator.Temp);
                UnsafeUtility.Free(layers, Allocator.Temp);
                UnsafeUtility.Free(lanePoint, Allocator.Temp);
                UnsafeUtility.Free(laneLayer, Allocator.Temp);
            }

            /// <summary>The corner program on <paramref name="n" /> (point, layer) lanes, as P4 runs it on a corner column.</summary>
            private void RunCorners(in GraphCode code, in GraphProgram cp, int markerRegister, int h, int start, float* registers, float* scratch, float* lattice,
                float* layers, int* lanePoint, int* laneLayer, int n, ref GraphWorkCounters counters){
                int padded = (n + 3) & ~3;
                for (int i = 0; i < padded; i++){
                    int k = math.min(i, n - 1);
                    float2 p = Points[start + lanePoint[k]];
                    registers[i] = p.x;
                    registers[Lanes + i] = laneLayer[k] * h;
                    registers[2 * Lanes + i] = p.y;
                }

                for (int s = 0; s < cp.InputCount; s++){
                    GraphSlot slot = code.Slots[cp.InputStart + s];
                    float* row = registers + slot.Register * Lanes;
                    for (int i = 0; i < padded; i++) row[i] = lattice[slot.Slot * Batch + lanePoint[math.min(i, n - 1)]];
                }

                code.Run(cp, registers, scratch, n, Seed, ref counters);
                float* from = registers + markerRegister * Lanes;
                for (int i = 0; i < n; i++) layers[lanePoint[i] * MaxLayers + laneLayer[i]] = from[i];
            }
        }
    }
}
