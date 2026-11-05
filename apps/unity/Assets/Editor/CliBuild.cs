using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Mas3D.Editor
{
    /// <summary>
    /// The packaging entry point: one IL2CPP Windows player, configured by code so the settings that
    /// matter for a shippable build cannot drift with whatever the ProjectSettings asset happens to say.
    /// The editor writes the settings back to ProjectSettings.asset as a side effect (approved: the diff
    /// is the record of what this build requires).
    ///
    /// Line format is fixed: tools/unity.ps1 greps for [build] to decide whether the run succeeded.
    /// </summary>
    public static class CliBuild
    {
        const string Summary = "[build]";

        public static void Build()
        {
            var watch = Stopwatch.StartNew();
            try
            {
                var args = Environment.GetCommandLineArgs();
                var requested = ArgValue(args, "-out");
                if (requested == null && HasFlag(args, "-out")) return; // ArgValue already failed
                var outPath = Path.GetFullPath(requested ?? DefaultOutPath());
                var development = HasFlag(args, "-dev");

                // A build takes minutes, so rubbish paths are refused before anything is written.
                if (Directory.Exists(outPath) || outPath.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
                {
                    Fail("out path is a directory: " + outPath);
                    return;
                }
                if (!outPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    outPath += ".exe";
                }

                var scenes = EditorBuildSettings.scenes
                    .Where(scene => scene.enabled && !string.IsNullOrEmpty(scene.path))
                    .Select(scene => scene.path)
                    .ToArray();

                if (scenes.Length == 0)
                {
                    Fail("no scenes");
                    return;
                }

                // Scene paths are project-relative; the editor's CWD is not guaranteed to be
                // apps/unity, so anchor them at the project folder before touching the disk.
                var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
                foreach (var scene in scenes)
                {
                    var sceneFile = Path.Combine(projectRoot, scene);
                    if (File.Exists(sceneFile)) continue;
                    Fail("scene missing: " + sceneFile);
                    return;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(outPath));

                // Product identity and window behaviour travel with the player, so they are set here
                // rather than left to the editor defaults (productName was still "unity").
                PlayerSettings.productName = "Mas3D";
                PlayerSettings.companyName = "Mas3D";
                PlayerSettings.defaultScreenWidth = 1280;
                PlayerSettings.defaultScreenHeight = 720;
                PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
                PlayerSettings.resizableWindow = true;
                PlayerSettings.runInBackground = true;
                PlayerSettings.SetScriptingBackend(BuildTargetGroup.Standalone, ScriptingImplementation.IL2CPP);
                // Unity 6 already defaults the API profile to .NET Standard 2.1, and this
                // editor build no longer exposes a NET_Standard_2_1 enum member, so nothing is set.
                PlayerSettings.SetManagedStrippingLevel(BuildTargetGroup.Standalone, ManagedStrippingLevel.Low);
                PlayerSettings.SetIl2CppCompilerConfiguration(BuildTargetGroup.Standalone, Il2CppCompilerConfiguration.Release);
                AssetDatabase.SaveAssets();

                var options = new BuildPlayerOptions
                {
                    scenes = scenes,
                    locationPathName = outPath,
                    target = BuildTarget.StandaloneWindows64,
                    options = development ? BuildOptions.Development : BuildOptions.None,
                };

                var report = BuildPipeline.BuildPlayer(options);
                if (report.summary.result != BuildResult.Succeeded)
                {
                    Fail("BuildPlayer " + report.summary.result + ", errors=" + report.summary.totalErrors);
                    return;
                }

                if (!File.Exists(outPath))
                {
                    Fail("player missing: " + outPath);
                    return;
                }

                var dataDir = Path.Combine(Path.GetDirectoryName(outPath),
                    Path.GetFileNameWithoutExtension(outPath) + "_Data");
                if (!Directory.Exists(Path.Combine(dataDir, "il2cpp_data")))
                {
                    Fail("not an IL2CPP player: " + Path.Combine(dataDir, "il2cpp_data") + " missing");
                    return;
                }

                watch.Stop();
                Debug.Log(string.Format(CultureInfo.InvariantCulture,
                    "{0} target={1} backend={2} result=Succeeded path={3} bytes={4} elapsed={5:F1}",
                    Summary, "StandaloneWindows64",
                    PlayerSettings.GetScriptingBackend(BuildTargetGroup.Standalone),
                    outPath, new FileInfo(outPath).Length, watch.Elapsed.TotalSeconds));
                EditorApplication.Exit(0);
            }
            catch (Exception boom)
            {
                Fail(boom.GetType().Name + ": " + boom.Message);
            }
        }

        static void Fail(string reason)
        {
            var firstLine = (reason ?? "unknown").Split('\n')[0].Trim();
            Debug.Log(string.Format(CultureInfo.InvariantCulture, "{0} result=Failed error={1}", Summary, firstLine));
            EditorApplication.Exit(1);
        }

        /// <summary>Repo-relative default, resolved from the project folder the same way the other CLIs do.</summary>
        static string DefaultOutPath()
        {
            return Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "..", "build", "Mas3D.exe"));
        }

        static string ArgValue(string[] args, string flag)
        {
            for (var i = 0; i < args.Length - 1; i++)
            {
                if (!string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase)) continue;
                var value = args[i + 1];
                if (string.IsNullOrEmpty(value) || value.StartsWith("-", StringComparison.Ordinal))
                {
                    Fail("missing value for " + flag);
                    return null;
                }
                return value;
            }
            return null;
        }

        static bool HasFlag(string[] args, string flag)
        {
            for (var i = 0; i < args.Length; i++)
                if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }
}
