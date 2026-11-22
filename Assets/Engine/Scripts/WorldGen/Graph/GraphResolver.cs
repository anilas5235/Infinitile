using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Engine.Scripts.WorldGen.Core;

namespace Engine.Scripts.WorldGen.Graph{
    /// <summary>A graph output after resolution: its name, node and whether it is evaluated for every column.</summary>
    internal struct ResolvedOutput{
        public string Name;
        public int Node;
        public bool PerColumn;
    }

    /// <summary>A graph as a node list in topological order, with its splines and noise entries (no float math done yet).</summary>
    internal sealed class GraphIr{
        public readonly List<GraphNode> Nodes = new();

        /// <summary>Name of each node for messages: the graph's name, or the path of an inline node.</summary>
        public readonly List<string> NodeNames = new();

        public readonly List<SplineInfo> Splines = new();
        public readonly List<SplinePoint> Points = new();
        public readonly List<NoiseEntry> Entries = new();
        public readonly List<NoiseSource> Sources = new();
        public readonly List<ResolvedOutput> Outputs = new();
        public int NoiseDefinitionCount;

        /// <summary>Name of each noise definition, by the index <see cref="NoiseEntry.Definition" /> holds.</summary>
        public readonly List<string> NoiseNames = new();

        public int Add(GraphNode node, string name){
            Nodes.Add(node);
            NodeNames.Add(name);
            return Nodes.Count - 1;
        }
    }

    /// <summary>
    ///     Turns a <see cref="NoiseGraph" /> into a <see cref="GraphIr" />: resolves references, inlines constants and
    ///     inline nodes, merges shift into its noise, lowers y_gradient, wraps the climate outputs in a clamp to
    ///     [−1, 1] and reports every structural error with the node named (unknown names, cycles, bad fields).
    /// </summary>
    internal sealed class GraphResolver{
        private static readonly Dictionary<string, (string[] required, string[] optional)> Fields = new(){
            ["const"] = (new[]{ "value" }, Array.Empty<string>()),
            ["simplex2"] = (new[]{ "noise" }, Array.Empty<string>()),
            ["simplex3"] = (new[]{ "noise" }, Array.Empty<string>()),
            ["fbm"] = (new[]{ "noise" }, Array.Empty<string>()),
            ["ridged"] = (new[]{ "noise" }, Array.Empty<string>()),
            ["shift"] = (new[]{ "in" }, new[]{ "x", "y", "z" }),
            ["add"] = (new[]{ "a", "b" }, Array.Empty<string>()),
            ["sub"] = (new[]{ "a", "b" }, Array.Empty<string>()),
            ["mul"] = (new[]{ "a", "b" }, Array.Empty<string>()),
            ["min"] = (new[]{ "a", "b" }, Array.Empty<string>()),
            ["max"] = (new[]{ "a", "b" }, Array.Empty<string>()),
            ["abs"] = (new[]{ "in" }, Array.Empty<string>()),
            ["neg"] = (new[]{ "in" }, Array.Empty<string>()),
            ["square"] = (new[]{ "in" }, Array.Empty<string>()),
            ["cube"] = (new[]{ "in" }, Array.Empty<string>()),
            ["half_negative"] = (new[]{ "in" }, Array.Empty<string>()),
            ["quarter_negative"] = (new[]{ "in" }, Array.Empty<string>()),
            ["soft_clamp"] = (new[]{ "in" }, Array.Empty<string>()),
            ["clamp"] = (new[]{ "in", "min", "max" }, Array.Empty<string>()),
            ["lerp"] = (new[]{ "t", "a", "b" }, Array.Empty<string>()),
            ["smoothstep"] = (new[]{ "in", "from", "to" }, Array.Empty<string>()),
            ["range_choice"] = (new[]{ "in", "min", "max", "inRange", "outRange" }, Array.Empty<string>()),
            ["y_gradient"] = (new[]{ "fromY", "toY", "fromValue", "toValue" }, Array.Empty<string>()),
            ["spline"] = (new[]{ "coordinate", "points" }, new[]{ "interpolation" }),
            ["interpolated"] = (new[]{ "in", "cellHeight" }, Array.Empty<string>())
        };

