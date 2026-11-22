using System;

namespace Engine.Scripts.WorldGen.Graph{
    /// <summary>
    ///     Operation of a graph node or of a program instruction. Operands A, B, C are node indices in a node list
    ///     and register indices in a program; P0…P3 hold the operation's constants.
    /// </summary>
    public enum GraphOp : byte{
        /// <summary>P0.</summary>
        Const,

        /// <summary>Block coordinate x (node lists only; register 0 in a program).</summary>
        X,

        /// <summary>Block coordinate y (node lists only; register 1 in a program).</summary>
        Y,

        /// <summary>Block coordinate z (node lists only; register 2 in a program).</summary>
        Z,

        /// <summary>Noise entry Aux at the coordinates shifted by A, B, C (−1: no shift on that axis).</summary>
        Noise,

        Add,
        Sub,
        Mul,
        Min,
        Max,

        /// <summary>A + P0.</summary>
        AddK,

        /// <summary>A − P0.</summary>
        SubK,

        /// <summary>P0 − A.</summary>
        KSub,

        /// <summary>A · P0.</summary>
        MulK,

        /// <summary>min(A, P0).</summary>
        MinK,

        /// <summary>max(A, P0).</summary>
        MaxK,

        Abs,
        Neg,
        Square,
        Cube,

        /// <summary>A / 2 where A &lt; 0.</summary>
        HalfNegative,

        /// <summary>A / 4 where A &lt; 0.</summary>
        QuarterNegative,

        /// <summary>v = clamp(A, −1, 1); 1.5 v − 0.5 v³.</summary>
        SoftClamp,

        /// <summary>clamp(A, P0, P1).</summary>
        Clamp,

        /// <summary>B + A · (C − B): A is t, B the value at t = 0, C the value at t = 1.</summary>
        Lerp,

        /// <summary>t = saturate((A − P0) · P1); t² (3 − 2t). P1 is 1 / (to − from) once prepared.</summary>
        Smoothstep,

        /// <summary>P0 ≤ A &lt; P1 ? B : C (both branches evaluated).</summary>
        RangeChoice,

        /// <summary>P2 + saturate((A − P0) · P1) · P3: y_gradient once prepared (P1 = 1 / (toY − fromY), P3 = toValue − fromValue).</summary>
        ClampedMap,

        /// <summary>Spline Aux over coordinate A.</summary>
        Spline,

        /// <summary>The interpolated marker with cell height Aux; identity in a direct evaluation.</summary>
        Marker
    }

    /// <summary>A node of a resolved graph. Node lists are in topological order: operands come before their users.</summary>
    public struct GraphNode{
        public GraphOp Op;
        public int A, B, C;
        public int Aux;
        public float P0, P1, P2, P3;

        public static GraphNode Make(GraphOp op, int a = -1, int b = -1, int c = -1){
            return new GraphNode{ Op = op, A = a, B = b, C = c, Aux = -1 };
        }
    }

    /// <summary>One instruction of a flat program; operands and Dst are registers.</summary>
    public struct GraphInstruction{
        public GraphOp Op;
        public short Dst, A, B, C;
        public int Aux;
        public float P0, P1, P2, P3;
    }

    /// <summary>How a noise entry combines its octaves.</summary>
    public enum NoiseMode : byte{
        /// <summary>Σ n · amp (simplex: one octave; fbm: amplitudes already divided by their sum).</summary>
        Sum = 0,

        /// <summary>Σ (1 − |n|) · amp.</summary>
        Ridged = 1
    }

    /// <summary>A noise as sampled by one node kind: its dimension, how octaves combine and its octave range.</summary>
    public struct NoiseEntry{
        public int Dimensions;
        public NoiseMode Mode;
        public int OctaveStart, OctaveCount;

        /// <summary>Index of the noise definition (for the noise-count limit and messages).</summary>
        public int Definition;
    }

    /// <summary>One octave: frequencies across and along y, amplitude (already scaled) and the hash salt.</summary>
    public struct NoiseOctave{
        public float FrequencyXZ, FrequencyY, Amplitude;
        public uint Salt;
    }

    /// <summary>A spline: its point range and whether segments are linear instead of cubic.</summary>
    public struct SplineInfo{
        public int PointStart, PointCount;
        public int Linear;
    }

    /// <summary>
    ///     One spline point. Value is a node (node lists) or a register (programs); a negative Value means the
    ///     constant <see cref="Constant" />. Span and InverseSpan describe the segment to the next point.
    /// </summary>
    public struct SplinePoint{
        public float Location, Derivative, Constant, Span, InverseSpan;
        public int Value;

        /// <summary>1 when the derivative was given in the graph, 0 when it is derived.</summary>
        public int HasDerivative;
    }

    /// <summary>Which data a program runs on.</summary>
    public enum GraphStage : byte{
        /// <summary>Stride-4 lattice points; y is 0.</summary>
        Lattice = 0,

        /// <summary>Every column of a region (outputs flagged per column); y is 0.</summary>
        Column = 1,

        /// <summary>The corners of one corner column (inputs of the markers with one cell height).</summary>
        Corner = 2,

        /// <summary>The voxels of one cell (4 × 8 × 4).</summary>
        Voxel = 3
    }

    /// <summary>Where an input register of a program is filled from.</summary>
    public enum GraphSlotSource : byte{
        /// <summary>A lattice value (exact in the corner program, bilinear per column in the voxel program).</summary>
        Lattice = 0,

        /// <summary>A marker's corner values, trilinear per voxel.</summary>
        Marker = 1
    }

