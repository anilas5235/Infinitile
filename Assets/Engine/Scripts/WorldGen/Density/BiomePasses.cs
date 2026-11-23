using Engine.Scripts.WorldGen.Biomes;
using Engine.Scripts.WorldGen.Blending;
using Engine.Scripts.WorldGen.Graph;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;

namespace Engine.Scripts.WorldGen.Density{
    /// <summary>Work of the biome passes, summed over chunks: a change in the work shows up without a timing run.</summary>
    public struct BiomeWorkCounters{
        /// <summary>Columns of the padded biome maps, and the 4 × 4 cells that took one box without a lookup.</summary>
        public long Columns, UniformCells, LookupColumns;

        /// <summary>Lattice points of the wetness test: decided by the early-out, and with their two layers computed.</summary>
        public long WetSurePoints, WetComputedPoints;

        /// <summary>Cells whose 16 columns took one wet flag from the early-out, and columns of the dry-column rule.</summary>
        public long WetUniformCells, DrySubstitutes;

        /// <summary>Chunk columns of the blend: summed over the kernel, uniform windows, and chunks skipped as one biome.</summary>
        public long BlendKernelColumns, BlendUniformColumns, BlendSkippedChunks, BorderColumns;

        public void Add(in BiomeWorkCounters o){
            Columns += o.Columns;
            UniformCells += o.UniformCells;
            LookupColumns += o.LookupColumns;
            WetSurePoints += o.WetSurePoints;
            WetComputedPoints += o.WetComputedPoints;
            WetUniformCells += o.WetUniformCells;
            DrySubstitutes += o.DrySubstitutes;
            BlendKernelColumns += o.BlendKernelColumns;
            BlendUniformColumns += o.BlendUniformColumns;
            BlendSkippedChunks += o.BlendSkippedChunks;
            BorderColumns += o.BorderColumns;
        }

        public override string ToString(){
            return $"biome columns {Columns} (uniform cells {UniformCells}, lookups {LookupColumns}), wet points sure {WetSurePoints} / computed {WetComputedPoints}, " +
                   $"wet uniform cells {WetUniformCells}, dry substitutes {DrySubstitutes}; blend kernel {BlendKernelColumns} / uniform {BlendUniformColumns} " +
                   $"columns, skipped chunks {BlendSkippedChunks}, border columns {BorderColumns}";
        }
    }

    /// <summary>
    ///     The biome passes of one chunk (§6, §12): P2 gives every column of the padded biome map (chunk ± 16) its hard
    ///     biome from the interpolated climate (W2-BIO-01, -06), with the wetness test at y 123 and the dry-column rule;
    ///     P3 blends the hard biomes with DistanceBlend into the surface-source biome and the 4 largest weights per chunk
    ///     column. Burst only (FloatMode.Strict).
    /// </summary>
    public static unsafe class BiomePasses{
        private const int Lanes = GraphLimits.MaxLanes;
        private const int P = BiomeBox.Parameters;

        /// <summary>
        ///     P2 for the chunk whose first column is <paramref name="chunk" />, from P1's padded lattice: writes
        ///     <see cref="DensityChunkBuffers.Hard" /> and <see cref="DensityChunkBuffers.Wet" /> over the 64² columns from
        ///     chunk − 16.
        /// </summary>
        internal static void Biomes<TPrograms>(TPrograms programs, in DensityTerrainGraph terrain, in GraphCode code, uint seed, int2 chunk,
            in BiomeLayoutData layout, ref DensityChunkBuffers b, ref DensityWorkCounters counters) where TPrograms : struct, IGraphPrograms{
            Wetness(programs, terrain, code, seed, chunk, ref b, ref counters);
            Lookup(terrain, layout, ref b, ref counters.Biomes);
        }

