using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Engine.Editor.Build{
    /// <summary>
    ///     Builds a release player (non-development, Mono, x64) of the scenes in the build list for a GitHub release
    ///     (see <c>Docs/Releases.md</c>). The version (major.minor.patch) goes into
    ///     <see cref="PlayerSettings.bundleVersion" />, so the player reads it as <see cref="Application.version" />. The
    ///     version, the Mono backend and the Windows CPU architecture are set for the build only and put back afterwards;
    ///     in the repository the version stays <c>dev</c>.
    ///     <para>
    ///         Entry point for <c>-executeMethod Engine.Editor.Build.ReleaseBuild.BuildFromCommandLine</c>. It takes the
    ///         arguments GameCI's unity-builder passes (<c>-customBuildTarget StandaloneWindows64|StandaloneLinux64</c>,
    ///         <c>-customBuildPath &lt;folder&gt;/&lt;file&gt;</c>, <c>-buildVersion X.Y.Z</c>); the local fallback
    ///         <c>Tools/Release/release-local.sh</c> passes the same ones. The player goes into the folder of
    ///         <c>-customBuildPath</c> and is named after the product (<c>Infinitile.exe</c> or
    ///         <c>Infinitile.x86_64</c>), whatever file name the path ends in.
    ///     </para>
    /// </summary>
    internal static class ReleaseBuild{
        private const string TargetArgument = "-customBuildTarget";
        private const string PathArgument = "-customBuildPath";
        private const string VersionArgument = "-buildVersion";
        private const string LogPrefix = "[ReleaseBuild] ";

        // The Windows player's CPU architecture is an Editor user setting (it may be ARM64); the build pins x64.
        private const string ArchitectureSetting = "Architecture";
        private const string X64 = "x64";

        private static readonly Regex ReleaseVersion = new(@"^\d+\.\d+\.\d+$", RegexOptions.CultureInvariant);

        /// <summary>Entry point for <c>-executeMethod</c>; in batch mode it exits with 1 when the build fails.</summary>
        internal static void BuildFromCommandLine(){
            string[] args = Environment.GetCommandLineArgs();
            string targetName = Argument(args, TargetArgument);
            string path = Argument(args, PathArgument);
            string version = Argument(args, VersionArgument);

            BuildReport report = null;
            if (!TryParseTarget(targetName, out BuildTarget target))
                Debug.LogError($"{LogPrefix}{TargetArgument} must be StandaloneWindows64 or StandaloneLinux64, not '{targetName}'");
            else if (string.IsNullOrEmpty(path))
                Debug.LogError($"{LogPrefix}{PathArgument} is missing");
            else if (version == null || !ReleaseVersion.IsMatch(version))
                Debug.LogError($"{LogPrefix}{VersionArgument} must be major.minor.patch (e.g. 1.0.0), not '{version}'");
            else
                report = Build(target, Path.GetDirectoryName(Path.GetFullPath(path)), version);

            bool succeeded = report != null && report.summary.result == BuildResult.Succeeded;
            if (Application.isBatchMode) EditorApplication.Exit(succeeded ? 0 : 1);
        }

        private static string Argument(string[] args, string name){
            int index = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }

        private static bool TryParseTarget(string name, out BuildTarget target){
            if (string.Equals(name, nameof(BuildTarget.StandaloneWindows64), StringComparison.OrdinalIgnoreCase)){
                target = BuildTarget.StandaloneWindows64;
                return true;
            }

            if (string.Equals(name, nameof(BuildTarget.StandaloneLinux64), StringComparison.OrdinalIgnoreCase)){
                target = BuildTarget.StandaloneLinux64;
                return true;
            }

            target = BuildTarget.NoTarget;
            return false;
        }

        /// <summary>The player's executable name for <paramref name="target" />.</summary>
        internal static string ExecutableName(BuildTarget target){
            return PlayerSettings.productName + (target == BuildTarget.StandaloneWindows64 ? ".exe" : ".x86_64");
        }

        /// <summary>Builds a release player of version <paramref name="version" /> for <paramref name="target" /> into <paramref name="folder" />.</summary>
        internal static BuildReport Build(BuildTarget target, string folder, string version){
            string[] scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
            if (scenes.Length == 0){
                Debug.LogError($"{LogPrefix}no enabled scene in the build list");
                return null;
            }

            Directory.CreateDirectory(folder);
            BuildPlayerOptions options = new(){
                scenes = scenes,
                locationPathName = Path.Combine(folder, ExecutableName(target)),
                target = target,
                targetGroup = BuildTargetGroup.Standalone,
                options = BuildOptions.None
            };

            NamedBuildTarget named = NamedBuildTarget.Standalone;
            ScriptingImplementation projectBackend = PlayerSettings.GetScriptingBackend(named);
            string projectVersion = PlayerSettings.bundleVersion;
            string windows = BuildPipeline.GetBuildTargetName(BuildTarget.StandaloneWindows64);
            string userArchitecture = EditorUserBuildSettings.GetPlatformSettings(windows, ArchitectureSetting);
            bool pinArchitecture = target == BuildTarget.StandaloneWindows64 && userArchitecture != X64;

            Debug.Log($"{LogPrefix}building release {version} for {target} (Mono) of {string.Join(", ", scenes)} into {folder}");
            BuildReport report;
            try{
                PlayerSettings.bundleVersion = version;
                if (projectBackend != ScriptingImplementation.Mono2x)
                    PlayerSettings.SetScriptingBackend(named, ScriptingImplementation.Mono2x);
                if (pinArchitecture) EditorUserBuildSettings.SetPlatformSettings(windows, ArchitectureSetting, X64);
                report = BuildPipeline.BuildPlayer(options);
            }
            finally{
                PlayerSettings.bundleVersion = projectVersion;
                if (PlayerSettings.GetScriptingBackend(named) != projectBackend)
                    PlayerSettings.SetScriptingBackend(named, projectBackend);
                if (pinArchitecture) EditorUserBuildSettings.SetPlatformSettings(windows, ArchitectureSetting, userArchitecture);
            }

            BuildSummary summary = report.summary;
            string text = string.Format(CultureInfo.InvariantCulture,
                "{0}{1} {2}: {3} in {4:0} s, {5:0.0} MB, {6} error(s), {7} warning(s); {8}", LogPrefix, target, version,
                summary.result, summary.totalTime.TotalSeconds, summary.totalSize / (1024d * 1024d), summary.totalErrors,
                summary.totalWarnings, options.locationPathName);
            if (summary.result == BuildResult.Succeeded) Debug.Log(text);
            else Debug.LogError(text);
            return report;
        }
    }
}
