using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace VRSL.URP.EditorScripts
{
    /// <summary>
    /// The headless half of the harness: compare two stored runs and gate on the
    /// verdict.
    ///
    /// It compares rather than captures, and that is deliberate. Batch mode has no
    /// GPU clock — <c>FrameTimingManager</c> returns a real CPU frame time there and
    /// a GPU time of exactly zero — so a headless capture produces CPU-basis figures
    /// that are not the ones a milestone quotes. What headless can do perfectly well
    /// is adjudicate: the verdict comes from <see cref="VRSLBaseline.Compare"/>,
    /// which is the same code the editor window calls, so the two cannot disagree.
    /// That is A-M0-6 satisfied by construction rather than by testing.
    /// </summary>
    static class VRSLBenchCommandLine
    {
        /// <summary>
        /// Compare a baseline against a candidate and exit non-zero if any row
        /// regressed.
        ///
        /// <c>-candidate &lt;path&gt;</c> is the run under test.
        /// <c>-baseline &lt;path&gt;</c> is what to judge it against; omitted, the committed
        /// reference is used, which is what lets this gate a branch unattended.
        /// <c>-force</c> compares across mismatched hardware and says so on every row.
        /// <c>-out &lt;path&gt;</c> writes the markdown comparison.
        /// </summary>
        public static void CompareFromCommandLine()
        {
            int exit = 1;
            try
            {
                string basePath      = Argument("-baseline");
                string candidatePath = Argument("-candidate");
                string outPath       = Argument("-out");
                bool   force         = HasFlag("-force");

                if (candidatePath == null)
                {
                    Debug.LogError("[VRSL bench] FAIL — needs -candidate <run.json>.");
                    return;
                }

                // No hardware check is needed before falling back: Compare refuses on an
                // environment mismatch, so on a machine that did not produce the reference
                // the answer is a refusal naming the difference, not a fabricated delta.
                bool defaulted = basePath == null;
                if (defaulted) basePath = VRSLBaseline.ReferencePath;

                if (basePath == null)
                {
                    string home = VRSLBaseline.ReferenceHome;
                    Debug.LogError("[VRSL bench] FAIL — no -baseline given and no committed "
                                 + "reference to fall back on. "
                                 + (string.IsNullOrEmpty(home)
                                        ? "VRSL_PERF_HOME is not set."
                                        : $"VRSL_PERF_HOME is {home}, which holds no baseline.json.")
                                 + " Set it to the programme folder, or pass -baseline <run.json>.");
                    return;
                }

                if (defaulted)
                    Debug.Log($"[VRSL bench] no -baseline given, using the reference: {basePath}");

                var baseline  = Read(basePath);
                var candidate = Read(candidatePath);
                if (baseline == null || candidate == null) return;

                var comparison = VRSLBaseline.Compare(baseline, candidate, force);

                if (comparison.Refused)
                {
                    // Refused, not failed, and it gets its own exit code so a hardware
                    // mismatch cannot be read as a regression. Exiting 1 here would train
                    // whoever reads the gate to pass -force by reflex, which is the
                    // opposite of what a refusal is for.
                    Debug.LogError("[VRSL bench] REFUSED — these runs are from different "
                                 + $"machines or configurations: {comparison.environmentMismatch}. "
                                 + "Pass -force to compare anyway, knowing the deltas are "
                                 + "partly hardware.");
                    // Emitted here too, so that every invocation that reaches a comparison
                    // ends with a verdict line whatever the outcome. It reads zero rows,
                    // which is what a refusal did: the line above says why. Without it the
                    // script's "no verdict line, so nothing was compared" check fires
                    // first and reports exit 1, turning a refusal into a regression — and
                    // that check sits ahead of the one that looks for REFUSED.
                    Debug.Log("[VRSL bench] " + comparison.VerdictLine);
                    exit = 2;
                    return;
                }

                if (!string.IsNullOrEmpty(outPath))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
                    File.WriteAllText(outPath, VRSLBaseline.ToMarkdown(comparison));
                }

                foreach (var row in comparison.rows)
                {
                    Debug.Log("[VRSL bench] " + row.Describe());
                    if (row.counterChanges.Count > 0)
                        Debug.Log("[VRSL bench]     counters: " + string.Join(", ", row.counterChanges));
                }

                // The line the shell script requires to be present. A runner that exits
                // successfully having compared nothing is worse than no runner, so the
                // script fails when this is absent rather than trusting the exit code.
                Debug.Log("[VRSL bench] " + comparison.VerdictLine);

                if (comparison.rows.Count == 0)
                {
                    Debug.LogError("[VRSL bench] FAIL — nothing was compared. The two runs share "
                                 + "no configuration, so one of them is not the sweep you think.");
                    return;
                }

                exit = comparison.AnyRegressed ? 1 : 0;
                Debug.Log(exit == 0
                    ? "[VRSL bench] PASS — no row regressed."
                    : $"[VRSL bench] FAIL — {comparison.Count(VRSLVerdict.Regressed)} row(s) regressed.");
            }
            catch (Exception e)
            {
                Debug.LogError($"[VRSL bench] FAIL — {e.Message}");
            }
            finally
            {
                EditorApplication.Exit(exit);
            }
        }

        static VRSLBenchmarkRun Read(string path)
        {
            if (!File.Exists(path))
            {
                Debug.LogError($"[VRSL bench] FAIL — no such run: {path}");
                return null;
            }
            var run = VRSLBenchmarkRun.FromJson(File.ReadAllText(path));
            if (run == null || run.rows == null || run.rows.Count == 0)
            {
                Debug.LogError($"[VRSL bench] FAIL — {path} holds no rows. An empty document "
                             + "compares clean against anything, which is the failure this "
                             + "check exists to refuse.");
                return null;
            }
            return run;
        }

        static string Argument(string name)
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == name)
                    // A following flag is a missing value, not a value. Returning it gives
                    // "no such run: -force", which sends the reader looking for a file.
                    return args[i + 1].StartsWith("-") ? null : args[i + 1];
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