        /// <summary>The wetness test's early-out per lattice point, then the two layers of every corner of an undecided cell.</summary>
        private static void Wetness<TPrograms>(TPrograms programs, in DensityTerrainGraph terrain, in GraphCode code, uint seed, int2 chunk,
            ref DensityChunkBuffers b, ref DensityWorkCounters counters) where TPrograms : struct, IGraphPrograms{
            const int size = DensityLayout.LatticeSize;
            const int lattice = DensityLayout.BiomeLattice;
            const int offset = DensityLayout.BiomeLatticeOffset;
            const int cells = DensityLayout.BiomeCells;
            int h = terrain.CellHeight;
            int cy = DensityLayout.WetnessY / h;
            float yLo = cy * h, yHi = (cy + 1) * h;
            float* surface = b.Lattice + terrain.PreliminarySurface * size * size;

            // Early-out (§6.5): below preliminary_surface − Δdown the terrain is surely solid, above + Δup surely air.
            for (int lz = 0; lz < lattice; lz++)
            for (int lx = 0; lx < lattice; lx++){
                int l = (offset + lz) * size + offset + lx;
                float p = surface[l];
                byte state = p - yHi > b.DeltaDown[l] ? (byte)0 : yLo - p > b.DeltaUp[l] ? (byte)1 : (byte)2;
                b.PointState[lz * lattice + lx] = state;
            }

            // A cell whose corners are not all surely dry or all surely wet needs the layers of all four corners.
            for (int cz = 0; cz < cells; cz++)
            for (int cx = 0; cx < cells; cx++){
                int p00 = cz * lattice + cx;
                int s00 = b.PointState[p00] & 3, s10 = b.PointState[p00 + 1] & 3;
                int s01 = b.PointState[p00 + lattice] & 3, s11 = b.PointState[p00 + lattice + 1] & 3;
                if (s00 != 2 && s00 == s10 && s00 == s01 && s00 == s11) continue;
                b.PointState[p00] |= 4;
                b.PointState[p00 + 1] |= 4;
                b.PointState[p00 + lattice] |= 4;
                b.PointState[p00 + lattice + 1] |= 4;
            }

            // The corner program on the two layers of every needed point, two lanes per point.
            GraphProgram program = code.Programs[terrain.CornerProgram];
            int markerRegister = -1;
            for (int o = 0; o < program.OutputCount; o++){
                GraphSlot s = code.Slots[program.OutputStart + o];
                if (s.Slot == terrain.Marker) markerRegister = s.Register;
            }

            int* points = stackalloc int[Lanes / 2];
            int count = 0;
            for (int i = 0; i < lattice * lattice; i++){
                if ((b.PointState[i] & 4) == 0){
                    counters.Biomes.WetSurePoints++;
                    continue;
                }

                points[count++] = i;
                if (count == Lanes / 2){
                    RunWetLayers(programs, terrain, code, seed, chunk, cy, h, markerRegister, points, count, ref b, ref counters);
                    count = 0;
                }
            }

            if (count > 0) RunWetLayers(programs, terrain, code, seed, chunk, cy, h, markerRegister, points, count, ref b, ref counters);
        }

        private static void RunWetLayers<TPrograms>(TPrograms programs, in DensityTerrainGraph terrain, in GraphCode code, uint seed, int2 chunk, int cy, int h,
            int markerRegister, int* points, int count, ref DensityChunkBuffers b, ref DensityWorkCounters counters) where TPrograms : struct, IGraphPrograms{
            const int size = DensityLayout.LatticeSize;
            const int lattice = DensityLayout.BiomeLattice;
            const int offset = DensityLayout.BiomeLatticeOffset;
            GraphProgram program = code.Programs[terrain.CornerProgram];
            float* registers = b.Registers;
            int n = 2 * count;
            int padded = (n + 3) & ~3;
            float originX = chunk.x - DensityLayout.BiomePadding, originZ = chunk.y - DensityLayout.BiomePadding;
            for (int i = 0; i < padded; i++){
                int k = math.min(i, n - 1);
                int point = points[k >> 1];
                int lz = point / lattice, lx = point - lz * lattice;
                registers[i] = originX + 4 * lx;
                registers[Lanes + i] = (cy + (k & 1)) * h;
                registers[2 * Lanes + i] = originZ + 4 * lz;
            }

            for (int s = 0; s < program.InputCount; s++){
                GraphSlot slot = code.Slots[program.InputStart + s];
                float* values = b.Lattice + slot.Slot * size * size;
                float* row = registers + slot.Register * Lanes;
                for (int i = 0; i < padded; i++){
                    int point = points[math.min(i, n - 1) >> 1];
                    int lz = point / lattice, lx = point - lz * lattice;
                    row[i] = values[(offset + lz) * size + offset + lx];
                }
            }

            programs.Run(terrain.CornerProgram, code, registers, b.Scratch, n, seed, ref counters.Graph);
            float* from = registers + markerRegister * Lanes;
            for (int i = 0; i < n; i++) b.WetLayers[points[i >> 1] * 2 + (i & 1)] = from[i];
            counters.Graph.Corners += n;
            counters.Biomes.WetComputedPoints += count;
        }

