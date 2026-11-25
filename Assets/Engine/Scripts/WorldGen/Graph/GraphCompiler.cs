using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using Unity.Jobs;

namespace Engine.Scripts.WorldGen.Graph{
    /// <summary>Result of a compile: the graph (null when an error was reported) and every message.</summary>
    public sealed class GraphCompileResult{
        public CompiledNoiseGraph Graph;
        public readonly List<GraphMessage> Messages = new();

        public bool Succeeded => Graph != null;

        public IEnumerable<GraphMessage> Errors => Messages.Where(m => m.Severity == GraphMessageSeverity.Error);
    }

    /// <summary>
    ///     Compiles a <see cref="NoiseGraph" /> into flat programs:
    ///     <list type="bullet">
    ///         <item>the lattice program: the 2D values (nodes that do not depend on y) on the stride-4 lattice;</item>
    ///         <item>the column program: the 2D outputs flagged per column, for every column;</item>
    ///         <item>one corner program per cell height: the inputs of the interpolated markers at the lattice corners;</item>
    ///         <item>the voxel program: the nodes above the markers, per voxel of a 4 × 8 × 4 cell.</item>
    ///     </list>
    ///     2D values enter the corner program exact and the voxel program interpolated per column; marker values
    ///     enter the voxel program interpolated trilinearly. Each program gets topological order, constant folding,
    ///     common subexpression elimination and register allocation. All float math (folding, tangents, bounds,
    ///     octave tables) runs in Burst jobs; the managed side only moves values and compares bit patterns.
    /// </summary>
    public static class GraphCompiler{
        /// <summary>Parses and compiles JSON text; a malformed file is an error naming <paramref name="file" />.</summary>
        public static GraphCompileResult Compile(string json, string file, IEnumerable<string> requiredOutputs = null){
            NoiseGraph graph;
            try{
                graph = NoiseGraphJson.Parse(json);
            }
            catch (FormatException e){
                GraphCompileResult failed = new();
                failed.Messages.Add(new GraphMessage(GraphMessageSeverity.Error, file, null, e.Message));
                return failed;
            }

            return Compile(graph, file, requiredOutputs);
        }

        public static GraphCompileResult Compile(NoiseGraph graph, string file, IEnumerable<string> requiredOutputs = null){
            GraphCompileResult result = new();
            GraphIr ir = GraphResolver.Resolve(graph, file, requiredOutputs ?? GraphOutputs.Climate, result.Messages);
            if (ir == null) return result;
            result.Graph = new Builder(ir, file, result.Messages).Build();
            return result;
        }

        private sealed class Builder{
            private readonly GraphIr _ir;
            private readonly string _file;
            private readonly List<GraphMessage> _messages;

            // Folded node list.
            private readonly List<GraphNode> _nodes = new();
            private readonly List<SplineInfo> _splines = new();
            private readonly List<SplinePoint> _points = new();
            private readonly List<bool> _dependsY = new();
            private readonly List<string> _names = new();

            // Programs.
            private readonly List<GraphInstruction> _code = new();
            private readonly List<GraphProgram> _programs = new();
            private readonly List<GraphSlot> _slots = new();
            private readonly List<SplinePoint> _programPoints = new();
            private readonly List<GraphProgramStats> _stats = new();
            private readonly List<int> _latticeNodes = new();
            private readonly Dictionary<int, int> _latticeIndex = new();
            private readonly List<int> _markerNodes = new();
            private readonly Dictionary<int, int> _markerIndex = new();

            public Builder(GraphIr ir, string file, List<GraphMessage> messages){
                _ir = ir;
                _file = file;
                _messages = messages;
            }

            private void Error(string node, string text){
                _messages.Add(new GraphMessage(GraphMessageSeverity.Error, _file, node, text));
            }

