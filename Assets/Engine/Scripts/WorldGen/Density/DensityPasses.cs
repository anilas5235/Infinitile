using System;
using Engine.Scripts.WorldGen.Graph;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;

namespace Engine.Scripts.WorldGen.Density{
    /// <summary>Sizes of the density passes for one chunk of 32 × 256 × 32.</summary>
    public static class DensityLayout{
        public const int ChunkSize = 32;
        public const int Height = GraphLimits.WorldHeight;

        /// <summary>Horizontal cell size and lattice stride.</summary>
        public const int CellXZ = GraphLimits.LatticeStride;

        /// <summary>Height of a voxel block (the unit of the uniform test): one cell of height 8, two of height 4.</summary>
        public const int BlockHeight = GraphLimits.CellBlockHeight;

        public const int BlocksPerColumn = Height / BlockHeight;

        /// <summary>Corner columns along x and z of a chunk.</summary>
        public const int CornerColumns = ChunkSize / CellXZ + 1;

        /// <summary>Cell columns along x and z of a chunk.</summary>
        public const int CellColumns = ChunkSize / CellXZ;

        /// <summary>Most corner layers of a corner column (cell height 4).</summary>
        public const int MaxLayers = Height / 4 + 1;

        /// <summary>Blocks the lattice reaches before the chunk on x and z: the padded lattice covers chunk ± 27 (§2.2).</summary>
        public const int Padding = 28;

        /// <summary>Lattice points along x and z of the padded lattice: 23.</summary>
        public const int LatticeSize = (ChunkSize + 2 * Padding) / CellXZ + 1;

        /// <summary>Lattice index of the chunk's first corner column.</summary>
        public const int ChunkLatticeOffset = Padding / CellXZ;

        public const int Voxels = ChunkSize * Height * ChunkSize;
        public const int Columns = ChunkSize * ChunkSize;

        /// <summary>
        ///     Columns the biome passes reach before the chunk on x and z (P2, §6): the blend kernel's radius (R = 16,
        ///     §12), so P3 reads the hard biome of every tap. V6b's flora ring widens it.
        /// </summary>
        public const int BiomePadding = 16;

        /// <summary>Columns along x and z of the padded biome map: 64.</summary>
        public const int BiomeSpan = ChunkSize + 2 * BiomePadding;

        /// <summary>Lattice points along x and z of the padded biome map: 17.</summary>
        public const int BiomeLattice = BiomeSpan / CellXZ + 1;

        /// <summary>Lattice index of the biome map's first corner column.</summary>
        public const int BiomeLatticeOffset = (Padding - BiomePadding) / CellXZ;

        /// <summary>Cells along x and z of the padded biome map: 16.</summary>
        public const int BiomeCells = BiomeSpan / CellXZ;

        /// <summary>The y of the wetness test (AD-08): the top water layer.</summary>
        public const int WetnessY = 123;
    }

    /// <summary>
    ///     Scratch of the density passes for one chunk, allocated once per chunk (W2-ARC-03) and reused by every
    ///     pass. <see cref="Solid" /> is [x][z][y] (y contiguous, as the chunk's voxel buffer), 1 for terrain.
    /// </summary>
    public unsafe struct DensityChunkBuffers : IDisposable{
        public float* Registers, Scratch;

        /// <summary>[value][lz][lx] on the padded lattice.</summary>
        public float* Lattice;

        /// <summary>Δup and Δdown per lattice point.</summary>
        public float* DeltaUp, DeltaDown;

        /// <summary>[corner column (z · 9 + x)][layer] of the terrain marker.</summary>
        public float* Corners;

        /// <summary>Own corner-layer range per corner column (outside: the sign of the bounds), and the computed range.</summary>
        public int* OwnLow, OwnHigh, NeedLow, NeedHigh;

        /// <summary>First and last block of each cell column that the ranges leave open.</summary>
        public int* FirstOpen, LastOpen;

        public byte* Solid;

        /// <summary>Top terrain voxel per column (x · 32 + z), −1 when the column has none (§5.6).</summary>
        public short* Heightmap;