    /// <summary>An input or output register of a program and the slot it maps to.</summary>
    public struct GraphSlot{
        public int Register;
        public GraphSlotSource Source;
        public int Slot;
    }

    /// <summary>A flat program inside <see cref="CompiledNoiseGraph" />'s instruction and slot tables.</summary>
    public struct GraphProgram{
        public GraphStage Stage;
        public int CellHeight;
        public int InstructionStart, InstructionCount;
        public int RegisterCount;
        public int InputStart, InputCount;
        public int OutputStart, OutputCount;
    }

    /// <summary>Interval [Min, Max] that every value of a node lies in.</summary>
    [Serializable]
    public struct GraphBounds{
        public float Min, Max;

        public GraphBounds(float min, float max){
            Min = min;
            Max = max;
        }

        public bool Contains(float v){
            return v >= Min && v <= Max;
        }

        public override string ToString(){
            return $"[{Min:0.####}, {Max:0.####}]";
        }
    }

    /// <summary>Severity of a validator message.</summary>
    public enum GraphMessageSeverity{
        Warning,
        Error
    }

    /// <summary>A validator message naming the file and, where there is one, the node.</summary>
    public readonly struct GraphMessage{
        public readonly GraphMessageSeverity Severity;
        public readonly string File;
        public readonly string Node;
        public readonly string Text;

        public GraphMessage(GraphMessageSeverity severity, string file, string node, string text){
            Severity = severity;
            File = file;
            Node = node;
            Text = text;
        }

        public override string ToString(){
            string where = string.IsNullOrEmpty(Node) ? File : $"{File}, node \"{Node}\"";
            return $"{Severity}: {where}: {Text}";
        }
    }

    /// <summary>Limits of a graph (the validator rejects graphs over them).</summary>
    public static class GraphLimits{
        public const int MaxInstructions = 512;
        public const int MaxRegisters = 64;
        public const int MaxNoises = 64;
        public const int MaxSplinePoints = 16;
        public const int MaxSplineDepth = 8;
        public const int MaxOctaves = 16;

        /// <summary>Deepest nesting of objects and arrays a graph file may have (inline nodes, spline points).</summary>
        public const int MaxJsonNesting = 64;

        /// <summary>Lanes of the interpreter's registers: one 4 × 8 × 4 voxel cell.</summary>
        public const int MaxLanes = 128;

        /// <summary>Stride of the 2D lattice and horizontal size of a cell, in blocks.</summary>
        public const int LatticeStride = 4;

        /// <summary>Height of a voxel cell (the voxel program's lanes are 4 × CellBlockHeight × 4).</summary>
        public const int CellBlockHeight = 8;

        /// <summary>The y range of the world: 0 to WorldHeight − 1.</summary>
        public const int WorldHeight = 256;

        /// <summary>Bounds of the x and z inputs the interval rules assume.</summary>
        public const float CoordinateLimit = 16777216f;

        /// <summary>Relative margin every rounding bound is widened by (times max(1, |bound|)).</summary>
        public const float BoundsMargin = 1e-4f;
    }

    /// <summary>The named outputs the generator reads; climate outputs and PV are clamped to [−1, 1].</summary>
    public static class GraphOutputs{
        public const string Temperature = "temperature";
        public const string Humidity = "humidity";
        public const string Continentalness = "continentalness";
        public const string Erosion = "erosion";
        public const string Weirdness = "weirdness";
        public const string PeaksValleys = "pv";

        /// <summary>Outputs every graph must have, clamped to [−1, 1].</summary>
        public static readonly string[] Climate ={ Temperature, Humidity, Continentalness, Erosion, Weirdness, PeaksValleys };

        public static bool IsClimate(string name){
            return Array.IndexOf(Climate, name) >= 0;
        }
    }

    /// <summary>Work done by an evaluation, summed over runs: lanes per program, noise samples, instructions.</summary>
    public struct GraphWorkCounters{
        /// <summary>Lanes of lattice program runs (lattice points).</summary>
        public long LatticePoints;

        /// <summary>Lanes of column program runs.</summary>
        public long ColumnPoints;

        /// <summary>Lanes of corner program runs (corners).</summary>
        public long Corners;

        /// <summary>Lanes of voxel program runs (voxels).</summary>
        public long Voxels;

        /// <summary>Voxel cells run.</summary>
        public long Cells;

        /// <summary>Single-octave noise samples, 2D and 3D.</summary>
        public long NoiseSamples2D, NoiseSamples3D;

        /// <summary>Instructions dispatched and instruction × lanes.</summary>
        public long Instructions, InstructionLanes;

        /// <summary>Marker values interpolated per voxel.</summary>
        public long InterpolatedValues;

        public void Add(in GraphWorkCounters o){
            LatticePoints += o.LatticePoints;
            ColumnPoints += o.ColumnPoints;
            Corners += o.Corners;
            Voxels += o.Voxels;
            Cells += o.Cells;
            NoiseSamples2D += o.NoiseSamples2D;
            NoiseSamples3D += o.NoiseSamples3D;
            Instructions += o.Instructions;
            InstructionLanes += o.InstructionLanes;
            InterpolatedValues += o.InterpolatedValues;
        }

        public override string ToString(){
            return $"lattice {LatticePoints}, columns {ColumnPoints}, corners {Corners}, voxels {Voxels} in {Cells} cells, " +
                   $"noise 2D {NoiseSamples2D} / 3D {NoiseSamples3D}, instructions {Instructions} ({InstructionLanes} lane ops), " +
                   $"interpolated {InterpolatedValues}";
        }
    }
}
