using System;
using System.Runtime.CompilerServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;

namespace Engine.Scripts.WorldGen.Graph{
    /// <summary>
    ///     The interpolation of 2D values per column and of marker corners per voxel, shared by the region job and
    ///     hand-written comparison code. lerp(a, b, t) is a + t (b − a); x first, then z, then y.
    /// </summary>
    public static unsafe class GraphInterpolation{
        /// <summary>Corner slots per corner column and marker: y 0 to the world height at cell height 4.</summary>
        public const int CornerStride = GraphLimits.WorldHeight / 4 + 1;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Lerp(float a, float b, float t){
            return a + t * (b - a);
        }

        /// <summary>
        ///     The 16 column values of a 4 × 4 column cell from its four lattice corners (column index lz · 4 + lx).
        /// </summary>
        public static void Bilinear(float v00, float v10, float v01, float v11, float* columns){
            for (int lz = 0; lz < 4; lz++){
                float tz = lz * 0.25f;
                for (int lx = 0; lx < 4; lx++){
                    float tx = lx * 0.25f;
                    columns[lz * 4 + lx] = Lerp(Lerp(v00, v10, tx), Lerp(v01, v11, tx), tz);
                }
            }
        }

        /// <summary>
        ///     Trilinear marker values for the 128 voxels of a cell (lane = ly · 16 + lz · 4 + lx, y = baseY + ly) from
        ///     the corner columns c00, c10, c01, c11 (x then z), each <see cref="CornerStride" /> long.
        /// </summary>
        public static void Trilinear(float* c00, float* c10, float* c01, float* c11, int cellHeight, int baseY, float* lanes){
            float inverse = cellHeight == 4 ? 0.25f : 0.125f;
            for (int ly = 0; ly < GraphLimits.CellBlockHeight; ly++){
                int y = baseY + ly;
                int cy = y / cellHeight;
                float ty = (y - cy * cellHeight) * inverse;
                for (int lz = 0; lz < 4; lz++){
                    float tz = lz * 0.25f;
                    for (int lx = 0; lx < 4; lx++){
                        float tx = lx * 0.25f;
                        float v00 = Lerp(c00[cy], c10[cy], tx);
                        float v10 = Lerp(c01[cy], c11[cy], tx);
                        float v01 = Lerp(c00[cy + 1], c10[cy + 1], tx);
                        float v11 = Lerp(c01[cy + 1], c11[cy + 1], tx);
                        lanes[ly * 16 + lz * 4 + lx] = Lerp(Lerp(v00, v10, tz), Lerp(v01, v11, tz), ty);
                    }
                }
            }
        }
    }

    /// <summary>
    ///     Evaluates a compiled graph over a region of columns: the lattice program on the region's stride-4
    ///     lattice, the column program for every column, and (optionally) the corner programs on every corner
    ///     column and the voxel program on every 4 × 8 × 4 cell of the full height. No skipping: every cell runs.
    /// </summary>
    [BurstCompile(FloatMode = FloatMode.Strict, CompileSynchronously = true)]
    public unsafe struct GraphRegionJob : IJob{
        public GraphData Graph;
        public uint Seed;

        /// <summary>First column of the region (multiples of 4).</summary>
        public int2 Origin;

        /// <summary>Columns of the region along x and z (multiples of 4).</summary>
        public int2 Size;

        /// <summary>1: run the corner and voxel programs.</summary>
        public int Evaluate3D;

        /// <summary>1: write every lattice value interpolated per column into <see cref="Columns2D" />.</summary>
        public int InterpolateColumns;

        /// <summary>[value][lz][lx] on the (Size / 4 + 1)² lattice.</summary>
        public NativeArray<float> Lattice;

        /// <summary>[column output][z][x].</summary>
        [WriteOnly] public NativeArray<float> Columns;

        /// <summary>[lattice value][z][x], bilinear per column.</summary>
        [WriteOnly] public NativeArray<float> Columns2D;

        /// <summary>[marker][lz][lx][cy], <see cref="GraphInterpolation.CornerStride" /> per corner column.</summary>
        public NativeArray<float> Corners;

        /// <summary>[voxel output][z][x][y].</summary>
        [WriteOnly] public NativeArray<float> Voxels;

        public NativeArray<GraphWorkCounters> Counters;

        private const int Lanes = GraphLimits.MaxLanes;