            public CompiledNoiseGraph Build(){
                int count = _ir.Nodes.Count;
                byte[] constant = ConstantNodes();
                int octaveCount = _ir.Entries.Sum(e => e.OctaveCount);

                // Burst: octave tables, prepared constants, spline spans and tangents, folded values.
                NativeArray<GraphNode> nodes = new(_ir.Nodes.ToArray(), Allocator.Persistent);
                NativeArray<SplinePoint> points = new(_ir.Points.ToArray(), Allocator.Persistent);
                NativeArray<SplineInfo> splines = new(_ir.Splines.ToArray(), Allocator.Persistent);
                NativeArray<NoiseEntry> entries = new(_ir.Entries.ToArray(), Allocator.Persistent);
                NativeArray<NoiseOctave> octaves = new(octaveCount, Allocator.Persistent);
                using NativeArray<NoiseSource> sources = new(_ir.Sources.ToArray(), Allocator.TempJob);
                using NativeArray<byte> constantFlags = new(constant, Allocator.TempJob);
                using NativeArray<float> values = new(count, Allocator.TempJob);
                new GraphPrepareJob{
                    Nodes = nodes,
                    Points = points,
                    Splines = splines,
                    Sources = sources,
                    Entries = entries,
                    Octaves = octaves,
                    Constant = constantFlags,
                    Values = values
                }.Run();

                bool octavesFinite = CheckOctaves(octaves);
                GraphNode[] prepared = nodes.ToArray();
                SplinePoint[] preparedPoints = points.ToArray();
                Fold(prepared, preparedPoints, constant, values);

                NativeArray<GraphBounds> referenceBounds = Bounds(nodes, splines, points, entries, octaves, Allocator.Persistent);
                using NativeArray<GraphNode> foldedNodes = new(_nodes.ToArray(), Allocator.TempJob);
                using NativeArray<SplineInfo> foldedSplines = new(_splines.ToArray(), Allocator.TempJob);
                using NativeArray<SplinePoint> foldedPoints = new(_points.ToArray(), Allocator.TempJob);
                using NativeArray<GraphBounds> foldedBounds = Bounds(foldedNodes, foldedSplines, foldedPoints, entries, octaves, Allocator.TempJob);
                if (octavesFinite) CheckBounds(foldedBounds);

                List<GraphOutputInfo> outputs = Stage(foldedBounds);
                CheckTerrain(outputs);
                int instructions = _code.Count;
                if (instructions > GraphLimits.MaxInstructions)
                    Error(null, $"The graph compiles to {instructions} instructions; the limit is {GraphLimits.MaxInstructions}.");

                if (_messages.Any(m => m.Severity == GraphMessageSeverity.Error)){
                    nodes.Dispose();
                    points.Dispose();
                    splines.Dispose();
                    entries.Dispose();
                    octaves.Dispose();
                    referenceBounds.Dispose();
                    return null;
                }

                return new CompiledNoiseGraph{
                    Data = new GraphData{
                        Instructions = new NativeArray<GraphInstruction>(_code.ToArray(), Allocator.Persistent),
                        Programs = new NativeArray<GraphProgram>(_programs.ToArray(), Allocator.Persistent),
                        Slots = new NativeArray<GraphSlot>(_slots.ToArray(), Allocator.Persistent),
                        Entries = entries,
                        Octaves = octaves,
                        Points = new NativeArray<SplinePoint>(_programPoints.ToArray(), Allocator.Persistent),
                        MarkerCellHeights = new NativeArray<int>(_markerNodes.Select(m => _nodes[m].Aux).ToArray(), Allocator.Persistent),
                        LatticeProgram = ProgramIndex(GraphStage.Lattice, 0),
                        ColumnProgram = ProgramIndex(GraphStage.Column, 0),
                        CornerProgram4 = ProgramIndex(GraphStage.Corner, 4),
                        CornerProgram8 = ProgramIndex(GraphStage.Corner, 8),
                        VoxelProgram = ProgramIndex(GraphStage.Voxel, 0),
                        LatticeValueCount = _latticeNodes.Count,
                        ColumnOutputCount = outputs.Count(o => o.ColumnOutput >= 0),
                        MarkerCount = _markerNodes.Count,
                        VoxelOutputCount = outputs.Count(o => o.Is3D)
                    },
                    ReferenceNodes = nodes,
                    ReferenceSplines = splines,
                    ReferencePoints = points,
                    ReferenceBounds = referenceBounds,
                    ReferenceNames = _ir.NodeNames.ToArray(),
                    Outputs = outputs,
                    Programs = _stats,
                    InstructionCount = instructions,
                    NoiseCount = _ir.NoiseDefinitionCount
                };
            }

