using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Aetheria.UnityClient.EditorTools
{
    /// <summary>
    /// Headless build entry point — the LAUNCHER calls this so nobody has to open Unity:
    ///   Unity.exe -quit -batchmode -projectPath unity/AetheriaClient
    ///             -executeMethod Aetheria.UnityClient.EditorTools.CommandLineBuild.Build
    ///             -buildPath C:\...\out
    /// Builds a Windows player into buildPath (Aetheria.exe). Creates a minimal scene when the
    /// project has none in its build settings (the bootstrap builds the whole game at runtime).
    /// </summary>
    public static class CommandLineBuild
    {
        public static void Build()
        {
            string outDir = Arg("-buildPath") ?? Path.Combine("Builds", "Auto");
            Directory.CreateDirectory(outDir);

            string[] scenes = EditorBuildSettings.scenes
                .Where(s => s.enabled)
                .Select(s => s.path)
                .Where(File.Exists)
                .ToArray();

            if (scenes.Length == 0)
            {
                // No scene configured: make an empty one — AetheriaBootstrap does the rest.
                const string AutoScene = "Assets/Scenes/Auto.unity";
                if (!File.Exists(AutoScene))
                {
                    Directory.CreateDirectory("Assets/Scenes");
                    var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
                    EditorSceneManager.SaveScene(scene, AutoScene);
                }

                scenes = new[] { AutoScene };
            }

            BuildReport report = BuildPipeline.BuildPlayer(
                scenes,
                Path.Combine(outDir, "Aetheria.exe"),
                BuildTarget.StandaloneWindows64,
                BuildOptions.None);

            if (report.summary.result != BuildResult.Succeeded)
            {
                Debug.LogError("[Aetheria] Build FAILED: " + report.summary.result +
                               " (" + report.summary.totalErrors + " erreurs)");
                EditorApplication.Exit(1);
                return;
            }

            Debug.Log("[Aetheria] Build OK → " + outDir);
            EditorApplication.Exit(0);
        }

        private static string Arg(string name)
        {
            string[] args = System.Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == name)
                {
                    return args[i + 1];
                }
            }

            return null;
        }
    }
}