        /// <summary>
        ///     Per cell of the biome map: the wet flags of its columns, then the hard biome, from one box when the cell is
        ///     uniform (§6.3) and from the lookup otherwise, and the dry-column rule.
        /// </summary>
        private static void Lookup(in DensityTerrainGraph terrain, in BiomeLayoutData layout, ref DensityChunkBuffers b, ref BiomeWorkCounters counters){
            const int size = DensityLayout.LatticeSize;
            const int lattice = DensityLayout.BiomeLattice;
            const int offset = DensityLayout.BiomeLatticeOffset;
            const int cells = DensityLayout.BiomeCells;
            const int span = DensityLayout.BiomeSpan;
            int h = terrain.CellHeight;
            float ty = (DensityLayout.WetnessY - DensityLayout.WetnessY / h * h) * GraphInterpolation.InverseCellHeight(h);

            int* slots = stackalloc int[P];
            for (int p = 0; p < P; p++) slots[p] = terrain.ClimateSlot(p);
            float* c00 = stackalloc float[P];
            float* c10 = stackalloc float[P];
            float* c01 = stackalloc float[P];
            float* c11 = stackalloc float[P];
            float* lo = stackalloc float[P];
            float* hi = stackalloc float[P];
            float* column = stackalloc float[P];
            bool hasLand = layout.Land.BoxCount > 0;

            for (int cz = 0; cz < cells; cz++)
            for (int cx = 0; cx < cells; cx++){
                int l00 = (offset + cz) * size + offset + cx;
                for (int p = 0; p < P; p++){
                    int s = slots[p];
                    float* values = b.Lattice + s * size * size;
                    c00[p] = s < 0 ? 0f : values[l00];
                    c10[p] = s < 0 ? 0f : values[l00 + 1];
                    c01[p] = s < 0 ? 0f : values[l00 + size];
                    c11[p] = s < 0 ? 0f : values[l00 + size + 1];
                    lo[p] = math.min(math.min(c00[p], c10[p]), math.min(c01[p], c11[p]));
                    hi[p] = math.max(math.max(c00[p], c10[p]), math.max(c01[p], c11[p]));
                }

                // Wet flags of the 16 columns.
                // The cell's own corners decide (a neighbour cell may have computed some of them anyway).
                int q00 = cz * lattice + cx;
                int s00 = b.PointState[q00] & 3;
                bool wetUniform = s00 != 2 && s00 == (b.PointState[q00 + 1] & 3) && s00 == (b.PointState[q00 + lattice] & 3) &&
                                  s00 == (b.PointState[q00 + lattice + 1] & 3);
                float* w00 = b.WetLayers + q00 * 2, w10 = w00 + 2, w01 = w00 + lattice * 2, w11 = w01 + 2;
                for (int lz = 0; lz < 4; lz++){
                    float tz = lz * 0.25f;
                    for (int lx = 0; lx < 4; lx++){
                        byte wet;
                        if (wetUniform) wet = (byte)s00;
                        else{
                            // As P4's trilinear interpolation: x, then z, then y between the two layers.
                            float tx = lx * 0.25f;
                            float low = GraphInterpolation.Lerp(GraphInterpolation.Lerp(w00[0], w10[0], tx), GraphInterpolation.Lerp(w01[0], w11[0], tx), tz);
                            float high = GraphInterpolation.Lerp(GraphInterpolation.Lerp(w00[1], w10[1], tx), GraphInterpolation.Lerp(w01[1], w11[1], tx), tz);
                            wet = GraphInterpolation.Lerp(low, high, ty) > 0f ? (byte)0 : (byte)1;
                        }

                        b.Wet[(cz * 4 + lz) * span + cx * 4 + lx] = wet;
                    }
                }

                if (wetUniform) counters.WetUniformCells++;

                // Hard biomes.
                int box = layout.Main.Lookup(c00);
                bool uniform = box >= 0 && layout.Main.HoldsBounds(box, lo, hi);
                if (uniform) counters.UniformCells++;
                byte uniformBiome = box >= 0 ? layout.Main.BoxBiome[box] : (byte)255;
                for (int lz = 0; lz < 4; lz++){
                    float tz = lz * 0.25f;
                    for (int lx = 0; lx < 4; lx++){
                        float tx = lx * 0.25f;
                        int i = (cz * 4 + lz) * span + cx * 4 + lx;
                        byte id;
                        bool computed = false;
                        if (uniform) id = uniformBiome;
                        else{
                            Interpolate(c00, c10, c01, c11, tx, tz, column);
                            computed = true;
                            id = layout.Main.BiomeAt(column);
                            counters.LookupColumns++;
                        }

                        // Dry-column rule (§6.5): a water biome on a dry column takes the nearest land box.
                        if (hasLand && b.Wet[i] == 0 && layout.Water.Contains(id)){
                            if (!computed) Interpolate(c00, c10, c01, c11, tx, tz, column);
                            id = layout.Land.BiomeAt(column);
                            counters.DrySubstitutes++;
                        }

                        b.Hard[i] = id;
                    }
                }
            }

            counters.Columns += span * span;
        }