        /// <summary>128 interpolated values of one block.</summary>
        public float* Block;

        /// <summary>[z][x] over the padded biome map (64²): the hard biome (after the dry-column rule) and the wet flag.</summary>
        public byte* Hard, Wet;

        /// <summary>[lz][lx] over the biome lattice (17²): 0 surely dry, 1 surely wet, 2 undecided; 3 when its layers are computed.</summary>
        public byte* PointState;

        /// <summary>[lz][lx][2]: the terrain marker at the two corner layers around the wetness y.</summary>
        public float* WetLayers;

        /// <summary>Per chunk column (x · 32 + z): the surface-source biome (§12, AD-25).</summary>
        public byte* Surface;

        /// <summary>Per chunk column: the 4 largest blend weights' biomes (255 unused, by id) and weights (Σ 1).</summary>
        public int4* BlendBiomes;

        public float4* BlendWeights;

        /// <summary>The blend's per-biome sums (256) and the ids it touched.</summary>
        public float* BlendSums;

        public byte* Touched;

        /// <summary>Summed-area tables of the biome map's x and z edges, 65², for the uniform-window test.</summary>
        public int* EdgesX, EdgesZ;

        /// <summary>Per chunk column (z · 32 + x): the border threshold of the surface pick.</summary>
        public float* Border;

        private Allocator _allocator;

        public static DensityChunkBuffers Allocate(int latticeValues, Allocator allocator){
            const int points = DensityLayout.LatticeSize * DensityLayout.LatticeSize;
            const int cornerColumns = DensityLayout.CornerColumns * DensityLayout.CornerColumns;
            const int cellColumns = DensityLayout.CellColumns * DensityLayout.CellColumns;
            DensityChunkBuffers b = new(){
                _allocator = allocator,
                Registers = Floats(GraphInterpreter.RegisterFloats, allocator),
                Scratch = Floats(GraphInterpreter.ScratchFloats, allocator),
                Lattice = Floats(math.max(1, latticeValues) * points, allocator),
                DeltaUp = Floats(points, allocator),
                DeltaDown = Floats(points, allocator),
                Corners = Floats(cornerColumns * DensityLayout.MaxLayers, allocator),
                OwnLow = Ints(cornerColumns, allocator),
                OwnHigh = Ints(cornerColumns, allocator),
                NeedLow = Ints(cornerColumns, allocator),
                NeedHigh = Ints(cornerColumns, allocator),
                FirstOpen = Ints(cellColumns, allocator),
                LastOpen = Ints(cellColumns, allocator),
                Solid = (byte*)UnsafeUtility.Malloc(DensityLayout.Voxels, 64, allocator),
                Heightmap = (short*)UnsafeUtility.Malloc(DensityLayout.Columns * sizeof(short), 64, allocator),
                Block = Floats(GraphLimits.MaxLanes, allocator),
                Hard = (byte*)UnsafeUtility.Malloc(DensityLayout.BiomeSpan * DensityLayout.BiomeSpan, 64, allocator),
                Wet = (byte*)UnsafeUtility.Malloc(DensityLayout.BiomeSpan * DensityLayout.BiomeSpan, 64, allocator),
                PointState = (byte*)UnsafeUtility.Malloc(DensityLayout.BiomeLattice * DensityLayout.BiomeLattice, 64, allocator),
                WetLayers = Floats(DensityLayout.BiomeLattice * DensityLayout.BiomeLattice * 2, allocator),
                Surface = (byte*)UnsafeUtility.Malloc(DensityLayout.Columns, 64, allocator),
                BlendBiomes = (int4*)UnsafeUtility.Malloc(DensityLayout.Columns * sizeof(int4), 64, allocator),
                BlendWeights = (float4*)UnsafeUtility.Malloc(DensityLayout.Columns * sizeof(float4), 64, allocator),
                BlendSums = Floats(256, allocator),
                Touched = (byte*)UnsafeUtility.Malloc(256, 64, allocator),
                EdgesX = Ints((DensityLayout.BiomeSpan + 1) * (DensityLayout.BiomeSpan + 1), allocator),
                EdgesZ = Ints((DensityLayout.BiomeSpan + 1) * (DensityLayout.BiomeSpan + 1), allocator),
                Border = Floats(DensityLayout.Columns, allocator)
            };
            UnsafeUtility.MemClear(b.BlendSums, 256 * sizeof(float));
            // Lanes past a program's count are read (padded to four) but never used; clear them once.
            UnsafeUtility.MemClear(b.Registers, GraphInterpreter.RegisterFloats * sizeof(float));
            return b;
        }