        private static readonly Dictionary<string, GraphOp> Binary = new(){
            ["add"] = GraphOp.Add, ["sub"] = GraphOp.Sub, ["mul"] = GraphOp.Mul, ["min"] = GraphOp.Min, ["max"] = GraphOp.Max
        };

        private static readonly Dictionary<string, GraphOp> Unary = new(){
            ["abs"] = GraphOp.Abs, ["neg"] = GraphOp.Neg, ["square"] = GraphOp.Square, ["cube"] = GraphOp.Cube,
            ["half_negative"] = GraphOp.HalfNegative, ["quarter_negative"] = GraphOp.QuarterNegative, ["soft_clamp"] = GraphOp.SoftClamp
        };

        /// <summary>Names a reference may use for the block coordinates; nodes cannot take these names.</summary>
        public static readonly string[] InputNames ={ "x", "y", "z" };

        private readonly NoiseGraph _graph;
        private readonly string _file;
        private readonly List<GraphMessage> _messages;
        private readonly GraphIr _ir = new();
        private readonly Dictionary<string, int> _named = new(StringComparer.Ordinal);
        private readonly List<string> _visiting = new();
        private readonly HashSet<string> _reportedCycles = new(StringComparer.Ordinal);
        private readonly Dictionary<(string noise, int mode), int> _entries = new();
        private readonly Dictionary<string, int> _definitionIndex = new(StringComparer.Ordinal);
        private readonly int[] _inputs ={ -1, -1, -1 };

        private GraphResolver(NoiseGraph graph, string file, List<GraphMessage> messages){
            _graph = graph;
            _file = file;
            _messages = messages;
        }

        /// <summary>Resolves <paramref name="graph" />; returns null when an error was reported.</summary>
        public static GraphIr Resolve(NoiseGraph graph, string file, IEnumerable<string> requiredOutputs, List<GraphMessage> messages){
            int before = messages.Count(m => m.Severity == GraphMessageSeverity.Error);
            GraphResolver r = new(graph, file, messages);
            r.Run(requiredOutputs);
            return messages.Count(m => m.Severity == GraphMessageSeverity.Error) > before ? null : r._ir;
        }

        /// <summary>
        ///     Hash salt of a noise name (FNV-1a over its UTF-8 bytes, then mixed). Each noise's seed is the world
        ///     seed hashed with this salt, so a noise keeps its values when other noises are added or renamed.
        /// </summary>
        public static uint NoiseSalt(string name){
            uint h = 2166136261u;
            foreach (byte b in Encoding.UTF8.GetBytes(name)){
                h ^= b;
                h *= 16777619u;
            }

            return Hash.Mix(h);
        }

        private void Error(string node, string text){
            _messages.Add(new GraphMessage(GraphMessageSeverity.Error, _file, node, text));
        }