            private static bool Finite(float v){
                return !float.IsNaN(v) && !float.IsInfinity(v);
            }

            /// <summary>
            ///     Reports each noise whose octave table left the float range (a gain or lacunarity raised to many
            ///     octaves, or a tiny wavelength): every value of such a noise would be NaN. False when one did.
            /// </summary>
            private bool CheckOctaves(NativeArray<NoiseOctave> octaves){
                HashSet<int> reported = new();
                foreach (NoiseEntry e in _ir.Entries)
                    for (int o = e.OctaveStart; o < e.OctaveStart + e.OctaveCount; o++){
                        NoiseOctave octave = octaves[o];
                        if (Finite(octave.FrequencyXZ) && Finite(octave.FrequencyY) && Finite(octave.Amplitude)) continue;
                        if (reported.Add(e.Definition))
                            Error(null, $"Noise \"{_ir.NoiseNames[e.Definition]}\": its octaves leave the float range; use a smaller gain or "
                                        + "lacunarity, fewer octaves or a longer wavelength.");
                        break;
                    }

                return reported.Count == 0;
            }

            /// <summary>
            ///     Reports the first node whose bound is not finite: its values can overflow, so the bound promises
            ///     nothing (and NaN would follow from inf − inf or 0 · inf).
            /// </summary>
            private void CheckBounds(NativeArray<GraphBounds> bounds){
                for (int f = 0; f < bounds.Length; f++){
                    GraphBounds b = bounds[f];
                    if (Finite(b.Min) && Finite(b.Max)) continue;
                    Error(null, $"\"{_names[f]}\": its values can leave the float range (bound {b}); clamp it or use smaller numbers.");
                    return;
                }
            }

            private int ProgramIndex(GraphStage stage, int cellHeight){
                for (int i = 0; i < _programs.Count; i++)
                    if (_programs[i].Stage == stage && (stage != GraphStage.Corner || _programs[i].CellHeight == cellHeight))
                        return i;
                return -1;
            }

            private static NativeArray<GraphBounds> Bounds(NativeArray<GraphNode> nodes, NativeArray<SplineInfo> splines, NativeArray<SplinePoint> points,
                NativeArray<NoiseEntry> entries, NativeArray<NoiseOctave> octaves, Allocator allocator){
                NativeArray<GraphBounds> bounds = new(nodes.Length, allocator);
                new GraphBoundsJob{ Nodes = nodes, Splines = splines, Points = points, Entries = entries, Octaves = octaves, Bounds = bounds }.Run();
                return bounds;
            }

            /// <summary>1 for nodes that depend on no coordinate and no noise.</summary>
            private byte[] ConstantNodes(){
                byte[] constant = new byte[_ir.Nodes.Count];
                for (int i = 0; i < constant.Length; i++){
                    GraphOp op = _ir.Nodes[i].Op;
                    if (op == GraphOp.Const){
                        constant[i] = 1;
                        continue;
                    }

                    if (op is GraphOp.X or GraphOp.Y or GraphOp.Z or GraphOp.Noise) continue;
                    bool all = true;
                    foreach (int o in GraphResolver.Operands(_ir, i)) all &= constant[o] != 0;
                    constant[i] = (byte)(all ? 1 : 0);
                }

                return constant;
            }

            // ---- Folding and common subexpressions ------------------------------------------------------------

