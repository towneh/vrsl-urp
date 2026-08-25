using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Profiling;
using UnityEngine;

namespace VRSL.URP
{
    /// <summary>
    /// How many frames a capture spends where. Defaults are the ones the sweep
    /// runs; a caller wanting a quicker, noisier answer lowers them and the
    /// resulting spread says what that cost.
    /// </summary>
    [Serializable]
    public class VRSLBenchmarkSettings
    {
        /// <summary>Frames discarded before anything is measured. Shader variants
        /// compile lazily, Render Graph pools settle, and the first frames of any
        /// capture are a different machine from the rest.</summary>
        public int warmUpFrames = 90;

        /// <summary>Frames discarded after each toggle of the managers, before that
        /// block's frames start counting.
        ///
        /// Two things need absorbing here. Disabling a manager releases its
        /// RTHandles and re-enabling reallocates them, so without a settle the
        /// allocation lands inside the difference and reads as package cost. And
        /// <c>FrameTimingManager</c> reports a few frames behind, so samples either
        /// side of a toggle would otherwise be attributed to the wrong block.</summary>
        public int settleFrames = 20;

        /// <summary>Measured frames per block.</summary>
        public int blockFrames = 30;

        /// <summary>Blocks per side. The two sides interleave — enabled, disabled,
        /// enabled, disabled — rather than running one after the other, so thermal
        /// drift over the capture moves both medians together instead of turning
        /// into a difference.</summary>
        public int blocks = 3;

        /// <summary>Game seconds per frame while capturing. Fixed, so the frame
        /// index is the clock and anything integrating over time in the package
        /// advances identically every run.</summary>
        public float captureDeltaTime = 1f / 60f;

        public int randomSeed = 20260824;

        /// <summary>Hard stop on the session settle, so a host that never goes quiet
        /// does not hang the run. Reaching it is reported rather than passed over.</summary>
        public int settleCeilingFrames = 4000;

        /// <summary>Total frames one configuration costs, both sides included.</summary>
        public int TotalFrames => warmUpFrames + blocks * 2 * (settleFrames + blockFrames);
    }

    /// <summary>
    /// The capture loop.
    ///
    /// Lives in the runtime assembly on purpose: <b>Analyse This Scene</b> has to
    /// work in a built player and on a world that never imported the profiling
    /// sample. It is inert until <see cref="CaptureRow"/> is pumped, and nothing in
    /// the light path calls into it.
    ///
    /// <b>Pump it from a method the caller's own driver reaches.</b> The test runner
    /// drives one level of coroutine nesting, so a helper that yields this instead
    /// of yielding <c>null</c> itself advances no frames at all — and an unrendered
    /// frame reads exactly like a package that costs nothing.
    /// </summary>
    public static class VRSLBenchmark
    {
        /// <summary>
        /// Whether this process has already thrown a capture away.
        ///
        /// A plain static, and it survives a play session where the project has disabled
        /// domain reload — which would leave the second and later sessions skipping the
        /// warm-up, so the first capture of each measures the opening cadence the warm-up
        /// exists to absorb. Whoever drives a session clears it;
        /// <c>VRSLSweepRunner</c> does so on entering play mode.
        /// </summary>
        static bool s_sessionWarmedUp;

        /// <summary>Frames per window when deciding whether the machine has settled.</summary>
        const int SettleWindow = 60;
        /// <summary>How closely two consecutive windows have to agree.</summary>
        const double SettleTolerance = 0.05;