        private void Run(IEnumerable<string> requiredOutputs){
            if (_graph.FormatVersion <= 0) Error(null, "\"formatVersion\" is missing.");
            else if (_graph.FormatVersion > NoiseGraph.CurrentFormatVersion)
                Error(null, $"Format version {_graph.FormatVersion} is newer than this game reads ({NoiseGraph.CurrentFormatVersion}).");

            int index = 0;
            foreach (NoiseDefinition n in _graph.Noises.Values){
                _definitionIndex[n.Name] = index++;
                _ir.NoiseNames.Add(n.Name);
                CheckNoise(n);
            }

            _ir.NoiseDefinitionCount = _graph.Noises.Count;
            if (_graph.Noises.Count > GraphLimits.MaxNoises)
                Error(null, $"The graph has {_graph.Noises.Count} noises; the limit is {GraphLimits.MaxNoises}.");

            foreach (string name in _graph.Nodes.Keys){
                if (Array.IndexOf(InputNames, name) >= 0)
                    Error(name, $"\"{name}\" is the name of a coordinate input and cannot name a node.");
                // Messages name a field as node.field, so a dot would make them point at another node.
                if (name.IndexOf('.') >= 0) Error(name, $"\"{name}\": a node name cannot contain \".\".");
            }

            foreach (string required in requiredOutputs)
                if (!_graph.Outputs.ContainsKey(required))
                    Error(null, $"The required output \"{required}\" is missing.");

            // Every named node resolves here, deepest first, also those no output uses: a broken node never waits until
            // someone wires it up, and a long chain of named nodes never nests the resolution.
            ResolveNamedNodes();

            foreach (KeyValuePair<string, object> output in _graph.Outputs){
                object reference = output.Value;
                bool perColumn = false;
                if (reference is Dictionary<string, object> o && !o.ContainsKey("op")){
                    foreach (string key in o.Keys)
                        if (key != "node" && key != "perColumn")
                            Error(null, $"Output \"{output.Key}\": unknown field \"{key}\" (allowed: node, perColumn).");
                    if (!o.TryGetValue("node", out reference)){
                        Error(null, $"Output \"{output.Key}\" has no \"node\".");
                        continue;
                    }

                    if (o.TryGetValue("perColumn", out object flag)){
                        if (flag is bool b) perColumn = b;
                        else Error(null, $"Output \"{output.Key}\": \"perColumn\" must be true or false.");
                    }
                }

                int node = Reference(reference, $"output {output.Key}");
                if (node < 0) continue;
                if (GraphOutputs.IsClimate(output.Key)){
                    GraphNode n = _ir.Nodes[node];
                    bool clamped = n.Op == GraphOp.Clamp && n.P0 >= -1f && n.P1 <= 1f;
                    if (!clamped){
                        GraphNode clamp = GraphNode.Make(GraphOp.Clamp, node);
                        clamp.P0 = -1f;
                        clamp.P1 = 1f;
                        node = _ir.Add(clamp, $"output {output.Key} (clamp to [-1, 1])");
                    }
                }

                _ir.Outputs.Add(new ResolvedOutput{ Name = output.Key, Node = node, PerColumn = perColumn });
            }

            CheckStructure();
        }

        /// <summary>
        ///     Resolves the named nodes in dependency order, found with an explicit stack: each node resolves after the
        ///     named nodes it reads, so resolving it never recurses into another named node. Reports each cycle once,
        ///     starting at its ordinally first name, and leaves its nodes unresolved.
        /// </summary>
        private void ResolveNamedNodes(){
            Dictionary<string, bool> finished = new(StringComparer.Ordinal); // false while on the stack
            List<string> order = new();
            List<string> path = new();
            List<IEnumerator<string>> pending = new();
            foreach (string root in _graph.Nodes.Keys){
                if (finished.ContainsKey(root)) continue;
                finished[root] = false;
                path.Add(root);
                pending.Add(NamedReferences(_graph.Nodes[root].Fields).GetEnumerator());
                while (path.Count > 0){
                    int top = path.Count - 1;
                    if (!pending[top].MoveNext()){
                        finished[path[top]] = true;
                        order.Add(path[top]);
                        path.RemoveAt(top);
                        pending.RemoveAt(top);
                        continue;
                    }

                    string next = pending[top].Current;
                    if (!_graph.Nodes.TryGetValue(next, out GraphNodeDefinition definition)) continue;
                    if (finished.TryGetValue(next, out bool done)){
                        if (!done) ReportCycle(path.GetRange(path.IndexOf(next), path.Count - path.IndexOf(next)));
                        continue;
                    }

                    finished[next] = false;
                    path.Add(next);
                    pending.Add(NamedReferences(definition.Fields).GetEnumerator());
                }
            }

            foreach (string name in order) Named(name, name);
        }