        public void Execute(){
            GraphWorkCounters counters = default;
            float* registers = (float*)UnsafeUtility.Malloc(GraphInterpreter.RegisterFloats * sizeof(float), 64, Allocator.Temp);
            float* scratch = (float*)UnsafeUtility.Malloc(GraphInterpreter.ScratchFloats * sizeof(float), 64, Allocator.Temp);
            UnsafeUtility.MemClear(registers, GraphInterpreter.RegisterFloats * sizeof(float));
            int lx = Size.x / 4 + 1, lz = Size.y / 4 + 1;

            if (Graph.LatticeProgram >= 0) RunLattice(registers, scratch, lx, lz, ref counters);
            if (Graph.ColumnProgram >= 0) RunColumns(registers, scratch, ref counters);
            if (InterpolateColumns != 0) InterpolateLattice(lx, lz);
            if (Evaluate3D != 0){
                if (Graph.CornerProgram4 >= 0) RunCorners(Graph.CornerProgram4, 4, registers, scratch, lx, lz, ref counters);
                if (Graph.CornerProgram8 >= 0) RunCorners(Graph.CornerProgram8, 8, registers, scratch, lx, lz, ref counters);
                if (Graph.VoxelProgram >= 0) RunVoxels(registers, scratch, lx, lz, ref counters);
            }

            Counters[0] = counters;
            UnsafeUtility.Free(registers, Allocator.Temp);
            UnsafeUtility.Free(scratch, Allocator.Temp);
        }

        private void Run(int programIndex, float* registers, float* scratch, int lanes, ref GraphWorkCounters counters){
            GraphProgram program = Graph.Programs[programIndex];
            GraphInterpreter.Run(program, (GraphInstruction*)Graph.Instructions.GetUnsafeReadOnlyPtr(), (NoiseEntry*)Graph.Entries.GetUnsafeReadOnlyPtr(),
                (NoiseOctave*)Graph.Octaves.GetUnsafeReadOnlyPtr(), (SplinePoint*)Graph.Points.GetUnsafeReadOnlyPtr(), registers, scratch, lanes, Seed,
                ref counters);
        }

        private static void Fill(float* row, float value, int padded){
            for (int i = 0; i < padded; i++) row[i] = value;
        }

        private void RunLattice(float* registers, float* scratch, int lx, int lz, ref GraphWorkCounters counters){
            GraphProgram program = Graph.Programs[Graph.LatticeProgram];
            for (int z = 0; z < lz; z++)
            for (int x0 = 0; x0 < lx; x0 += Lanes){
                int n = math.min(Lanes, lx - x0);
                int padded = (n + 3) & ~3;
                for (int i = 0; i < padded; i++) registers[i] = Origin.x + 4 * (x0 + math.min(i, n - 1));
                Fill(registers + Lanes, 0f, padded);
                Fill(registers + 2 * Lanes, Origin.y + 4 * z, padded);
                Run(Graph.LatticeProgram, registers, scratch, n, ref counters);
                for (int o = 0; o < program.OutputCount; o++){
                    GraphSlot s = Graph.Slots[program.OutputStart + o];
                    float* row = registers + s.Register * Lanes;
                    for (int i = 0; i < n; i++) Lattice[(s.Slot * lz + z) * lx + x0 + i] = row[i];
                }

                counters.LatticePoints += n;
            }
        }

        private void RunColumns(float* registers, float* scratch, ref GraphWorkCounters counters){
            GraphProgram program = Graph.Programs[Graph.ColumnProgram];
            for (int z = 0; z < Size.y; z++)
            for (int x0 = 0; x0 < Size.x; x0 += Lanes){
                int n = math.min(Lanes, Size.x - x0);
                int padded = (n + 3) & ~3;
                for (int i = 0; i < padded; i++) registers[i] = Origin.x + x0 + math.min(i, n - 1);
                Fill(registers + Lanes, 0f, padded);
                Fill(registers + 2 * Lanes, Origin.y + z, padded);
                Run(Graph.ColumnProgram, registers, scratch, n, ref counters);
                for (int o = 0; o < program.OutputCount; o++){
                    GraphSlot s = Graph.Slots[program.OutputStart + o];
                    float* row = registers + s.Register * Lanes;
                    for (int i = 0; i < n; i++) Columns[(s.Slot * Size.y + z) * Size.x + x0 + i] = row[i];
                }

                counters.ColumnPoints += n;
            }
        }