        /// <summary>
        /// Render frames without measuring any of them, once per process, before the
        /// first capture.
        ///
        /// A session's opening frames run at a fixed cadence — 16.67 ms each, stable
        /// to a twentieth of a millisecond — and everything after runs free. That
        /// outlasts a capture's own warm-up, so the first capture in a process
        /// otherwise reports whatever the cadence allows rather than what the work
        /// costs. Measured 2026-08-24 in batch mode: the first capture put the
        /// package at 0.006 ms and every capture after it between 0.21 and 0.31 ms.
        ///
        /// Called explicitly rather than folded into <see cref="CaptureRow"/>, so a
        /// capture is one thing that does one thing. Idempotent — later calls return
        /// immediately.
        /// </summary>
        public static IEnumerator WarmUpSession(
            VRSLBenchmarkSettings settings, Action onFrame = null, VRSLBenchmarkRun run = null)
        {
            if (s_sessionWarmedUp) yield break;

            settings ??= new VRSLBenchmarkSettings();
            using var frames = new FrameSampler();

            int  rendered = 0;
            int  answered = 0;
            // Deliberately not `== warmUpFrames`: a caller who raises the warm-up above the
            // ceiling would never reach the equality and never step down at all.
            int  nextDowngrade = Math.Max(1, settings.warmUpFrames);
            var  window   = new List<double>();
            double previous = 0.0;
            bool settled = false;

            while (rendered < settings.settleCeilingFrames)
            {
                yield return null;
                onFrame?.Invoke();
                rendered++;

                if (!frames.TrySample(out double cpuMs, out double gpuMs))
                {
                    // Step down each time a source has had the warm-up budget and produced
                    // nothing, not once. There are two rungs below the preferred source and
                    // the middle one can be present but silent — a valid ProfilerRecorder
                    // that never returns a positive value would otherwise strand the loop
                    // at its ceiling, reporting the session as still drifting when in truth
                    // nothing ever sampled it. Downgrade stops of its own accord at the
                    // bottom rung, so calling it again is harmless.
                    if (answered == 0 && rendered >= nextDowngrade)
                    {
                        frames.Downgrade();
                        nextDowngrade += Math.Max(1, settings.warmUpFrames);
                    }
                    continue;
                }
                answered++;
                window.Add(gpuMs > 0.0 ? gpuMs : cpuMs);
                if (window.Count < SettleWindow) continue;

                double median = VRSLStat.From(window).median;
                window.Clear();

                // Two consecutive windows agreeing is the condition. A single window
                // looking calm happens all the time during a lull in whatever else is
                // starting up.
                if (previous > 0.0 && rendered >= settings.warmUpFrames)
                {
                    double drift = Math.Abs(median - previous) / Math.Max(0.0001, previous);
                    if (drift < SettleTolerance) { settled = true; break; }
                }
                previous = median;
            }

            run?.Note(settled
                ? $"Session settled after {rendered} frames."
                : $"Session did NOT settle within {rendered} frames — the frame time was still "
                + "drifting when measurement started, so treat every figure here as provisional. "
                + "Something outside the package is still doing work: in a Basis project the "
                + "client's own startup runs for roughly a thousand frames.");

            // Set once the warm-up has actually happened, rather than on entry. An
            // enumerator that is built and never pumped, or abandoned partway when play
            // mode ends, has warmed nothing up — and a session marked done sends the
            // next capture straight into the opening cadence this exists to absorb.
            s_sessionWarmedUp = true;
        }

        /// <summary>Make the next capture pay for a session warm-up again. For a
        /// caller measuring after a long idle, where the cadence may have returned.</summary>
        public static void ResetSessionWarmUp() => s_sessionWarmedUp = false;

        /// <summary>
        /// Fixes everything a measurement would otherwise inherit from whatever the
        /// editor or the project happened to be doing, and puts it all back on
        /// dispose.
        ///
        /// VSync and a frame-rate cap are the two that matter most: under either,
        /// every configuration reports the same frame time and every difference
        /// collapses to zero, which reads as a package that is free rather than as a
        /// measurement that never happened.
        /// </summary>
        public sealed class DeterminismScope : IDisposable
        {
            readonly float  _captureWas;
            readonly int    _vSyncWas;
            readonly int    _targetWas;
            readonly UnityEngine.Random.State _randomWas;

            public DeterminismScope(VRSLBenchmarkSettings settings)
            {
                _captureWas = Time.captureDeltaTime;
                _vSyncWas   = QualitySettings.vSyncCount;
                _targetWas  = Application.targetFrameRate;
                _randomWas  = UnityEngine.Random.state;

                Time.captureDeltaTime      = settings.captureDeltaTime;
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = -1;
                UnityEngine.Random.InitState(settings.randomSeed);
            }

