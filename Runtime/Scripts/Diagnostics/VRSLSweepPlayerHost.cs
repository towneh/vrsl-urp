using System;
using System.Collections;
using UnityEngine;

namespace VRSL.URP
{
    /// <summary>
    /// Runs the standard sweep in a built player and quits.
    ///
    /// R-M0-6 wants the numbers a results table quotes to come from a player rather
    /// than the editor, and batch mode cannot stand in: FrameTimingManager returns a
    /// GPU time of exactly zero there, so a headless capture is CPU-basis whatever
    /// else is true of it. A player with a window has a real GPU clock.
    ///
    /// Placed in the built scene by the build step, which is also where the serialized
    /// fields are filled — a player has no VERSION.txt beside it, no checkout to ask git
    /// about and no EditorPrefs to read a noise floor from, so what it knows about
    /// itself is what was baked in.
    /// </summary>
    class VRSLSweepPlayerHost : MonoBehaviour
    {
        [SerializeField] internal string packageVersion;
        [SerializeField] internal string gitCommit;
        /// <summary>The player-context floor measured on the machine that built this,
        /// or zero. A run carrying zero is honest about it and the report says so; a run
        /// carrying the editor's would be quietly judged against the wrong thing.</summary>
        [SerializeField] internal double noiseFloorMs;

        /// <summary>The GPU that floor was measured on. A player copied to another
        /// machine leaves it behind rather than judging that machine against this
        /// one — a floor is a property of the hardware, and an imported one only ever
        /// masks regressions, since a floor is raised and never lowered.</summary>
        [SerializeField] internal string noiseFloorGpu;

        /// <summary>Where the run's folder goes, from <c>-vrsl-out &lt;dir&gt;</c>. The
        /// build folder when it is not given, which is where a player's
        /// <c>Application.dataPath</c> points.</summary>
        const string OutputArgument = "-vrsl-out";
        /// <summary>Present on the command line: draw VRSL's own normals everywhere,
        /// for the half of a pair that measures what reading URP's is worth.</summary>
        const string ForceOwnNormalsArgument = "-vrsl-force-own-normals";
        /// <summary>Present on the command line: measure the mirror matrix rather
        /// than the standard one.</summary>
        const string MirrorsArgument = "-vrsl-mirrors";

        /// <summary>What a script greps the log for. The player writes the folder
        /// itself rather than the caller guessing at a timestamp.</summary>
        internal const string ReportPrefix = "[VRSL sweep] report: ";

        /// <summary>
        /// Put back any cameras the sweep switched off, whatever happened to the
        /// coroutine.
        /// </summary>
        /// <remarks>
        /// Unity does not unwind a coroutine when its host is disabled or destroyed, so
        /// the job's own finally is not reached on that path and the suppression would
        /// outlive the run. Restoring is idempotent, so this costs nothing on the
        /// ordinary path where the job has already done it.
        /// </remarks>
        void OnDisable() => VRSLBenchmarkScene.RestoreCameras();

        IEnumerator Start()
        {
            // Unity throttles a player whose window is not focused, and a sweep measuring
            // throttled frames measures the throttle. Set here rather than in the build
            // step, which would mean writing to the host project's Player Settings for a
            // property a player can simply set for itself.
            Application.runInBackground = true;

            string output = Argument(OutputArgument);
            if (!string.IsNullOrEmpty(output)) VRSLBenchmarkReport.OutputRoot = output;

            var outcome = new VRSLSweepOutcome();
            bool forceOwnNormals = Array.IndexOf(Environment.GetCommandLineArgs(), ForceOwnNormalsArgument) >= 0;
            bool mirrors         = Array.IndexOf(Environment.GetCommandLineArgs(), MirrorsArgument) >= 0;
            var job     = VRSLSweepJob.Run(outcome, Stamp, forceOwnNormals, mirrors);

            // Stepped by hand so a throw can be caught. A coroutine that dies of an
            // exception simply stops, and this one is the only thing that ever quits:
            // the player would sit there with a window open, having written nothing and
            // returned no exit code, blocking whatever launched it until somebody kills
            // it. That is the one failure an unattended run cannot report for itself.
            while (true)
            {
                bool moved;
                try
                {
                    moved = job.MoveNext();
                }
                catch (Exception e)
                {
                    Debug.LogError($"[VRSL sweep] FAIL — the sweep threw: {e}");
                    Quit(1);
                    yield break;
                }

                if (!moved) break;
                yield return job.Current;
            }

            if (outcome.run == null)
            {
                Debug.LogError("[VRSL sweep] FAIL — "
                             + (outcome.error ?? "the sweep reported neither a result nor a reason."));
                Quit(1);
                yield break;
            }

            string folder;
            try
            {
                folder = VRSLBenchmarkReport.Write(outcome.run);
            }
            catch (Exception e)
            {
                // A matrix that was measured and cannot be written down has measured
                // nothing, so this is a failure rather than a warning.
                Debug.LogError($"[VRSL sweep] FAIL — could not write the report: {e.Message}");
                Quit(1);
                yield break;
            }

            Debug.Log(ReportPrefix + folder);
            Quit(0);
        }

        void Stamp(VRSLBenchmarkRun run)
        {
            run.environment = VRSLBenchmarkEnvironment.Capture();
            // Only when there is something to say. These are a fallback for two fields a
            // player cannot resolve for itself, not an override of what Capture found —
            // today it finds nothing for either, and writing an empty string over an
            // empty string is harmless, but the precedence is the part worth pinning.
            if (!string.IsNullOrEmpty(packageVersion)) run.environment.packageVersion = packageVersion;
            if (!string.IsNullOrEmpty(gitCommit))      run.environment.gitCommit      = gitCommit;

            if (noiseFloorMs > 0.0 && run.environment.graphicsDevice == noiseFloorGpu)
                run.noiseFloorMs = noiseFloorMs;
            else if (noiseFloorMs > 0.0)
                run.Note($"A noise floor of {noiseFloorMs:F3} ms was baked in for "
                       + $"'{noiseFloorGpu}', and this is '{run.environment.graphicsDevice}', "
                       + "so it has been left out. Establish one here with a null run.");
        }

        /// <summary>
        /// End the run.
        ///
        /// <c>Application.Quit</c> is a request rather than an exit — the player finishes
        /// the frame first — and in the editor it does nothing at all, which would leave
        /// play mode running after the sweep had finished.
        /// </summary>
        static void Quit(int code)
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit(code);
#endif
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
