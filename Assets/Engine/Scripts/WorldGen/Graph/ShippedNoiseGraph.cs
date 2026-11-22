using UnityEngine;

namespace Engine.Scripts.WorldGen.Graph{
    /// <summary>The noise graph that ships with the game, a JSON text asset in Resources.</summary>
    public static class ShippedNoiseGraph{
        /// <summary>Path of the graph under a Resources folder (without extension).</summary>
        public const string ResourcePath = "WorldGen/NoiseGraph";

        /// <summary>Name used for the file in validator messages.</summary>
        public const string FileName = "Resources/WorldGen/NoiseGraph.json";

        /// <summary>The graph's JSON text, or null when the asset is missing.</summary>
        public static string LoadJson(){
            TextAsset asset = Resources.Load<TextAsset>(ResourcePath);
            return asset != null ? asset.text : null;
        }

        /// <summary>Compiles the shipped graph; a missing asset is an error.</summary>
        public static GraphCompileResult Compile(){
            string json = LoadJson();
            if (json != null) return GraphCompiler.Compile(json, FileName);
            GraphCompileResult missing = new();
            missing.Messages.Add(new GraphMessage(GraphMessageSeverity.Error, FileName, null, "The noise graph asset is missing."));
            return missing;
        }
    }
}