            public void Dispose()
            {
                // Restoring captureDeltaTime matters beyond the measurement: left
                // set, it decouples the whole editor from real time.
                Time.captureDeltaTime       = _captureWas;
                QualitySettings.vSyncCount  = _vSyncWas;
                Application.targetFrameRate = _targetWas;
                UnityEngine.Random.state    = _randomWas;
            }
        }

        // ── Frame timing ──────────────────────────────────────────────────────

        /// <summary>
        /// One frame's CPU and GPU cost, from whichever source the platform
        /// actually provides. The chosen source is carried on every row rather than
        /// assumed, because wall clock and a GPU timer are not interchangeable and a
        /// report that hides the difference invites one being read as the other.
        /// </summary>
        sealed class FrameSampler : IDisposable
        {
            public VRSLTimingSource Source { get; private set; }
            public string Note { get; private set; }

            ProfilerRecorder _mainThread;
            double _lastRealtime;
            readonly FrameTiming[] _timings = new FrameTiming[1];

            public FrameSampler()
            {
                // FrameTimingManager is the only source here that attributes GPU
                // time at all, so it is worth asking for even where it needs a
                // player-settings toggle to answer. Whether it does is not knowable
                // until frames have gone through it, so the choice is provisional
                // and Downgrade below settles it.
                FrameTimingManager.CaptureFrameTimings();
                Source = VRSLTimingSource.FrameTimingManager;

                _mainThread = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "Main Thread");
                _lastRealtime = Time.realtimeSinceStartupAsDouble;
            }

            /// <summary>Called once per frame whether or not the frame counts, so a
            /// wall-clock delta is never taken across a gap.</summary>
            public bool TrySample(out double cpuMs, out double gpuMs)
            {
                double now   = Time.realtimeSinceStartupAsDouble;
                double wallMs = (now - _lastRealtime) * 1000.0;
                _lastRealtime = now;

                cpuMs = gpuMs = 0.0;

                if (Source == VRSLTimingSource.FrameTimingManager)
                {
                    FrameTimingManager.CaptureFrameTimings();
                    if (FrameTimingManager.GetLatestTimings(1, _timings) > 0
                     && _timings[0].cpuFrameTime > 0.0)
                    {
                        cpuMs = _timings[0].cpuFrameTime;
                        gpuMs = _timings[0].gpuFrameTime;
                        return true;
                    }
                    return false; // Not yet answering. Downgrade decides if it never will.
                }

                if (Source == VRSLTimingSource.ProfilerRecorder && _mainThread.Valid)
                {
                    long ns = _mainThread.LastValue;
                    if (ns > 0)
                    {
                        cpuMs = ns / 1e6;
                        return true; // No GPU attribution from this source.
                    }
                    return false;
                }

                cpuMs = wallMs;
                return wallMs > 0.0;
            }

            /// <summary>Step down to the next source. Called when the current one has
            /// been given the warm-up to answer and has not.</summary>
            public void Downgrade()
            {
                switch (Source)
                {
                    case VRSLTimingSource.FrameTimingManager:
                        Source = _mainThread.Valid
                               ? VRSLTimingSource.ProfilerRecorder
                               : VRSLTimingSource.WallClock;
                        Append("FrameTimingManager reported nothing, so there is no GPU "
                             + "attribution in this run. Enable Frame Timing Stats in Player "
                             + "Settings to get it.");
                        break;
                    case VRSLTimingSource.ProfilerRecorder:
                        Source = VRSLTimingSource.WallClock;
                        Append("Fell back to wall-clock frame delta. CPU only, and it "
                             + "includes anything else the machine was doing.");
                        break;
                }
            }

            /// <summary>Both step-downs can happen in one capture, and the first carries
            /// the actionable half — how to get GPU attribution back. Replacing rather
            /// than appending loses exactly the part worth reading.</summary>
            void Append(string note) =>
                Note = string.IsNullOrEmpty(Note) ? note : Note + " " + note;

            public void Dispose()
            {
                if (_mainThread.Valid) _mainThread.Dispose();
            }
        }

