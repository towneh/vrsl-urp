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
    /// Kept per GPU and per context, because a floor is a property of the machine and
    /// of how it was measured — carrying one across hardware, or across the gap between
    /// an editor capture and a built player, would be worse than having none. Kept in
    /// EditorPrefs rather than in the package, which ships to users and has no business
    /// holding one machine's timings.
    /// </summary>
    static class VRSLPerfFloor
    {
        const string Prefix = "VRSL.Perf.Floor.";

        /// <summary>
        /// Filed per GPU <b>and</b> per context, because an editor floor and a player
        /// floor are two different measurements of two different things and neither
        /// describes the other.
        ///
        /// The editor's key is the bare GPU name, which is the shape already in
        /// EditorPrefs: appending the context to both would silently discard a floor
        /// somebody measured properly, and a floor is only established by a null run
        /// nobody wants to repeat.
        /// </summary>
        static string Key(string gpu, string context)
        {
            string device = string.IsNullOrEmpty(gpu) ? "unknown" : gpu;
            return string.IsNullOrEmpty(context) || context == "Editor"
                 ? Prefix + device
                 : Prefix + context + "." + device;
        }

        /// <summary>The stored floor in milliseconds, or 0 when none has been
        /// established for this GPU in this context.</summary>
        public static double Get(string gpu, string context) =>
            EditorPrefs.GetFloat(Key(gpu, context), 0f);

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
        public static double Raise(string gpu, string context, double milliseconds)
        {
            double floor = System.Math.Max(Get(gpu, context), milliseconds);
            EditorPrefs.SetFloat(Key(gpu, context), (float)floor);
            return floor;
        }

        /// <summary>Forget this machine's floor, so the next measurement establishes it
        /// from scratch. For a machine that has genuinely changed — different drivers,
        /// something noisy uninstalled — rather than for a run that came out flattering.</summary>
        public static void Clear(string gpu, string context) =>
            EditorPrefs.DeleteKey(Key(gpu, context));

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