        private void InterpolateLattice(int lx, int lz){
            float* columns = stackalloc float[16];
            for (int v = 0; v < Graph.LatticeValueCount; v++)
            for (int cz = 0; cz < Size.y / 4; cz++)
            for (int cx = 0; cx < Size.x / 4; cx++){
                GraphInterpolation.Bilinear(Lattice[(v * lz + cz) * lx + cx], Lattice[(v * lz + cz) * lx + cx + 1],
                    Lattice[(v * lz + cz + 1) * lx + cx], Lattice[(v * lz + cz + 1) * lx + cx + 1], columns);
                for (int i = 0; i < 16; i++){
                    int x = cx * 4 + (i & 3), z = cz * 4 + (i >> 2);
                    Columns2D[(v * Size.y + z) * Size.x + x] = columns[i];
                }
            }
        }

        private void RunCorners(int programIndex, int cellHeight, float* registers, float* scratch, int lx, int lz, ref GraphWorkCounters counters){
            GraphProgram program = Graph.Programs[programIndex];
            int lanes = GraphLimits.WorldHeight / cellHeight + 1;
            int padded = (lanes + 3) & ~3;
            for (int i = 0; i < padded; i++) registers[Lanes + i] = math.min(i, lanes - 1) * cellHeight;
            for (int z = 0; z < lz; z++)
            for (int x = 0; x < lx; x++){
                Fill(registers, Origin.x + 4 * x, padded);
                Fill(registers + 2 * Lanes, Origin.y + 4 * z, padded);
                for (int i = 0; i < program.InputCount; i++){
                    GraphSlot s = Graph.Slots[program.InputStart + i];
                    Fill(registers + s.Register * Lanes, Lattice[(s.Slot * lz + z) * lx + x], padded);
                }

                Run(programIndex, registers, scratch, lanes, ref counters);
                for (int o = 0; o < program.OutputCount; o++){
                    GraphSlot s = Graph.Slots[program.OutputStart + o];
                    float* row = registers + s.Register * Lanes;
                    int at = ((s.Slot * lz + z) * lx + x) * GraphInterpolation.CornerStride;
                    for (int i = 0; i < lanes; i++) Corners[at + i] = row[i];
                }

                counters.Corners += lanes;
            }
        }

        private void RunVoxels(float* registers, float* scratch, int lx, int lz, ref GraphWorkCounters counters){
            GraphProgram program = Graph.Programs[Graph.VoxelProgram];
            float* columns = (float*)UnsafeUtility.Malloc(math.max(1, program.InputCount) * 16 * sizeof(float), 16, Allocator.Temp);
            float* corners = (float*)Corners.GetUnsafeReadOnlyPtr();
            const int stride = GraphInterpolation.CornerStride;
            int markers = 0;
            for (int i = 0; i < program.InputCount; i++)
                if (Graph.Slots[program.InputStart + i].Source == GraphSlotSource.Marker)
                    markers++;

            for (int cz = 0; cz < Size.y / 4; cz++)
            for (int cx = 0; cx < Size.x / 4; cx++){
                for (int i = 0; i < program.InputCount; i++){
                    GraphSlot s = Graph.Slots[program.InputStart + i];
                    if (s.Source != GraphSlotSource.Lattice) continue;
                    int at = (s.Slot * lz + cz) * lx + cx;
                    GraphInterpolation.Bilinear(Lattice[at], Lattice[at + 1], Lattice[at + lx], Lattice[at + lx + 1], columns + i * 16);
                }

                for (int lane = 0; lane < Lanes; lane++){
                    registers[lane] = Origin.x + cx * 4 + (lane & 3);
                    registers[2 * Lanes + lane] = Origin.y + cz * 4 + ((lane >> 2) & 3);
                }

                for (int cy = 0; cy < GraphLimits.WorldHeight / GraphLimits.CellBlockHeight; cy++){
                    int baseY = cy * GraphLimits.CellBlockHeight;
                    for (int lane = 0; lane < Lanes; lane++) registers[Lanes + lane] = baseY + (lane >> 4);
                    for (int i = 0; i < program.InputCount; i++){
                        GraphSlot s = Graph.Slots[program.InputStart + i];
                        float* row = registers + s.Register * Lanes;
                        if (s.Source == GraphSlotSource.Lattice){
                            float* c = columns + i * 16;
                            for (int lane = 0; lane < Lanes; lane++) row[lane] = c[lane & 15];
                        }
                        else{
                            float* m = corners + (s.Slot * lz + cz) * lx * stride;
                            GraphInterpolation.Trilinear(m + cx * stride, m + (cx + 1) * stride, m + (lx + cx) * stride, m + (lx + cx + 1) * stride,
                                Graph.MarkerCellHeights[s.Slot], baseY, row);
                        }
                    }

                    Run(Graph.VoxelProgram, registers, scratch, Lanes, ref counters);
                    for (int o = 0; o < program.OutputCount; o++){
                        GraphSlot s = Graph.Slots[program.OutputStart + o];
                        float* row = registers + s.Register * Lanes;
                        for (int lane = 0; lane < Lanes; lane++){
                            int x = cx * 4 + (lane & 3), z = cz * 4 + ((lane >> 2) & 3), y = baseY + (lane >> 4);
                            Voxels[((s.Slot * Size.y + z) * Size.x + x) * GraphLimits.WorldHeight + y] = row[lane];
                        }
                    }

                    counters.Voxels += Lanes;
                    counters.Cells++;
                    counters.InterpolatedValues += (long)markers * Lanes;
                }
            }

            UnsafeUtility.Free(columns, Allocator.Temp);
        }
    }

