using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Engine.Editor.Build{
    /// <summary>
    ///     Builds a release (non-development) Windows x64 player of the scenes in the build list into
    ///     <c>Builds/&lt;backend&gt;/</c> at the repository root, for the goal run of a player build (start the player
    ///     with <c>-goalrun</c>). The scripting define symbols stay as they are; a backend other than the project's
    ///     and a CPU architecture other than x64 are set for the build only and put back afterwards. Next to the executable it writes
    ///     <c>build-info.txt</c> (commit, backend, result), which the goal run copies into its result file.
    ///     <para>
    ///         In the open Editor: the menu items under Infinitile/Goal Run (the build starts after the menu call
    ///         returns). Without an Editor: <c>-batchmode -quit -projectPath &lt;repo&gt; -executeMethod
    ///         Engine.Editor.Build.GoalRunPlayerBuild.BuildFromCommandLine -goalrunBackend Mono</c> (or IL2CPP).
    ///     </para>
    /// </summary>
    internal static class GoalRunPlayerBuild{
        private const string MenuRoot = "Infinitile/Goal Run/";
        private const string BackendArgument = "-goalrunBackend";
        internal const string BuildInfoFile = "build-info.txt";

        // The Windows player's CPU architecture is an Editor user setting (it may be ARM64); the build pins x64.
        private const string ArchitectureSetting = "Architecture";
        private const string X64 = "x64";

        [MenuItem(MenuRoot + "Build Release Player (Mono)")]
        private static void BuildMonoFromMenu(){
            BuildOnNextUpdate(ScriptingImplementation.Mono2x);
        }

        [MenuItem(MenuRoot + "Build Release Player (IL2CPP)")]
        private static void BuildIl2CppFromMenu(){
            BuildOnNextUpdate(ScriptingImplementation.IL2CPP);
        }

        /// <summary>Starts the build on the Editor's next update, so the menu call returns at once.</summary>
        private static void BuildOnNextUpdate(ScriptingImplementation backend){
            Debug.Log($"[GoalRunBuild] {BackendName(backend)} build requested");
            void Run(){
                EditorApplication.update -= Run;
                Build(backend);
            }

            EditorApplication.update += Run;
        }

        /// <summary>Entry point for <c>-executeMethod</c>; exits with 1 when the build fails.</summary>
        internal static void BuildFromCommandLine(){
            string[] args = Environment.GetCommandLineArgs();
            int index = Array.FindIndex(args, a => string.Equals(a, BackendArgument, StringComparison.OrdinalIgnoreCase));
            string name = index >= 0 && index + 1 < args.Length ? args[index + 1] : "Mono";
            ScriptingImplementation backend = string.Equals(name, "IL2CPP", StringComparison.OrdinalIgnoreCase)
                ? ScriptingImplementation.IL2CPP
                : ScriptingImplementation.Mono2x;
            BuildReport report = Build(backend);
            if (Application.isBatchMode) EditorApplication.Exit(report != null && report.summary.result == BuildResult.Succeeded ? 0 : 1);
        }

        private static string BackendName(ScriptingImplementation backend){
            return backend == ScriptingImplementation.IL2CPP ? "IL2CPP" : "Mono";
        }

        /// <summary>The folder the player of <paramref name="backend" /> is built into.</summary>
        internal static string OutputFolder(ScriptingImplementation backend){
            string root = Path.GetDirectoryName(Application.dataPath) ?? ".";
            return Path.Combine(root, "Builds", BackendName(backend));
        }

        internal static BuildReport Build(ScriptingImplementation backend){
            string[] scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
            string folder = OutputFolder(backend);
            Directory.CreateDirectory(folder);
            string infoPath = Path.Combine(folder, BuildInfoFile);
            if (File.Exists(infoPath)) File.Delete(infoPath); // a stale one would read as this build's
            if (scenes.Length == 0){
                Debug.LogError("[GoalRunBuild] no enabled scene in the build list");
                return null;
            }

            NamedBuildTarget target = NamedBuildTarget.Standalone;
            ScriptingImplementation projectBackend = PlayerSettings.GetScriptingBackend(target);
            string platform = BuildPipeline.GetBuildTargetName(BuildTarget.StandaloneWindows64);
            string userArchitecture = EditorUserBuildSettings.GetPlatformSettings(platform, ArchitectureSetting);
            BuildPlayerOptions options = new(){
                scenes = scenes,
                locationPathName = Path.Combine(folder, PlayerSettings.productName + ".exe"),
                target = BuildTarget.StandaloneWindows64,
                targetGroup = BuildTargetGroup.Standalone,
                options = BuildOptions.None
            };

            Debug.Log($"[GoalRunBuild] building a release {BackendName(backend)} player of {string.Join(", ", scenes)} " +
                      $"into {folder}");
            BuildReport report;
            try{
                if (projectBackend != backend) PlayerSettings.SetScriptingBackend(target, backend);
                if (userArchitecture != X64) EditorUserBuildSettings.SetPlatformSettings(platform, ArchitectureSetting, X64);
                report = BuildPipeline.BuildPlayer(options);
            }
            finally{
                if (PlayerSettings.GetScriptingBackend(target) != projectBackend)
                    PlayerSettings.SetScriptingBackend(target, projectBackend);
                if (EditorUserBuildSettings.GetPlatformSettings(platform, ArchitectureSetting) != userArchitecture)
                    EditorUserBuildSettings.SetPlatformSettings(platform, ArchitectureSetting, userArchitecture);
            }

            BuildSummary summary = report.summary;
            File.WriteAllText(infoPath, BuildInfo(backend, summary));
            string text = string.Format(CultureInfo.InvariantCulture,
                "[GoalRunBuild] {0}: {1} in {2:0} s, {3:0.0} MB, {4} error(s), {5} warning(s); {6}", BackendName(backend),
                summary.result, summary.totalTime.TotalSeconds, summary.totalSize / (1024d * 1024d), summary.totalErrors,
                summary.totalWarnings, options.locationPathName);
            if (summary.result == BuildResult.Succeeded) Debug.Log(text);
            else Debug.LogError(text);
            return report;
        }

        private static string BuildInfo(ScriptingImplementation backend, BuildSummary summary){
            StringBuilder text = new();
            text.AppendLine($"commit {Git("rev-parse --short HEAD")}{(Git("status --porcelain").Length > 0 ? " with uncommitted changes" : "")}" +
                            $" on branch {Git("rev-parse --abbrev-ref HEAD")}");
            text.AppendLine(string.Format(CultureInfo.InvariantCulture, "built {0:yyyy-MM-dd HH:mm} with Unity {1}: " +
                                                                        "Windows x64, {2}, release (not development)",
                DateTime.Now, Application.unityVersion, BackendName(backend)));
            string defines = PlayerSettings.GetScriptingDefineSymbols(NamedBuildTarget.Standalone);
            text.AppendLine($"scripting defines: {(defines.Length > 0 ? defines : "none")}");
            text.AppendLine($"build result: {summary.result}, platform {summary.platform}");
            return text.ToString();
        }

        private static string Git(string arguments){
            try{
                using Process git = Process.Start(new ProcessStartInfo("git", arguments){
                    WorkingDirectory = Path.GetDirectoryName(Application.dataPath) ?? ".",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                if (git == null) return "unknown";
                string output = git.StandardOutput.ReadToEnd().Trim();
                git.WaitForExit(10000);
                return output;
            }
            catch (Exception){
                return "unknown";
            }
        }
    }
}