            private void Fold(GraphNode[] prepared, SplinePoint[] preparedPoints, byte[] constant, NativeArray<float> values){
                Dictionary<(GraphOp, int, int, int, int, int, int, int, int), int> known = new();
                int[] map = new int[prepared.Length];
                for (int i = 0; i < prepared.Length; i++){
                    GraphNode n = prepared[i];
                    if (constant[i] != 0){
                        n = GraphNode.Make(GraphOp.Const);
                        n.P0 = values[i];
                    }
                    else{
                        n.A = n.A >= 0 ? map[n.A] : -1;
                        n.B = n.B >= 0 ? map[n.B] : -1;
                        n.C = n.C >= 0 ? map[n.C] : -1;
                        if (n.Op == GraphOp.Marker && !_dependsY[n.A]){
                            // A marker over a 2D value interpolates nothing: it is the identity.
                            map[i] = n.A;
                            continue;
                        }

                        if (n.Op == GraphOp.Spline){
                            SplineInfo s = _ir.Splines[n.Aux];
                            int start = _points.Count;
                            for (int k = 0; k < s.PointCount; k++){
                                SplinePoint p = preparedPoints[s.PointStart + k];
                                if (p.Value >= 0) p.Value = map[p.Value];
                                _points.Add(p);
                            }

                            n.Aux = _splines.Count;
                            _splines.Add(new SplineInfo{ PointStart = start, PointCount = s.PointCount, Mode = s.Mode });
                        }

                        if (n.Op is GraphOp.Add or GraphOp.Mul && n.A > n.B) (n.A, n.B) = (n.B, n.A);
                    }

                    var key = (n.Op, n.A, n.B, n.C, n.Aux, Bits(n.P0), Bits(n.P1), Bits(n.P2), Bits(n.P3));
                    if (known.TryGetValue(key, out int existing)){
                        map[i] = existing;
                        continue;
                    }

                    map[i] = _nodes.Count;
                    known[key] = _nodes.Count;
                    _nodes.Add(n);
                    _names.Add(_ir.NodeNames[i]);
                    _dependsY.Add(DependsOnY(_nodes.Count - 1));
                }

                for (int o = 0; o < _ir.Outputs.Count; o++){
                    ResolvedOutput output = _ir.Outputs[o];
                    _foldedOutputs.Add(map[output.Node]);
                }
            }

            private readonly List<int> _foldedOutputs = new();

            private static int Bits(float v){
                return BitConverter.SingleToInt32Bits(v);
            }

            private bool DependsOnY(int f){
                GraphNode n = _nodes[f];
                if (n.Op == GraphOp.Y) return true;
                if (n.Op == GraphOp.Noise && _ir.Entries[n.Aux].Dimensions == 3) return true;
                foreach (int o in Operands(f))
                    if (_dependsY[o])
                        return true;
                return false;
            }

            private IEnumerable<int> Operands(int f){
                GraphNode n = _nodes[f];
                if (n.A >= 0) yield return n.A;
                if (n.B >= 0) yield return n.B;
                if (n.C >= 0) yield return n.C;
                if (n.Op != GraphOp.Spline) yield break;
                SplineInfo s = _splines[n.Aux];
                for (int k = 0; k < s.PointCount; k++){
                    int v = _points[s.PointStart + k].Value;
                    if (v >= 0) yield return v;
                }
            }

            private bool IsTrivial(int f){
                return _nodes[f].Op is GraphOp.Const or GraphOp.X or GraphOp.Y or GraphOp.Z;
            }

            /// <summary>A 2D value worth a lattice slot: independent of y and not a constant or coordinate.</summary>
            private bool Is2D(int f){
                return !_dependsY[f] && !IsTrivial(f);
            }

            // ---- Staging ------------------------------------------------------------------------------------------

