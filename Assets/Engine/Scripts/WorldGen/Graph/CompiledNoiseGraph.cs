using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Engine.Scripts.WorldGen.Graph{
    /// <summary>One output of a compiled graph and where an evaluation puts its values.</summary>
    public sealed class GraphOutputInfo{
        public string Name;

        /// <summary>True for outputs that depend on y (evaluated per voxel); false for 2D outputs.</summary>
        public bool Is3D;

        /// <summary>2D outputs evaluated for every column (besides the lattice).</summary>
        public bool PerColumn;

        /// <summary>Index among the lattice values (2D outputs), else −1.</summary>
        public int LatticeValue = -1;

        /// <summary>Index among the voxel outputs (3D outputs), else −1.</summary>
        public int VoxelOutput = -1;

        /// <summary>Index among the column outputs (per-column 2D outputs), else −1.</summary>
        public int ColumnOutput = -1;

        /// <summary>The marker slot when the output is an interpolated marker itself, else −1.</summary>
        public int Marker = -1;

        /// <summary>Only the bound is known: no program computes the values (LatticeValue and VoxelOutput are −1).</summary>
        public bool BoundOnly;

        /// <summary>The output's node in the uncompiled node list (for the direct evaluation).</summary>
        public int ReferenceNode;

        /// <summary>Bound of every value of the output.</summary>
        public GraphBounds Bounds;
    }

    /// <summary>Size of one program, for the preview and reports.</summary>
    public struct GraphProgramStats{
        public GraphStage Stage;
        public int CellHeight, Instructions, Registers, Inputs, Outputs;

        public override string ToString(){
            string cell = Stage == GraphStage.Corner ? $" (cell height {CellHeight})" : "";
            return $"{Stage}{cell}: {Instructions} instructions, {Registers} registers, {Inputs} inputs, {Outputs} outputs";
        }
    }

    /// <summary>The tables of a compiled graph as a job reads them.</summary>
    public struct GraphData{
        [ReadOnly] public NativeArray<GraphInstruction> Instructions;
        [ReadOnly] public NativeArray<GraphProgram> Programs;
        [ReadOnly] public NativeArray<GraphSlot> Slots;
        [ReadOnly] public NativeArray<NoiseEntry> Entries;
        [ReadOnly] public NativeArray<NoiseOctave> Octaves;
        [ReadOnly] public NativeArray<SplinePoint> Points;

        /// <summary>Cell height of each marker (4 or 8).</summary>
        [ReadOnly] public NativeArray<int> MarkerCellHeights;

        /// <summary>Program indices, −1 when the graph has no such program.</summary>
        public int LatticeProgram, ColumnProgram, CornerProgram4, CornerProgram8, VoxelProgram;

        public int LatticeValueCount, ColumnOutputCount, MarkerCount, VoxelOutputCount;
    }

    /// <summary>
    ///     Lengths of a graph's tables. Generated code reads the running graph's tables with offsets of the graph it
    ///     was made from, so it runs only a graph with the same sizes as well as the same code hash.
    /// </summary>
    internal readonly struct GraphTableSizes : IEquatable<GraphTableSizes>{
        public readonly int Instructions, Programs, Slots, Entries, Octaves, Points, Markers;

        public GraphTableSizes(int instructions, int programs, int slots, int entries, int octaves, int points, int markers){
            Instructions = instructions;
            Programs = programs;
            Slots = slots;
            Entries = entries;
            Octaves = octaves;
            Points = points;
            Markers = markers;
        }

        public static GraphTableSizes Of(in GraphData d){
            return new GraphTableSizes(Length(d.Instructions), Length(d.Programs), Length(d.Slots), Length(d.Entries), Length(d.Octaves),
                Length(d.Points), Length(d.MarkerCellHeights));
        }

        private static int Length<T>(NativeArray<T> array) where T : struct{
            return array.IsCreated ? array.Length : 0;
        }

        public bool Equals(GraphTableSizes o){
            return Instructions == o.Instructions && Programs == o.Programs && Slots == o.Slots && Entries == o.Entries && Octaves == o.Octaves &&
                   Points == o.Points && Markers == o.Markers;
        }

        public override bool Equals(object obj){
            return obj is GraphTableSizes o && Equals(o);
        }

        public override int GetHashCode(){
            return HashCode.Combine(Instructions, Programs, Slots, Entries, Octaves, Points, Markers);
        }

        public override string ToString(){
            return $"{Instructions} instructions, {Programs} programs, {Slots} slots, {Entries} noise entries, {Octaves} octaves, {Points} spline points, "
                   + $"{Markers} markers";
        }
    }

    /// <summary>64-bit FNV-1a over the fields of a graph's tables (float fields by their bits), in table order.</summary>
    internal static class GraphCodeHash{
        private const ulong Offset = 14695981039346656037UL;
        private const ulong Prime = 1099511628211UL;

        public static ulong Of(in GraphData d){
            ulong h = Offset;
            Mix(ref h, d.LatticeProgram, d.ColumnProgram, d.CornerProgram4, d.CornerProgram8, d.VoxelProgram);
            Mix(ref h, d.LatticeValueCount, d.ColumnOutputCount, d.MarkerCount, d.VoxelOutputCount);
            Mix(ref h, Length(d.Instructions));
            for (int k = 0; k < Length(d.Instructions); k++){
                GraphInstruction i = d.Instructions[k];
                Mix(ref h, (int)i.Op, i.Dst, i.A, i.B, i.C, i.Aux);
                MixFloats(ref h, i.P0, i.P1, i.P2, i.P3);
            }

            Mix(ref h, Length(d.Programs));
            for (int k = 0; k < Length(d.Programs); k++){
                GraphProgram p = d.Programs[k];
                Mix(ref h, (int)p.Stage, p.CellHeight, p.InstructionStart, p.InstructionCount, p.RegisterCount);
                Mix(ref h, p.InputStart, p.InputCount, p.OutputStart, p.OutputCount);
            }

            Mix(ref h, Length(d.Slots));
            for (int k = 0; k < Length(d.Slots); k++){
                GraphSlot s = d.Slots[k];
                Mix(ref h, s.Register, (int)s.Source, s.Slot);
            }

            Mix(ref h, Length(d.Entries));
            for (int k = 0; k < Length(d.Entries); k++){
                NoiseEntry e = d.Entries[k];
                Mix(ref h, e.Dimensions, (int)e.Mode, e.OctaveStart, e.OctaveCount, e.Definition);
            }

            Mix(ref h, Length(d.Octaves));
            for (int k = 0; k < Length(d.Octaves); k++){
                NoiseOctave o = d.Octaves[k];
                MixFloats(ref h, o.FrequencyXZ, o.FrequencyY, o.Amplitude);
                Mix(ref h, unchecked((int)o.Salt));
            }

            Mix(ref h, Length(d.Points));
            for (int k = 0; k < Length(d.Points); k++){
                SplinePoint p = d.Points[k];
                MixFloats(ref h, p.Location, p.Derivative, p.Constant, p.Span);
                MixFloats(ref h, p.InverseSpan);
                Mix(ref h, p.Value, p.HasDerivative);
            }

            Mix(ref h, Length(d.MarkerCellHeights));
            for (int k = 0; k < Length(d.MarkerCellHeights); k++) Mix(ref h, d.MarkerCellHeights[k]);
            return h;
        }

        private static int Length<T>(NativeArray<T> array) where T : struct{
            return array.IsCreated ? array.Length : 0;
        }

        private static void Mix(ref ulong h, params int[] values){
            foreach (int v in values){
                uint u = unchecked((uint)v);
                for (int b = 0; b < 4; b++) h = unchecked((h ^ ((u >> (8 * b)) & 0xFF)) * Prime);
            }
        }

        private static void MixFloats(ref ulong h, params float[] values){
            foreach (float v in values) Mix(ref h, unchecked((int)math.asuint(v)));
        }
    }

    /// <summary>
    ///     A compiled noise graph: the 2D (lattice and column), corner and voxel programs with their tables, the
    ///     outputs and bounds, and the uncompiled node list for the direct evaluation. Independent of the world
    ///     seed, which every evaluation takes. Owns native memory: dispose it.
    /// </summary>
    public sealed class CompiledNoiseGraph : IDisposable{
        internal GraphData Data;

        internal NativeArray<GraphNode> ReferenceNodes;
        internal NativeArray<SplineInfo> ReferenceSplines;
        internal NativeArray<SplinePoint> ReferencePoints;

        /// <summary>Bound of every node of the uncompiled node list.</summary>
        internal NativeArray<GraphBounds> ReferenceBounds;

        internal string[] ReferenceNames;

        public IReadOnlyList<GraphOutputInfo> Outputs{ get; internal set; }
        public IReadOnlyList<GraphProgramStats> Programs{ get; internal set; }

        /// <summary>Instructions over all programs (the limit is <see cref="GraphLimits.MaxInstructions" />).</summary>
        public int InstructionCount{ get; internal set; }

        public int NoiseCount{ get; internal set; }

        public int ReferenceNodeCount => ReferenceNodes.Length;

        private ulong _codeHash;

        /// <summary>
        ///     Hash over every field of the program tables. Generated code carries the hash of the graph it was made
        ///     from and only runs a graph with the same hash.
        /// </summary>
        public ulong CodeHash{
            get{
                if (_codeHash == 0) _codeHash = GraphCodeHash.Of(Data);
                return _codeHash;
            }
        }

        public GraphData Tables => Data;

        internal GraphTableSizes Sizes => GraphTableSizes.Of(Data);

        public GraphOutputInfo Output(string name){
            foreach (GraphOutputInfo o in Outputs)
                if (o.Name == name)
                    return o;
            return null;
        }

        public GraphBounds ReferenceBound(int node){
            return ReferenceBounds[node];
        }

        public string ReferenceName(int node){
            return ReferenceNames[node];
        }

        /// <summary>
        ///     Direct, uncompiled evaluation of <paramref name="nodes" /> (indices into the uncompiled node list) at
        ///     <paramref name="positions" />: values position-major, <c>[position * nodes.Length + i]</c>.
        /// </summary>
        public float[] EvaluateDirect(IReadOnlyList<float3> positions, IReadOnlyList<int> nodes, uint seed){
            using NativeArray<float3> p = new(positions.ToArray(), Allocator.TempJob);
            using NativeArray<int> select = new(nodes.ToArray(), Allocator.TempJob);
            using NativeArray<float> values = new(positions.Count * nodes.Count, Allocator.TempJob);
            new GraphDirectJob{
                Nodes = ReferenceNodes,
                Splines = ReferenceSplines,
                Points = ReferencePoints,
                Entries = Data.Entries,
                Octaves = Data.Octaves,
                Positions = p,
                Select = select,
                Seed = seed,
                Values = values
            }.Run();
            return values.ToArray();
        }

        public void Dispose(){
            Dispose(ref Data.Instructions);
            Dispose(ref Data.Programs);
            Dispose(ref Data.Slots);
            Dispose(ref Data.Entries);
            Dispose(ref Data.Octaves);
            Dispose(ref Data.Points);
            Dispose(ref Data.MarkerCellHeights);
            Dispose(ref ReferenceNodes);
            Dispose(ref ReferenceSplines);
            Dispose(ref ReferencePoints);
            Dispose(ref ReferenceBounds);
        }

        private static void Dispose<T>(ref NativeArray<T> array) where T : struct{
            if (array.IsCreated) array.Dispose();
            array = default;
        }
    }
}
