using System;
using System.Collections;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace VRSL.URP.EditorScripts
{
    enum VRSLPerfJob
    {
        None = 0,
        Sweep,
        AnalyseScene,
    }

    /// <summary>
    /// Drives a benchmark through play mode and back, and writes what it found.
    ///
    /// A capture has to happen in play mode: the managers only enqueue their passes
    /// while the game is running, and the integrators only advance from
    /// <c>LateUpdate</c>. Entering play mode reloads the domain and takes every
    /// static field with it, so the request survives in <see cref="SessionState"/>
    /// and the result comes back as a file. The window polls for it.
    ///
    /// <b>The whole matrix runs inside one play-mode session.</b> The scene is built
    /// at its largest count before entering, and each configuration activates a
    /// subset of the truss rather than rebuilding — see
    /// <see cref="VRSLBenchmarkScene"/> for why that decides the shape of the sweep.
    /// </summary>
    [InitializeOnLoad]
    static class VRSLSweepRunner
    {
        const string JobKey       = "VRSL.Perf.Job";
        const string RefreshKey   = "VRSL.Perf.RefreshHz";
        const string ResultKey    = "VRSL.Perf.ResultPath";
        const string ErrorKey     = "VRSL.Perf.Error";
        const string RunningKey   = "VRSL.Perf.Running";

        static VRSLSweepRunner()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        // ── Requests ──────────────────────────────────────────────────────────

        public static VRSLPerfJob PendingJob => (VRSLPerfJob)SessionState.GetInt(JobKey, 0);

        /// <summary>Path of the last report written, or empty. Cleared by the window
        /// once it has shown it.</summary>
        public static string ResultPath => SessionState.GetString(ResultKey, "");

        /// <summary>Why the last run produced nothing, or empty.</summary>
        public static string LastError => SessionState.GetString(ErrorKey, "");

        public static void ClearResult()
        {
            SessionState.EraseString(ResultKey);
            SessionState.EraseString(ErrorKey);
        }

        public static void Start(VRSLPerfJob job, int refreshHz)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                SessionState.SetString(ErrorKey,
                    "Already in play mode. Leave play mode and run it again — a capture "
                  + "needs to start the scene itself so the warm-up and the frame index "
                  + "mean the same thing every run.");
                return;
            }

            ClearResult();
            SessionState.SetInt(JobKey, (int)job);
            SessionState.SetInt(RefreshKey, refreshHz);

            try
            {
                if (job == VRSLPerfJob.Sweep && VRSLBenchmarkScene.Build() == null)
                {
                    SessionState.SetInt(JobKey, 0);
                    SessionState.SetString(ErrorKey,
                        "Cancelled — the sweep needs its own scene and the open one was not saved.");
                    return;
                }
            }
            catch (Exception e)
            {
                SessionState.SetInt(JobKey, 0);
                SessionState.SetString(ErrorKey, $"Could not build the sweep scene: {e.Message}");
                return;
            }

            EditorApplication.EnterPlaymode();

            // EnterPlaymode does not always enter — compile errors refuse it outright, and
            // it says nothing when it does. The job would then sit in SessionState until
            // the author next pressed play themselves, and be picked up there: an analysis
            // measuring their scene and leaving play mode without anybody asking.
            EditorApplication.delayCall += () =>
            {
                if (EditorApplication.isPlayingOrWillChangePlaymode) return;
                if (PendingJob == VRSLPerfJob.None) return;
                SessionState.SetInt(JobKey, 0);
                SessionState.SetString(ErrorKey,
                    "The editor did not enter play mode, so nothing was measured. Compile "
                  + "errors stop it; the Console will have them.");
            };
        }

        static void OnPlayModeChanged(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.ExitingPlayMode)
            {
                // Unity does not unwind a coroutine when its host is destroyed, so
                // stopping play mode by hand never reaches the finally in the job and
                // never reaches Finish either. Nothing would then be written, and the
                // window would wait for a result that is not coming — showing "entering
                // play mode" until somebody restarts the editor.
                if (SessionState.GetBool(RunningKey, false))
                {
                    SessionState.SetBool(RunningKey, false);
                    SessionState.SetString(ErrorKey,
                        "Play mode ended before the run finished, so nothing was written. "
                      + "Leave the editor alone while a run is going, or start it again.");
                }
                return;
            }

            if (change != PlayModeStateChange.EnteredPlayMode) return;

            // A new play session has to pay for its own warm-up. The flag behind this is a
            // static, so with domain reload disabled it would otherwise survive from the
            // last session and every session after the first would skip the settle.
            VRSLBenchmark.ResetSessionWarmUp();

            var job = PendingJob;
            if (job == VRSLPerfJob.None) return;
            SessionState.SetInt(JobKey, 0);

            SessionState.SetBool(RunningKey, true);
            var host = new GameObject("VRSL Benchmark Host") { hideFlags = HideFlags.HideAndDontSave };
            host.AddComponent<Host>().Begin(job, SessionState.GetInt(RefreshKey, 90));
        }

        // ── The host ──────────────────────────────────────────────────────────

        /// <summary>Runs the capture inside play mode. An editor-assembly
        /// MonoBehaviour: a player has its own host, and what the two share is the
        /// capture loop rather than the thing driving it.</summary>
        class Host : MonoBehaviour
        {
            public void Begin(VRSLPerfJob job, int refreshHz)
            {
                StartCoroutine(job == VRSLPerfJob.Sweep ? RunSweep() : RunAnalysis(refreshHz));
            }

            /// <summary>
            /// Drive the standard sweep and report what it found.
            ///
            /// The capture loop is in the runtime assembly so a built player runs the
            /// same one; what is left here is play mode, the environment only the editor
            /// can stamp, and where the result goes.
            /// </summary>
            IEnumerator RunSweep()
            {
                var outcome = new VRSLSweepOutcome();
                var job     = VRSLSweepJob.Run(outcome, StampEnvironment);
                while (job.MoveNext()) yield return job.Current;

                // A result and a reason are the only two things the job reports, and
                // neither would leave the window polling for one that is not coming.
                Finish(outcome.run,
                       outcome.error ?? (outcome.run == null
                           ? "The sweep reported neither a result nor a reason."
                           : null));
            }

            /// <summary>
            /// Analyse whatever scene is open.
            ///
            /// One capture per quality level, and everything reported is derived from
            /// those three rather than from extra captures: the volumetric cost is
            /// what the level at <c>Off</c> gives back, and the rest of the package's
            /// cost is what survives it. Three captures rather than six, and the two
            /// figures always add up to the total by construction.
            /// </summary>
            IEnumerator RunAnalysis(int refreshHz)
            {
                var settings = new VRSLBenchmarkSettings();
                var run = NewRun("analyse-scene");
                run.Note($"Frame budget judged against {refreshHz} Hz.");

                var manager = VRSL_URPLightManager.Instance;
                if (manager == null)
                {
                    Finish(null, "There is no VRSL DMX light manager in this scene, so the "
                               + "package is not costing anything here.");
                    yield break;
                }

                if (Camera.main == null)
                    run.Note("No camera tagged MainCamera; measured against whatever cameras "
                           + "the scene renders.");

                // Held at a preset from here, so every exit restores it. This one runs on
                // the author's own scene, so a level left behind is a change to their
                // scene rather than to a throwaway one.
                bool completed = false;
                VRSLQualityPreset.Session quality = null;
                try
                {
                quality = VRSLQualityPreset.Session.Begin(manager);
                using var determinism = new VRSLBenchmark.DeterminismScope(settings);

                var warm = VRSLBenchmark.WarmUpSession(settings, null, run);
                while (warm.MoveNext()) yield return warm.Current;
                StampEnvironment(run);

                int done = 0;
                foreach (var level in VRSLQualityPreset.All)
                {
                    quality.Apply(level);
                    var config = new VRSLRowConfig
                    {
                        scene         = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name,
                        fixtureCount  = manager.FixtureCount,
                        cameraVariant = "SceneCamera",
                        quality       = level.ToString(),
                    };

                    Debug.Log($"[VRSL analyse] quality {level} "
                            + $"({++done} of {VRSLQualityPreset.All.Length})");

                    var capture = VRSLBenchmark.CaptureRow(settings, config, run);
                    while (capture.MoveNext()) yield return capture.Current;
                }

                completed = true;
                }
                finally
                {
                    quality?.Restore();
                    if (!completed)
                        Finish(null, "The analysis stopped before it finished. The Console "
                                   + "holds the exception; nothing was written.");
                }
                if (completed) Finish(run, null);
            }

            static VRSLBenchmarkRun NewRun(string label) => new() { label = label };

            /// <summary>
            /// Record the machine, <b>after</b> the session has settled rather than
            /// before it.
            ///
            /// A host applies its own graphics settings while it boots, so sampling at
            /// the start of a run races that: two sweeps of a scene the sweep builds
            /// itself came back with MSAA 2x and 1x, and the comparison refused them as
            /// different machines. The environment block is what a comparison refuses
            /// on, so one that cannot be reproduced is worse than none — and taken
            /// after the settle it describes what actually rendered, which is what it
            /// was always meant to say.
            /// </summary>
            static void StampEnvironment(VRSLBenchmarkRun run)
            {
                run.environment = VRSLBenchmarkEnvironment.Capture();
                VRSLPackageStamp.StampPackage(run.environment);

                // Carry this machine's measured floor into the run, so a comparison has
                // something honest to judge against. Zero when none has been
                // established, and the window says so rather than quietly falling back
                // to a figure that reads better than reality.
                run.noiseFloorMs = VRSLPerfFloor.Get(run.environment.graphicsDevice);
            }

            void Finish(VRSLBenchmarkRun run, string error)
            {
                // Cleared here so the ExitingPlayMode handler knows this run reported for
                // itself and does not overwrite its answer with an abandonment notice.
                SessionState.SetBool(RunningKey, false);
                if (error != null) SessionState.SetString(ErrorKey, error);
                if (run != null)
                {
                    if (string.IsNullOrEmpty(run.environment.unityVersion)) StampEnvironment(run);
                    try { SessionState.SetString(ResultKey, VRSLBenchmarkReport.Write(run)); }
                    catch (Exception e) { SessionState.SetString(ErrorKey, $"Could not write the report: {e.Message}"); }
                }
                EditorApplication.ExitPlaymode();
            }
        }
    }

    /// <summary>What only the editor knows about the package, stamped into a run's
    /// environment block. A built player carries these baked in instead, because it has
    /// no VERSION.txt beside it and no checkout to ask git about.</summary>
    static class VRSLPackageStamp
    {
        const string PackageRoot = "Packages/town.mr.vrsl-urp";

        /// <summary>Fill the two fields a running player cannot know: the package's
        /// version and the commit it was built from.</summary>
        public static void StampPackage(VRSLBenchmarkEnvironment environment)
        {
            try
            {
                string version = Path.Combine(Path.GetFullPath(PackageRoot), "Runtime", "VERSION.txt");
                if (File.Exists(version)) environment.packageVersion = File.ReadAllText(version).Trim();
            }
            catch (Exception) { /* A report without a version is still a report. */ }

            try
            {
                var info = new System.Diagnostics.ProcessStartInfo("git", "rev-parse --short HEAD")
                {
                    WorkingDirectory       = Path.GetFullPath(PackageRoot),
                    RedirectStandardOutput = true,
                    UseShellExecute        = false,
                    CreateNoWindow         = true,
                };
                using var git = System.Diagnostics.Process.Start(info);
                if (git != null)
                {
                    // Wait first, then read. ReadToEnd blocks until the child closes
                    // stdout, so reading first means the timeout below can never cap
                    // anything — and git can stall on a credential prompt or a stale
                    // index.lock. This runs on the main thread during a capture, so a
                    // report without a commit is a far better outcome than a frozen editor.
                    if (git.WaitForExit(2000))
                        environment.gitCommit = git.StandardOutput.ReadToEnd().Trim();
                    else
                        try { git.Kill(); } catch (Exception) { }
                }
            }
            catch (Exception) { /* No git, or the package is not a checkout. */ }
        }
    }

    /// <summary>
    /// Turns an <b>Analyse This Scene</b> run into something a world author can act
    /// on. Names the action, not the diagnostic: what it costs and what returns it.
    /// </summary>
    static class VRSLSceneAnalysis
    {
        public static string Summarise(VRSLBenchmarkRun run, int refreshHz)
        {
            if (run == null || run.rows.Count == 0) return "Nothing was measured.";

            VRSLBenchmarkRow off = null, standard = null, high = null;
            foreach (var row in run.rows)
            {
                if (row.config.quality == nameof(VRSLQualityPreset.Level.Off))      off = row;
                if (row.config.quality == nameof(VRSLQualityPreset.Level.Standard)) standard = row;
                if (row.config.quality == nameof(VRSLQualityPreset.Level.High))     high = row;
            }

            var current = standard ?? high ?? off;
            if (current == null) return "Nothing was measured.";

            double budgetMs = 1000.0 / Mathf.Max(1, refreshHz);
            double frameMs  = current.timings.HasGpu
                            ? current.timings.gpuEnabled.median
                            : current.timings.cpuEnabled.median;
            double totalMs  = current.timings.CostMs;

            var text = new System.Text.StringBuilder();

            if (!current.timings.Usable)
            {
                text.AppendLine("This run did not measure anything usable, so there is no cost to "
                              + "report.");
                text.AppendLine();
                text.AppendLine(current.timings.Unusable + ".");
                text.AppendLine();
                foreach (string note in run.notes)
                    if (note.Contains("capped") || note.Contains("NOT USABLE"))
                        text.AppendLine(note);
                text.AppendLine();
                text.AppendLine("The per-pass figures in report.md are still real, and they are "
                              + "the useful part of a run like this one.");
                return text.ToString();
            }

            text.AppendLine($"VRSL is costing {totalMs:F1} ms of a {frameMs:F1} ms frame "
                          + $"({100.0 * totalMs / Math.Max(0.001, frameMs):F0}%).");
            text.AppendLine();

            if (off != null && standard != null)
            {
                double volumetric = standard.timings.CostMs - off.timings.CostMs;
                double bound = Math.Max(off.timings.Noise, standard.timings.Noise);

                // A quality level that costs less than the one below it is not a
                // measurement, whatever the arithmetic says: turning volumetrics on
                // cannot make the frame faster. Splitting the total on a difference
                // that small produces a breakdown where every line is noise wearing a
                // number, which is worse than declining to split it.
                if (volumetric <= bound)
                {
                    text.AppendLine("  Too close to call. The difference between quality levels");
                    text.AppendLine($"  here ({volumetric:F3} ms) is smaller than this measurement");
                    text.AppendLine($"  can resolve (+-{bound:F3} ms), so splitting the cost between");
                    text.AppendLine("  beams and surface lighting would be guesswork. That usually");
                    text.AppendLine("  means VRSL is cheap in this scene, not that something is wrong.");
                }
                else
                {
                    double rest = Math.Max(0.0, standard.timings.CostMs - volumetric);
                    text.AppendLine($"  Beams in the air    {volumetric:F1} ms   "
                                  + "setting quality to Off gives this back");
                    text.AppendLine($"  Light on surfaces   {rest:F1} ms   "
                                  + "this is the part VRSL is for");
                }
                text.AppendLine();
            }

            // Every tile holding every fixture means the cull is saving nothing from
            // this viewpoint, which is a far more useful thing to know than the
            // milliseconds beside it.
            if (current.counters.tileCullEngaged && current.counters.fixtures > 1
             && current.counters.lightsPerTileAverage >= current.counters.fixtures - 0.5f)
                text.AppendLine($"From this camera angle every part of the screen is lit by all "
                              + $"{current.counters.fixtures} fixtures at once. VRSL normally skips "
                              + "fixtures that cannot reach a given part of the screen, and here "
                              + "there are none to skip, so this view is the worst case. Pointing "
                              + "the camera at less of the rig, or spreading the fixtures further "
                              + "apart, costs less.");

            text.AppendLine($"{current.counters.fixtures} fixtures. On average each part of the "
                          + $"screen is lit by {current.counters.lightsPerTileAverage:F1} of them at "
                          + "once, which is what the cost above mostly follows"
                          + (current.counters.tileCullEngaged
                             ? "."
                             : ". Tile culling is NOT running, so every pixel is paying for every "
                             + "fixture — assign lightCullShader on the manager."));

            if (current.counters.cappedTiles > 0)
                text.AppendLine($"{current.counters.cappedTiles} tile(s) are over the per-tile light "
                              + "cap, so some fixtures are being dropped there.");

            text.AppendLine();
            foreach (var level in VRSLQualityPreset.All)
            {
                var row = level == VRSLQualityPreset.Level.Off      ? off
                        : level == VRSLQualityPreset.Level.Standard ? standard
                                                                    : high;
                if (row == null) continue;
                double levelFrame = row.timings.HasGpu
                                  ? row.timings.gpuEnabled.median
                                  : row.timings.cpuEnabled.median;
                text.AppendLine(levelFrame <= budgetMs
                    ? $"At quality {level} this scene fits a {refreshHz} Hz budget with "
                    + $"{budgetMs - levelFrame:F1} ms spare."
                    : $"At quality {level} it does not fit {refreshHz} Hz ({levelFrame:F1} ms "
                    + $"against a {budgetMs:F1} ms budget).");
            }

            if (!current.timings.HasGpu)
            {
                text.AppendLine();
                text.AppendLine("These are CPU-side figures: this run had no GPU clock, so the "
                              + "numbers above understate what the package costs on the graphics "
                              + "card. Treat them as a lower bound.");
            }

            return text.ToString();
        }
    }
}