        private static float* Floats(int count, Allocator allocator){
            return (float*)UnsafeUtility.Malloc(count * sizeof(float), 64, allocator);
        }

        private static int* Ints(int count, Allocator allocator){
            return (int*)UnsafeUtility.Malloc(count * sizeof(int), 64, allocator);
        }

        public void Dispose(){
            UnsafeUtility.Free(Registers, _allocator);
            UnsafeUtility.Free(Scratch, _allocator);
            UnsafeUtility.Free(Lattice, _allocator);
            UnsafeUtility.Free(DeltaUp, _allocator);
            UnsafeUtility.Free(DeltaDown, _allocator);
            UnsafeUtility.Free(Corners, _allocator);
            UnsafeUtility.Free(OwnLow, _allocator);
            UnsafeUtility.Free(OwnHigh, _allocator);
            UnsafeUtility.Free(NeedLow, _allocator);
            UnsafeUtility.Free(NeedHigh, _allocator);
            UnsafeUtility.Free(FirstOpen, _allocator);
            UnsafeUtility.Free(LastOpen, _allocator);
            UnsafeUtility.Free(Solid, _allocator);
            UnsafeUtility.Free(Heightmap, _allocator);
            UnsafeUtility.Free(Block, _allocator);
            UnsafeUtility.Free(Hard, _allocator);
            UnsafeUtility.Free(Wet, _allocator);
            UnsafeUtility.Free(PointState, _allocator);
            UnsafeUtility.Free(WetLayers, _allocator);
            UnsafeUtility.Free(Surface, _allocator);
            UnsafeUtility.Free(BlendBiomes, _allocator);
            UnsafeUtility.Free(BlendWeights, _allocator);
            UnsafeUtility.Free(BlendSums, _allocator);
            UnsafeUtility.Free(Touched, _allocator);
            UnsafeUtility.Free(EdgesX, _allocator);
            UnsafeUtility.Free(EdgesZ, _allocator);
            UnsafeUtility.Free(Border, _allocator);
        }
    }

    /// <summary>
    ///     The density passes of one chunk (§2.2): P1 runs the graph's 2D program on the padded lattice and derives
    ///     Δup and Δdown per lattice point; P4 evaluates the terrain marker's corners within each corner column's
    ///     range and classifies the voxels block by block. Each pass is a function of world coordinates and its own
    ///     loop (W2-ARC-01). Burst only (FloatMode.Strict): the classification must match the full-lattice
    ///     interpolation bit for bit (W2-DEN-03).
    /// </summary>
    public static unsafe class DensityPasses{
        private const int Lanes = GraphLimits.MaxLanes;

        /// <summary>
        ///     P1 on a square tile of <paramref name="size" />² lattice points from <paramref name="origin" /> (a
        ///     multiple of 4): the lattice program, then Δup = 64 · A / K and Δdown = min(32, Δup) per point (§5.1).
        /// </summary>
        internal static void Columns<TPrograms>(TPrograms programs, in DensityTerrainGraph terrain, in GraphCode code, uint seed, int2 origin, int size,
            ref DensityChunkBuffers b, ref GraphWorkCounters counters) where TPrograms : struct, IGraphPrograms{
            GraphData graph = terrain.Graph;
            GraphProgram program = code.Programs[graph.LatticeProgram];
            float* registers = b.Registers;
            for (int z = 0; z < size; z++)
            for (int x0 = 0; x0 < size; x0 += Lanes){
                int n = math.min(Lanes, size - x0);
                int padded = (n + 3) & ~3;
                for (int i = 0; i < padded; i++){
                    registers[i] = origin.x + 4 * (x0 + math.min(i, n - 1));
                    registers[Lanes + i] = 0f;
                    registers[2 * Lanes + i] = origin.y + 4 * z;
                }

                programs.Run(graph.LatticeProgram, code, registers, b.Scratch, n, seed, ref counters);
                for (int o = 0; o < program.OutputCount; o++){
                    GraphSlot s = code.Slots[program.OutputStart + o];
                    float* row = registers + s.Register * Lanes;
                    float* to = b.Lattice + (s.Slot * size + z) * size + x0;
                    for (int i = 0; i < n; i++) to[i] = row[i];
                }

                counters.LatticePoints += n;
            }

            float* factor = b.Lattice + terrain.Factor * size * size;
            float scale = 64f * terrain.NoiseBound;
            for (int i = 0; i < size * size; i++){
                float up = scale / factor[i];
                b.DeltaUp[i] = up;
                b.DeltaDown[i] = math.min(32f, up);
            }
        }

