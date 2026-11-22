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

        /// <summary><see cref="Lerp(float, float, float)" /> lane by lane, with the same operations per lane.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float4 Lerp(float4 a, float4 b, float4 t){
            return a + t * (b - a);
        }

        /// <summary>
        ///     The 16 column values of a 4 × 4 column cell from its four lattice corners (column index lz · 4 + lx).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
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
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Trilinear(float* c00, float* c10, float* c01, float* c11, int cellHeight, int baseY, float* lanes){
            // Four lanes along x at a time; the corner values are read once per row, since the stores into lanes may
            // alias the corner columns for all the compiler knows.
            float inverse = cellHeight == 4 ? 0.25f : 0.125f;
            float4 tx = new(0f, 0.25f, 0.5f, 0.75f);
            for (int ly = 0; ly < GraphLimits.CellBlockHeight; ly++){
                int y = baseY + ly;
                int cy = y / cellHeight;
                float ty = (y - cy * cellHeight) * inverse;
                float4 v00 = Lerp(c00[cy], c10[cy], tx);
                float4 v10 = Lerp(c01[cy], c11[cy], tx);
                float4 v01 = Lerp(c00[cy + 1], c10[cy + 1], tx);
                float4 v11 = Lerp(c01[cy + 1], c11[cy + 1], tx);
                for (int lz = 0; lz < 4; lz++){
                    float tz = lz * 0.25f;
                    *(float4*)(lanes + ly * 16 + lz * 4) = Lerp(Lerp(v00, v10, tz), Lerp(v01, v11, tz), ty);
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
            Evaluate(new InterpretedPrograms());
        }

        /// <summary>The evaluation with the programs run by <typeparamref name="TPrograms" /> (interpreted or generated).</summary>
        internal void Evaluate<TPrograms>(TPrograms programs) where TPrograms : struct, IGraphPrograms{
            GraphWorkCounters counters = default;
            float* registers = (float*)UnsafeUtility.Malloc(GraphInterpreter.RegisterFloats * sizeof(float), 64, Allocator.Temp);
            float* scratch = (float*)UnsafeUtility.Malloc(GraphInterpreter.ScratchFloats * sizeof(float), 64, Allocator.Temp);
            UnsafeUtility.MemClear(registers, GraphInterpreter.RegisterFloats * sizeof(float));
            int lx = Size.x / 4 + 1, lz = Size.y / 4 + 1;
            GraphCode code = GraphCode.Of(Graph);
            float* lattice = (float*)Lattice.GetUnsafePtr();

            if (Graph.LatticeProgram >= 0) RunLattice(programs, code, lattice, registers, scratch, lx, lz, ref counters);
            if (Graph.ColumnProgram >= 0) RunColumns(programs, code, registers, scratch, ref counters);
            if (InterpolateColumns != 0) InterpolateLattice(lattice, lx, lz);
            if (Evaluate3D != 0){
                float* corners = (float*)Corners.GetUnsafePtr();
                if (Graph.CornerProgram4 >= 0) RunCorners(programs, code, Graph.CornerProgram4, 4, lattice, corners, registers, scratch, lx, lz, ref counters);
                if (Graph.CornerProgram8 >= 0) RunCorners(programs, code, Graph.CornerProgram8, 8, lattice, corners, registers, scratch, lx, lz, ref counters);
                if (Graph.VoxelProgram >= 0) RunVoxels(programs, code, lattice, corners, registers, scratch, lx, lz, ref counters);
            }

            Counters[0] = counters;
            UnsafeUtility.Free(registers, Allocator.Temp);
            UnsafeUtility.Free(scratch, Allocator.Temp);
        }

        private static void Fill(float* row, float value, int padded){
            for (int i = 0; i < padded; i++) row[i] = value;
        }

        private void RunLattice<TPrograms>(TPrograms programs, in GraphCode code, float* lattice, float* registers, float* scratch, int lx, int lz,
            ref GraphWorkCounters counters) where TPrograms : struct, IGraphPrograms{
            GraphProgram program = code.Programs[Graph.LatticeProgram];
            for (int z = 0; z < lz; z++)
            for (int x0 = 0; x0 < lx; x0 += Lanes){
                int n = math.min(Lanes, lx - x0);
                int padded = (n + 3) & ~3;
                for (int i = 0; i < padded; i++) registers[i] = Origin.x + 4 * (x0 + math.min(i, n - 1));
                Fill(registers + Lanes, 0f, padded);
                Fill(registers + 2 * Lanes, Origin.y + 4 * z, padded);
                programs.Run(Graph.LatticeProgram, code, registers, scratch, n, Seed, ref counters);
                for (int o = 0; o < program.OutputCount; o++){
                    GraphSlot s = code.Slots[program.OutputStart + o];
                    float* row = registers + s.Register * Lanes;
                    float* to = lattice + (s.Slot * lz + z) * lx + x0;
                    for (int i = 0; i < n; i++) to[i] = row[i];
                }

                counters.LatticePoints += n;
            }
        }

        private void RunColumns<TPrograms>(TPrograms programs, in GraphCode code, float* registers, float* scratch, ref GraphWorkCounters counters)
            where TPrograms : struct, IGraphPrograms{
            GraphProgram program = code.Programs[Graph.ColumnProgram];
            float* columns = (float*)Columns.GetUnsafePtr();
            for (int z = 0; z < Size.y; z++)
            for (int x0 = 0; x0 < Size.x; x0 += Lanes){
                int n = math.min(Lanes, Size.x - x0);
                int padded = (n + 3) & ~3;
                for (int i = 0; i < padded; i++) registers[i] = Origin.x + x0 + math.min(i, n - 1);
                Fill(registers + Lanes, 0f, padded);
                Fill(registers + 2 * Lanes, Origin.y + z, padded);
                programs.Run(Graph.ColumnProgram, code, registers, scratch, n, Seed, ref counters);
                for (int o = 0; o < program.OutputCount; o++){
                    GraphSlot s = code.Slots[program.OutputStart + o];
                    float* row = registers + s.Register * Lanes;
                    float* to = columns + (s.Slot * Size.y + z) * Size.x + x0;
                    for (int i = 0; i < n; i++) to[i] = row[i];
                }

                counters.ColumnPoints += n;
            }
        }

        private void InterpolateLattice(float* lattice, int lx, int lz){
            float* columns = stackalloc float[16];
            float* to = (float*)Columns2D.GetUnsafePtr();
            for (int v = 0; v < Graph.LatticeValueCount; v++)
            for (int cz = 0; cz < Size.y / 4; cz++)
            for (int cx = 0; cx < Size.x / 4; cx++){
                float* l = lattice + (v * lz + cz) * lx + cx;
                GraphInterpolation.Bilinear(l[0], l[1], l[lx], l[lx + 1], columns);
                for (int i = 0; i < 16; i++){
                    int x = cx * 4 + (i & 3), z = cz * 4 + (i >> 2);
                    to[(v * Size.y + z) * Size.x + x] = columns[i];
                }
            }
        }

        private void RunCorners<TPrograms>(TPrograms programs, in GraphCode code, int programIndex, int cellHeight, float* lattice, float* corners, float* registers, float* scratch,
            int lx, int lz, ref GraphWorkCounters counters) where TPrograms : struct, IGraphPrograms{
            GraphProgram program = code.Programs[programIndex];
            int lanes = GraphLimits.WorldHeight / cellHeight + 1;
            int padded = (lanes + 3) & ~3;
            for (int i = 0; i < padded; i++) registers[Lanes + i] = math.min(i, lanes - 1) * cellHeight;
            for (int z = 0; z < lz; z++)
            for (int x = 0; x < lx; x++){
                Fill(registers, Origin.x + 4 * x, padded);
                Fill(registers + 2 * Lanes, Origin.y + 4 * z, padded);
                for (int i = 0; i < program.InputCount; i++){
                    GraphSlot s = code.Slots[program.InputStart + i];
                    Fill(registers + s.Register * Lanes, lattice[(s.Slot * lz + z) * lx + x], padded);
                }

                programs.Run(programIndex, code, registers, scratch, lanes, Seed, ref counters);
                for (int o = 0; o < program.OutputCount; o++){
                    GraphSlot s = code.Slots[program.OutputStart + o];
                    float* row = registers + s.Register * Lanes;
                    float* to = corners + ((s.Slot * lz + z) * lx + x) * GraphInterpolation.CornerStride;
                    for (int i = 0; i < lanes; i++) to[i] = row[i];
                }

                counters.Corners += lanes;
            }
        }

        private void RunVoxels<TPrograms>(TPrograms programs, in GraphCode code, float* lattice, float* corners, float* registers, float* scratch, int lx, int lz,
            ref GraphWorkCounters counters) where TPrograms : struct, IGraphPrograms{
            GraphProgram program = code.Programs[Graph.VoxelProgram];
            GraphSlot* inputs = code.Slots + program.InputStart;
            GraphSlot* outputs = code.Slots + program.OutputStart;
            int* cellHeights = (int*)Graph.MarkerCellHeights.GetUnsafeReadOnlyPtr();
            float* voxels = (float*)Voxels.GetUnsafePtr();
            float* columns = (float*)UnsafeUtility.Malloc(math.max(1, program.InputCount) * 16 * sizeof(float), 16, Allocator.Temp);
            const int stride = GraphInterpolation.CornerStride;
            const int height = GraphLimits.WorldHeight;
            int markers = 0;
            for (int i = 0; i < program.InputCount; i++)
                if (inputs[i].Source == GraphSlotSource.Marker)
                    markers++;

            for (int cz = 0; cz < Size.y / 4; cz++)
            for (int cx = 0; cx < Size.x / 4; cx++){
                for (int i = 0; i < program.InputCount; i++){
                    if (inputs[i].Source != GraphSlotSource.Lattice) continue;
                    float* l = lattice + (inputs[i].Slot * lz + cz) * lx + cx;
                    GraphInterpolation.Bilinear(l[0], l[1], l[lx], l[lx + 1], columns + i * 16);
                }

                for (int lane = 0; lane < Lanes; lane++){
                    registers[lane] = Origin.x + cx * 4 + (lane & 3);
                    registers[2 * Lanes + lane] = Origin.y + cz * 4 + ((lane >> 2) & 3);
                }

                for (int cy = 0; cy < height / GraphLimits.CellBlockHeight; cy++){
                    int baseY = cy * GraphLimits.CellBlockHeight;
                    for (int lane = 0; lane < Lanes; lane++) registers[Lanes + lane] = baseY + (lane >> 4);
                    for (int i = 0; i < program.InputCount; i++){
                        GraphSlot s = inputs[i];
                        float* row = registers + s.Register * Lanes;
                        if (s.Source == GraphSlotSource.Lattice){
                            float4* c = (float4*)(columns + i * 16);
                            for (int q = 0; q < Lanes / 4; q++) ((float4*)row)[q] = c[q & 3];
                        }
                        else{
                            float* m = corners + (s.Slot * lz + cz) * lx * stride;
                            float* c00 = m + cx * stride, c10 = m + (cx + 1) * stride, c01 = m + (lx + cx) * stride, c11 = m + (lx + cx + 1) * stride;
                            // A constant cell height lets Burst turn the division by it into a shift.
                            if (cellHeights[s.Slot] == 4) GraphInterpolation.Trilinear(c00, c10, c01, c11, 4, baseY, row);
                            else GraphInterpolation.Trilinear(c00, c10, c01, c11, 8, baseY, row);
                        }
                    }

                    programs.Run(Graph.VoxelProgram, code, registers, scratch, Lanes, Seed, ref counters);
                    for (int o = 0; o < program.OutputCount; o++){
                        float* row = registers + outputs[o].Register * Lanes;
                        float* to = voxels + ((outputs[o].Slot * Size.y + cz * 4) * Size.x + cx * 4) * height + baseY;
                        for (int vz = 0; vz < 4; vz++)
                        for (int vx = 0; vx < 4; vx++){
                            float* column = to + (vz * Size.x + vx) * height;
                            for (int vy = 0; vy < GraphLimits.CellBlockHeight; vy++) column[vy] = row[vy * 16 + vz * 4 + vx];
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

    /// <summary>Raw pointers to a compiled graph's tables, taken once per job instead of per array access.</summary>
    internal unsafe struct GraphCode{
        public GraphInstruction* Instructions;
        public GraphProgram* Programs;
        public GraphSlot* Slots;
        public NoiseEntry* Entries;
        public NoiseOctave* Octaves;
        public SplinePoint* Points;

        public static GraphCode Of(in GraphData graph){
            return new GraphCode{
                Instructions = (GraphInstruction*)graph.Instructions.GetUnsafeReadOnlyPtr(),
                Programs = (GraphProgram*)graph.Programs.GetUnsafeReadOnlyPtr(),
                Slots = (GraphSlot*)graph.Slots.GetUnsafeReadOnlyPtr(),
                Entries = (NoiseEntry*)graph.Entries.GetUnsafeReadOnlyPtr(),
                Octaves = (NoiseOctave*)graph.Octaves.GetUnsafeReadOnlyPtr(),
                Points = (SplinePoint*)graph.Points.GetUnsafeReadOnlyPtr()
            };
        }

        public void Run(in GraphProgram program, float* registers, float* scratch, int lanes, uint seed, ref GraphWorkCounters counters){
            GraphInterpreter.Run(program, Instructions, Entries, Octaves, Points, registers, scratch, lanes, seed, ref counters);
        }
    }

    /// <summary>
    ///     Runs the programs of a compiled graph on the register file: the interpreter, or code generated for one
    ///     graph. Either reads the input rows and leaves the outputs in their rows, with the same values bit for bit.
    /// </summary>
    internal unsafe interface IGraphPrograms{
        /// <summary>Code hash of the graph the programs belong to (<see cref="CompiledNoiseGraph.CodeHash" />); 0 for any graph.</summary>
        ulong CodeHash{ get; }

        /// <summary>Table sizes of the graph the programs belong to; checked with the hash before generated code runs.</summary>
        GraphTableSizes Sizes{ get; }

        void Run(int program, in GraphCode code, float* registers, float* scratch, int lanes, uint seed, ref GraphWorkCounters counters);
    }

    /// <summary>The interpreter, for any graph.</summary>
    internal unsafe struct InterpretedPrograms : IGraphPrograms{
        public ulong CodeHash => 0;
        public GraphTableSizes Sizes => default;

        public void Run(int program, in GraphCode code, float* registers, float* scratch, int lanes, uint seed, ref GraphWorkCounters counters){
            code.Run(code.Programs[program], registers, scratch, lanes, seed, ref counters);
        }
    }

    /// <summary>
    ///     <see cref="GraphRegionJob" /> with the programs of generated code. Each generated type registers its job
    ///     instance with <see cref="RegisterGenericJobTypeAttribute" />.
    /// </summary>
    [BurstCompile(FloatMode = FloatMode.Strict, CompileSynchronously = true)]
    internal struct GeneratedRegionJob<TPrograms> : IJob where TPrograms : struct, IGraphPrograms{
        public GraphRegionJob Region;

        public void Execute(){
            Region.Evaluate(default(TPrograms));
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

        /// <summary>
        ///     True when <paramref name="graph" /> is the graph the shipped generated code was made from (same code
        ///     hash and table sizes); any other graph, a mod's or an edited one, runs in the interpreter.
        /// </summary>
        public static bool HasGeneratedCode(CompiledNoiseGraph graph){
            return Fits<ShippedNoiseGraphCode>(graph);
        }

        /// <summary>
        ///     True when <typeparamref name="TPrograms" /> was generated from <paramref name="graph" />: the code hash
        ///     and the table sizes match, so a hash collision alone never runs code against tables it does not fit.
        /// </summary>
        internal static bool Fits<TPrograms>(CompiledNoiseGraph graph) where TPrograms : struct, IGraphPrograms{
            TPrograms programs = default;
            return programs.CodeHash == graph.CodeHash && programs.Sizes.Equals(graph.Sizes);
        }

        /// <summary>
        ///     Evaluates a region synchronously (see <see cref="GraphRegionJob" />): with the generated code where
        ///     <see cref="HasGeneratedCode" />, else (or with <paramref name="interpreterOnly" />) in the interpreter.
        /// </summary>
        public static GraphRegionResult EvaluateRegion(CompiledNoiseGraph graph, uint seed, int2 origin, int2 size, bool evaluate3D,
            bool interpolateColumns = false, bool interpreterOnly = false){
            GraphRegionResult r = Allocate(graph, origin, size, evaluate3D, interpolateColumns, out GraphRegionJob job);
            job.Seed = seed;
            if (!interpreterOnly && HasGeneratedCode(graph)) new GeneratedRegionJob<ShippedNoiseGraphCode>{ Region = job }.Run();
            else job.Run();
            return r;
        }

        /// <summary>
        ///     Evaluates a region with the generated code <typeparamref name="TPrograms" />, which must have been made
        ///     from <paramref name="graph" />.
        /// </summary>
        internal static GraphRegionResult EvaluateRegion<TPrograms>(CompiledNoiseGraph graph, uint seed, int2 origin, int2 size, bool evaluate3D,
            bool interpolateColumns = false) where TPrograms : struct, IGraphPrograms{
            if (!Fits<TPrograms>(graph))
                throw new ArgumentException($"{typeof(TPrograms).Name} was generated from another graph; regenerate it.");
            GraphRegionResult r = Allocate(graph, origin, size, evaluate3D, interpolateColumns, out GraphRegionJob job);
            job.Seed = seed;
            new GeneratedRegionJob<TPrograms>{ Region = job }.Run();
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
