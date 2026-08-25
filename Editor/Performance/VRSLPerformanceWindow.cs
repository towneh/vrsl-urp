using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace VRSL.URP.EditorScripts
{
    /// <summary>
    /// Three buttons and a result. The front end for the whole harness.
    ///
    /// One field of configuration in the entire window, the refresh rate the budget
    /// line is judged against, because a benchmark that has to be set up before it
    /// answers anything is a benchmark a world author will not run.
    /// </summary>
    public class VRSLPerformanceWindow : EditorWindow
    {
        const string RefreshPref = "VRSL.Perf.RefreshHz";

        const string SummaryKey = "VRSL.Perf.Summary";
        const string FolderKey  = "VRSL.Perf.Folder";
        const string AdvancedPref = "VRSL.Perf.ShowAdvanced";

        int    _refreshHz = 90;
        Vector2 _scroll;

        // The last comparison, kept only so the noise-floor button can adopt it. Not
        // serialised: losing it to a domain reload costs one button press.
        VRSLComparison _lastComparison;
        string         _lastComparisonGpu;
        /// <summary>Whether the compared runs were captured on this machine at all.</summary>
        bool           _lastComparisonLocal;

        // Held in SessionState rather than in serialised fields on the window.
        //
        // A plain field does not survive the domain reload that entering play mode
        // causes, so the window went blank mid-run. But a [SerializeField] is worse:
        // Unity restores the window from a serialised snapshot on its own schedule, so
        // clicking the window replaced the run that had just finished with the one
        // before it — two sources of truth, and the stale one winning on interaction.
        // SessionState is the single copy, it outlives the reload, and nothing else
        // writes it.
        static string Summary
        {
            get => SessionState.GetString(SummaryKey, "");
            set => SessionState.SetString(SummaryKey, value ?? "");
        }

        static string Folder
        {
            get => SessionState.GetString(FolderKey, "");
            set => SessionState.SetString(FolderKey, value ?? "");
        }

        [MenuItem("VRSL/URP/Performance/Performance Window", false, 400)]
        public static void ShowWindow() =>
            GetWindow<VRSLPerformanceWindow>("VRSL Performance").minSize = new Vector2(460, 420);

        [MenuItem("VRSL/URP/Performance/Analyse This Scene", false, 401)]
        public static void AnalyseFromMenu()
        {
            ShowWindow();
            GetWindow<VRSLPerformanceWindow>().Run(VRSLPerfJob.AnalyseScene);
        }

        [MenuItem("VRSL/URP/Performance/Run Standard Sweep", false, 402)]
        public static void SweepFromMenu()
        {
            ShowWindow();
            GetWindow<VRSLPerformanceWindow>().Run(VRSLPerfJob.Sweep);
        }

        void OnEnable()
        {
            _refreshHz = EditorPrefs.GetInt(RefreshPref, 90);
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        void OnDisable() => EditorApplication.playModeStateChanged -= OnPlayModeChanged;

        /// <summary>Nudge the window when a run finishes, so it updates without
        /// waiting for the mouse. The pickup itself does not depend on this arriving.</summary>
        void OnPlayModeChanged(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.EnteredEditMode) Repaint();
        }

        /// <summary>
        /// A run hands its answer back through a file, because entering play mode
        /// reloads the domain and takes the static state with it.
        ///
        /// Called from <c>OnGUI</c> rather than from an <c>EditorApplication.update</c>
        /// subscription. A subscription made in <c>OnEnable</c> has to survive two
        /// domain reloads to see the answer, and when it did not the run completed,
        /// wrote its report, and the window sat there showing nothing. Drawing is the
        /// one thing a visible window is guaranteed to do.
        /// </summary>
        void TryPickUpResult()
        {
            // Layout only. Setting Summary adds whole sections to the result panel, and
            // IMGUI counts the controls a Layout pass laid out against the ones every
            // later event draws — picking the result up mid-repaint changes the count
            // and the window throws instead of drawing.
            if (Event.current != null && Event.current.type != EventType.Layout) return;

            string error = VRSLSweepRunner.LastError;
            string path  = VRSLSweepRunner.ResultPath;
            if (string.IsNullOrEmpty(error) && string.IsNullOrEmpty(path)) return;
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;

            if (!string.IsNullOrEmpty(error))
            {
                Summary = error;
                Folder = "";
            }
            else
            {
                Folder = path;
                try
                {
                    var run = VRSLBenchmarkRun.FromJson(File.ReadAllText(Path.Combine(path, "run.json")));
                    Summary = run == null || run.rows == null || run.rows.Count == 0
                             ? "The run finished but its report holds no rows, so nothing was "
                             + "measured. The Console will say why."
                             : run.label == "analyse-scene"
                             ? VRSLSceneAnalysis.Summarise(run, _refreshHz)
                             : SweepSummary(run);
                }
                catch (Exception e)
                {
                    Summary = $"The run finished but its report could not be read: {e.Message}";
                }
            }

            VRSLSweepRunner.ClearResult();
        }

        static string SweepSummary(VRSLBenchmarkRun run)
        {
            var text = new System.Text.StringBuilder();
            text.AppendLine($"{run.rows.Count} configuration(s) measured on {run.environment.graphicsDevice}.");
            text.AppendLine(run.environment.Summary);
            text.AppendLine();

            VRSLBenchmarkRow worst = null;
            foreach (var row in run.rows)
                if (worst == null || row.timings.CostMs > worst.timings.CostMs) worst = row;
            if (worst != null)
                text.AppendLine($"Most expensive: {worst.config} at {worst.timings.CostMs:F2} ms "
                              + $"({worst.timings.CostBasis}), {worst.counters.lightsPerTileAverage:F1} "
                              + "lights per tile.");

            text.AppendLine();
            text.AppendLine("The full table is in report.md beside this.");
            return text.ToString();
        }

        void Run(VRSLPerfJob job)
        {
            EditorPrefs.SetInt(RefreshPref, _refreshHz);
            Summary = job == VRSLPerfJob.Sweep
                     ? "Building the sweep scene and entering play mode. The whole matrix runs "
                     + "in one session; leave the editor alone while it does."
                     : "Entering play mode to measure this scene at each quality level.";
            Folder = "";
            Repaint();
            VRSLSweepRunner.Start(job, _refreshHz);
        }

        void OnGUI()
        {
            TryPickUpResult();
            VRSL_EditorHeader.Draw();

            // One scroll view around everything, rather than one around the result.
            // A comparison prints a line per configuration and the sweep has thirty of
            // them, so an inner scroll view with no bounded height simply grew the
            // window instead of scrolling — far enough to need two monitors stacked.
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            DrawForAuthors();
            DrawForDevelopers();
            DrawResult();

            EditorGUILayout.EndScrollView();
        }

        /// <summary>
        /// The half a world author wants, and the only half most people should see.
        ///
        /// <b>Plain language throughout.</b> The reader here is deciding what to change
        /// about their scene, and "the whole matrix", "camera variants" and "cost basis"
        /// tell them nothing about that. The analysis half below is deliberately not
        /// written this way — see <see cref="DrawForDevelopers"/>.
        /// </summary>
        void DrawForAuthors()
        {
            EditorGUILayout.LabelField("What is VRSL costing this scene?", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "Measures your scene as it is, with the lights on and again with them off, "
              + "and tells you the difference in milliseconds. It also tries each quality "
              + "level so you can see what turning things down would give back. Nothing to "
              + "set up first.",
                EditorStyles.wordWrappedLabel);

            EditorGUILayout.Space(4);

            using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode))
                if (GUILayout.Button("Analyse This Scene", GUILayout.Height(36)))
                    Run(VRSLPerfJob.AnalyseScene);

            // Clamped at the field. The budget arithmetic downstream already floors the
            // divisor, so a zero here does not divide by nothing — it reaches the report
            // as a verdict about fitting a 0 Hz frame, which is a sentence nobody can act
            // on. The value is remembered between sessions, so it would keep saying it.
            _refreshHz = Mathf.Clamp(EditorGUILayout.IntField(
                new GUIContent("Frame rate to aim for",
                    "The summary says whether your scene fits in a frame at this rate. "
                  + "90 is typical for desktop VR, 60 for a flatscreen game."),
                _refreshHz), 1, 1000);

            if (EditorApplication.isPlayingOrWillChangePlaymode)
                EditorGUILayout.HelpBox("Measuring — it will leave play mode by itself when "
                                      + "it has finished.", MessageType.Info);
        }

        /// <summary>
        /// The half that only matters to somebody landing a change to the package.
        ///
        /// Folded away by default, because a world author opening this window should not
        /// have to work out which of three equally-sized buttons is the one that will not
        /// replace their scene.
        ///
        /// <b>The language here stays precise, and that is not an oversight.</b> A reader
        /// in this section is deciding whether to believe a number, and softening "no
        /// floor measured, so verdicts fall back to each row's standard error" into
        /// something friendlier is exactly how a false improvement gets quoted. One null
        /// run reported four improvements and a regression when nothing had changed;
        /// vagueness here is the failure, not the cure.
        /// </summary>
        void DrawForDevelopers()
        {
            EditorGUILayout.Space(8);
            bool open = EditorPrefs.GetBool(AdvancedPref, false);
            bool nowOpen = EditorGUILayout.Foldout(open,
                "Regression testing — for changes to VRSL itself", true, EditorStyles.foldoutHeader);
            if (nowOpen != open) EditorPrefs.SetBool(AdvancedPref, nowOpen);
            if (!nowOpen) return;

            EditorGUI.indentLevel++;
            using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode))
            {
                EditorGUILayout.Space(4);
                if (GUILayout.Button("Run Standard Sweep", GUILayout.Height(28)))
                    Run(VRSLPerfJob.Sweep);
                EditorGUILayout.HelpBox(
                    "Replaces the open scene with one of its own — it will offer to save "
                  + "yours first. Measures 10 to 200 fixtures, two camera positions and "
                  + "every quality level, and writes a report you can compare against later.",
                    MessageType.None);

                EditorGUILayout.Space(4);
                if (GUILayout.Button("Compare With Baseline", GUILayout.Height(28)))
                    CompareWithBaseline();
                EditorGUILayout.HelpBox(
                    "Pick two run.json files from previous sweeps. Each configuration gets a "
                  + "verdict against the noise floor below, and any counter that moved is "
                  + "listed beside it. Refuses to compare across different hardware.",
                    MessageType.None);
            }

            DrawNoiseFloor();
            EditorGUI.indentLevel--;
        }

        void DrawResult()
        {
            if (string.IsNullOrEmpty(Summary)) return;

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Result", EditorStyles.boldLabel);

            // A selectable label has no intrinsic height, so inside a scroll view it
            // needs telling. Measured against the current width so wrapped lines are
            // counted rather than guessed at.
            var style   = EditorStyles.wordWrappedLabel;
            float width = Mathf.Max(120f, EditorGUIUtility.currentViewWidth - 40f);
            EditorGUILayout.SelectableLabel(Summary, style,
                GUILayout.Height(style.CalcHeight(new GUIContent(Summary), width)));

            using (new EditorGUILayout.HorizontalScope())
            {
                if (!string.IsNullOrEmpty(Folder))
                {
                    if (GUILayout.Button("Open report folder")) EditorUtility.RevealInFinder(Folder);
                    if (GUILayout.Button("Copy path")) EditorGUIUtility.systemCopyBuffer = Folder;
                }
                if (GUILayout.Button("Clear")) { Summary = ""; Folder = ""; }
            }
        }

        /// <summary>
        /// Adopt a comparison's largest disagreement as this machine's noise floor.
        ///
        /// Only a person can say whether two runs were meant to be identical, which is
        /// why this is a button rather than something the tool infers. What it adopts
        /// is the largest disagreement, not the median: a floor set at the median waves
        /// through half of what a null run actually produced, and those are exactly the
        /// differences that later get quoted as improvements.
        /// </summary>
        void DrawNoiseFloor()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Noise floor", EditorStyles.boldLabel);

            string gpu = string.IsNullOrEmpty(_lastComparisonGpu)
                       ? SystemInfo.graphicsDeviceName
                       : _lastComparisonGpu;
            double stored = VRSLPerfFloor.Get(gpu);

            EditorGUILayout.LabelField(gpu, EditorStyles.miniLabel);

            // Editable as well as adopted. A floor is measured, not invented, but the
            // measurement lives in two sweeps that get deleted like any other output —
            // and losing a figure you took properly, because the files it came from are
            // gone, is a worse outcome than being able to type it back in.
            //
            // <b>Delayed, and it has to be.</b> A plain field reports its value on every
            // repaint, so the first keystroke wrote a half-typed number straight back to
            // EditorPrefs and the control was rebound to it — the field could never get
            // past one character. A delayed field reports only on Enter or focus loss,
            // which is what lets someone type a number with a decimal point in it.
            double typed = EditorGUILayout.DelayedDoubleField(
                new GUIContent(stored > 0.0 ? "Floor (ms)" : "Floor (ms) — not measured",
                    "The largest disagreement between two runs with nothing changed "
                  + "between them. Adopt it from a comparison, or type one you measured "
                  + "earlier, then press Enter. A difference smaller than this is not a "
                  + "result."),
                stored);
            if (System.Math.Abs(typed - stored) > 1e-6 && typed >= 0.0)
            {
                VRSLPerfFloor.Clear(gpu);
                if (typed > 0.0) VRSLPerfFloor.Raise(gpu, typed);
                stored = typed;
            }

            if (stored <= 0.0)
                EditorGUILayout.HelpBox(
                    "No floor measured on this machine, so verdicts fall back to each row's "
                  + "standard error — which reads two to three times better than reality and "
                  + "will report noise as improvement.", MessageType.Warning);

            // A forced comparison crossed mismatched hardware, so its deltas are partly
            // the two machines differing. Adopting them files that difference as this
            // machine's noise, and the floor only ever rises — so an inflated value
            // survives until somebody presses Reset, masking real regressions the whole
            // time.
            bool adoptable = _lastComparison != null
                          && !_lastComparison.forced
                          && _lastComparisonLocal;
            using (new EditorGUI.DisabledScope(!adoptable))
            {
                double largest = adoptable
                               ? VRSLPerfFloor.LargestDisagreement(_lastComparison)
                               : 0.0;
                string label = _lastComparison == null
                             ? "Adopt from a comparison (compare two runs first)"
                             : _lastComparison.forced
                             ? "Cannot adopt from a forced comparison — its deltas are partly hardware"
                             : !_lastComparisonLocal
                             ? "Cannot adopt — those runs were measured on another machine"
                             : largest > stored
                               ? $"Those two runs were identical — raise the floor to {largest:F3} ms"
                               : $"Those two runs were identical — disagreed by {largest:F3} ms, "
                               + $"already covered by {stored:F3} ms";

                if (GUILayout.Button(label))
                {
                    double now = VRSLPerfFloor.Raise(gpu, largest);
                    Summary = largest > stored
                        ? $"Noise floor for {gpu} raised to {now:F3} ms, from the largest "
                        + "disagreement across that comparison. Sweeps from now on carry it, and "
                        + "a difference smaller than it is not a result."
                        : $"Those two runs disagreed by {largest:F3} ms, which the existing "
                        + $"{now:F3} ms floor already covers, so it is unchanged. A floor is "
                        + "raised by the worst disagreement seen, never lowered by a quiet run — "
                        + "otherwise it drifts down to whatever the calmest pair gave and the "
                        + "false verdicts come back. Use Reset if this machine has genuinely "
                        + "changed.";
                    // Out, and back in on a fresh Layout pass. Setting the summary adds
                    // sections the layout pass for this event never counted, and IMGUI
                    // matches the control sequence across passes rather than tolerating it.
                    GUIUtility.ExitGUI();
                }
            }

            using (new EditorGUI.DisabledScope(stored <= 0.0))
                if (GUILayout.Button("Reset this machine's floor"))
                {
                    VRSLPerfFloor.Clear(gpu);
                    Summary = $"Noise floor for {gpu} cleared. Compare two identical runs to "
                            + "establish it again.";
                    GUIUtility.ExitGUI();
                }

            EditorGUILayout.HelpBox(
                "Press this only after comparing two runs with nothing changed between them. "
              + "It is what gives a verdict something to be significant against; without it "
              + "the rows fall back to their own standard error, which reads two to three "
              + "times better than this machine actually reproduces.",
                MessageType.None);
        }

        /// <summary>Why a loaded document is not a run, or null.</summary>
        static string Unusable(VRSLBenchmarkRun run, string path)
        {
            if (run == null)
                return $"That file is not a benchmark run: {path}";
            if (run.rows == null || run.rows.Count == 0)
                return $"That run holds no rows, so it compares clean against anything: {path}";
            return null;
        }

        void CompareWithBaseline()
        {
            string basePath = EditorUtility.OpenFilePanel("Baseline run.json", "", "json");
            if (string.IsNullOrEmpty(basePath)) return;
            string candidatePath = EditorUtility.OpenFilePanel("Run to compare", Path.GetDirectoryName(basePath), "json");
            if (string.IsNullOrEmpty(candidatePath)) return;

            // Forgotten here, once two files are actually chosen. Every path below this
            // either replaces these or fails, and a failed or refused compare that left
            // them standing would leave the adopt button offering a floor from a
            // comparison the summary no longer describes. Cancelling a picker returns
            // above this line, so backing out changes nothing.
            _lastComparison      = null;
            _lastComparisonGpu   = null;
            _lastComparisonLocal = false;

            try
            {
                var baseline  = VRSLBenchmarkRun.FromJson(File.ReadAllText(basePath));
                var candidate = VRSLBenchmarkRun.FromJson(File.ReadAllText(candidatePath));

                // Named before anything dereferences them. Without this a file that is
                // not a run throws on the first field read, and the catch below reports
                // "object reference not set" — which says nothing about which of the two
                // files was wrong.
                string bad = Unusable(baseline, basePath) ?? Unusable(candidate, candidatePath);
                if (bad != null) { Summary = bad; Repaint(); return; }

                // A floor is a property of the machine, not of a file. Runs written
                // before one was established carry zero, and refusing to apply what this
                // machine has since been measured at would mean re-capturing perfectly
                // good results to benefit from it.
                string comparingGpu = candidate.environment.graphicsDevice;
                double machineFloor = VRSLPerfFloor.Get(comparingGpu);
                bool   borrowed     = machineFloor > 0.0
                                   && baseline.noiseFloorMs <= 0.0
                                   && candidate.noiseFloorMs <= 0.0;
                if (borrowed) candidate.noiseFloorMs = machineFloor;

                var comparison = VRSLBaseline.Compare(baseline, candidate);

                if (comparison.environmentMismatch != null)
                {
                    bool force = EditorUtility.DisplayDialog("Different machines",
                        $"These two runs do not match: {comparison.environmentMismatch}.\n\n"
                      + "GPU timings are not comparable across hardware, and a regression report "
                      + "that is really a hardware difference costs an afternoon. Compare anyway?",
                        "Compare anyway", "Cancel");
                    if (!force) { Summary = $"Refused: {comparison.environmentMismatch}"; Repaint(); return; }
                    comparison = VRSLBaseline.Compare(baseline, candidate, force: true);
                }

                string folder = VRSLPerfReport.Folder("comparison");
                File.WriteAllText(Path.Combine(folder, "comparison.md"), VRSLBaseline.ToMarkdown(comparison));
                Folder = folder;

                // Remembered so the "these two were identical" button below has
                // something to adopt without re-running anything.
                _lastComparison = comparison;
                // Always filed under the local device, because that is the key a later
                // sweep reads back. Naming the candidate's device would report a floor as
                // adopted while every sweep on this machine still carried zero.
                //
                // Whether the deltas describe this machine is a separate question, and
                // `forced` does not answer it: two runs copied from CI agree with each
                // other perfectly and neither happened here.
                _lastComparisonGpu   = SystemInfo.graphicsDeviceName;
                _lastComparisonLocal = candidate.environment.graphicsDevice
                                     == SystemInfo.graphicsDeviceName;

                var text = new System.Text.StringBuilder();
                text.AppendLine(comparison.VerdictLine);

                if (borrowed)
                {
                    text.AppendLine($"Neither run carried a noise floor, so this machine's "
                                  + $"measured {machineFloor:F3} ms was used.");
                    text.AppendLine();
                }
                else if (baseline.noiseFloorMs <= 0.0 && candidate.noiseFloorMs <= 0.0)
                {
                    text.AppendLine("NO NOISE FLOOR HAS BEEN MEASURED on this machine, so these "
                        + "verdicts use each row's own standard error. That figure assumes "
                        + "consecutive frames are independent and they are not, so it reads "
                        + "better than reality and small differences will be reported as real. "
                        + "Compare two runs with nothing changed between them, then use the "
                        + "button below.");
                    text.AppendLine();
                }
                if (comparison.forced)
                    text.AppendLine("FORCED across mismatched hardware — none of these deltas mean "
                                  + "what they appear to.");
                text.AppendLine();
                foreach (var row in comparison.rows)
                {
                    text.AppendLine(row.Describe());
                    if (row.counterChanges.Count > 0)
                        text.AppendLine($"    counters: {string.Join(", ", row.counterChanges)}");
                }
                Summary = text.ToString();
            }
            catch (Exception e)
            {
                Summary = $"Could not compare those two runs: {e.Message}";
            }
            Repaint();
        }
    }
}