        private void ReportCycle(List<string> cycle){
            int first = 0;
            for (int i = 1; i < cycle.Count; i++)
                if (string.CompareOrdinal(cycle[i], cycle[first]) < 0)
                    first = i;
            List<string> names = cycle.Skip(first).Concat(cycle.Take(first)).ToList();
            foreach (string name in cycle) _named[name] = -1;
            // A node that reads the same name twice meets the cycle twice; report it once.
            if (!_reportedCycles.Add(string.Join(" → ", names))) return;
            Error(names[0], $"Cycle: {string.Join(" → ", names.Append(names[0]))}.");
        }

        /// <summary>Fields that hold a number or a noise name, never a node reference.</summary>
        private static readonly HashSet<string> ValueFields = new(StringComparer.Ordinal){
            "noise", "interpolation", "min", "max", "from", "to", "fromY", "toY", "fromValue", "toValue", "cellHeight"
        };

        /// <summary>
        ///     Every string a node's reference fields may name another node with, inline nodes and spline point values
        ///     included. Unknown ops and fields and fields that hold numbers are skipped: resolving the node reports
        ///     them, and a wrong string there is a bad field, not a cycle.
        /// </summary>
        private static IEnumerable<string> NamedReferences(object value){
            switch (value){
                case string s:
                    yield return s;
                    break;
                case Dictionary<string, object> fields:
                    if (!fields.TryGetValue("op", out object opValue) || opValue is not string op || op == "const" ||
                        !Fields.TryGetValue(op, out (string[] required, string[] optional) allowed))
                        yield break;
                    foreach (KeyValuePair<string, object> field in fields){
                        if (ValueFields.Contains(field.Key) || (Array.IndexOf(allowed.required, field.Key) < 0 && Array.IndexOf(allowed.optional, field.Key) < 0))
                            continue;
                        if (field.Key == "points"){
                            if (field.Value is not List<object> points) continue;
                            foreach (object point in points)
                                if (point is Dictionary<string, object> p && p.TryGetValue("value", out object pointValue))
                                    foreach (string name in NamedReferences(pointValue))
                                        yield return name;
                            continue;
                        }

                        foreach (string name in NamedReferences(field.Value)) yield return name;
                    }

                    break;
            }
        }

        private void CheckNoise(NoiseDefinition n){
            if (n.Octaves < 1 || n.Octaves > GraphLimits.MaxOctaves)
                Error(null, $"Noise \"{n.Name}\": \"octaves\" must be 1 to {GraphLimits.MaxOctaves}.");
            if (!(n.Wavelength > 0f) || float.IsInfinity(n.Wavelength)) Error(null, $"Noise \"{n.Name}\": \"wavelength\" must be above 0.");
            if (!(n.WavelengthY > 0f) || float.IsInfinity(n.WavelengthY)) Error(null, $"Noise \"{n.Name}\": \"wavelengthY\" must be above 0.");
            if (!(n.Lacunarity > 0f) || float.IsInfinity(n.Lacunarity)) Error(null, $"Noise \"{n.Name}\": \"lacunarity\" must be above 0.");
            if (!(n.Gain > 0f) || float.IsInfinity(n.Gain)) Error(null, $"Noise \"{n.Name}\": \"gain\" must be above 0.");
            if (float.IsNaN(n.Amplitude) || float.IsInfinity(n.Amplitude)) Error(null, $"Noise \"{n.Name}\": \"amplitude\" must be a finite number.");
            if (n.Kind == NoiseKind.Simplex2 && n.WavelengthY != n.Wavelength)
                Error(null, $"Noise \"{n.Name}\": a 2D noise has no \"wavelengthY\".");
        }