            private List<GraphOutputInfo> Stage(NativeArray<GraphBounds> bounds){
                List<GraphOutputInfo> outputs = new();
                List<(int node, int slot)> voxelRoots = new();
                List<(int node, int slot)> columnRoots = new();
                for (int o = 0; o < _ir.Outputs.Count; o++){
                    ResolvedOutput r = _ir.Outputs[o];
                    int f = _foldedOutputs[o];
                    GraphOutputInfo info = new(){
                        Name = r.Name, Is3D = _dependsY[f], PerColumn = r.PerColumn, BoundOnly = r.BoundOnly, ReferenceNode = r.Node, Bounds = bounds[f]
                    };
                    if (info.BoundOnly){
                        // Only the bound is read; no program computes the values.
                    }
                    else if (info.Is3D){
                        if (r.PerColumn) Error(null, $"Output \"{r.Name}\" depends on y; only 2D outputs can be evaluated per column.");
                        info.VoxelOutput = voxelRoots.Count;
                        voxelRoots.Add((f, info.VoxelOutput));
                    }
                    else{
                        info.LatticeValue = LatticeSlot(f);
                        if (r.PerColumn){
                            info.ColumnOutput = columnRoots.Count;
                            columnRoots.Add((f, info.ColumnOutput));
                        }
                    }

                    outputs.Add(info);
                }

                if (voxelRoots.Count > 0)
                    BuildProgram(GraphStage.Voxel, 0, voxelRoots, f => {
                        if (_nodes[f].Op == GraphOp.Marker) return (true, GraphSlotSource.Marker, MarkerSlot(f));
                        return Is2D(f) ? (true, GraphSlotSource.Lattice, LatticeSlot(f)) : (false, GraphSlotSource.Lattice, -1);
                    });

                // An output that is a marker itself names its marker slot (assigned while the voxel program was built).
                for (int o = 0; o < outputs.Count; o++)
                    if (!outputs[o].BoundOnly && _markerIndex.TryGetValue(_foldedOutputs[o], out int marker))
                        outputs[o].Marker = marker;

                foreach (int cellHeight in new[]{ 4, 8 }){
                    List<(int node, int slot)> roots = new();
                    for (int m = 0; m < _markerNodes.Count; m++)
                        if (_nodes[_markerNodes[m]].Aux == cellHeight)
                            roots.Add((_nodes[_markerNodes[m]].A, m));
                    if (roots.Count > 0)
                        BuildProgram(GraphStage.Corner, cellHeight, roots,
                            f => Is2D(f) ? (true, GraphSlotSource.Lattice, LatticeSlot(f)) : (false, GraphSlotSource.Lattice, -1));
                }

                if (_latticeNodes.Count > 0)
                    BuildProgram(GraphStage.Lattice, 0, _latticeNodes.Select((n, i) => (n, i)).ToList(), _ => (false, GraphSlotSource.Lattice, -1));
                if (columnRoots.Count > 0) BuildProgram(GraphStage.Column, 0, columnRoots, _ => (false, GraphSlotSource.Lattice, -1));
                return outputs;
            }

