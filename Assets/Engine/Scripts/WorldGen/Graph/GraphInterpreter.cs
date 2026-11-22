using Engine.Scripts.WorldGen.Noise;
using Unity.Mathematics;

namespace Engine.Scripts.WorldGen.Graph{
    /// <summary>
    ///     The batched interpreter: one switch per instruction and inside each case a loop over the lanes, four at a
    ///     time. Registers are <see cref="GraphLimits.MaxRegisters" /> rows of <see cref="GraphLimits.MaxLanes" />
    ///     floats; rows 0, 1, 2 hold x, y, z and the input rows are filled by the caller. Only called from Burst jobs.
    /// </summary>
    internal static unsafe class GraphInterpreter{
        public const int Lanes = GraphLimits.MaxLanes;

        /// <summary>Floats of register scratch one interpreter needs.</summary>
        public const int RegisterFloats = GraphLimits.MaxRegisters * Lanes;

        /// <summary>Floats of coordinate scratch (shifted noise coordinates) one interpreter needs.</summary>
        public const int ScratchFloats = 3 * Lanes;

        /// <summary>Row <paramref name="register" /> of the register file.</summary>
        public static float* Row(float* registers, int register){
            return registers + register * Lanes;
        }

        /// <summary>Runs <paramref name="program" /> on the first <paramref name="lanes" /> lanes (padded to four).</summary>
        public static void Run(in GraphProgram program, GraphInstruction* instructions, NoiseEntry* entries, NoiseOctave* octaves,
            SplinePoint* points, float* registers, float* scratch, int lanes, uint seed, ref GraphWorkCounters counters){
            int padded = SimplexLanes.Padded(lanes);
            int quads = padded >> 2;
            float4* splineValues = stackalloc float4[GraphKernels.SplineScratch];
            GraphInstruction* code = instructions + program.InstructionStart;
            long noise2 = 0, noise3 = 0;

            for (int i = 0; i < program.InstructionCount; i++){
                GraphInstruction ins = code[i];
                float4* d = (float4*)(registers + ins.Dst * Lanes);
                float4* a = (float4*)(registers + ins.A * Lanes);
                float4* b = (float4*)(registers + ins.B * Lanes);
                float4* c = (float4*)(registers + ins.C * Lanes);
                float k = ins.P0;

                switch (ins.Op){
                    case GraphOp.Const:{
                        float4 v = k;
                        for (int q = 0; q < quads; q++) d[q] = v;
                        break;
                    }
                    case GraphOp.Noise:{
                        NoiseEntry e = entries[ins.Aux];
                        float* px = registers;
                        float* py = registers + Lanes;
                        float* pz = registers + 2 * Lanes;
                        if (ins.A >= 0){
                            Shift(scratch, px, (float*)a, quads);
                            px = scratch;
                        }

                        if (ins.B >= 0){
                            Shift(scratch + Lanes, py, (float*)b, quads);
                            py = scratch + Lanes;
                        }

                        if (ins.C >= 0){
                            Shift(scratch + 2 * Lanes, pz, (float*)c, quads);
                            pz = scratch + 2 * Lanes;
                        }

                        for (int q = 0; q < quads; q++) d[q] = float4.zero;
                        float* acc = (float*)d;
                        bool ridged = e.Mode == NoiseMode.Ridged;
                        for (int o = 0; o < e.OctaveCount; o++){
                            NoiseOctave oct = octaves[e.OctaveStart + o];
                            if (e.Dimensions == 2){
                                if (ridged) SimplexLanes.AccumulateRidged2(px, pz, acc, padded, oct.FrequencyXZ, oct.Amplitude, seed, oct.Salt);
                                else SimplexLanes.AccumulateFbm2(px, pz, acc, padded, oct.FrequencyXZ, oct.Amplitude, seed, oct.Salt);
                            }
                            else{
                                if (ridged)
                                    SimplexLanes.AccumulateRidged3(px, py, pz, acc, padded, oct.FrequencyXZ, oct.FrequencyY, oct.Amplitude, seed,
                                        oct.Salt);
                                else
                                    SimplexLanes.AccumulateFbm3(px, py, pz, acc, padded, oct.FrequencyXZ, oct.FrequencyY, oct.Amplitude, seed,
                                        oct.Salt);
                            }
                        }

                        if (e.Dimensions == 2) noise2 += e.OctaveCount;
                        else noise3 += e.OctaveCount;
                        break;
                    }
                    case GraphOp.Add:
                        for (int q = 0; q < quads; q++) d[q] = a[q] + b[q];
                        break;
                    case GraphOp.Sub:
                        for (int q = 0; q < quads; q++) d[q] = a[q] - b[q];
                        break;
                    case GraphOp.Mul:
                        for (int q = 0; q < quads; q++) d[q] = a[q] * b[q];
                        break;
                    case GraphOp.Min:
                        for (int q = 0; q < quads; q++) d[q] = math.min(a[q], b[q]);
                        break;
                    case GraphOp.Max:
                        for (int q = 0; q < quads; q++) d[q] = math.max(a[q], b[q]);
                        break;
                    case GraphOp.AddK:
                        for (int q = 0; q < quads; q++) d[q] = a[q] + k;
                        break;
                    case GraphOp.SubK:
                        for (int q = 0; q < quads; q++) d[q] = a[q] - k;
                        break;
                    case GraphOp.KSub:
                        for (int q = 0; q < quads; q++) d[q] = k - a[q];
                        break;
                    case GraphOp.MulK:
                        for (int q = 0; q < quads; q++) d[q] = a[q] * k;
                        break;
                    case GraphOp.MinK:
                        for (int q = 0; q < quads; q++) d[q] = math.min(a[q], k);
                        break;
                    case GraphOp.MaxK:
                        for (int q = 0; q < quads; q++) d[q] = math.max(a[q], k);
                        break;
                    case GraphOp.Abs:
                        for (int q = 0; q < quads; q++) d[q] = math.abs(a[q]);
                        break;
                    case GraphOp.Neg:
                        for (int q = 0; q < quads; q++) d[q] = -a[q];
                        break;
                    case GraphOp.Square:
                        for (int q = 0; q < quads; q++) d[q] = a[q] * a[q];
                        break;
                    case GraphOp.Cube:
                        for (int q = 0; q < quads; q++) d[q] = GraphKernels.Cube(a[q]);
                        break;
                    case GraphOp.HalfNegative:
                        for (int q = 0; q < quads; q++) d[q] = GraphKernels.HalfNegative(a[q]);
                        break;
                    case GraphOp.QuarterNegative:
                        for (int q = 0; q < quads; q++) d[q] = GraphKernels.QuarterNegative(a[q]);
                        break;
                    case GraphOp.SoftClamp:
                        for (int q = 0; q < quads; q++) d[q] = GraphKernels.SoftClamp(a[q]);
                        break;
                    case GraphOp.Clamp:
                        for (int q = 0; q < quads; q++) d[q] = math.clamp(a[q], k, ins.P1);
                        break;
                    case GraphOp.Lerp:
                        for (int q = 0; q < quads; q++) d[q] = GraphKernels.Lerp(a[q], b[q], c[q]);
                        break;
                    case GraphOp.Smoothstep:
                        for (int q = 0; q < quads; q++) d[q] = GraphKernels.Smoothstep(a[q], k, ins.P1);
                        break;
                    case GraphOp.RangeChoice:
                        for (int q = 0; q < quads; q++) d[q] = GraphKernels.RangeChoice(a[q], b[q], c[q], k, ins.P1);
                        break;
                    case GraphOp.ClampedMap:
                        for (int q = 0; q < quads; q++) d[q] = GraphKernels.ClampedMap(a[q], k, ins.P1, ins.P2, ins.P3);
                        break;
                    case GraphOp.Spline:{
                        // Aux: first point in the program's point table; B: point count; C: the SplineMode.
                        SplinePoint* p = points + ins.Aux;
                        int count = ins.B;
                        int mode = ins.C;
                        for (int q = 0; q < quads; q++){
                            for (int j = 0; j < count; j++)
                                splineValues[j] = p[j].Value >= 0 ? ((float4*)(registers + p[j].Value * Lanes))[q] : p[j].Constant;
                            d[q] = GraphKernels.Spline(a[q], p, splineValues, count, mode);
                        }

                        break;
                    }
                }
            }

            counters.Instructions += program.InstructionCount;
            counters.InstructionLanes += (long)program.InstructionCount * lanes;
            counters.NoiseSamples2D += noise2 * lanes;
            counters.NoiseSamples3D += noise3 * lanes;
        }

        private static void Shift(float* destination, float* coordinate, float* offset, int quads){
            float4* d = (float4*)destination;
            float4* p = (float4*)coordinate;
            float4* o = (float4*)offset;
            for (int q = 0; q < quads; q++) d[q] = p[q] + o[q];
        }
    }
}