        /// <summary>A column's climate from its cell's corners: <see cref="GraphInterpolation.Bilinear" />'s formula.</summary>
        private static void Interpolate(float* c00, float* c10, float* c01, float* c11, float tx, float tz, float* column){
            for (int p = 0; p < P; p++)
                column[p] = GraphInterpolation.Lerp(GraphInterpolation.Lerp(c00[p], c10[p], tx), GraphInterpolation.Lerp(c01[p], c11[p], tx), tz);
        }

        /// <summary>
        ///     P3 (§12): DistanceBlend over the padded hard biomes with the kernel <paramref name="kernel" /> (stride 2),
        ///     keeping the 4 largest weights per chunk column (AD-28), then the surface-source biome by the patchy pick
        ///     with the graph's border threshold. A padded map of one biome skips the pass; a column whose kernel window
        ///     holds one biome takes it without the sum (both exact). <paramref name="distanceBlend" /> false is None.
        /// </summary>
        internal static void Blend<TPrograms>(TPrograms programs, in DensityTerrainGraph terrain, in GraphCode code, uint seed, int2 chunk, bool distanceBlend,
            in KernelSet kernel, in NativeArray<KernelTap> taps, ref DensityChunkBuffers b, ref DensityWorkCounters counters) where TPrograms : struct, IGraphPrograms{
            const int span = DensityLayout.BiomeSpan;
            const int pad = DensityLayout.BiomePadding;
            const int n = DensityLayout.ChunkSize;

            bool oneBiome = true;
            byte first = b.Hard[0];
            for (int i = 1; i < span * span && oneBiome; i++) oneBiome = b.Hard[i] == first;
            if (!distanceBlend || !kernel.IsCreated || oneBiome){
                for (int x = 0; x < n; x++)
                for (int z = 0; z < n; z++)
                    OneHot(ref b, x * n + z, b.Hard[(z + pad) * span + x + pad]);
                if (oneBiome) counters.Biomes.BlendSkippedChunks++;
                return;
            }

            BuildEdgeTables(ref b);
            if (terrain.BorderColumn >= 0) RunBorder(programs, terrain, code, seed, chunk, ref b, ref counters);

            int reach = kernel.Reach;
            if (reach > pad){
                // The padded map does not cover the kernel (the scheduler refuses such a width); never read outside it.
                for (int x = 0; x < n; x++)
                for (int z = 0; z < n; z++)
                    OneHot(ref b, x * n + z, b.Hard[(z + pad) * span + x + pad]);
                return;
            }

            byte* kept = stackalloc byte[4];
            float* keptSum = stackalloc float[4];
            for (int z = 0; z < n; z++)
            for (int x = 0; x < n; x++){
                int px = x + pad, pz = z + pad;
                int c = x * n + z;
                if (WindowIsUniform(ref b, px - reach, pz - reach, px + reach, pz + reach)){
                    OneHot(ref b, c, b.Hard[pz * span + px]);
                    counters.Biomes.BlendUniformColumns++;
                    continue;
                }

                int phase = kernel.Phase(chunk.x + x, chunk.y + z);
                int start = kernel.PhaseStart[phase], end = start + kernel.PhaseCount[phase];
                int touched = 0;
                for (int t = start; t < end; t++){
                    KernelTap tap = taps[t];
                    byte id = b.Hard[(pz + tap.Offset.y) * span + px + tap.Offset.x];
                    if (b.BlendSums[id] == 0f) b.Touched[touched++] = id;
                    b.BlendSums[id] += tap.K;
                }

                // The 4 largest sums (ties: the lower id), then by id, so the pick walks them in biome order as v1 did.
                int keep = math.min(4, touched);
                for (int k = 0; k < keep; k++){
                    int best = -1;
                    for (int j = 0; j < touched; j++){
                        byte id = b.Touched[j];
                        float sum = b.BlendSums[id];
                        if (sum < 0f) continue; // taken
                        if (best < 0 || sum > b.BlendSums[b.Touched[best]] || sum == b.BlendSums[b.Touched[best]] && id < b.Touched[best]) best = j;
                    }

                    kept[k] = b.Touched[best];
                    keptSum[k] = b.BlendSums[kept[k]];
                    b.BlendSums[kept[k]] = -1f;
                }

                for (int j = 0; j < touched; j++) b.BlendSums[b.Touched[j]] = 0f;
                for (int k = 1; k < keep; k++){
                    byte id = kept[k];
                    float s = keptSum[k];
                    int m = k - 1;
                    while (m >= 0 && kept[m] > id){
                        kept[m + 1] = kept[m];
                        keptSum[m + 1] = keptSum[m];
                        m--;
                    }

                    kept[m + 1] = id;
                    keptSum[m + 1] = s;
                }

                float total = 0f;
                for (int k = 0; k < keep; k++) total += keptSum[k];
                float4 w = 0f;
                int4 ids = 255;
                for (int k = 0; k < keep; k++){
                    w[k] = keptSum[k] / total;
                    ids[k] = kept[k];
                }

                b.BlendBiomes[c] = ids;
                b.BlendWeights[c] = w;
                b.Surface[c] = keep == 1 ? kept[0] : Pick(ids, w, keep, terrain.BorderColumn >= 0 ? b.Border[z * n + x] : 0.5f);
                counters.Biomes.BlendKernelColumns++;
            }
        }