        /// <summary>Resolves a named node once; reports a cycle when the name is met again while it resolves.</summary>
        private int Named(string name, string context){
            if (_named.TryGetValue(name, out int done)) return done;
            int at = _visiting.IndexOf(name);
            if (at >= 0){
                string path = string.Join(" → ", _visiting.Skip(at).Append(name));
                Error(name, $"Cycle: {path}.");
                return -1;
            }

            if (!_graph.Nodes.TryGetValue(name, out GraphNodeDefinition definition)){
                string hint = _graph.Noises.ContainsKey(name) ? $" (\"{name}\" is a noise; sample it with {{\"op\": \"fbm\", \"noise\": \"{name}\"}})" : "";
                Error(Owner(context), $"{context}: unknown node \"{name}\"{hint}.");
                return -1;
            }

            _visiting.Add(name);
            int node = Node(definition.Fields, name);
            _visiting.RemoveAt(_visiting.Count - 1);
            _named[name] = node;
            return node;
        }

        private int Input(int axis){
            if (_inputs[axis] < 0) _inputs[axis] = _ir.Add(GraphNode.Make((GraphOp)((int)GraphOp.X + axis)), InputNames[axis]);
            return _inputs[axis];
        }

        /// <summary>A reference: a node name, a coordinate name, a number or an inline node object.</summary>
        private int Reference(object value, string context){
            switch (value){
                case string s:
                    int axis = Array.IndexOf(InputNames, s);
                    return axis >= 0 ? Input(axis) : Named(s, context);
                case double d:
                    float f = (float)d;
                    if (float.IsNaN(f) || float.IsInfinity(f)){
                        Error(Owner(context), $"{context}: {d} is not a finite float.");
                        return -1;
                    }

                    GraphNode c = GraphNode.Make(GraphOp.Const);
                    c.P0 = f;
                    return _ir.Add(c, context);
                case Dictionary<string, object> inline:
                    return Node(inline, context);
                default:
                    Error(Owner(context), $"{context}: expected a node name, a number or a node object.");
                    return -1;
            }
        }

        private string Owner(string context){
            int slash = context.IndexOf('.');
            return slash > 0 ? context.Substring(0, slash) : context;
        }

