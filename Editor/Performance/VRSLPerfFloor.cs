using UnityEditor;

namespace VRSL.URP.EditorScripts
{
    /// <summary>
    /// This machine's measured noise floor, remembered between sessions.
    ///
    /// A verdict needs something to be significant against, and the only honest
    /// source is a null run: capture, change nothing, capture again, and see how far
    /// apart the two come out. Everything else is theory. The standard error the rows
    /// carry assumes independent samples and consecutive frame times are correlated,
    /// so it reads two to three times better than reality — measured 2026-08-24, a
    /// null run of 30 configurations disagreed by up to 0.30 ms against stated
    /// precisions of 0.08 to 0.26, and five rows were reported as improved or
    /// regressed when nothing had changed.
    ///
    /// Kept per GPU, because a floor is a property of the machine and carrying one
    /// across hardware would be worse than having none. Kept in EditorPrefs rather
    /// than in the package, which ships to users and has no business holding one
    /// machine's timings.
    /// </summary>
    static class VRSLPerfFloor
    {
        const string Prefix = "VRSL.Perf.Floor.";

        static string Key(string gpu) => Prefix + (string.IsNullOrEmpty(gpu) ? "unknown" : gpu);

        /// <summary>The stored floor in milliseconds, or 0 when none has been
        /// established for this GPU.</summary>
        public static double Get(string gpu) => EditorPrefs.GetFloat(Key(gpu), 0f);

        /// <summary>
        /// Raise the floor to cover a newly observed disagreement, never lower it.
        ///
        /// A floor has to cover the worst two supposedly identical runs have been seen
        /// to differ by. Overwriting with each new measurement ratchets it down to
        /// whatever the quietest pair happened to give and the false verdicts come
        /// back: measured 2026-08-24, three null runs on one machine gave 0.314, 0.301
        /// and 0.206 ms, and adopting the last of those would have thrown away both
        /// larger observations.
        ///
        /// Returns the floor now in force, which is not always what was passed in.
        /// </summary>
        public static double Raise(string gpu, double milliseconds)
        {
            double floor = System.Math.Max(Get(gpu), milliseconds);
            EditorPrefs.SetFloat(Key(gpu), (float)floor);
            return floor;
        }

        /// <summary>Forget this machine's floor, so the next measurement establishes it
        /// from scratch. For a machine that has genuinely changed — different drivers,
        /// something noisy uninstalled — rather than for a run that came out flattering.</summary>
        public static void Clear(string gpu) => EditorPrefs.DeleteKey(Key(gpu));

        /// <summary>
        /// The largest disagreement across a comparison, which is what a null run's
        /// floor is: the most two supposedly identical runs differed by.
        ///
        /// The maximum rather than the median, deliberately. A floor set at the median
        /// waves through half the disagreements a null run actually produced, and
        /// those are the ones that get quoted as improvements.
        /// </summary>
        public static double LargestDisagreement(VRSLComparison comparison)
        {
            double largest = 0.0;
            foreach (var row in comparison.rows)
            {
                if (row.verdict == VRSLVerdict.Missing) continue;
                double delta = row.packageCostDeltaMs;
                if (delta < 0) delta = -delta;
                if (delta > largest) largest = delta;
            }
            return largest;
        }
    }
}
