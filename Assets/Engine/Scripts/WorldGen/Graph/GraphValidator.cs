using System.Collections.Generic;

namespace Engine.Scripts.WorldGen.Graph{
    /// <summary>
    ///     Checks a noise graph without keeping the compiled result: malformed JSON, unknown names and ops, bad fields,
    ///     cycles, missing required outputs and graphs over the <see cref="GraphLimits" />. Every message names the file
    ///     and, where there is one, the node. The checks are the compiler's own, so a graph that validates compiles.
    /// </summary>
    public static class GraphValidator{
        public static List<GraphMessage> Validate(NoiseGraph graph, string file, IEnumerable<string> requiredOutputs = null){
            GraphCompileResult result = GraphCompiler.Compile(graph, file, requiredOutputs);
            result.Graph?.Dispose();
            return result.Messages;
        }

        public static List<GraphMessage> Validate(string json, string file, IEnumerable<string> requiredOutputs = null){
            GraphCompileResult result = GraphCompiler.Compile(json, file, requiredOutputs);
            result.Graph?.Dispose();
            return result.Messages;
        }
    }
}