    /// <summary>
    ///     Runs the lattice program at arbitrary points (x, z): exact 2D values anywhere, for the preview and tests.
    /// </summary>
    [BurstCompile(FloatMode = FloatMode.Strict, CompileSynchronously = true)]
    public unsafe struct GraphSampleJob : IJob{
        public GraphData Graph;
        public uint Seed;
        [ReadOnly] public NativeArray<float2> Points;

        /// <summary>[lattice value][point].</summary>
        [WriteOnly] public NativeArray<float> Values;

        public NativeArray<GraphWorkCounters> Counters;

        public void Execute(){
            if (Graph.LatticeProgram < 0) return;
            GraphWorkCounters counters = default;
            const int lanes = GraphLimits.MaxLanes;
            float* registers = (float*)UnsafeUtility.Malloc(GraphInterpreter.RegisterFloats * sizeof(float), 64, Allocator.Temp);
            float* scratch = (float*)UnsafeUtility.Malloc(GraphInterpreter.ScratchFloats * sizeof(float), 64, Allocator.Temp);
            UnsafeUtility.MemClear(registers, GraphInterpreter.RegisterFloats * sizeof(float));
            GraphProgram program = Graph.Programs[Graph.LatticeProgram];
            int count = Points.Length;
            for (int p0 = 0; p0 < count; p0 += lanes){
                int n = math.min(lanes, count - p0);
                int padded = (n + 3) & ~3;
                for (int i = 0; i < padded; i++){
                    float2 p = Points[p0 + math.min(i, n - 1)];
                    registers[i] = p.x;
                    registers[lanes + i] = 0f;
                    registers[2 * lanes + i] = p.y;
                }

                GraphInterpreter.Run(program, (GraphInstruction*)Graph.Instructions.GetUnsafeReadOnlyPtr(), (NoiseEntry*)Graph.Entries.GetUnsafeReadOnlyPtr(),
                    (NoiseOctave*)Graph.Octaves.GetUnsafeReadOnlyPtr(), (SplinePoint*)Graph.Points.GetUnsafeReadOnlyPtr(), registers, scratch, n, Seed,
                    ref counters);
                for (int o = 0; o < program.OutputCount; o++){
                    GraphSlot s = Graph.Slots[program.OutputStart + o];
                    float* row = registers + s.Register * lanes;
                    for (int i = 0; i < n; i++) Values[s.Slot * count + p0 + i] = row[i];
                }

                counters.LatticePoints += n;
            }

            Counters[0] = counters;
            UnsafeUtility.Free(registers, Allocator.Temp);
            UnsafeUtility.Free(scratch, Allocator.Temp);
        }
    }

    /// <summary>Values of a region evaluation; owns native memory.</summary>
    public sealed class GraphRegionResult : IDisposable{
        public int2 Origin, Size;
        public int LatticeWidth, LatticeDepth;
        public NativeArray<float> Lattice, Columns, Columns2D, Corners, Voxels;
        public NativeArray<GraphWorkCounters> CounterStore;

        public GraphWorkCounters Counters => CounterStore[0];

        /// <summary>Lattice value <paramref name="value" /> at lattice point (lx, lz) = column (Origin + 4 · l).</summary>
        public float LatticeValue(int value, int lx, int lz){
            return Lattice[(value * LatticeDepth + lz) * LatticeWidth + lx];
        }

        public float Column(int output, int x, int z){
            return Columns[(output * Size.y + z) * Size.x + x];
        }

        public float Column2D(int value, int x, int z){
            return Columns2D[(value * Size.y + z) * Size.x + x];
        }