        /// <summary>
        ///     The patchy pick (v1's <see cref="SurfacePick.PickPatchy" />): the first biome whose running weight exceeds
        ///     the threshold times Σ w, by id.
        /// </summary>
        private static byte Pick(int4 ids, float4 w, int count, float threshold01){
            float sum = 0f;
            for (int k = 0; k < count; k++) sum += w[k];
            float threshold = threshold01 * sum;
            float cumulative = 0f;
            for (int k = 0; k < count; k++){
                cumulative += w[k];
                if (cumulative > threshold) return (byte)ids[k];
            }

            return (byte)ids[count - 1];
        }

        private static void OneHot(ref DensityChunkBuffers b, int c, byte id){
            b.Surface[c] = id;
            b.BlendBiomes[c] = new int4(id, 255, 255, 255);
            b.BlendWeights[c] = new float4(1f, 0f, 0f, 0f);
        }

        /// <summary>Summed-area tables of the biome changes between x and z neighbours of the padded map.</summary>
        private static void BuildEdgeTables(ref DensityChunkBuffers b){
            const int span = DensityLayout.BiomeSpan;
            const int w = span + 1;
            UnsafeUtility.MemClear(b.EdgesX, w * sizeof(int));
            UnsafeUtility.MemClear(b.EdgesZ, w * sizeof(int));
            for (int z = 0; z < span; z++){
                b.EdgesX[(z + 1) * w] = 0;
                b.EdgesZ[(z + 1) * w] = 0;
                int rowX = 0, rowZ = 0;
                for (int x = 0; x < span; x++){
                    byte v = b.Hard[z * span + x];
                    rowX += x < span - 1 && v != b.Hard[z * span + x + 1] ? 1 : 0;
                    rowZ += z < span - 1 && v != b.Hard[(z + 1) * span + x] ? 1 : 0;
                    b.EdgesX[(z + 1) * w + x + 1] = b.EdgesX[z * w + x + 1] + rowX;
                    b.EdgesZ[(z + 1) * w + x + 1] = b.EdgesZ[z * w + x + 1] + rowZ;
                }
            }
        }