            /// <summary>
            ///     The terrain outputs come as a set (§5.1): terrain_density an interpolated marker itself (so its corner
            ///     signs decide uniform cells), preliminary_surface and factor 2D, factor above 0, terrain_noise,
            ///     top_slide_start and top_slide_end bound-only, the slide's start and end constants with
            ///     0 ≤ start ≤ end ≤ the world top, and preliminary_surface at most the slide's end, the graph's
            ///     generation top (AD-45).
            /// </summary>
            private void CheckTerrain(List<GraphOutputInfo> outputs){
                GraphOutputInfo Find(string name) => outputs.FirstOrDefault(o => o.Name == name);
                GraphOutputInfo density = Find(GraphOutputs.TerrainDensity);
                // terrain_density alone is an ordinary 3D output (the V1 test graphs); any of the other four makes the
                // graph a terrain graph, which needs all five.
                bool any = GraphOutputs.Terrain.Any(n => n != GraphOutputs.TerrainDensity && Find(n) != null);
                if (!any) return;
                foreach (string name in GraphOutputs.Terrain)
                    if (Find(name) == null)
                        Error(null, $"The terrain output \"{name}\" is missing; the terrain outputs {string.Join(", ", GraphOutputs.Terrain)} come together.");
                if (density != null && (!density.Is3D || density.Marker < 0))
                    Error(null, $"Output \"{GraphOutputs.TerrainDensity}\" must be an interpolated node itself.");
                foreach (string name in new[]{ GraphOutputs.PreliminarySurface, GraphOutputs.Factor }){
                    GraphOutputInfo o = Find(name);
                    if (o != null && (o.Is3D || o.BoundOnly)) Error(null, $"Output \"{name}\" must be a 2D output.");
                }

                GraphOutputInfo factor = Find(GraphOutputs.Factor);
                if (factor != null && !(factor.Bounds.Min > 0f))
                    Error(null, $"Output \"{GraphOutputs.Factor}\" must stay above 0; its bound is {factor.Bounds}.");
                GraphOutputInfo noise = Find(GraphOutputs.TerrainNoise);
                if (noise != null && !noise.BoundOnly) Error(null, $"Output \"{GraphOutputs.TerrainNoise}\" must be bound-only.");
                GraphOutputInfo slide = Find(GraphOutputs.TopSlideStart);
                if (slide != null && (!slide.BoundOnly || slide.Bounds.Min != slide.Bounds.Max || !(slide.Bounds.Min >= 0f) ||
                                      !(slide.Bounds.Max <= GraphLimits.WorldHeight)))
                    Error(null, $"Output \"{GraphOutputs.TopSlideStart}\" must be a bound-only constant between 0 and {GraphLimits.WorldHeight}.");
                // The generation top (AD-45): the slide's end, under the world's top voxel, and preliminary_surface
                // under it, so the ranges' solid-below shortcut never reaches past the top (W2-DEN-04).
                GraphOutputInfo end = Find(GraphOutputs.TopSlideEnd);
                bool endValid = end != null && end.BoundOnly && end.Bounds.Min == end.Bounds.Max && end.Bounds.Max <= GraphLimits.WorldHeight - 1 &&
                                (slide == null || end.Bounds.Min >= slide.Bounds.Max);
                if (end != null && !endValid)
                    Error(null, $"Output \"{GraphOutputs.TopSlideEnd}\" must be a bound-only constant between \"{GraphOutputs.TopSlideStart}\" and {GraphLimits.WorldHeight - 1}.");
                GraphOutputInfo surface = Find(GraphOutputs.PreliminarySurface);
                if (surface != null && endValid && !(surface.Bounds.Max <= end.Bounds.Max + GraphOutputs.PreliminarySurfaceMargin))
                    Error(null, $"Output \"{GraphOutputs.PreliminarySurface}\" reaches {surface.Bounds.Max:0.###}; it must stay at or below the generation top, " +
                                $"y {end.Bounds.Max:0.###} (\"{GraphOutputs.TopSlideEnd}\").");
            }

            private int LatticeSlot(int f){
                if (_latticeIndex.TryGetValue(f, out int slot)) return slot;
                _latticeIndex[f] = _latticeNodes.Count;
                _latticeNodes.Add(f);
                return _latticeNodes.Count - 1;
            }

            private int MarkerSlot(int f){
                if (_markerIndex.TryGetValue(f, out int slot)) return slot;
                _markerIndex[f] = _markerNodes.Count;
                _markerNodes.Add(f);
                return _markerNodes.Count - 1;
            }

            // ---- Programs -----------------------------------------------------------------------------------------