        private int Node(Dictionary<string, object> fields, string name){
            if (!fields.TryGetValue("op", out object opValue) || opValue is not string op){
                Error(Owner(name), $"{name}: \"op\" is missing.");
                return -1;
            }

            if (!Fields.TryGetValue(op, out (string[] required, string[] optional) allowed)){
                Error(Owner(name), $"{name}: unknown op \"{op}\".");
                return -1;
            }

            bool ok = true;
            foreach (string key in fields.Keys){
                if (key == "op" || Array.IndexOf(allowed.required, key) >= 0 || Array.IndexOf(allowed.optional, key) >= 0) continue;
                Error(Owner(name), $"{name}: unknown field \"{key}\" for op \"{op}\".");
                ok = false;
            }

            foreach (string key in allowed.required){
                if (fields.ContainsKey(key)) continue;
                Error(Owner(name), $"{name}: op \"{op}\" needs \"{key}\".");
                ok = false;
            }

            if (!ok) return -1;

            if (Binary.TryGetValue(op, out GraphOp binary)){
                int a = Reference(fields["a"], $"{name}.a"), b = Reference(fields["b"], $"{name}.b");
                return a < 0 || b < 0 ? -1 : _ir.Add(GraphNode.Make(binary, a, b), name);
            }

            if (Unary.TryGetValue(op, out GraphOp unary)){
                int a = Reference(fields["in"], $"{name}.in");
                return a < 0 ? -1 : _ir.Add(GraphNode.Make(unary, a), name);
            }

            switch (op){
                case "const":{
                    if (!Number(fields, "value", name, out float v)) return -1;
                    GraphNode c = GraphNode.Make(GraphOp.Const);
                    c.P0 = v;
                    return _ir.Add(c, name);
                }
                case "simplex2":
                case "simplex3":
                case "fbm":
                case "ridged":
                    return NoiseNode(op, fields["noise"], name);
                case "shift":{
                    int inner = Reference(fields["in"], $"{name}.in");
                    if (inner < 0) return -1;
                    GraphNode n = _ir.Nodes[inner];
                    if (n.Op != GraphOp.Noise || n.A >= 0 || n.B >= 0 || n.C >= 0){
                        Error(Owner(name), $"{name}: \"in\" must be a simplex2, simplex3, fbm or ridged node.");
                        return -1;
                    }

                    bool flat = _ir.Entries[n.Aux].Dimensions == 2;
                    if (flat && fields.ContainsKey("y")){
                        Error(Owner(name), $"{name}: a 2D noise has no y to shift.");
                        return -1;
                    }

                    bool failed = false;
                    n.A = Offset(fields, "x", name, ref failed);
                    n.B = Offset(fields, "y", name, ref failed);
                    n.C = Offset(fields, "z", name, ref failed);
                    return failed ? -1 : _ir.Add(n, name);
                }
                case "clamp":{
                    int a = Reference(fields["in"], $"{name}.in");
                    if (!Number(fields, "min", name, out float lo) | !Number(fields, "max", name, out float hi) || a < 0) return -1;
                    if (!(lo <= hi)){
                        Error(Owner(name), $"{name}: \"min\" must not be above \"max\".");
                        return -1;
                    }

                    GraphNode n = GraphNode.Make(GraphOp.Clamp, a);
                    n.P0 = lo;
                    n.P1 = hi;
                    return _ir.Add(n, name);
                }
                case "lerp":{
                    int t = Reference(fields["t"], $"{name}.t"), a = Reference(fields["a"], $"{name}.a"), b = Reference(fields["b"], $"{name}.b");
                    return t < 0 || a < 0 || b < 0 ? -1 : _ir.Add(GraphNode.Make(GraphOp.Lerp, t, a, b), name);
                }
                case "smoothstep":{
                    int a = Reference(fields["in"], $"{name}.in");
                    if (!Number(fields, "from", name, out float from) | !Number(fields, "to", name, out float to) || a < 0) return -1;
                    if (from == to){
                        Error(Owner(name), $"{name}: \"from\" and \"to\" must differ.");
                        return -1;
                    }

                    GraphNode n = GraphNode.Make(GraphOp.Smoothstep, a);
                    n.P0 = from;
                    n.P1 = to;
                    return _ir.Add(n, name);
                }
                case "range_choice":{
                    int a = Reference(fields["in"], $"{name}.in");
                    int b = Reference(fields["inRange"], $"{name}.inRange");
                    int c = Reference(fields["outRange"], $"{name}.outRange");
                    if (!Number(fields, "min", name, out float lo) | !Number(fields, "max", name, out float hi) || a < 0 || b < 0 || c < 0)
                        return -1;
                    GraphNode n = GraphNode.Make(GraphOp.RangeChoice, a, b, c);
                    n.P0 = lo;
                    n.P1 = hi;
                    return _ir.Add(n, name);
                }
                case "y_gradient":{
                    if (!Number(fields, "fromY", name, out float fromY) | !Number(fields, "toY", name, out float toY) |
                        !Number(fields, "fromValue", name, out float fromValue) | !Number(fields, "toValue", name, out float toValue))
                        return -1;
                    if (fromY == toY){
                        Error(Owner(name), $"{name}: \"fromY\" and \"toY\" must differ.");
                        return -1;
                    }

                    GraphNode n = GraphNode.Make(GraphOp.ClampedMap, Input(1));
                    n.P0 = fromY;
                    n.P1 = toY;
                    n.P2 = fromValue;
                    n.P3 = toValue;
                    return _ir.Add(n, name);
                }
                case "spline":
                    return SplineNode(fields, name);
                case "interpolated":{
                    int a = Reference(fields["in"], $"{name}.in");
                    if (!Number(fields, "cellHeight", name, out float h) || a < 0) return -1;
                    if (h != 4f && h != 8f){
                        Error(Owner(name), $"{name}: \"cellHeight\" must be 4 or 8.");
                        return -1;
                    }

                    GraphNode n = GraphNode.Make(GraphOp.Marker, a);
                    n.Aux = h == 4f ? 4 : 8;
                    return _ir.Add(n, name);
                }
                default:
                    Error(Owner(name), $"{name}: unknown op \"{op}\".");
                    return -1;
            }
        }