        /// <summary>
        ///     P4's terrain part for the chunk at <paramref name="chunk" /> (its first column), from the padded lattice of
        ///     <see cref="Columns{TPrograms}" />: corner-column ranges, the blocks they leave open, the corner program on
        ///     each corner column's open span, then per block the uniform test or the trilinear interpolation; writes
        ///     <see cref="DensityChunkBuffers.Solid" /> and the heightmap.
        /// </summary>
        internal static void Terrain<TPrograms>(TPrograms programs, in DensityTerrainGraph terrain, in GraphCode code, uint seed, int2 chunk,
            ref DensityChunkBuffers b, ref DensityWorkCounters counters) where TPrograms : struct, IGraphPrograms{
            const int corners = DensityLayout.CornerColumns;
            const int cells = DensityLayout.CellColumns;
            const int size = DensityLayout.LatticeSize;
            const int offset = DensityLayout.ChunkLatticeOffset;
            int h = terrain.CellHeight;
            int layers = terrain.Layers;
            int perBlock = DensityLayout.BlockHeight / h;
            float* surface = b.Lattice + terrain.PreliminarySurface * size * size;

            // Own ranges (§5.4): corners are computed from preliminary_surface − Δdown to preliminary_surface + Δup and
            // one layer beyond; below the corner is solid, above it is air. In the top slide the density can turn
            // negative over solid rock (flat ground near y 255 has a small Δdown), so a range that reaches the slide
            // starts at the slide at the latest.
            for (int cz = 0; cz < corners; cz++)
            for (int cx = 0; cx < corners; cx++){
                int l = (offset + cz) * size + offset + cx;
                float p = surface[l];
                float low = p - b.DeltaDown[l], high = p + b.DeltaUp[l];
                if (high >= terrain.SlideStart) low = math.min(low, terrain.SlideStart);
                int c = cz * corners + cx;
                b.OwnLow[c] = (int)math.clamp(math.ceil(low / h) - 1f, 0f, layers);
                b.OwnHigh[c] = (int)math.clamp(math.floor(high / h) + 1f, -1f, layers - 1);
                b.NeedLow[c] = layers;
                b.NeedHigh[c] = -1;
            }

            // Open blocks per cell column: a block is filled from the ranges when its corner layers all lie below every
            // own range (solid) or all above (air). Every corner of an open block is computed.
            for (int bz = 0; bz < cells; bz++)
            for (int bx = 0; bx < cells; bx++){
                int c00 = bz * corners + bx, c10 = c00 + 1, c01 = c00 + corners, c11 = c01 + 1;
                int minLow = math.min(math.min(b.OwnLow[c00], b.OwnLow[c10]), math.min(b.OwnLow[c01], b.OwnLow[c11]));
                int maxHigh = math.max(math.max(b.OwnHigh[c00], b.OwnHigh[c10]), math.max(b.OwnHigh[c01], b.OwnHigh[c11]));
                // Block k spans layers k · perBlock to (k + 1) · perBlock: open when its top layer reaches minLow and its
                // bottom layer is at most maxHigh.
                int first = math.max(0, CeilDiv(minLow - perBlock, perBlock));
                int last = math.min(DensityLayout.BlocksPerColumn - 1, FloorDiv(maxHigh, perBlock));
                int cell = bz * cells + bx;
                b.FirstOpen[cell] = first;
                b.LastOpen[cell] = last;
                if (first > last) continue;
                int lo = first * perBlock, hi = (last + 1) * perBlock;
                Need(ref b, c00, lo, hi);
                Need(ref b, c10, lo, hi);
                Need(ref b, c01, lo, hi);
                Need(ref b, c11, lo, hi);
            }

            RunCorners(programs, terrain, code, seed, chunk, ref b, ref counters);
            Classify(terrain, ref b, ref counters);
        }