            /// <summary>
            ///     Emits one program for <paramref name="roots" />. <paramref name="boundary" /> says which nodes are
            ///     inputs (filled by the evaluator) instead of being computed.
            /// </summary>
            private void BuildProgram(GraphStage stage, int cellHeight, List<(int node, int slot)> roots,
                Func<int, (bool input, GraphSlotSource source, int slot)> boundary){
                List<int> order = new();
                List<(int node, GraphSlotSource source, int slot)> inputs = new();
                HashSet<int> visited = new();
                HashSet<int> inputNodes = new();

                // True when f is new and computed here, so its operands are visited next.
                bool Enter(int f){
                    if (!visited.Add(f) || IsTrivial(f)) return false;
                    (bool input, GraphSlotSource source, int slot) b = boundary(f);
                    if (!b.input) return true;
                    inputs.Add((f, b.source, b.slot));
                    inputNodes.Add(f);
                    return false;
                }

                // Depth first with an explicit stack, operands in order, so a long chain of named nodes never
                // nests calls; the input and slot order is that of the recursive walk.
                Stack<(int node, IEnumerator<int> operands)> stack = new();
                foreach ((int node, int _) in roots){
                    if (!Enter(node)) continue;
                    stack.Push((node, Operands(node).GetEnumerator()));
                    while (stack.Count > 0){
                        (int f, IEnumerator<int> operands) = stack.Peek();
                        if (operands.MoveNext()){
                            int o = operands.Current;
                            if (Enter(o)) stack.Push((o, Operands(o).GetEnumerator()));
                            continue;
                        }

                        stack.Pop();
                        operands.Dispose();
                        order.Add(f);
                    }
                }

                // Constants that need a register: everything except K operands and spline point values.
                HashSet<int> materialised = new();
                foreach ((int node, int _) in roots)
                    if (_nodes[node].Op == GraphOp.Const)
                        materialised.Add(node);
                foreach (int f in order)
                    foreach (int o in RegisterOperands(f))
                        if (_nodes[o].Op == GraphOp.Const)
                            materialised.Add(o);

                List<int> items = order.Concat(materialised).OrderBy(f => f).ToList();
                Dictionary<int, int> lastUse = new();
                for (int p = 0; p < items.Count; p++)
                    foreach (int o in RegisterOperands(items[p]))
                        lastUse[o] = p;
                foreach ((int node, int _) in roots) lastUse[node] = int.MaxValue;

                Dictionary<int, int> register = new();
                for (int f = 0; f < _nodes.Count; f++)
                    if (_nodes[f].Op is GraphOp.X or GraphOp.Y or GraphOp.Z)
                        register[f] = _nodes[f].Op - GraphOp.X;

                SortedSet<int> free = new();
                int next = 3;

                int Allocate(){
                    if (free.Count == 0) return next++;
                    int r = free.Min;
                    free.Remove(r);
                    return r;
                }

                int inputStart = _slots.Count;
                foreach ((int node, GraphSlotSource source, int slot) in inputs){
                    register[node] = Allocate();
                    _slots.Add(new GraphSlot{ Register = register[node], Source = source, Slot = slot });
                }

                int codeStart = _code.Count;
                for (int p = 0; p < items.Count; p++){
                    int f = items[p];
                    int dst = Allocate();
                    _code.Add(Emit(f, dst, register));
                    register[f] = dst;
                    foreach (int o in RegisterOperands(f).Distinct())
                        if (lastUse[o] == p && _nodes[o].Op is not (GraphOp.X or GraphOp.Y or GraphOp.Z))
                            free.Add(register[o]);
                }

                int outputStart = _slots.Count;
                foreach ((int node, int slot) in roots) _slots.Add(new GraphSlot{ Register = register[node], Source = GraphSlotSource.Lattice, Slot = slot });

                GraphProgram program = new(){
                    Stage = stage,
                    CellHeight = cellHeight,
                    InstructionStart = codeStart,
                    InstructionCount = _code.Count - codeStart,
                    RegisterCount = next,
                    InputStart = inputStart,
                    InputCount = outputStart - inputStart,
                    OutputStart = outputStart,
                    OutputCount = roots.Count
                };
                _programs.Add(program);
                _stats.Add(new GraphProgramStats{
                    Stage = stage, CellHeight = cellHeight, Instructions = program.InstructionCount, Registers = next, Inputs = program.InputCount,
                    Outputs = roots.Count
                });

                if (next > GraphLimits.MaxRegisters){
                    string cell = stage == GraphStage.Corner ? $" (cell height {cellHeight})" : "";
                    Error(null, $"The {stage.ToString().ToLowerInvariant()} program{cell} needs {next} registers; the limit is {GraphLimits.MaxRegisters}.");
                }
            }

            private bool IsConst(int f){
                return _nodes[f].Op == GraphOp.Const;
            }