        /// <summary>The optional offset of a shift on one axis: −1 when absent.</summary>
        private int Offset(Dictionary<string, object> fields, string axis, string name, ref bool failed){
            if (!fields.TryGetValue(axis, out object value)) return -1;
            int node = Reference(value, $"{name}.{axis}");
            failed |= node < 0;
            return node;
        }

        private int NoiseNode(string op, object noiseValue, string name){
            if (noiseValue is not string noise){
                Error(Owner(name), $"{name}: \"noise\" must be the name of a noise.");
                return -1;
            }

            if (!_graph.Noises.TryGetValue(noise, out NoiseDefinition definition)){
                Error(Owner(name), $"{name}: unknown noise \"{noise}\".");
                return -1;
            }

            if (op == "simplex2" && definition.Kind != NoiseKind.Simplex2){
                Error(Owner(name), $"{name}: simplex2 needs a 2D noise; \"{noise}\" is 3D.");
                return -1;
            }

            if (op == "simplex3" && definition.Kind != NoiseKind.Simplex3){
                Error(Owner(name), $"{name}: simplex3 needs a 3D noise; \"{noise}\" is 2D.");
                return -1;
            }

            int mode = op switch{ "fbm" => 1, "ridged" => 2, _ => 0 };
            if (!_entries.TryGetValue((noise, mode), out int entry)){
                int octaves = mode == 0 ? 1 : Math.Max(1, Math.Min(GraphLimits.MaxOctaves, definition.Octaves));
                int start = _ir.Entries.Count == 0 ? 0 : _ir.Entries[^1].OctaveStart + _ir.Entries[^1].OctaveCount;
                entry = _ir.Entries.Count;
                _ir.Entries.Add(new NoiseEntry{
                    Dimensions = definition.Kind == NoiseKind.Simplex2 ? 2 : 3,
                    Mode = mode == 2 ? NoiseMode.Ridged : NoiseMode.Sum,
                    OctaveStart = start,
                    OctaveCount = octaves,
                    Definition = _definitionIndex[noise]
                });
                _ir.Sources.Add(new NoiseSource{
                    Octaves = octaves,
                    Wavelength = definition.Wavelength,
                    WavelengthY = definition.Kind == NoiseKind.Simplex2 ? definition.Wavelength : definition.WavelengthY,
                    Lacunarity = definition.Lacunarity,
                    Gain = definition.Gain,
                    Amplitude = definition.Amplitude,
                    Salt = NoiseSalt(noise)
                });
                _entries[(noise, mode)] = entry;
            }

            GraphNode n = GraphNode.Make(GraphOp.Noise);
            n.Aux = entry;
            return _ir.Add(n, name);
        }