        /// <summary>Whether the columns x0…x1 × z0…z1 of the padded map hold one biome: no x or z change inside.</summary>
        private static bool WindowIsUniform(ref DensityChunkBuffers b, int x0, int z0, int x1, int z1){
            return Sum(b.EdgesX, x0, z0, x1 - 1, z1) == 0 && Sum(b.EdgesZ, x0, z0, x1, z1 - 1) == 0;
        }

        private static int Sum(int* table, int x0, int z0, int x1, int z1){
            const int w = DensityLayout.BiomeSpan + 1;
            return table[(z1 + 1) * w + x1 + 1] - table[z0 * w + x1 + 1] - table[(z1 + 1) * w + x0] + table[z0 * w + x0];
        }

        /// <summary>The graph's column program on the chunk's columns, four rows per run, for the border threshold.</summary>
        private static void RunBorder<TPrograms>(TPrograms programs, in DensityTerrainGraph terrain, in GraphCode code, uint seed, int2 chunk,
            ref DensityChunkBuffers b, ref DensityWorkCounters counters) where TPrograms : struct, IGraphPrograms{
            const int n = DensityLayout.ChunkSize;
            GraphData graph = terrain.Graph;
            GraphProgram program = code.Programs[graph.ColumnProgram];
            int register = -1;
            for (int o = 0; o < program.OutputCount; o++){
                GraphSlot s = code.Slots[program.OutputStart + o];
                if (s.Slot == terrain.BorderColumn) register = s.Register;
            }

            float* registers = b.Registers;
            const int rows = Lanes / n;
            for (int z0 = 0; z0 < n; z0 += rows){
                for (int i = 0; i < Lanes; i++){
                    registers[i] = chunk.x + i % n;
                    registers[Lanes + i] = 0f;
                    registers[2 * Lanes + i] = chunk.y + z0 + i / n;
                }

                programs.Run(graph.ColumnProgram, code, registers, b.Scratch, Lanes, seed, ref counters.Graph);
                float* from = registers + register * Lanes;
                for (int i = 0; i < Lanes; i++) b.Border[z0 * n + i] = from[i];
                counters.Graph.ColumnPoints += Lanes;
            }

            counters.Biomes.BorderColumns += n * n;
        }
    }
}