            /// <summary>Operands of <paramref name="f" /> that are read from a register (not folded into a K form).</summary>
            private IEnumerable<int> RegisterOperands(int f){
                GraphNode n = _nodes[f];
                switch (n.Op){
                    case GraphOp.Const:
                        yield break;
                    case GraphOp.Add:
                    case GraphOp.Mul:
                    case GraphOp.Sub:
                        if (!IsConst(n.A)) yield return n.A;
                        if (!IsConst(n.B)) yield return n.B;
                        yield break;
                    case GraphOp.Min:
                    case GraphOp.Max:
                        yield return n.A;
                        if (!IsConst(n.B)) yield return n.B;
                        yield break;
                    case GraphOp.Spline:{
                        yield return n.A;
                        SplineInfo s = _splines[n.Aux];
                        for (int k = 0; k < s.PointCount; k++){
                            int v = _points[s.PointStart + k].Value;
                            if (v >= 0 && !IsConst(v)) yield return v;
                        }

                        yield break;
                    }
                    default:
                        if (n.A >= 0) yield return n.A;
                        if (n.B >= 0) yield return n.B;
                        if (n.C >= 0) yield return n.C;
                        yield break;
                }
            }

            private GraphInstruction Emit(int f, int dst, Dictionary<int, int> register){
                GraphNode n = _nodes[f];
                GraphInstruction ins = new(){
                    Op = n.Op, Dst = (short)dst, A = -1, B = -1, C = -1, Aux = n.Aux, P0 = n.P0, P1 = n.P1, P2 = n.P2, P3 = n.P3
                };

                short Reg(int node){
                    return node >= 0 ? (short)register[node] : (short)-1;
                }

                switch (n.Op){
                    case GraphOp.Const:
                        break;
                    case GraphOp.Add:
                    case GraphOp.Mul:
                        if (IsConst(n.A) || IsConst(n.B)){
                            bool firstConstant = IsConst(n.A);
                            ins.Op = n.Op == GraphOp.Add ? GraphOp.AddK : GraphOp.MulK;
                            ins.A = Reg(firstConstant ? n.B : n.A);
                            ins.P0 = _nodes[firstConstant ? n.A : n.B].P0;
                        }
                        else{
                            ins.A = Reg(n.A);
                            ins.B = Reg(n.B);
                        }

                        break;
                    case GraphOp.Sub:
                        if (IsConst(n.A)){
                            ins.Op = GraphOp.KSub;
                            ins.A = Reg(n.B);
                            ins.P0 = _nodes[n.A].P0;
                        }
                        else if (IsConst(n.B)){
                            ins.Op = GraphOp.SubK;
                            ins.A = Reg(n.A);
                            ins.P0 = _nodes[n.B].P0;
                        }
                        else{
                            ins.A = Reg(n.A);
                            ins.B = Reg(n.B);
                        }

                        break;
                    case GraphOp.Min:
                    case GraphOp.Max:
                        ins.A = Reg(n.A);
                        if (IsConst(n.B)){
                            ins.Op = n.Op == GraphOp.Min ? GraphOp.MinK : GraphOp.MaxK;
                            ins.P0 = _nodes[n.B].P0;
                        }
                        else{
                            ins.B = Reg(n.B);
                        }

                        break;
                    case GraphOp.Spline:{
                        SplineInfo s = _splines[n.Aux];
                        ins.A = Reg(n.A);
                        ins.Aux = _programPoints.Count;
                        ins.B = (short)s.PointCount;
                        ins.C = (short)s.Mode;
                        for (int k = 0; k < s.PointCount; k++){
                            SplinePoint p = _points[s.PointStart + k];
                            if (p.Value >= 0 && IsConst(p.Value)){
                                p.Constant = _nodes[p.Value].P0;
                                p.Value = -1;
                            }
                            else if (p.Value >= 0){
                                p.Value = register[p.Value];
                            }

                            _programPoints.Add(p);
                        }

                        break;
                    }
                    default:
                        ins.A = Reg(n.A);
                        ins.B = Reg(n.B);
                        ins.C = Reg(n.C);
                        break;
                }

                return ins;
            }
        }
    }
}