        private int SplineNode(Dictionary<string, object> fields, string name){
            int coordinate = Reference(fields["coordinate"], $"{name}.coordinate");
            bool linear = false;
            if (fields.TryGetValue("interpolation", out object interpolation)){
                switch (interpolation as string){
                    case "cubic": break;
                    case "linear":
                        linear = true;
                        break;
                    default:
                        Error(Owner(name), $"{name}: \"interpolation\" must be \"cubic\" or \"linear\".");
                        return -1;
                }
            }

            if (fields["points"] is not List<object> list || list.Count == 0){
                Error(Owner(name), $"{name}: \"points\" must be a list of 1 to {GraphLimits.MaxSplinePoints} points.");
                return -1;
            }

            if (list.Count > GraphLimits.MaxSplinePoints){
                Error(Owner(name), $"{name}: {list.Count} points; a spline has at most {GraphLimits.MaxSplinePoints}.");
                return -1;
            }

            List<SplinePoint> points = new();
            bool ok = coordinate >= 0;
            for (int k = 0; k < list.Count; k++){
                string at = $"{name}.points[{k}]";
                if (list[k] is not Dictionary<string, object> p){
                    Error(Owner(name), $"{at}: a point must be an object with location, value and optional derivative.");
                    ok = false;
                    continue;
                }

                foreach (string key in p.Keys)
                    if (key != "location" && key != "value" && key != "derivative"){
                        Error(Owner(name), $"{at}: unknown field \"{key}\".");
                        ok = false;
                    }

                if (!Number(p, "location", at, out float location) || !p.TryGetValue("value", out object value)){
                    if (!p.ContainsKey("value")) Error(Owner(name), $"{at}: \"value\" is missing.");
                    ok = false;
                    continue;
                }

                SplinePoint point = new(){ Location = location, Value = Reference(value, $"{at}.value") };
                if (point.Value < 0) ok = false;
                if (p.ContainsKey("derivative")){
                    if (!Number(p, "derivative", at, out float derivative)) ok = false;
                    point.Derivative = derivative;
                    point.HasDerivative = 1;
                }

                if (points.Count > 0 && !(location > points[^1].Location)){
                    Error(Owner(name), $"{at}: locations must increase strictly.");
                    ok = false;
                }

                points.Add(point);
            }

            if (!ok) return -1;
            GraphNode n = GraphNode.Make(GraphOp.Spline, coordinate);
            n.Aux = _ir.Splines.Count;
            _ir.Splines.Add(new SplineInfo{ PointStart = _ir.Points.Count, PointCount = points.Count, Linear = linear ? 1 : 0 });
            _ir.Points.AddRange(points);
            return _ir.Add(n, name);
        }

        private bool Number(Dictionary<string, object> fields, string key, string name, out float value){
            value = 0f;
            if (!fields.TryGetValue(key, out object v)){
                Error(Owner(name), $"{name}: \"{key}\" is missing.");
                return false;
            }

            if (v is not double d){
                Error(Owner(name), $"{name}: \"{key}\" must be a number.");
                return false;
            }

            value = (float)d;
            if (float.IsNaN(value) || float.IsInfinity(value)){
                Error(Owner(name), $"{name}: \"{key}\" is not a finite float.");
                return false;
            }

            return true;
        }

        /// <summary>Spline depth and nested markers, over the resolved node list.</summary>
        private void CheckStructure(){
            int count = _ir.Nodes.Count;
            int[] depth = new int[count];
            bool[] marker = new bool[count];
            for (int i = 0; i < count; i++){
                GraphNode n = _ir.Nodes[i];
                int d = 0;
                bool m = false;
                foreach (int o in Operands(_ir, i)){
                    d = Math.Max(d, depth[o]);
                    m |= marker[o];
                }

                if (n.Op == GraphOp.Marker && m) Error(Owner(_ir.NodeNames[i]), $"{_ir.NodeNames[i]}: an interpolated marker inside another one.");
                if (n.Op == GraphOp.Spline && ++d == GraphLimits.MaxSplineDepth + 1)
                    Error(Owner(_ir.NodeNames[i]), $"{_ir.NodeNames[i]}: splines nested more than {GraphLimits.MaxSplineDepth} deep.");
                depth[i] = d;
                marker[i] = m || n.Op == GraphOp.Marker;
            }
        }

        /// <summary>Every operand of node <paramref name="i" />: A, B, C and, for a spline, its point values.</summary>
        public static IEnumerable<int> Operands(GraphIr ir, int i){
            GraphNode n = ir.Nodes[i];
            if (n.A >= 0) yield return n.A;
            if (n.B >= 0) yield return n.B;
            if (n.C >= 0) yield return n.C;
            if (n.Op != GraphOp.Spline) yield break;
            SplineInfo s = ir.Splines[n.Aux];
            for (int k = 0; k < s.PointCount; k++){
                int v = ir.Points[s.PointStart + k].Value;
                if (v >= 0) yield return v;
            }
        }
    }
}
