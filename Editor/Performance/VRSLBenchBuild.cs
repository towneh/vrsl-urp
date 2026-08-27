using System;
using System.IO;
using UnityEditor;
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
    /// <b>It changes nothing in the host project.</b> The only thing it writes is a
    /// scratch scene under Assets/, deleted on the way out and again on the way in, so a
    /// build killed part-way through leaves no settings behind to notice later. What it
    /// needs and cannot set — frame timing stats — it refuses on rather than turning on,
    /// because turning a project setting on for somebody is how it stays on.
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
            // Asked here rather than in Build, which the headless path also calls: a modal
            // prompt would hang a batch run with nobody there to answer it. Build replaces
            // the open scene outright and NewScene does not prompt, so without this an
            // author loses unsaved work to a menu item that says nothing about scenes.
            // Returns false only on Cancel, and shows no dialog when nothing is dirty.
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

            string folder = EditorUtility.SaveFolderPanel(
                "Where to put the sweep player", "", "VRSL-Sweep-Player");
            if (string.IsNullOrEmpty(folder)) return;

            string restore = EditorSceneManager.GetActiveScene().path;
            string exe;
            string failure;
            try
            {
                exe = Build(folder, out failure);
            }
            finally
            {
                // The scratch scene is deleted on the way out, so leaving it open would
                // leave the author looking at a scene asset that no longer exists. In the
                // finally because a build that throws strands them there just as surely as
                // one that succeeds. The active scene only: a multi-scene setup comes back
                // as one scene, and the prompt above is what makes sure none of it is lost
                // rather than merely unloaded.
                if (string.IsNullOrEmpty(restore))
                    EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                else
                    EditorSceneManager.OpenScene(restore, OpenSceneMode.Single);
            }

            if (exe == null) EditorUtility.DisplayDialog("Sweep player", failure, "OK");
            else EditorUtility.RevealInFinder(exe);
        }

        /// <summary>The command-line entry point. <c>-out &lt;dir&gt;</c> is where the
        /// player goes.</summary>
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

                string exe = Build(outputDir, out string failure);
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
        static string Build(string outputDir, out string failure)
        {
            failure = null;

            // Checked rather than set. Without it FrameTimingManager answers with a GPU
            // time of zero and the whole run is CPU-basis, which is the thing a player
            // run exists to avoid — but it is the host project's setting, and a build
            // step that quietly turns project settings on leaves them on.
            if (!PlayerSettings.enableFrameTimingStats)
            {
                failure = "Frame Timing Stats is off in this project's Player Settings, so a "
                        + "player built from it would report a GPU time of exactly zero and "
                        + "every figure would be CPU-basis. Turn it on under Project Settings "
                        + "> Player > Other Settings and build again.";
                return null;
            }

            try
            {
                Discard();
                Directory.CreateDirectory(outputDir);
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
                Discard();
                AssetDatabase.Refresh();
            }
        }

        /// <summary>
        /// Get rid of the scratch scene.
        ///
        /// Called on the way in as well as out: a build killed part-way through never
        /// reaches its own cleanup, and leaving a 200-fixture truss inside somebody
        /// else's Assets folder is the kind of thing that gets noticed weeks later.
        /// </summary>
        static void Discard()
        {
            if (AssetDatabase.IsValidFolder(ScratchFolder)) AssetDatabase.DeleteAsset(ScratchFolder);
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
            host.noiseFloorMs  = VRSLPerfFloor.Get(
                host.noiseFloorGpu, VRSLBenchmarkEnvironment.PlayerContext);
        }

        static string Argument(string name)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == name) return args[i + 1];
            return null;
        }
    }
}
