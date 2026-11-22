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

        public GraphData Tables => Data;

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