        private static void Need(ref DensityChunkBuffers b, int c, int lo, int hi){
            b.NeedLow[c] = math.min(b.NeedLow[c], lo);
            b.NeedHigh[c] = math.max(b.NeedHigh[c], hi);
        }

        /// <summary>The corner program on the needed layers of each corner column, lanes = layers from the lowest.</summary>
        private static void RunCorners<TPrograms>(TPrograms programs, in DensityTerrainGraph terrain, in GraphCode code, uint seed, int2 chunk,
            ref DensityChunkBuffers b, ref DensityWorkCounters counters) where TPrograms : struct, IGraphPrograms{
            const int corners = DensityLayout.CornerColumns;
            const int size = DensityLayout.LatticeSize;
            const int offset = DensityLayout.ChunkLatticeOffset;
            GraphProgram program = code.Programs[terrain.CornerProgram];
            float* registers = b.Registers;
            int h = terrain.CellHeight;
            int markerRegister = -1;
            for (int o = 0; o < program.OutputCount; o++){
                GraphSlot s = code.Slots[program.OutputStart + o];
                if (s.Slot == terrain.Marker) markerRegister = s.Register;
            }

            for (int cz = 0; cz < corners; cz++)
            for (int cx = 0; cx < corners; cx++){
                int c = cz * corners + cx;
                int lo = b.NeedLow[c], hi = b.NeedHigh[c];
                if (hi < lo) continue;
                int n = hi - lo + 1;
                int padded = (n + 3) & ~3;
                float wx = chunk.x + 4 * cx, wz = chunk.y + 4 * cz;
                int l = (offset + cz) * size + offset + cx;
                for (int i = 0; i < padded; i++){
                    registers[i] = wx;
                    registers[Lanes + i] = (lo + math.min(i, n - 1)) * h;
                    registers[2 * Lanes + i] = wz;
                }

                for (int i = 0; i < program.InputCount; i++){
                    GraphSlot s = code.Slots[program.InputStart + i];
                    float v = b.Lattice[s.Slot * size * size + l];
                    float* row = registers + s.Register * Lanes;
                    for (int k = 0; k < padded; k++) row[k] = v;
                }

                programs.Run(terrain.CornerProgram, code, registers, b.Scratch, n, seed, ref counters.Graph);
                float* from = registers + markerRegister * Lanes;
                float* to = b.Corners + c * DensityLayout.MaxLayers + lo;
                for (int i = 0; i < n; i++) to[i] = from[i];
                counters.Graph.Corners += n;
                counters.Corners += n;
                counters.CornerColumns++;
            }
        }

