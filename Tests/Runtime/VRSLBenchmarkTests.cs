using System;
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace VRSL.URP.Tests
{
    /// <summary>
    /// The two rows that make every other number in the performance programme mean
    /// something: a null run that must report nothing moved, and a deliberately
    /// broken build that must report that it did.
    ///
    /// They are here rather than performed by hand because they have to be re-run
    /// after any change to how results are read. A measurement tool that is trusted
    /// and wrong is worse than no tool, and the failure mode is silent.
    ///
    /// <b>These rows measure, so they are slower than the rest of the suite</b> and
    /// they render at a raised resolution — at the rig's default 256 square there is
    /// not enough per-pixel work for a real regression to clear the noise, and a row
    /// that cannot fail under the fault it is looking for is worse than no row.
    /// </summary>
    class VRSLBenchmarkTests : VRSLDMXTest
    {
        /// <summary>Large enough that the volumetric raymarch dominates the frame.
        /// This is what gives the difference something to be a difference of.</summary>
        const int BenchTargetSize = 1024;

        /// <summary>Short next to the sweep's defaults. The suite has to stay quick
        /// enough to run on every change, and these rows are judging the harness
        /// rather than producing a number anybody quotes.</summary>
        static VRSLBenchmarkSettings Settings() => new()
        {
            warmUpFrames = 40,
            settleFrames = 8,
            // 160 measured frames a side rather than 48. A median's precision improves
            // with the square root of the sample count, and batch mode needs the help:
            // three captures of an unchanged scene spread 34% of the measured cost at
            // 48, which is wider than most of what these rows are trying to see.
            blockFrames  = 32,
            blocks       = 5,
        };

        /// <summary>Set once the process has thrown a full capture away. See
        /// <see cref="WarmUpProcess"/>.</summary>
        static bool s_processWarmedUp;

        /// <summary>
        /// Throw one complete capture away, rig and all, before any row measures.
        ///
        /// The first capture in a batch-mode process runs pinned at exactly the
        /// capture delta — measured 2026-08-24, both halves at 16.669 ms with an IQR
        /// of 0.02, against 1.5 to 1.8 ms for every capture after it. The counters
        /// are correct throughout, so the package is running; the frame is simply
        /// capped, and a cap applies to both halves equally and cancels to a
        /// difference of zero.
        ///
        /// Idle frames do not clear it, and neither does a longer warm-up inside the
        /// capture: what clears it is the rig being disposed and rebuilt. So the
        /// warm-up is a whole capture, taken and discarded. It is judged by nothing,
        /// which is why it cannot use <see cref="Capture"/>'s own assertions — but
        /// those assertions still guard every measured capture, so if this ever stops
        /// curing it the rows say so rather than quietly reporting zero.
        /// </summary>
        static IEnumerator WarmUpProcess()
        {
            if (s_processWarmedUp) yield break;
            var discard = new List<VRSLBenchmarkRun>();
            var capture = Capture("process-warm-up", discard, judge: false);
            while (capture.MoveNext()) yield return capture.Current;
            // Set after the capture, not before. Setting it first means a warm-up that
            // throws leaves every later row skipping it — and those rows then measure the
            // pinned first capture this exists to discard, reporting a difference of zero
            // with nothing to say the warm-up never ran.
            s_processWarmedUp = true;
            Debug.Log($"[bench] process warm-up discarded: "
                    + $"{discard[0].rows[0].timings.cpuEnabled} enabled");
        }

        /// <summary>
        /// Capture one configuration on a freshly built rig and hand back the run.
        ///
        /// The rig is rebuilt per capture rather than reused, so the two halves of a
        /// null run are as independent as two separate sessions would be. Reusing it
        /// would let a warmed pool flatter the second capture and turn a real drift
        /// into an apparent match.
        /// </summary>
        static IEnumerator Capture(string label, List<VRSLBenchmarkRun> into,
                                   System.Action<VRSLDMXRig> configure = null,
                                   bool judge = true)
        {
            var settings = Settings();
            var run = new VRSLBenchmarkRun
            {
                label       = label,
                environment = VRSLBenchmarkEnvironment.Capture(),
            };

            using var rig = VRSLDMXRig.Build(targetSize: BenchTargetSize);
            rig.Source.pattern = VRSL_SyntheticDMXChannelSource.Pattern.Ramp;

            // Settle the manager's passes before anything is configured, so every
            // capture has been through the same lifecycle whatever it is measuring.
            //
            // <b>Configuration comes after this, not before.</b> A bounce rebuilds
            // whatever OnEnable builds, which includes the volumetric material — so
            // configuring first and bouncing second silently undid quality Off and
            // measured it as identical to Standard. A caller whose change needs a
            // bounce to take, like clearing the cull shader, does its own.
            rig.Manager.enabled = false;
            rig.Manager.enabled = true;

            configure?.Invoke(rig);

            using var determinism = new VRSLBenchmark.DeterminismScope(settings);

            // Once per process, and only the first caller pays. Without it the first
            // capture of a session measures the session's opening cadence rather than
            // the work.
            var warm = VRSLBenchmark.WarmUpSession(settings, rig.RenderFrame);
            while (warm.MoveNext()) yield return warm.Current;

            var config = new VRSLRowConfig
            {
                scene         = "rig",
                fixtureCount  = VRSLDMXRig.FixtureCount,
                cameraVariant = "Fixed",
                quality       = "Shipped",
            };

            // Yielded straight from the test method's own driver rather than nested
            // one level deeper: the runner pumps one level of coroutine, so a helper
            // that yields this without yielding null itself advances no frames at
            // all, and a capture over no frames reads as a package that costs
            // nothing.
            var capture = VRSLBenchmark.CaptureRow(settings, config, run, onFrame: rig.RenderFrame);
            while (capture.MoveNext()) yield return capture.Current;

            // Assert before indexing. Reversed, an empty list throws
            // ArgumentOutOfRangeException on the line above and the authored message —
            // the entire reason these guards exist — never reaches the log.
            Assert.That(run.rows, Is.Not.Empty, $"capture '{label}' produced no rows");
            var measured = run.rows[0];

            Debug.Log($"[bench] {label}: fixtures {measured.counters.fixtures}, cull "
                    + $"{measured.counters.tileCullEngaged}, tiles {measured.counters.activeTiles}, "
                    + $"lights/tile {measured.counters.lightsPerTileAverage:F1}");
            Debug.Log($"[bench] {label}: source {measured.timings.source}, basis "
                    + $"{measured.timings.CostBasis}, cost {measured.timings.CostMs:F4} ms, "
                    + $"cpu on {measured.timings.cpuEnabled} / off {measured.timings.cpuDisabled}, "
                    + $"gpu on {measured.timings.gpuEnabled} / off {measured.timings.gpuDisabled}");
            foreach (string note in run.notes) Debug.Log($"[bench] note: {note}");

            // Silence is not success. A capture that ran over no frames, or over
            // frames nothing timed, produces a row of zeroes — and a row of zeroes
            // compares as unchanged against anything, so every row below would pass
            // while measuring nothing at all. Fail here instead, where the message
            // says which part was missing.
            if (!judge) { into.Add(run); yield break; }

            Assert.IsTrue(measured.timings.Measured,
                $"capture '{label}' timed nothing: cpu {measured.timings.cpuEnabled}, "
              + $"gpu {measured.timings.gpuEnabled}, source {measured.timings.source}. "
              + "Either no frames were rendered or no timing source answered.");
            Assert.Greater(measured.timings.CostMs, 0.0,
                $"capture '{label}' put the package's cost at {measured.timings.CostMs:F4} ms "
              + $"({measured.timings.CostBasis}). {VRSLDMXRig.FixtureCount} volumetric fixtures "
              + $"at {BenchTargetSize} square are not free, so a cost of zero means the "
              + "enabled and disabled halves rendered the same thing — check that toggling "
              + "the manager actually removes the passes.");


            into.Add(run);
        }

        /// <summary>
        /// A-M0-1. Capture, change nothing, capture again. Every row must read as
        /// unchanged, and the noise floor the report states must be at or above the
        /// spread the two runs actually showed.
        ///
        /// This is the row that makes every other number meaningful. If it fails,
        /// nothing else the harness says can be believed — including a regression it
        /// reports, which may only be the machine.
        /// </summary>
        [UnityTest]
        public IEnumerator A_M0_1_NullRunReportsEverythingUnchanged()
        {
            yield return WarmUpProcess();

            var runs = new List<VRSLBenchmarkRun>();
            yield return Capture("null-a", runs);
            yield return Capture("null-b", runs);

            double floor = VRSLBaseline.DeriveNoiseFloor(runs[0], runs[1]);
            runs[0].noiseFloorMs = floor;
            runs[1].noiseFloorMs = floor;

            var comparison = VRSLBaseline.Compare(runs[0], runs[1]);
            Debug.Log($"[A-M0-1] {comparison.VerdictLine}, noise floor {floor:F3} ms");
            foreach (var row in comparison.rows) Debug.Log($"[A-M0-1] {row.Describe()}");

            Assert.IsNull(comparison.environmentMismatch,
                $"the two halves of a null run were captured on the same machine moments "
              + $"apart, so an environment mismatch here is the environment block reading "
              + $"something unstable rather than a real difference: "
              + $"{comparison.environmentMismatch}");
            Assert.That(comparison.rows, Is.Not.Empty, "the null run compared no rows at all");

            foreach (var row in comparison.rows)
                Assert.AreEqual(VRSLVerdict.Unchanged, row.verdict,
                    $"a null run reported {row.verdict} on {row.config}. Either the noise "
                  + $"floor is understated or the capture is not deterministic: "
                  + $"{row.Describe()}");

            // The floor has to cover what was actually seen, not what a quiet machine
            // would have shown. A floor below the observed spread waves real jitter
            // through as a verdict.
            foreach (var run in runs)
                foreach (var row in run.rows)
                    Assert.GreaterOrEqual(floor, row.timings.Noise,
                        $"the stated noise floor {floor:F3} ms sits below the spread row "
                      + $"{row.config} actually showed ({row.timings.Noise:F3} ms)");
        }

        /// <summary>
        /// A-M0-2. The harness has to be able to say "regressed" at all.
        ///
        /// <b>Judged on the comparison rather than on a seeded workload</b>, and the
        /// history is why. This row used to make the build genuinely worse and look for
        /// the verdict, which needed a lever big enough to clear the noise floor — and
        /// in this scene nothing shippable is. Both candidates were measured and
        /// rejected: clearing the cull cost 0.0015 ms while the counters showed it
        /// rejecting nine tenths of the per-light work, and quality Standard to High
        /// cost 0.089 ms against a 0.097 ms floor. With volumetrics off the package
        /// still costs 2.55 ms, so any lever that only scales volumetrics is scaling a
        /// twentieth of the total. The row therefore ran on an absurd step count, which
        /// M1 removed along with every other numeric cost field.
        ///
        /// Keeping it would have meant a way to set a step count that exists solely for
        /// this row — a second path to cost, in the milestone whose whole point is that
        /// there is one. What the row is actually for is the verdict logic, and that is
        /// pure arithmetic over two documents: testable exactly, on any clock, without a
        /// GPU or a workload. It runs on every headless suite now rather than only when
        /// somebody opens the editor, which is a stronger guarantee than it had.
        ///
        /// What is not covered here is a real workload change flowing end to end into a
        /// verdict. A-M0-7 is that row: it rebuilds with a changed constant and reads
        /// the sweep, and editing the quality table is still its lever.
        /// </summary>
        [Test]
        public void A_M0_2_AGenuineSlowdownIsReportedAsRegressed()
        {
            const double floor = 0.100;

            // Comfortably past the floor, and the sign says which way.
            var slower = CompareSynthetic(before: 1.000, after: 1.400, floor: floor);
            Assert.AreEqual(VRSLVerdict.Regressed, slower.rows[0].verdict,
                            "0.4 ms slower against a 0.1 ms floor has to read as a regression, "
                          + "or the harness cannot report one at all");
            Assert.IsTrue(slower.AnyRegressed, "and the run-level verdict has to follow the row");

            var faster = CompareSynthetic(before: 1.400, after: 1.000, floor: floor);
            Assert.AreEqual(VRSLVerdict.Improved, faster.rows[0].verdict,
                            "the same move the other way is an improvement, not a regression");
            Assert.IsFalse(faster.AnyRegressed);

            // Inside the floor. This is the half that stops the row above being passed
            // by something that simply calls everything regressed.
            var jitter = CompareSynthetic(before: 1.000, after: 1.050, floor: floor);
            Assert.AreEqual(VRSLVerdict.Unchanged, jitter.rows[0].verdict,
                            "a move smaller than the floor is jitter, whatever its sign");

            // And exactly at it, because a boundary written as <= is a decision.
            var onTheFloor = CompareSynthetic(before: 1.000, after: 1.100, floor: floor);
            Assert.AreEqual(VRSLVerdict.Unchanged, onTheFloor.rows[0].verdict,
                            "a delta equal to the floor is not past it");
        }

        /// <summary>Two runs alike in everything but one row's package cost.</summary>
        static VRSLComparison CompareSynthetic(double before, double after, double floor)
        {
            VRSLBenchmarkRun Run(double cost) => new VRSLBenchmarkRun
            {
                // The same environment on both sides: Compare refuses across a
                // difference, and a refusal reports no rows at all rather than a verdict.
                environment  = new VRSLBenchmarkEnvironment(),
                noiseFloorMs = floor,
                rows =
                {
                    new VRSLBenchmarkRow
                    {
                        config   = new VRSLRowConfig { scene = "synthetic", fixtureCount = 1,
                                                       cameraVariant = "InsideCones",
                                                       quality = "Standard" },
                        timings  = new VRSLTimings
                        {
                            packageGpuMs = cost,
                            packageCpuMs = cost,
                            gpuEnabled   = new VRSLStat { median = cost, samples = 30 },
                            cpuEnabled   = new VRSLStat { median = cost, samples = 30 },
                        },
                        counters = new VRSLCounters(),
                    },
                },
            };

            return VRSLBaseline.Compare(Run(before), Run(after));
        }

        /// <summary>
        /// H5. The quality preset has to actually change what is rendered, or the
        /// sweep's most useful axis reports three identical rows.
        ///
        /// <c>Off</c> is the one that can go wrong quietly. Clearing
        /// <c>volumetricShader</c> does not stop the volumetric pass, because the
        /// manager builds its material once and never drops it when the shader goes
        /// away — so a preset that only cleared the field would leave the pass
        /// running at full cost while the report claimed volumetrics were off.
        ///
        /// Judged on cost rather than on a field, and closable on a CPU clock because
        /// removing a fullscreen pass removes its CPU-side setup too, unlike the
        /// cull-shader regression in A-M0-2.
        /// </summary>
        [UnityTest]
        public IEnumerator H5_QualityOffCostsLessThanStandard()
        {
            yield return WarmUpProcess();

            var runs = new List<VRSLBenchmarkRun>();
            yield return Capture("quality-standard", runs,
                rig => VRSLQualityPreset.Session.Begin(rig.Manager)
                          .Apply(VRSLQuality.Standard));

            // Off is captured without the shared judging: the whole point of the level
            // is that it costs less, and it may legitimately cost near enough nothing
            // to trip the "package cost must exceed zero" guard.
            var offRuns = new List<VRSLBenchmarkRun>();
            yield return Capture("quality-off", offRuns,
                rig => VRSLQualityPreset.Session.Begin(rig.Manager)
                          .Apply(VRSLQuality.Off),
                judge: false);

            var standard = runs[0].rows[0];
            var off      = offRuns[0].rows[0];
            double floor = Math.Max(standard.timings.Noise, off.timings.Noise);

            Debug.Log($"[H5] Standard {standard.timings.CostMs:F4} ms, "
                    + $"Off {off.timings.CostMs:F4} ms, floor {floor:F4} ms, "
                    + $"steps/light {standard.counters.stepsPerLight} vs {off.counters.stepsPerLight}");

            // The counter half, and it holds on any clock. Steps per light reads zero
            // only where the manager is not enqueueing the volumetric pass, so this is
            // the observable that says Off actually took rather than merely being set.
            Assert.Greater(standard.counters.stepsPerLight, 0,
                "the Standard capture reports no volumetric steps, so this row is "
              + "comparing two scenes with volumetrics already off");
            Assert.AreEqual(0, off.counters.stepsPerLight,
                "quality Off left the volumetric pass being enqueued. Clearing "
              + "volumetricShader alone does not do it — the manager builds its material "
              + "once and keeps it — so the preset has to drop the material itself");

            // The whole timing half needs a GPU clock, direction included.
            //
            // Direction looked safe on one favourable reading — a 0.069 ms gap against a
            // stated 0.021 — and was asserted on that basis. It then measured Off at
            // 0.207 ms against Standard at 0.171, dearer by 0.036, while the counters
            // still said Off was not enqueueing the pass at all. A-M0-3 explains it:
            // run-to-run spread on the CPU clock here is around 0.055 ms, which is wider
            // than the gap, so the sign flips as often as not. On a CPU clock, removing
            // a fullscreen pass gives back only its issue cost and that is simply too
            // small to see from batch mode.
            if (!standard.timings.HasGpu || !off.timings.HasGpu)
                Assert.Inconclusive(
                    "H5's timing half needs GPU attribution and this run has none. The "
                  + "counter half passed, so quality Off is removing the pass — but on the "
                  + "CPU clock the gap is inside the run-to-run spread and its sign is not "
                  + $"reliable. Observed: Standard {standard.timings.CostMs:F4} ms against "
                  + $"Off {off.timings.CostMs:F4} ms, floor {floor:F4} ms.");

            Assert.Greater(standard.timings.CostMs - off.timings.CostMs, floor,
                $"quality Off did not measurably cost less than Standard "
              + $"({off.timings.CostMs:F4} against {standard.timings.CostMs:F4} ms, floor "
              + $"{floor:F4}), even though the counters say the pass was removed");
        }

        /// <summary>
        /// M3's opening question: what is the existing tile cull actually worth?
        ///
        /// Not an acceptance row — it asserts almost nothing, on purpose. M3 is premised
        /// on <c>lightsInTile x steps x work</c> dominating, and proposes attacking the
        /// first term by culling cones instead of bounding spheres. Before building that,
        /// this measures what the cull already saves, so the milestone starts from a
        /// number rather than from the premise.
        ///
        /// It was measured once before at 0.0015 ms and the figure was withdrawn: the rig
        /// had no floor then, so its beams landed on nothing and it rendered a frame with
        /// 0.09% of pixels lit. A scene with nothing to light cannot say what culling
        /// saves. With a floor it renders 62.8% lit, which is why the question is worth
        /// putting again.
        ///
        /// What it does assert is that the comparison was valid — the cull engaged on one
        /// side and not the other, and it rejected something. The saving itself is logged
        /// rather than bounded, because nobody yet knows what it should be, and a row
        /// asserting a number here would encode the guess this exists to replace.
        /// </summary>
        [UnityTest]
        public IEnumerator M3_HowMuchDoesTheTileCullActuallySave()
        {
            yield return WarmUpProcess();

            var runs = new List<VRSLBenchmarkRun>();
            yield return Capture("cull-on", runs);
            yield return Capture("cull-off", runs, rig =>
            {
                // The cull resolves its shader in its constructor and the manager drops
                // the pass on disable, so clearing the field takes only after a bounce.
                rig.Manager.lightCullShader = null;
                rig.Manager.enabled = false;
                rig.Manager.enabled = true;
            });

            var on  = runs[0].rows[0];
            var off = runs[1].rows[0];
            double floor  = Math.Max(on.timings.Noise, off.timings.Noise);
            double saving = off.timings.CostMs - on.timings.CostMs;

            Debug.Log($"[M3] cull on  {on.timings.CostMs:F4} ms, "
                    + $"{on.counters.lightsPerTileAverage:F1} lights/tile avg, "
                    + $"{on.counters.lightsPerTileMax} max, "
                    + $"{on.counters.emptyTilePercent:F0}% empty tiles");
            Debug.Log($"[M3] cull off {off.timings.CostMs:F4} ms, iterating all "
                    + $"{off.counters.fixtures} fixtures per pixel");
            Debug.Log($"[M3] saving {saving:F4} ms ({on.timings.CostBasis}) against a floor "
                    + $"of {floor:F4} ms. "
                    + (saving > floor
                       ? "A real saving, so M3 has something to improve on."
                       : "Inside the noise, so the cull is worth little here and M3's "
                       + "premise wants checking before the milestone is built."));

            // The comparison has to have been valid, whatever the answer turns out to be.
            Assert.IsTrue(on.counters.tileCullEngaged,
                "the baseline capture reports tile culling inactive, so both sides ran "
              + "unculled and the measurement is of nothing");
            Assert.IsFalse(off.counters.tileCullEngaged,
                "clearing lightCullShader left the cull reporting as engaged");
            Assert.Less(on.counters.lightsPerTileAverage, (float)on.counters.fixtures,
                $"the cull kept all {on.counters.fixtures} fixtures in the average tile, so "
              + "it rejected nothing from this viewpoint and there is no saving to measure "
              + "— a finding about the scene rather than about the cull");

            // The saving is a GPU question and a CPU clock answers it backwards: the cull
            // costs CPU time to dispatch and saves GPU time per pixel, so on the CPU it
            // reliably measures as a small loss however well it is working. Batch mode has
            // no GPU clock, so the number it produces here is not the one M3 needs.
            if (!on.timings.HasGpu || !off.timings.HasGpu)
                Assert.Inconclusive(
                    $"the cull rejected well — {on.counters.lightsPerTileAverage:F1} of "
                  + $"{on.counters.fixtures} fixtures per tile on average — but what that is "
                  + "worth is a GPU question and this run has no GPU clock. On the CPU the "
                  + "sign is inverted by construction, since dispatching the cull costs CPU "
                  + $"and saves GPU. Observed: {saving:F4} ms CPU. Run it from the editor.");
        }

        /// <summary>
        /// H6. The sweep varies fixture count by activating a subset of one truss, so
        /// the count a row claims has to be the count the manager actually collected.
        /// Rounding collisions at small counts are the way this goes wrong, and it
        /// goes wrong silently — every figure in the row stays plausible.
        /// </summary>
        [UnityTest]
        public IEnumerator H6_TheSweepActivatesTheFixtureCountItClaims()
        {
            // Populate, not Build. Build opens a new scene behind a modal save prompt,
            // which would hang a headless run and unload the fixture under the runner.
            var root = VRSLBenchmarkScene.Populate();
            try
            {
                yield return null;

                foreach (int want in VRSLBenchmarkScene.FixtureCounts)
                {
                    VRSLBenchmarkScene.SetActiveFixtures(root, want);
                    yield return null;

                    int active = 0;
                    var truss = root.transform.Find("Truss");
                    Assert.IsNotNull(truss, "the scene builder produced no 'Truss' child, so "
                                          + "its layout contract has changed under this row");
                    for (int i = 0; i < truss.childCount; i++)
                        if (truss.GetChild(i).gameObject.activeSelf) active++;

                    Debug.Log($"[H6] asked for {want}, activated {active}");
                    Assert.AreEqual(want, active,
                        $"the sweep asked for {want} fixtures and activated {active}. Every "
                      + "figure in that row would be attributed to the wrong fixture count");
                }
            }
            finally
            {
                if (root != null) UnityEngine.Object.DestroyImmediate(root);
                // The scene builder switches off directional lights that are not its own,
                // and those outlive the root. Left off, they change every row after this one.
                VRSLBenchmarkScene.RestoreScene();
            }
        }

        /// <summary>
        /// The reproducibility the harness is declared to have, as a fraction of the
        /// cost being measured.
        ///
        /// <b>This is an engineering declaration, not a derived number</b>, and it is
        /// stated here rather than computed from the runs being judged because a floor
        /// derived from the same data it adjudicates cannot fail. A-M0-3 measures the
        /// real spread and checks it against this.
        ///
        /// <b>It is a smoke test for a broken harness, not a precision certificate.</b>
        /// Batch mode on the CPU clock reproduces poorly and inconsistently: measured
        /// 2026-08-24 at 48 frames a side, three captures of an unchanged scene spread
        /// 5.9% of the measured cost on one run and 33.9% on the next. Tightening this
        /// until those pass would be fitting a constant to noise; loosening it until
        /// nothing ever fails would be worse. What it has to catch is a harness that
        /// has stopped working, which produces spreads of a different order — all
        /// zeroes, or hundreds of percent — not one having a noisy afternoon.
        ///
        /// The practical consequence is worth stating plainly: <b>a headless run cannot
        /// see a small regression at all.</b> An editor or player run on the GPU clock
        /// does far better — the sweep's rows carry signals ten to thirty times their
        /// stated precision — which is why the numbers a milestone quotes come from
        /// there and this is only a gate.
        /// </summary>
        const double DeclaredReproducibility = 0.40;

        /// <summary>Below this the fraction is meaningless, so the tolerance is
        /// absolute instead.</summary>
        const double DeclaredFloorMs = 0.05;

        /// <summary>
        /// A-M0-3. Three captures of an unchanged scene, and the spread between them
        /// is what the harness can actually resolve.
        ///
        /// The spread is measured across all three and judged against a tolerance
        /// declared in code. Deriving the floor from the same three runs would produce
        /// a row that passes whatever the harness does, which is the failure mode this
        /// whole milestone exists to avoid.
        /// </summary>
        [UnityTest]
        public IEnumerator A_M0_3_ThreeRunsAgreeWithinTheDeclaredTolerance()
        {
            yield return WarmUpProcess();

            var runs = new List<VRSLBenchmarkRun>();
            yield return Capture("repeat-a", runs);
            yield return Capture("repeat-b", runs);
            yield return Capture("repeat-c", runs);

            double lowest = double.MaxValue, highest = double.MinValue;
            foreach (var run in runs)
            {
                double cost = run.rows[0].timings.CostMs;
                lowest  = Math.Min(lowest, cost);
                highest = Math.Max(highest, cost);
            }

            double spread    = highest - lowest;
            double middle    = (highest + lowest) * 0.5;
            double tolerance = Math.Max(DeclaredFloorMs, middle * DeclaredReproducibility);

            Debug.Log($"[A-M0-3] three runs: {runs[0].rows[0].timings.CostMs:F4}, "
                    + $"{runs[1].rows[0].timings.CostMs:F4}, {runs[2].rows[0].timings.CostMs:F4} ms "
                    + $"({runs[0].rows[0].timings.CostBasis}). Spread {spread:F4} ms "
                    + $"({100.0 * spread / Math.Max(0.0001, middle):F1}%), tolerance {tolerance:F4} ms.");

            // What the stated +- would have claimed, logged beside what actually
            // happened. The gap between the two is the whole reason this row states its
            // own tolerance rather than trusting that column.
            Debug.Log($"[A-M0-3] the rows' own stated precision was "
                    + $"{runs[0].rows[0].timings.Noise:F4} ms, against an observed spread of "
                    + $"{spread:F4} ms. The stated figure is a lower bound.");

            Assert.LessOrEqual(spread, tolerance,
                $"three captures of an unchanged scene spread {spread:F4} ms over a "
              + $"{middle:F4} ms cost, beyond the {tolerance:F4} ms this harness declares it "
              + "can reproduce. Either the machine is busier than a measurement allows, or "
              + "the capture is not as deterministic as it claims");
        }
    }
}