        /// <summary>Per-pass CPU marker time. Detail, not measurement: Render Graph
        /// merges and reorders passes, so these attribute rather than account.</summary>
        sealed class PassSampler : IDisposable
        {
            readonly List<(string name, ProfilerRecorder rec, List<double> samples)> _passes = new();

            public PassSampler(IEnumerable<string> passNames, bool gpu)
            {
                foreach (string name in passNames)
                {
                    // A GPU recorder times the marker on the device rather than on
                    // the thread that issued it, which is the only per-pass GPU
                    // attribution available here. Where the platform has none the
                    // recorder is simply never valid and the pass drops out of the
                    // report.
                    var options = gpu
                        ? ProfilerRecorderOptions.Default | ProfilerRecorderOptions.GpuRecorder
                        : ProfilerRecorderOptions.Default;
                    var rec = ProfilerRecorder.StartNew(
                        ProfilerCategory.Render, name, 1, options);
                    _passes.Add((name, rec, new List<double>()));
                }
            }

            public void Sample()
            {
                foreach (var (_, rec, samples) in _passes)
                {
                    if (!rec.Valid) continue;
                    long ns = rec.LastValue;
                    if (ns > 0) samples.Add(ns / 1e6);
                }
            }

            public List<VRSLPassTiming> Collect()
            {
                var result = new List<VRSLPassTiming>();
                foreach (var (name, _, samples) in _passes)
                {
                    if (samples.Count == 0) continue;
                    result.Add(new VRSLPassTiming { name = name, time = VRSLStat.From(samples) });
                }
                return result;
            }

            public void Dispose()
            {
                foreach (var (_, rec, _) in _passes) if (rec.Valid) rec.Dispose();
            }
        }

        /// <summary>The markers the package emits, in pipeline order. Names that do
        /// not resolve are simply absent from the report.</summary>
        public static readonly string[] PassMarkers =
        {
            "VRSL Surface Properties Prepass",
            "VRSL Normals Prepass",
            "VRSL Tile Light Cull",
            "VRSL DMX Light Compute",
            "VRSL AudioLink Light Compute",
            "VRSL Lighting Pass",
            "VRSL AudioLink Lighting Pass",
            "VRSL Vol Depth Downsample",
            "VRSL Vol Raymarch",
            "VRSL Vol Raymarch FullRes",
            "VRSL Vol Upsample",
        };

        // ── The managers ──────────────────────────────────────────────────────

        /// <summary>
        /// Toggling the managers is how cost is attributed, so it is worth being
        /// explicit that this is the same switch a world author has: enabling and
        /// disabling the component, not a benchmark-only branch inside the light
        /// path. Nothing in the package knows a measurement is happening.
        /// </summary>
        public sealed class ManagerSet
        {
            readonly List<Behaviour> _managers   = new();
            readonly List<bool>      _wasEnabled = new();

            public int Count => _managers.Count;
            public VRSL_URPLightManager Dmx { get; private set; }

            public static ManagerSet Find()
            {
                var set = new ManagerSet();
                var dmx = VRSL_URPLightManager.Instance;
                if (dmx != null) { set.Track(dmx); set.Dmx = dmx; }

                var al = VRSL_AudioLinkURPLightManager.Instance;
                if (al != null) set.Track(al);
                return set;
            }

            void Track(Behaviour manager)
            {
                _managers.Add(manager);
                _wasEnabled.Add(manager.enabled);
            }

            public void SetEnabled(bool on)
            {
                foreach (var m in _managers) if (m != null) m.enabled = on;
            }

            /// <summary>
            /// Put every manager back the way the scene had it.
            ///
            /// Not the same as switching them all on. An author who had deliberately
            /// disabled a manager before running a capture would otherwise find it
            /// enabled afterwards — and in a built player that change lasts the rest of
            /// the session.
            /// </summary>
            public void Restore()
            {
                for (int i = 0; i < _managers.Count; i++)
                    if (_managers[i] != null) _managers[i].enabled = _wasEnabled[i];
            }
        }

        // ── Capture ───────────────────────────────────────────────────────────

