using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace VRSL.URP.EditorScripts
{
    /// <summary>
    /// Bakes the sweep scene into a standalone player that measures the matrix and
    /// quits.
    ///
    /// R-M0-6 asks for the numbers a results table quotes to come from a player. The
    /// editor can produce a GPU-basis matrix and batch mode cannot — FrameTimingManager
    /// returns a GPU time of exactly zero there — so without this the only quotable run
    /// is one somebody sat through in the editor window.
    ///
    /// It builds only the sweep's own scene, so nothing else in the host project boots
    /// with it, and it puts everything it changed in the project back afterwards.
    /// </summary>
    static class VRSLBenchBuild
    {
        /// <summary>Under Assets/ because a scene has to be in the project to be built,
        /// and deleted again on the way out.</summary>
        const string ScratchFolder = "Assets/VRSL-BenchBuild";
        const string ScenePath     = ScratchFolder + "/VRSL-Sweep.unity";
        const string ExeName       = "VRSLSweep.exe";

        [MenuItem("VRSL/URP/Performance/Build Sweep Player")]
        static void BuildFromMenu()
        {
            string folder = EditorUtility.SaveFolderPanel(
                "Where to put the sweep player", "", "VRSL-Sweep-Player");
            if (string.IsNullOrEmpty(folder)) return;

            string exe = Build(folder, forceMono: false, out string failure);
            if (exe == null) EditorUtility.DisplayDialog("Sweep player", failure, "OK");
            else EditorUtility.RevealInFinder(exe);
        }

        /// <summary>
        /// The command-line entry point.
        ///
        /// <c>-out &lt;dir&gt;</c> is where the player goes.
        /// <c>-mono</c> builds against Mono rather than whatever the project is set to,
        /// which is minutes rather than tens of minutes — at the cost of CPU-side
        /// figures that are not the ones a shipped player would produce. The run records
        /// which backend it was, so the two cannot be mixed up afterwards.
        /// </summary>
        public static void BuildFromCommandLine()
        {
            int exit = 1;
            try
            {
                string outputDir = Argument("-out");
                if (outputDir == null)
                {
                    Debug.LogError("[VRSL bench build] FAIL — needs -out <dir>.");
                    return;
                }

                string exe = Build(outputDir, HasFlag("-mono"), out string failure);
                if (exe == null)
                {
                    Debug.LogError($"[VRSL bench build] FAIL — {failure}");
                    return;
                }

                Debug.Log($"[VRSL bench build] player: {exe}");
                exit = 0;
            }
            catch (Exception e)
            {
                Debug.LogError($"[VRSL bench build] FAIL — {e}");
            }
            finally
            {
                if (Application.isBatchMode) EditorApplication.Exit(exit);
            }
        }

        /// <summary>Returns the path of the built executable, or null with a reason.</summary>
        static string Build(string outputDir, bool forceMono, out string failure)
        {
            failure = null;

            var standalone = NamedBuildTarget.Standalone;
            var backendWas = PlayerSettings.GetScriptingBackend(standalone);
            bool timingWas     = PlayerSettings.enableFrameTimingStats;
            bool backgroundWas = PlayerSettings.runInBackground;

            try
            {
                // Without this FrameTimingManager answers with a GPU time of zero and the
                // whole run is CPU-basis — which is the thing a player run exists to
                // avoid. Set rather than checked, because a project that happens to have
                // it off would otherwise produce a plausible-looking useless matrix.
                PlayerSettings.enableFrameTimingStats = true;

                // A player whose window loses focus is throttled by Unity, and a sweep
                // measuring throttled frames measures the throttle.
                PlayerSettings.runInBackground = true;

                if (forceMono) PlayerSettings.SetScriptingBackend(standalone, ScriptingImplementation.Mono2x);

                Directory.CreateDirectory(outputDir);
                if (!AssetDatabase.IsValidFolder(ScratchFolder))
                    AssetDatabase.CreateFolder("Assets", Path.GetFileName(ScratchFolder));

                var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                if (VRSLBenchmarkScene.Populate() == null)
                {
                    failure = "the sweep scene could not be built.";
                    return null;
                }
                AddHost();
                if (!EditorSceneManager.SaveScene(scene, ScenePath))
                {
                    failure = $"could not save the sweep scene to {ScenePath}.";
                    return null;
                }

                string exe = Path.Combine(outputDir, ExeName);
                var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
                {
                    // The sweep's scene and nothing else, so none of the host project's
                    // own bootstrapping runs alongside the thing being measured.
                    scenes           = new[] { ScenePath },
                    locationPathName = exe,
                    target           = BuildTarget.StandaloneWindows64,
                    targetGroup      = BuildTargetGroup.Standalone,
                    options          = BuildOptions.None,
                });

                if (report.summary.result != BuildResult.Succeeded)
                {
                    failure = $"the build {report.summary.result.ToString().ToLowerInvariant()} "
                            + $"with {report.summary.totalErrors} error(s).";
                    return null;
                }
                return exe;
            }
            finally
            {
                PlayerSettings.SetScriptingBackend(standalone, backendWas);
                PlayerSettings.enableFrameTimingStats = timingWas;
                PlayerSettings.runInBackground        = backgroundWas;

                // The scene is inside the host project's Assets folder, so leaving it
                // behind leaves somebody else's project carrying a 200-fixture truss.
                if (AssetDatabase.IsValidFolder(ScratchFolder)) AssetDatabase.DeleteAsset(ScratchFolder);
                AssetDatabase.Refresh();
            }
        }

        /// <summary>
        /// Put what only the editor can know into the built scene.
        ///
        /// A player has no VERSION.txt beside it, no checkout to ask git about and no
        /// EditorPrefs holding a noise floor, so all three are baked in here or the run
        /// does without them.
        /// </summary>
        static void AddHost()
        {
            var host = new GameObject("VRSL Sweep Host").AddComponent<VRSLSweepPlayerHost>();

            var stamp = new VRSLBenchmarkEnvironment();
            VRSLPackageStamp.StampPackage(stamp);
            host.packageVersion = stamp.packageVersion;
            host.gitCommit      = stamp.gitCommit;

            // The player context's floor rather than the editor's: they are separate
            // measurements of separate things. Filed against the GPU it was measured on,
            // so a player carried to another machine leaves it behind rather than
            // judging that machine against this one.
            host.noiseFloorGpu = SystemInfo.graphicsDeviceName;
            host.noiseFloorMs  = VRSLPerfFloor.Get(host.noiseFloorGpu, "Player");
        }

        static string Argument(string name)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == name) return args[i + 1];
            return null;
        }

        static bool HasFlag(string name)
        {
            foreach (string arg in Environment.GetCommandLineArgs())
                if (arg == name) return true;
            return false;
        }
    }
}
