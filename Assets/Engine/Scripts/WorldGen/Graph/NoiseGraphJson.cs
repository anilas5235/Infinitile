using System;
using System.Collections.Generic;
using Engine.Scripts.WorldGen.Settings;

namespace Engine.Scripts.WorldGen.Graph{
    /// <summary>
    ///     JSON form of a <see cref="NoiseGraph" />:
    ///     <code>
    ///     { "formatVersion": 1,
    ///       "noises":  { "&lt;name&gt;": { "kind": "simplex2"|"simplex3", "octaves", "wavelength", "wavelengthY",
    ///                                  "lacunarity", "gain", "amplitude" } },
    ///       "nodes":   { "&lt;name&gt;": { "op": "&lt;operation&gt;", ...fields } },
    ///       "outputs": { "&lt;name&gt;": &lt;reference&gt; | { "node": &lt;reference&gt;, "perColumn": true } } }
    ///     </code>
    ///     A reference is a node name, one of "x", "y", "z", a number (a constant) or an inline node object. Writing
    ///     uses <see cref="CanonicalJson" /> (sorted keys), so the same graph always gives the same text.
    /// </summary>
    public static class NoiseGraphJson{
        /// <summary>
        ///     Rejects files nested deeper than <see cref="GraphLimits.MaxJsonNesting" /> before the recursive reader
        ///     and resolver see them, so a malformed or hostile file is an error, not a stack overflow.
        /// </summary>
        private static void CheckNesting(string json){
            int depth = 0;
            bool inString = false;
            for (int i = 0; i < json.Length; i++){
                char c = json[i];
                if (inString){
                    if (c == '\\') i++;
                    else if (c == '"') inString = false;
                    continue;
                }

                switch (c){
                    case '"':
                        inString = true;
                        break;
                    case '{':
                    case '[':
                        if (++depth > GraphLimits.MaxJsonNesting)
                            throw new FormatException($"Objects and arrays are nested more than {GraphLimits.MaxJsonNesting} deep.");
                        break;
                    case '}':
                    case ']':
                        depth--;
                        break;
                }
            }
        }

        /// <summary>
        ///     Reads a graph. Malformed JSON and wrong top-level types throw <see cref="FormatException" />; everything
        ///     else (unknown ops, missing fields, bad references) is left to <see cref="GraphValidator" />, which names
        ///     the node.
        /// </summary>
        public static NoiseGraph Parse(string json){
            CheckNesting(json);
            if (JsonReader.Parse(json) is not Dictionary<string, object> root)
                throw new FormatException("A noise graph must be a JSON object.");

            NoiseGraph graph = new(){ FormatVersion = 0 };
            if (root.TryGetValue("formatVersion", out object version)){
                if (version is not double v || v != Math.Floor(v)) throw new FormatException("\"formatVersion\" must be an integer.");
                graph.FormatVersion = (int)v;
            }

            foreach (string key in root.Keys)
                if (key != "formatVersion" && key != "noises" && key != "nodes" && key != "outputs")
                    throw new FormatException($"Unknown top-level field \"{key}\".");

            if (root.TryGetValue("noises", out object noises)){
                if (noises is not Dictionary<string, object> noiseObjects) throw new FormatException("\"noises\" must be an object.");
                foreach (KeyValuePair<string, object> pair in noiseObjects) graph.Noises[pair.Key] = ReadNoise(pair.Key, pair.Value);
            }

            if (root.TryGetValue("nodes", out object nodes)){
                if (nodes is not Dictionary<string, object> nodeObjects) throw new FormatException("\"nodes\" must be an object.");
                foreach (KeyValuePair<string, object> pair in nodeObjects){
                    if (pair.Value is not Dictionary<string, object> fields)
                        throw new FormatException($"Node \"{pair.Key}\" must be an object.");
                    graph.Nodes[pair.Key] = new GraphNodeDefinition{ Name = pair.Key, Fields = fields };
                }
            }

            if (root.TryGetValue("outputs", out object outputs)){
                if (outputs is not Dictionary<string, object> outputObjects) throw new FormatException("\"outputs\" must be an object.");
                foreach (KeyValuePair<string, object> pair in outputObjects) graph.Outputs[pair.Key] = pair.Value;
            }

            return graph;
        }

        private static NoiseDefinition ReadNoise(string name, object value){
            if (value is not Dictionary<string, object> o) throw new FormatException($"Noise \"{name}\" must be an object.");
            NoiseDefinition noise = new(){ Name = name };
            foreach (KeyValuePair<string, object> pair in o){
                switch (pair.Key){
                    case "kind":
                        noise.Kind = pair.Value as string switch{
                            "simplex2" => NoiseKind.Simplex2,
                            "simplex3" => NoiseKind.Simplex3,
                            _ => throw new FormatException($"Noise \"{name}\": \"kind\" must be \"simplex2\" or \"simplex3\".")
                        };
                        break;
                    case "octaves":
                        double octaves = Number(name, pair);
                        if (octaves != Math.Floor(octaves)) throw new FormatException($"Noise \"{name}\": \"octaves\" must be an integer.");
                        noise.Octaves = (int)Math.Max(int.MinValue, Math.Min(int.MaxValue, octaves));
                        break;
                    case "wavelength": noise.Wavelength = (float)Number(name, pair); break;
                    case "wavelengthY": noise.WavelengthY = (float)Number(name, pair); break;
                    case "lacunarity": noise.Lacunarity = (float)Number(name, pair); break;
                    case "gain": noise.Gain = (float)Number(name, pair); break;
                    case "amplitude": noise.Amplitude = (float)Number(name, pair); break;
                    default: throw new FormatException($"Noise \"{name}\": unknown field \"{pair.Key}\".");
                }
            }

            if (!o.ContainsKey("kind")) throw new FormatException($"Noise \"{name}\": \"kind\" is missing.");
            if (!o.ContainsKey("wavelength")) throw new FormatException($"Noise \"{name}\": \"wavelength\" is missing.");
            if (!o.ContainsKey("wavelengthY")) noise.WavelengthY = noise.Wavelength;
            return noise;
        }

        private static double Number(string noise, KeyValuePair<string, object> pair){
            if (pair.Value is double d) return d;
            throw new FormatException($"Noise \"{noise}\": \"{pair.Key}\" must be a number.");
        }

        /// <summary>Canonical JSON text of <paramref name="graph" /> (sorted keys, "\n" line ends).</summary>
        public static string Write(NoiseGraph graph){
            Dictionary<string, object> noises = new();
            foreach (NoiseDefinition n in graph.Noises.Values){
                Dictionary<string, object> o = new(){
                    ["kind"] = n.Kind == NoiseKind.Simplex2 ? "simplex2" : "simplex3",
                    ["octaves"] = n.Octaves,
                    ["wavelength"] = n.Wavelength,
                    ["lacunarity"] = n.Lacunarity,
                    ["gain"] = n.Gain,
                    ["amplitude"] = n.Amplitude
                };
                if (n.Kind == NoiseKind.Simplex3) o["wavelengthY"] = n.WavelengthY;
                noises[n.Name] = o;
            }

            Dictionary<string, object> nodes = new();
            foreach (GraphNodeDefinition n in graph.Nodes.Values) nodes[n.Name] = n.Fields;

            Dictionary<string, object> outputs = new();
            foreach (KeyValuePair<string, object> pair in graph.Outputs) outputs[pair.Key] = pair.Value;

            return CanonicalJson.Serialize(new Dictionary<string, object>{
                ["formatVersion"] = graph.FormatVersion,
                ["noises"] = noises,
                ["nodes"] = nodes,
                ["outputs"] = outputs
            });
        }
    }
}
