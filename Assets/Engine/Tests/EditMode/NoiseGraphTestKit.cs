using System;
using System.Collections.Generic;
using System.Linq;
using Engine.Scripts.WorldGen.Graph;
using NUnit.Framework;

namespace Engine.Tests.EditMode{
    /// <summary>Builds noise graphs in code and compares values for the noise graph tests.</summary>
    internal static class NoiseGraphTestKit{
        public const string File = "test.json";
        public const uint Seed = 1337u;

        /// <summary>A node object: <c>Op("add", ("a", "x"), ("b", 2.0))</c>. Numbers must be doubles, as JSON reads them.</summary>
        public static Dictionary<string, object> Op(string op, params (string key, object value)[] fields){
            Dictionary<string, object> o = new(){ ["op"] = op };
            foreach ((string key, object value) in fields) o[key] = value is int i ? (double)i : value is float f ? (double)f : value;
            return o;
        }

        public static Dictionary<string, object> Noise(string op, string noise){
            return Op(op, ("noise", noise));
        }

        /// <summary>A spline point; <paramref name="derivative" /> null leaves it out.</summary>
        public static Dictionary<string, object> Point(double location, object value, double? derivative = null){
            Dictionary<string, object> p = new(){ ["location"] = location, ["value"] = value is int i ? (double)i : value is float f ? (double)f : value };
            if (derivative.HasValue) p["derivative"] = derivative.Value;
            return p;
        }

        public static Dictionary<string, object> Spline(object coordinate, bool linear, params Dictionary<string, object>[] points){
            Dictionary<string, object> o = Op("spline", ("coordinate", coordinate), ("points", points.Cast<object>().ToList()));
            if (linear) o["interpolation"] = "linear";
            return o;
        }

        public static GraphCompileResult Compile(NoiseGraph graph){
            return GraphCompiler.Compile(graph, File, Array.Empty<string>());
        }

        /// <summary>Compiles and fails the test with every message if the compile reports an error.</summary>
        public static CompiledNoiseGraph CompileOrFail(NoiseGraph graph){
            GraphCompileResult r = Compile(graph);
            if (!r.Succeeded) Assert.Fail("Compile failed:\n" + string.Join("\n", r.Messages));
            return r.Graph;
        }

        /// <summary>Bit-equal, except that +0 and −0 count as equal (interpolation turns −0 into +0 at lattice points).</summary>
        public static bool SameValue(float a, float b){
            if (a == 0f && b == 0f) return true;
            return BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);
        }

        public static bool Close(float actual, double expected, double relative = 1e-5){
            return Math.Abs(actual - expected) <= relative * Math.Max(1.0, Math.Abs(expected));
        }
    }

    /// <summary>Fluent builder of a <see cref="NoiseGraph" />.</summary>
    internal sealed class GraphBuilder{
        public readonly NoiseGraph Graph = new();

        public GraphBuilder Noise(string name, NoiseKind kind, int octaves = 1, float wavelength = 100f, float wavelengthY = 0f,
            float lacunarity = 2f, float gain = 0.5f, float amplitude = 1f){
            Graph.Noises[name] = new NoiseDefinition{
                Name = name,
                Kind = kind,
                Octaves = octaves,
                Wavelength = wavelength,
                WavelengthY = kind == NoiseKind.Simplex3 && wavelengthY > 0f ? wavelengthY : wavelength,
                Lacunarity = lacunarity,
                Gain = gain,
                Amplitude = amplitude
            };
            return this;
        }

        public GraphBuilder Node(string name, Dictionary<string, object> fields){
            Graph.Nodes[name] = new GraphNodeDefinition{ Name = name, Fields = fields };
            return this;
        }

        public GraphBuilder Output(string name, object reference){
            Graph.Outputs[name] = reference is int i ? (double)i : reference;
            return this;
        }
    }
}