        public float Corner(int marker, int lx, int lz, int cy){
            return Corners[((marker * LatticeDepth + lz) * LatticeWidth + lx) * GraphInterpolation.CornerStride + cy];
        }

        /// <summary>Voxel output <paramref name="output" /> at region column (x, z) and height y.</summary>
        public float Voxel(int output, int x, int y, int z){
            return Voxels[((output * Size.y + z) * Size.x + x) * GraphLimits.WorldHeight + y];
        }

        public void Dispose(){
            if (Lattice.IsCreated) Lattice.Dispose();
            if (Columns.IsCreated) Columns.Dispose();
            if (Columns2D.IsCreated) Columns2D.Dispose();
            if (Corners.IsCreated) Corners.Dispose();
            if (Voxels.IsCreated) Voxels.Dispose();
            if (CounterStore.IsCreated) CounterStore.Dispose();
        }
    }

    /// <summary>Managed entry points that allocate buffers, run the evaluation jobs and return the values.</summary>
    public static class GraphEvaluator{
        /// <summary>
        ///     Allocates the buffers of a region evaluation and returns them with a job ready to run or schedule.
        ///     <paramref name="origin" /> and <paramref name="size" /> must be multiples of 4.
        /// </summary>
        public static GraphRegionResult Allocate(CompiledNoiseGraph graph, int2 origin, int2 size, bool evaluate3D, bool interpolateColumns,
            out GraphRegionJob job, Allocator allocator = Allocator.TempJob){
            if (origin.x % 4 != 0 || origin.y % 4 != 0 || size.x % 4 != 0 || size.y % 4 != 0 || size.x <= 0 || size.y <= 0)
                throw new ArgumentException("Region origin and size must be multiples of 4 (size above 0).");
            GraphData d = graph.Data;
            int lx = size.x / 4 + 1, lz = size.y / 4 + 1;
            int columns = size.x * size.y;
            GraphRegionResult r = new(){
                Origin = origin,
                Size = size,
                LatticeWidth = lx,
                LatticeDepth = lz,
                Lattice = new NativeArray<float>(math.max(1, d.LatticeValueCount * lx * lz), allocator),
                Columns = new NativeArray<float>(math.max(1, d.ColumnOutputCount * columns), allocator),
                Columns2D = new NativeArray<float>(interpolateColumns ? math.max(1, d.LatticeValueCount * columns) : 1, allocator),
                Corners = new NativeArray<float>(evaluate3D ? math.max(1, d.MarkerCount * lx * lz * GraphInterpolation.CornerStride) : 1, allocator),
                Voxels = new NativeArray<float>(evaluate3D ? math.max(1, d.VoxelOutputCount * columns * GraphLimits.WorldHeight) : 1, allocator),
                CounterStore = new NativeArray<GraphWorkCounters>(1, allocator)
            };
            job = new GraphRegionJob{
                Graph = d,
                Origin = origin,
                Size = size,
                Evaluate3D = evaluate3D ? 1 : 0,
                InterpolateColumns = interpolateColumns ? 1 : 0,
                Lattice = r.Lattice,
                Columns = r.Columns,
                Columns2D = r.Columns2D,
                Corners = r.Corners,
                Voxels = r.Voxels,
                Counters = r.CounterStore
            };
            return r;
        }

        /// <summary>Evaluates a region synchronously (see <see cref="GraphRegionJob" />).</summary>
        public static GraphRegionResult EvaluateRegion(CompiledNoiseGraph graph, uint seed, int2 origin, int2 size, bool evaluate3D,
            bool interpolateColumns = false){
            GraphRegionResult r = Allocate(graph, origin, size, evaluate3D, interpolateColumns, out GraphRegionJob job);
            job.Seed = seed;
            job.Run();
            return r;
        }

        /// <summary>Exact 2D values at <paramref name="points" />: [lattice value][point].</summary>
        public static float[] SampleLattice(CompiledNoiseGraph graph, uint seed, float2[] points, out GraphWorkCounters counters){
            using NativeArray<float2> p = new(points, Allocator.TempJob);
            using NativeArray<float> values = new(math.max(1, graph.Data.LatticeValueCount * points.Length), Allocator.TempJob);
            using NativeArray<GraphWorkCounters> c = new(1, Allocator.TempJob);
            new GraphSampleJob{ Graph = graph.Data, Seed = seed, Points = p, Values = values, Counters = c }.Run();
            counters = c[0];
            return values.ToArray();
        }
    }
}
