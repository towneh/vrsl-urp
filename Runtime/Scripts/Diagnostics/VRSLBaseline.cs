using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace VRSL.URP
{
    public enum VRSLVerdict
    {
        Unchanged,
        Improved,
        Regressed,
        /// <summary>The configuration exists on one side of the comparison only, so
        /// there is nothing to compare. Reported rather than skipped: a row that
        /// quietly vanished is the kind of thing a sweep should shout about.</summary>
        Missing,
    }

    /// <summary>One configuration, before and after.</summary>
    public class VRSLRowComparison
    {
        public VRSLRowConfig config;
        public VRSLVerdict   verdict;

        /// <summary>Change in the package's own cost. Positive is worse. This is
        /// what the verdict is decided on.</summary>
        public double packageCostDeltaMs;
        public double packageCostDeltaPercent;
        /// <summary>Change in whole-frame time, reported beside the headline because
        /// the two can move apart — a package that got cheaper inside a frame that
        /// got slower is a real and confusing result.</summary>
        public double frameDeltaMs;
        /// <summary>"GPU" or "CPU". Which clock the two figures above came off, and
        /// a comparison never mixes the two.</summary>
        public string costBasis = "GPU";

        /// <summary>What the delta had to beat. Stated on every row so a reader can
        /// tell a real move from jitter without going and finding the null run.</summary>
        public double noiseFloorMs;

        public readonly List<string> counterChanges = new();

        public string Describe()
        {
            if (verdict == VRSLVerdict.Missing) return $"{config} — MISSING";
            string sign = packageCostDeltaMs >= 0 ? "+" : "";
            return $"{config} — {verdict}: {sign}{packageCostDeltaMs:F3} ms {costBasis} "
                 + $"({sign}{packageCostDeltaPercent:F1}%), noise floor {noiseFloorMs:F3} ms";
        }
    }

    public class VRSLComparison
    {
        public string label = "";
        /// <summary>Non-null when the two runs were captured somewhere different
        /// enough that the numbers are not comparable.</summary>
        public string environmentMismatch;
        /// <summary>Whether the comparison went ahead despite that mismatch.</summary>
        public bool   forced;

        /// <summary>
        /// True when nothing was compared at all.
        ///
        /// Distinct from a comparison that ran and found no regression, and the
        /// distinction is the whole point: a refusal leaves no rows, so
        /// <see cref="AnyRegressed"/> is false and a consumer gating on that alone
        /// passes a run that compared nothing. Read this first.
        /// </summary>
        public bool Refused => environmentMismatch != null && !forced;

        public readonly List<VRSLRowComparison> rows = new();

        public int Count(VRSLVerdict v)
        {
            int n = 0;
            foreach (var r in rows) if (r.verdict == v) n++;
            return n;
        }

        public bool AnyRegressed => Count(VRSLVerdict.Regressed) > 0;

        /// <summary>The line a headless run is judged on. A runner that exits
        /// successfully having measured nothing is worse than no runner, so this is
        /// what <c>bench.sh</c> requires to be present in the log.</summary>
        public string VerdictLine =>
            $"VRSL BENCH VERDICT: {rows.Count} row(s), "
          + $"{Count(VRSLVerdict.Unchanged)} unchanged, "
          + $"{Count(VRSLVerdict.Improved)} improved, "
          + $"{Count(VRSLVerdict.Regressed)} regressed, "
          + $"{Count(VRSLVerdict.Missing)} missing";
    }

    /// <summary>
    /// Comparison, verdicts and the markdown report.
    ///
    /// In the runtime assembly rather than the editor one because it is pure work
    /// on the result document — no editor API is involved — and because the suite,
    /// the editor window and the headless runner all have to reach it. Two
    /// implementations of a verdict is two things to keep agreeing with each other.
    /// </summary>
    public static class VRSLBaseline
    {
        /// <summary>
        /// The committed run for the reference machine, or null.
        ///
        /// Found through <c>VRSL_PERF_HOME</c>, the same way the reference frames are,
        /// so a consuming project that holds neither takes no special path.
        ///
        /// <para>Defaulting a comparison to this is safe even though GPU timings do not
        /// travel between machines, because <see cref="Compare"/> refuses on an
        /// environment mismatch. Anywhere but the machine that produced it the answer is
        /// a refusal naming the difference, not a wrong number — which is why this can be
        /// a default where a committed reference image cannot.</para>
        /// </summary>
        public static string ReferencePath => ReferenceFor(
            SystemInfo.graphicsDeviceName, VRSLBenchmarkEnvironment.LocalContext);

        /// <summary>
        /// The committed run for a given machine and lineage, or null.
        ///
        /// Looks for <c>baselines/&lt;gpu&gt;-&lt;context&gt;.json</c> first and falls back
        /// to <c>baseline.json</c>, so a project holding only the older single file keeps
        /// working and a second machine is added by dropping a file in rather than by
        /// replacing anybody's.
        ///
        /// <para>Keyed by GPU <b>and</b> context for the same reason the noise floor is:
        /// an editor run and a player run are two measurements of two different things
        /// and neither describes the other. The fallback is safe because
        /// <see cref="Compare"/> refuses on an environment mismatch — picking the wrong
        /// file gives a refusal naming the difference, never a wrong number.</para>
        /// </summary>
        public static string ReferenceFor(string gpu, string context)
        {
            string home = Environment.GetEnvironmentVariable("VRSL_PERF_HOME");
            if (string.IsNullOrEmpty(home)) return null;

            string named = Path.Combine(home, "baselines", ReferenceFileName(gpu, context));
            if (File.Exists(named)) return named;

            string path = Path.Combine(home, "baseline.json");
            return File.Exists(path) ? path : null;
        }

        /// <summary>
        /// The file a machine's own reference would be under, as a name a filesystem
        /// accepts: GPU names carry spaces and can carry characters Windows refuses in a
        /// path, and a reference nobody can save is not a reference.
        ///
        /// <para><b>Both halves are reduced, and that is the point.</b> The context
        /// reaching here came out of a candidate <c>run.json</c> the caller was handed,
        /// so it is input rather than a constant: a context of <c>../../elsewhere</c>
        /// would otherwise walk straight out of the baselines folder and pick whatever
        /// JSON it found. Reducing rather than allow-listing the two known contexts keeps
        /// a third one working the day somebody adds it — an unknown context gets a file
        /// of its own, which is right, instead of quietly reading the editor's.</para>
        /// </summary>
        public static string ReferenceFileName(string gpu, string context)
        {
            string lineage = string.IsNullOrEmpty(context)
                           ? VRSLBenchmarkEnvironment.EditorContext : context;
            return $"{Reduce(gpu, "unknown")}-{Reduce(lineage, "unknown")}.json";
        }

        /// <summary>Letters and digits, everything else a dash, so the result is one path
        /// segment on any filesystem and cannot be a separator or a traversal.</summary>
        static string Reduce(string value, string whenEmpty)
        {
            if (string.IsNullOrEmpty(value)) value = whenEmpty;
            var name = new StringBuilder(value.Length);
            foreach (char c in value)
                name.Append(char.IsLetterOrDigit(c) ? c : '-');
            return name.ToString();
        }

        /// <summary>Where the reference would be, named whether or not it is there, so a
        /// message can say which path was looked at rather than only that one failed.</summary>
        public static string ReferenceHome =>
            Environment.GetEnvironmentVariable("VRSL_PERF_HOME");

        /// <summary>
        /// Compare a run against a baseline, row by row.
        ///
        /// Refuses across different hardware or renderer configuration unless
        /// forced, because a regression report that is really a hardware difference
        /// costs an afternoon before anyone suspects it. When forced, every row says
        /// so.
        /// </summary>
        public static VRSLComparison Compare(
            VRSLBenchmarkRun baseline, VRSLBenchmarkRun candidate, bool force = false)
        {
            if (baseline == null || candidate == null)
                throw new ArgumentNullException(baseline == null ? nameof(baseline) : nameof(candidate));
            if (baseline.environment == null || candidate.environment == null)
                throw new ArgumentException(
                    "a run with no environment block cannot be compared: the machine it was "
                  + "captured on is what decides whether the numbers mean anything. The file "
                  + "is not one this wrote, or it is truncated.");

            var comparison = new VRSLComparison
            {
                label  = $"{baseline.label} → {candidate.label}",
                forced = force,
            };

            if (!candidate.environment.Matches(baseline.environment, out string difference))
            {
                comparison.environmentMismatch = difference;
                if (!force) return comparison;
            }

            var baseRows = new Dictionary<string, VRSLBenchmarkRow>();
            foreach (var row in baseline.rows)
            {
                if (row?.config == null)
                    throw new ArgumentException(
                        "a baseline row has no configuration, so there is nothing to match a "
                      + "candidate row against. The file is not one this wrote, or it is "
                      + "truncated.");
                // Refused rather than swallowed. A sweep writes one row per configuration,
                // so a duplicate key is a file that has been merged or edited by hand — and
                // letting the last one win silently leaves the verdict counts disagreeing
                // with the matrix the report prints beside them.
                if (baseRows.ContainsKey(row.config.Key))
                    throw new ArgumentException(
                        $"the baseline has more than one row for {row.config.Key}. A sweep "
                      + "writes one row per configuration, so this file has been merged or "
                      + "edited; comparing it would report fewer rows than it contains.");
                baseRows[row.config.Key] = row;
            }

            // The floor cannot sit below the spread the runs actually showed, so a
            // stored figure from a quieter day is raised to meet them rather than
            // used to wave a real move through.
            double storedFloor = Math.Max(baseline.noiseFloorMs, candidate.noiseFloorMs);

            foreach (var row in candidate.rows)
            {
                if (row?.config == null)
                    throw new ArgumentException(
                        "a candidate row has no configuration, so there is nothing to match it "
                      + "against the baseline with. The file is not one this wrote, or it is "
                      + "truncated.");

                var entry = new VRSLRowComparison { config = row.config };

                if (!baseRows.TryGetValue(row.config.Key, out var before))
                {
                    entry.verdict = VRSLVerdict.Missing;
                    comparison.rows.Add(entry);
                    continue;
                }

                entry.noiseFloorMs = Math.Max(storedFloor,
                    Math.Max(row.timings.Noise, before.timings.Noise));

                // A row that timed nothing carries a cost of zero, and zero against a real
                // baseline cost is a delta the size of that cost — reported as a large
                // improvement, then fed to the verdict line and to whatever adopts a noise
                // floor from the largest disagreement. Report the failure, not the
                // artefact. The capture already notes such rows as unusable; this stops
                // the comparison dressing one up as a result.
                if (!row.timings.Measured || !before.timings.Measured)
                {
                    entry.verdict = VRSLVerdict.Missing;
                    comparison.rows.Add(entry);
                    continue;
                }

                // Both sides have to be on the same clock. Comparing a GPU-basis
                // baseline against a CPU-basis candidate produces a large delta that
                // is entirely an artefact of the two being different measurements.
                bool bothGpu = row.timings.HasGpu && before.timings.HasGpu;
                entry.costBasis = bothGpu ? "GPU" : "CPU";

                double after  = bothGpu ? row.timings.packageGpuMs    : row.timings.packageCpuMs;
                double origin = bothGpu ? before.timings.packageGpuMs : before.timings.packageCpuMs;

                entry.packageCostDeltaMs = after - origin;
                entry.packageCostDeltaPercent = Math.Abs(origin) > 1e-6
                    ? 100.0 * entry.packageCostDeltaMs / Math.Abs(origin)
                    : 0.0;
                entry.frameDeltaMs = bothGpu
                    ? row.timings.gpuEnabled.median - before.timings.gpuEnabled.median
                    : row.timings.cpuEnabled.median - before.timings.cpuEnabled.median;

                if (Math.Abs(entry.packageCostDeltaMs) <= entry.noiseFloorMs)
                    entry.verdict = VRSLVerdict.Unchanged;
                else
                    entry.verdict = entry.packageCostDeltaMs > 0
                                  ? VRSLVerdict.Regressed
                                  : VRSLVerdict.Improved;

                DescribeCounterChanges(before.counters, row.counters, entry.counterChanges);
                comparison.rows.Add(entry);
            }

            // A row present in the baseline and gone from the candidate is as much a
            // finding as one that moved.
            foreach (var pair in baseRows)
            {
                bool found = false;
                foreach (var row in candidate.rows)
                    if (row.config.Key == pair.Key) { found = true; break; }
                if (found) continue;
                comparison.rows.Add(new VRSLRowComparison
                {
                    config  = pair.Value.config,
                    verdict = VRSLVerdict.Missing,
                });
            }

            return comparison;
        }

        /// <summary>
        /// A frame-time change with no counter change means something outside the
        /// package moved, so what did and did not move here is usually the first
        /// thing worth reading after the verdict.
        /// </summary>
        static void DescribeCounterChanges(VRSLCounters before, VRSLCounters after, List<string> into)
        {
            if (before.fixtures != after.fixtures)
                into.Add($"fixtures {before.fixtures} → {after.fixtures}");
            if (Math.Abs(before.lightsPerTileAverage - after.lightsPerTileAverage) > 0.05f)
                into.Add($"lights/tile avg {before.lightsPerTileAverage:F2} → {after.lightsPerTileAverage:F2}");
            if (before.lightsPerTileMax != after.lightsPerTileMax)
                into.Add($"lights/tile max {before.lightsPerTileMax} → {after.lightsPerTileMax}");
            if (Math.Abs(before.emptyTilePercent - after.emptyTilePercent) > 0.5f)
                into.Add($"empty tiles {before.emptyTilePercent:F1}% → {after.emptyTilePercent:F1}%");
            if (before.stepsPerLight != after.stepsPerLight)
                into.Add($"steps/light {before.stepsPerLight} → {after.stepsPerLight}");
            // Emitting is the counter that says whether anything was lit at all, so a run
            // that went dark has to read as a counter change rather than as a quiet one.
            if (before.emittingFixtures != after.emittingFixtures)
                into.Add($"emitting {before.emittingFixtures} → {after.emittingFixtures}");
            if (before.channelCount != after.channelCount)
                into.Add($"channels {before.channelCount} → {after.channelCount}");
            if (before.activeTiles != after.activeTiles)
                into.Add($"active tiles {before.activeTiles} → {after.activeTiles}");
            if (Math.Abs(before.peakIntensity - after.peakIntensity) > 0.01f)
                into.Add($"peak intensity {before.peakIntensity:F2} → {after.peakIntensity:F2}");
            if (before.cappedTiles != after.cappedTiles)
                into.Add($"capped tiles {before.cappedTiles} → {after.cappedTiles}");
            if (before.tileCullEngaged != after.tileCullEngaged)
                into.Add($"tile cull {Engaged(before.tileCullEngaged)} → {Engaged(after.tileCullEngaged)}");
            if (before.normalsReuseEngaged != after.normalsReuseEngaged)
                into.Add($"normals reuse {Engaged(before.normalsReuseEngaged)} → {Engaged(after.normalsReuseEngaged)}");
            if (before.depthBoundEngaged != after.depthBoundEngaged)
                into.Add($"depth bound {Engaged(before.depthBoundEngaged)} → {Engaged(after.depthBoundEngaged)}");
        }

        static string Engaged(bool on) => on ? "engaged" : "off";

        /// <summary>
        /// Derive a noise floor from a pair of runs that should be identical.
        ///
        /// This is what makes every other verdict mean something, so it is
        /// deliberately pessimistic: the largest disagreement any row showed, not
        /// the average of them, and never below the spread within the rows
        /// themselves.
        /// </summary>
        public static double DeriveNoiseFloor(VRSLBenchmarkRun a, VRSLBenchmarkRun b)
        {
            var byKey = new Dictionary<string, VRSLBenchmarkRow>();
            foreach (var row in a.rows)
            {
                // Refused rather than skipped. The floor is the largest disagreement, so a
                // row quietly dropped can only make it smaller — which is the direction that
                // hides regressions, and the same failure the Measured guard below exists for.
                if (row?.config == null)
                    throw new ArgumentException(
                        "a row has no configuration, so the floor derived from these two runs "
                      + "would be taken from fewer rows than they contain. The file is not one "
                      + "this wrote, or it is truncated.");
                byKey[row.config.Key] = row;
            }

            double floor = 0.0;
            foreach (var row in b.rows)
            {
                if (row?.config == null)
                    throw new ArgumentException(
                        "a row has no configuration, so the floor derived from these two runs "
                      + "would be taken from fewer rows than they contain. The file is not one "
                      + "this wrote, or it is truncated.");
                if (!byKey.TryGetValue(row.config.Key, out var other)) continue;

                // A row that timed nothing carries a cost of zero, and zero against a
                // real cost is a disagreement the size of that cost. Adopting that as
                // the floor would swallow every regression the floor exists to catch —
                // and it is stored and reused, so a single null run carrying one
                // unmeasured row would go on doing so until somebody derived a new one.
                if (!row.timings.Measured || !other.timings.Measured) continue;
                floor = Math.Max(floor, Math.Abs(row.timings.CostMs - other.timings.CostMs));
                floor = Math.Max(floor, row.timings.Noise);
                floor = Math.Max(floor, other.timings.Noise);
            }
            return floor;
        }

        // ── Reports ───────────────────────────────────────────────────────────

        /// <summary>
        /// The markdown report for a run. The environment block sits at the top of
        /// the file rather than the bottom, so a table cannot be lifted out of it
        /// without the machine it was captured on coming too.
        /// </summary>
        public static string ToMarkdown(VRSLBenchmarkRun run)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# VRSL benchmark — {run.label}");
            sb.AppendLine();
            AppendEnvironment(sb, run.environment);

            if (run.noiseFloorMs > 0.0)
                sb.AppendLine($"Noise floor: **{run.noiseFloorMs:F3} ms**. A difference "
                            + "smaller than this is a reading of the machine.");
            else
                sb.AppendLine("Noise floor: **not established**. Run a null run — capture, "
                            + "change nothing, capture again — before reading any difference "
                            + "here as real.");
            sb.AppendLine();

            if (run.notes.Count > 0)
            {
                sb.AppendLine("## Notes");
                sb.AppendLine();
                foreach (string note in run.notes) sb.AppendLine($"- {note}");
                sb.AppendLine();
            }

            sb.AppendLine("## Rows");
            sb.AppendLine();
            sb.AppendLine("The `+-` column is the standard error of the difference of the two "
                        + "medians. It is a **lower bound** on the real uncertainty: consecutive "
                        + "frame times are correlated rather than independent, so the effective "
                        + "sample count is smaller than the frame count and the true spread is "
                        + "wider. The figure a verdict should actually be judged against comes "
                        + "from a null run on this machine, not from this column.");
            sb.AppendLine();
            sb.AppendLine("| Fixtures | Emitting | Camera | Quality | Package cost | +- | Basis | GPU frame | CPU frame | Lights/tile | Empty tiles | Source |");
            sb.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |");
            foreach (var row in run.rows)
            {
                sb.AppendLine(
                    $"| {row.config.fixtureCount} "
                  + $"| {(row.counters.emittingFixtures == 0 ? "**none**" : row.counters.emittingFixtures.ToString())} "
                  + $"| {row.config.cameraVariant} "
                  + $"| {row.config.quality} "
                  + $"| {(row.timings.Usable ? $"{row.timings.CostMs:F3} ms" : "**not usable**")} "
                  + $"| {row.timings.Noise:F3} ms "
                  + $"| {row.timings.CostBasis} "
                  + $"| {row.timings.gpuEnabled.median:F3} ms (IQR {row.timings.gpuEnabled.iqr:F3}) "
                  + $"| {row.timings.cpuEnabled.median:F3} ms (IQR {row.timings.cpuEnabled.iqr:F3}) "
                  + $"| {row.counters.lightsPerTileAverage:F1} avg / {row.counters.lightsPerTileMax} max "
                  + $"| {row.counters.emptyTilePercent:F0}% "
                  + $"| {row.timings.source} |");
            }
            sb.AppendLine();

            bool anyPasses = false;
            foreach (var row in run.rows) if (row.passes.Count > 0) { anyPasses = true; break; }
            if (anyPasses)
            {
                sb.AppendLine("## Per-pass detail");
                sb.AppendLine();
                sb.AppendLine("GPU marker time, where the platform provides a GPU recorder for "
                            + "the marker; a pass with none simply does not appear. Render Graph "
                            + "merges and reorders passes, so these attribute cost rather than "
                            + "account for it. The headline is the package cost column above.");
                sb.AppendLine();
                sb.AppendLine("| Configuration | Pass | Median |");
                sb.AppendLine("| --- | --- | --- |");
                foreach (var row in run.rows)
                    foreach (var pass in row.passes)
                        sb.AppendLine($"| {row.config} | {pass.name} | {pass.time.median:F3} ms |");
                sb.AppendLine();
            }

            return sb.ToString();
        }

        /// <summary>The markdown report for a comparison.</summary>
        public static string ToMarkdown(VRSLComparison comparison)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# VRSL benchmark comparison — {comparison.label}");
            sb.AppendLine();

            if (comparison.environmentMismatch != null)
            {
                sb.AppendLine(comparison.forced
                    ? $"> **Forced across a mismatched environment** ({comparison.environmentMismatch}). "
                    + "Every row below is comparing two different machines and none of the deltas "
                    + "mean what they appear to."
                    : $"> **Refused**: {comparison.environmentMismatch}. Nothing was compared.");
                sb.AppendLine();
                if (!comparison.forced)
                {
                    // The verdict line goes out even here. The headless gate requires one
                    // in the log and fails without it, so a refusal that omitted it would
                    // read as a run that never reached the comparison.
                    sb.AppendLine(comparison.VerdictLine);
                    sb.AppendLine();
                    return sb.ToString();
                }
            }

            sb.AppendLine(comparison.VerdictLine);
            sb.AppendLine();
            sb.AppendLine("| Fixtures | Camera | Quality | Verdict | Package cost Δ | Frame Δ | Basis | Noise floor | Counters that moved |");
            sb.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- | --- |");
            foreach (var row in comparison.rows)
            {
                if (row.verdict == VRSLVerdict.Missing)
                {
                    sb.AppendLine($"| {row.config.fixtureCount} | {row.config.cameraVariant} "
                                + $"| {row.config.quality} | **Missing** | — | — | — | — | — |");
                    continue;
                }
                string sign = row.packageCostDeltaMs >= 0 ? "+" : "";
                string counters = row.counterChanges.Count > 0
                                ? string.Join(", ", row.counterChanges)
                                : "—";
                sb.AppendLine(
                    $"| {row.config.fixtureCount} "
                  + $"| {row.config.cameraVariant} "
                  + $"| {row.config.quality} "
                  + $"| {(row.verdict == VRSLVerdict.Regressed ? "**Regressed**" : row.verdict.ToString())} "
                  + $"| {sign}{row.packageCostDeltaMs:F3} ms ({sign}{row.packageCostDeltaPercent:F1}%) "
                  + $"| {(row.frameDeltaMs >= 0 ? "+" : "")}{row.frameDeltaMs:F3} ms "
                  + $"| {row.costBasis} "
                  + $"| {row.noiseFloorMs:F3} ms "
                  + $"| {counters} |");
            }
            sb.AppendLine();
            return sb.ToString();
        }

        static void AppendEnvironment(StringBuilder sb, VRSLBenchmarkEnvironment env)
        {
            sb.AppendLine("## Environment");
            sb.AppendLine();
            sb.AppendLine("| | |");
            sb.AppendLine("| --- | --- |");
            sb.AppendLine($"| Captured | {env.capturedAtUtc} |");
            sb.AppendLine($"| Context | **{env.context}** |");
            sb.AppendLine($"| GPU | {env.graphicsDevice} |");
            sb.AppendLine($"| Graphics API | {env.graphicsApi} |");
            sb.AppendLine($"| Driver | {env.graphicsDriver} |");
            sb.AppendLine($"| Editor | {env.unityVersion} |");
            sb.AppendLine($"| Platform | {env.platform} |");
            if (!string.IsNullOrEmpty(env.scriptingBackend))
                sb.AppendLine($"| Scripting backend | {env.scriptingBackend} |");
            sb.AppendLine($"| Pipeline asset | {env.renderPipelineAsset} |");
            sb.AppendLine($"| Renderer | {env.renderer} |");
            sb.AppendLine($"| Depth priming | {env.depthPrimingMode} |");
            sb.AppendLine($"| MSAA | {env.msaaSamples}x |");
            sb.AppendLine($"| XR | {(env.xrActive ? "on" : "off")} |");
            sb.AppendLine($"| VSync | {env.vSyncCount} |");
            sb.AppendLine($"| Target frame rate | {(env.targetFrameRate < 0 ? "uncapped" : env.targetFrameRate.ToString())} |");
            sb.AppendLine($"| Display refresh | {env.refreshRateHz:F0} Hz |");
            sb.AppendLine($"| Screen | {env.screenWidth}x{env.screenHeight} |");
            sb.AppendLine($"| Rendered at | **{env.captureWidth}x{env.captureHeight}** |");
            if (!string.IsNullOrEmpty(env.packageVersion)) sb.AppendLine($"| Package | {env.packageVersion} |");
            if (!string.IsNullOrEmpty(env.gitCommit))      sb.AppendLine($"| Commit | {env.gitCommit} |");
            sb.AppendLine();
            sb.AppendLine("GPU timings are not comparable between machines. A comparison against a "
                        + "run from different hardware is refused rather than reported.");
            sb.AppendLine();
        }
    }
}
