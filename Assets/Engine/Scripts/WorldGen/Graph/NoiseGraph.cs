using System.Collections.Generic;

namespace Engine.Scripts.WorldGen.Graph{
    /// <summary>Dimension of a noise definition: sampled at (x, z) or at (x, y, z).</summary>
    public enum NoiseKind : byte{
        Simplex2 = 0,
        Simplex3 = 1
    }

    /// <summary>
    ///     One noise of a graph: the seeded simplex summed over octaves. Its seed is a hash of the world seed and
    ///     <see cref="Name" />, so adding, removing or reordering other noises never changes this one.
    /// </summary>
    public sealed class NoiseDefinition{
        public string Name;
        public NoiseKind Kind;
        public int Octaves = 1;

        /// <summary>Wavelength of the first octave across (x and z), in blocks.</summary>
        public float Wavelength = 1f;

        /// <summary>Wavelength of the first octave along y, in blocks (3D noises; defaults to <see cref="Wavelength" />).</summary>
        public float WavelengthY = 1f;

        /// <summary>Frequency factor from one octave to the next.</summary>
        public float Lacunarity = 2f;

        /// <summary>Amplitude factor from one octave to the next.</summary>
        public float Gain = 0.5f;

        /// <summary>Scale of the result: fbm and simplex lie in [−Amplitude, Amplitude], ridged in [0, Amplitude].</summary>
        public float Amplitude = 1f;
    }

    /// <summary>A named node: its operation and its raw fields as read from JSON (checked when the graph compiles, see <see cref="GraphValidator" />).</summary>
    public sealed class GraphNodeDefinition{
        public string Name;

        /// <summary>The node object as read: "op" plus the operation's fields (strings, numbers, nested objects, lists).</summary>
        public Dictionary<string, object> Fields = new();

        public string Op => Fields.TryGetValue("op", out object op) ? op as string : null;
    }

    /// <summary>
    ///     A noise graph as data: noises, named nodes and named outputs. The JSON form is read and written by
    ///     <see cref="NoiseGraphJson" />; <see cref="GraphValidator" /> checks it and <see cref="GraphCompiler" /> turns
    ///     it into flat programs for the interpreter.
    /// </summary>
    public sealed class NoiseGraph{
        /// <summary>Format version this code writes and the newest it reads.</summary>
        public const int CurrentFormatVersion = 1;

        /// <summary>Format version found in the file (0 when missing).</summary>
        public int FormatVersion = CurrentFormatVersion;

        public readonly SortedDictionary<string, NoiseDefinition> Noises = new(System.StringComparer.Ordinal);
        public readonly SortedDictionary<string, GraphNodeDefinition> Nodes = new(System.StringComparer.Ordinal);

        /// <summary>
        ///     Output name to its raw value: a node reference (name, number or inline node object) or an object with
        ///     "node" and an optional "perColumn" flag.
        /// </summary>
        public readonly SortedDictionary<string, object> Outputs = new(System.StringComparer.Ordinal);
    }
}