        /// <summary>
        /// Capture one configuration: warm up, then alternate enabled and disabled
        /// blocks, and report the medians of each side plus the difference.
        ///
        /// Yields <c>null</c> once per frame. The caller owns the
        /// <see cref="DeterminismScope"/> so a sweep can hold one across a whole
        /// matrix rather than churning it per row.
        /// </summary>
        public static IEnumerator CaptureRow(
            VRSLBenchmarkSettings settings,
            VRSLRowConfig         config,
            VRSLBenchmarkRun      run,
            Action<VRSLBenchmarkRow> onComplete = null,
            Action                onFrame       = null)
        {
            settings ??= new VRSLBenchmarkSettings();
            var row = new VRSLBenchmarkRow { config = config ?? new VRSLRowConfig() };

            var managers = ManagerSet.Find();
            if (managers.Count == 0)
                run.Note("No VRSL manager was present, so every row measures the scene "
                       + "without the package rather than the package's cost in it.");

            var cpuOn  = new List<double>();
            var gpuOn  = new List<double>();
            var cpuOff = new List<double>();
            var gpuOff = new List<double>();

            using var frames = new FrameSampler();
            using var passes = new PassSampler(PassMarkers, gpu: true);

            // Everything that touches manager state runs inside this, so an aborted
            // capture cannot leave the scene as it found it mid-block. The last thing the
            // loop does to a manager may well be SetEnabled(false), so without the finally
            // an abandoned run leaves every VRSL manager *disabled* — a scene that renders
            // unlit, and in a built player for the rest of the session.
            try
            {
                managers.SetEnabled(true);

                // Warm-up. Also where the timing source proves itself: if it has not
                // answered by the end of it, it never will, and the run steps down
                // rather than measuring nothing.
                int answered = 0;
                for (int i = 0; i < settings.warmUpFrames; i++)
                {
                    yield return null;
                    onFrame?.Invoke();
                    if (frames.TrySample(out _, out _)) answered++;
                }
                if (answered == 0)
                {
                    frames.Downgrade();
                    // One more short settle so the replacement has produced a sample of
                    // its own before a block starts counting on it.
                    for (int i = 0; i < settings.settleFrames; i++)
                    {
                        yield return null;
                        onFrame?.Invoke();
                        if (frames.TrySample(out _, out _)) answered++;
                    }
                    if (answered == 0) frames.Downgrade();
                }

                for (int block = 0; block < settings.blocks; block++)
                {
                    for (int side = 0; side < 2; side++)
                    {
                        bool enabled = side == 0;
                        managers.SetEnabled(enabled);

                        for (int i = 0; i < settings.settleFrames; i++)
                        {
                            yield return null;
                            onFrame?.Invoke();
                            frames.TrySample(out _, out _);
                        }

                        for (int i = 0; i < settings.blockFrames; i++)
                        {
                            yield return null;
                            onFrame?.Invoke();
                            if (!frames.TrySample(out double cpuMs, out double gpuMs)) continue;

                            if (enabled) { cpuOn.Add(cpuMs);  gpuOn.Add(gpuMs);  passes.Sample(); }
                            else         { cpuOff.Add(cpuMs); gpuOff.Add(gpuMs); }
                        }

                        // Counters come from the enabled side of the first block only:
                        // the readback stalls the GPU, so doing it per block would put
                        // that stall inside the very frames being measured.
                        if (enabled && block == 0)
                        {
                            managers.SetEnabled(true);
                            ReadCounters(managers, row.counters, run);
                        }
                    }
                }

            }
            finally { managers.Restore(); }

            row.timings.cpuEnabled  = VRSLStat.From(cpuOn);
            row.timings.gpuEnabled  = VRSLStat.From(gpuOn);
            row.timings.cpuDisabled = VRSLStat.From(cpuOff);
            row.timings.gpuDisabled = VRSLStat.From(gpuOff);
            row.timings.frameCount  = settings.blocks * settings.blockFrames;
            row.timings.source      = frames.Source.ToString();
            row.timings.ComputeDifference();
            row.passes = passes.Collect();

            if (!string.IsNullOrEmpty(frames.Note)) run.Note(frames.Note);
            if (row.passes.Count > 0)
                run.Note("Per-pass figures are GPU marker time, where the platform provides a "
                       + "GPU recorder for the marker; a pass with none does not appear at all. "
                       + "Render Graph merges and reorders passes, so they attribute cost rather "
                       + "than account for it.");
            string unusable = row.timings.Unusable;
            if (unusable != null)
                run.Note($"{config}: NOT USABLE — {unusable}.");

            // A frame sitting on the display's refresh interval is not measuring work,
            // it is measuring the wait. Under a cap the enabled half can even come out
            // faster than the disabled one, because what varies between them is idle.
            double refresh = Screen.currentResolution.refreshRateRatio.value;
            if (refresh > 1.0 && row.timings.cpuEnabled.median > 0.0)
            {
                double interval = 1000.0 / refresh;
                if (Math.Abs(row.timings.cpuEnabled.median - interval) < interval * 0.05)
                    run.Note($"The CPU frame time ({row.timings.cpuEnabled.median:F2} ms) sits on "
                           + $"the {refresh:F0} Hz refresh interval, so this run was frame-rate "
                           + "capped and the figures are of the cap rather than of the work. Turn "
                           + "VSync off — in the Game view toolbar for an editor run — or measure "
                           + "in a player build.");
            }

            if (gpuOn.Count > 0 && row.timings.gpuEnabled.median <= 0.0)
                run.Note("GPU frame time came back as zero, so package cost on this run is "
                       + "the CPU difference only.");

            run.rows.Add(row);
            onComplete?.Invoke(row);
        }