        /// <summary>
        ///     Classifies every block: below the open blocks solid, above them air, an open block uniform when all its
        ///     corners agree in sign (trilinear interpolation is a convex combination, so the test is exact) and
        ///     otherwise interpolated per voxel. Then the heightmap: the top terrain voxel of each column.
        /// </summary>
        private static void Classify(in DensityTerrainGraph terrain, ref DensityChunkBuffers b, ref DensityWorkCounters counters){
            const int corners = DensityLayout.CornerColumns;
            const int cells = DensityLayout.CellColumns;
            const int height = DensityLayout.Height;
            const int stride = DensityLayout.MaxLayers;
            int h = terrain.CellHeight;
            int perBlock = DensityLayout.BlockHeight / h;
            long solid = 0;

            for (int bz = 0; bz < cells; bz++)
            for (int bx = 0; bx < cells; bx++){
                int cell = bz * cells + bx;
                int first = b.FirstOpen[cell], last = b.LastOpen[cell];
                // With no open block, `first` still separates the solid blocks below from the air above.
                int solidTop = math.clamp(first, 0, DensityLayout.BlocksPerColumn) * DensityLayout.BlockHeight;
                int open = math.max(0, last - first + 1);
                int openTop = open > 0 ? (last + 1) * DensityLayout.BlockHeight : solidTop;
                for (int vz = 0; vz < 4; vz++)
                for (int vx = 0; vx < 4; vx++){
                    byte* column = b.Solid + ((bx * 4 + vx) * DensityLayout.ChunkSize + bz * 4 + vz) * height;
                    UnsafeUtility.MemSet(column, 1, solidTop);
                    UnsafeUtility.MemClear(column + openTop, height - openTop);
                }

                solid += 16L * solidTop;
                counters.BlocksByRange += DensityLayout.BlocksPerColumn - open;

                float* c00 = b.Corners + (bz * corners + bx) * stride;
                float* c10 = c00 + stride;
                float* c01 = c00 + corners * stride;
                float* c11 = c01 + stride;
                for (int k = first; k <= last; k++){
                    int baseY = k * DensityLayout.BlockHeight;
                    int j0 = k * perBlock, j1 = j0 + perBlock;
                    bool anySolid = false, anyAir = false;
                    for (int j = j0; j <= j1; j++){
                        bool4 above = new float4(c00[j], c10[j], c01[j], c11[j]) > 0f;
                        anySolid |= math.any(above);
                        anyAir |= !math.all(above);
                    }

                    if (!anyAir || !anySolid){
                        byte v = anyAir ? (byte)0 : (byte)1;
                        for (int vz = 0; vz < 4; vz++)
                        for (int vx = 0; vx < 4; vx++)
                            UnsafeUtility.MemSet(b.Solid + ((bx * 4 + vx) * DensityLayout.ChunkSize + bz * 4 + vz) * height + baseY, v,
                                DensityLayout.BlockHeight);
                        if (v != 0) solid += 16 * DensityLayout.BlockHeight;
                        counters.BlocksBySign++;
                        continue;
                    }

                    // A constant cell height lets Burst turn the division by it into a shift.
                    if (h == 4) GraphInterpolation.Trilinear(c00, c10, c01, c11, 4, baseY, b.Block);
                    else GraphInterpolation.Trilinear(c00, c10, c01, c11, 8, baseY, b.Block);
                    for (int vz = 0; vz < 4; vz++)
                    for (int vx = 0; vx < 4; vx++){
                        byte* column = b.Solid + ((bx * 4 + vx) * DensityLayout.ChunkSize + bz * 4 + vz) * height + baseY;
                        for (int ly = 0; ly < DensityLayout.BlockHeight; ly++){
                            byte v = b.Block[ly * 16 + vz * 4 + vx] > 0f ? (byte)1 : (byte)0;
                            column[ly] = v;
                            solid += v;
                        }
                    }

                    counters.BlocksMixed++;
                    counters.InterpolatedVoxels += GraphLimits.MaxLanes;
                    counters.Graph.InterpolatedValues += GraphLimits.MaxLanes;
                }

                // Heightmap: the top terrain voxel, searched down from the top of the open blocks.
                for (int vz = 0; vz < 4; vz++)
                for (int vx = 0; vx < 4; vx++){
                    int x = bx * 4 + vx, z = bz * 4 + vz;
                    byte* column = b.Solid + (x * DensityLayout.ChunkSize + z) * height;
                    int y = openTop - 1;
                    while (y >= 0 && column[y] == 0) y--;
                    b.Heightmap[x * DensityLayout.ChunkSize + z] = (short)y;
                }
            }

            counters.SolidVoxels += solid;
            counters.Chunks++;
        }

        private static int FloorDiv(int a, int b){
            int q = a / b;
            return q * b > a ? q - 1 : q;
        }

        private static int CeilDiv(int a, int b){
            return -FloorDiv(-a, b);
        }
    }
}