        static void ReadCounters(ManagerSet managers, VRSLCounters counters, VRSLBenchmarkRun run)
        {
            var dmx = managers.Dmx;
            if (dmx == null) return;

            counters.fixtures = dmx.FixtureCount;

            // Zero when the volumetric pass is not being enqueued at all, rather than
            // whatever the step field still says. The manager only enqueues the pass
            // where it holds a volumetric material, so a scene with volumetrics off
            // would otherwise report the step count of a march it never runs — a
            // counter that stays plausible while describing nothing.
            counters.stepsPerLight = dmx.VolumetricMaterial != null
                                   ? dmx.volumetricStepCount
                                   : 0;

            counters.channelCount = dmx.ChannelCount;

            var emission = VRSLDiagnostics.SummariseEmission(dmx.LightDataBuffer, dmx.FixtureCount);
            counters.emittingFixtures = emission.Emitting;
            counters.peakIntensity    = emission.Peak;

            // The failure this exists to make loud: a scene where nothing is lit still
            // renders, still fills a report, and still produces differences that look
            // like measurements. Every one of them is of an empty frame.
            if (emission.Total > 0 && emission.Emitting == 0)
                run.Note($"{counters.fixtures} fixture(s) collected and NONE of them are "
                       + "emitting, so this configuration measured an unlit scene. "
                       + (dmx.ChannelCount > 0
                          ? "A channel source is publishing, so the values are reaching the "
                          + "buffer but not the fixtures — check the patch."
                          : "No channel source is publishing and the CRT decode chain has "
                          + "nothing feeding it, so every channel reads zero."));

            var tiles = VRSLDiagnostics.SummariseTiles(dmx.TileCullPass);
            counters.tileCullEngaged      = tiles.Engaged;
            counters.activeTiles          = tiles.Tiles;
            counters.lightsPerTileAverage = tiles.Average;
            counters.lightsPerTileMax     = tiles.Max;
            counters.emptyTilePercent     = tiles.EmptyPercent;
            counters.cappedTiles          = tiles.Capped;

            // These two are set by M3 and M4. Recorded as false rather than omitted
            // so the row shape does not change when they land, and noted so a
            // permanently-false flag is not read as a regression.
            run.Note("normalsReuseEngaged and depthBoundEngaged are always false until "
                   + "M4 and M3 land the accelerations they report.");

            if (tiles.Capped > 0)
                run.Note($"{tiles.Capped} tile(s) hit the {VRSLTileCullPass.MaxLightsPerTile}-light "
                       + "cap during capture, so some fixtures were dropped for those tiles and "
                       + "the timings are of a scene that is not drawing everything.");
        }
    }
}
