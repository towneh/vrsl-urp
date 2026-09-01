using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace VRSL.URP
{
    /// <summary>
    /// A distribution, not a number. Every timing in a benchmark result carries
    /// its spread, because a single figure with nothing beside it cannot be told
    /// from noise — which is the whole reason the harness exists.
    ///
    /// Median rather than mean: a shader compile or an editor repaint lands as one
    /// enormous frame, and a mean carries it into the answer while a median does
    /// not.
    /// </summary>
    [Serializable]
    public class VRSLStat
    {
        public double median;
        /// <summary>Interquartile range. The spread a verdict has to beat.</summary>
        public double iqr;
        public int    samples;

        public static VRSLStat From(List<double> values)
        {
            var stat = new VRSLStat { samples = values?.Count ?? 0 };
            if (stat.samples == 0) return stat;

            var sorted = new List<double>(values);
            sorted.Sort();
            stat.median = Quantile(sorted, 0.5);
            stat.iqr    = Quantile(sorted, 0.75) - Quantile(sorted, 0.25);
            return stat;
        }

        /// <summary>Linear interpolation between order statistics, so a quantile of
        /// a short sample does not snap to whichever element happens to sit nearest
        /// the index.</summary>
        static double Quantile(List<double> sorted, double q)
        {
            if (sorted.Count == 1) return sorted[0];
            double pos = q * (sorted.Count - 1);
            int    lo  = Mathf.FloorToInt((float)pos);
            int    hi  = Mathf.Min(lo + 1, sorted.Count - 1);
            return sorted[lo] + (sorted[hi] - sorted[lo]) * (pos - lo);
        }

        public override string ToString() => $"{median:F3} ms (IQR {iqr:F3}, n={samples})";
    }

    /// <summary>Where a frame time came from. Named on every row, because the three
    /// are not equivalent and a report that hides which one it used invites a
    /// wall-clock number being read as a GPU one.</summary>
    public enum VRSLTimingSource
    {
        /// <summary>Unavailable — the row carries no usable timing.</summary>
        None = 0,
        FrameTimingManager,
        ProfilerRecorder,
        /// <summary>Frame delta off the CPU clock. No GPU attribution at all.</summary>
        WallClock,
    }

    /// <summary>One point in the sweep matrix.</summary>
    [Serializable]
    public class VRSLRowConfig
    {
        public int    fixtureCount;
        public string cameraVariant = "";
        public string quality       = "";
        public string scene         = "";

        /// <summary>Identity for matching a row against a baseline. Two runs
        /// describe the same configuration when this matches; nothing else is
        /// compared.</summary>
        public string Key => scene + "|" + fixtureCount + "|" + cameraVariant + "|" + quality;

        public override string ToString() =>
            $"{fixtureCount} fixtures, {cameraVariant}, quality {quality}";
    }

    /// <summary>
    /// The enabled and disabled halves of one configuration, and the difference
    /// between them.
    ///
    /// The difference is the headline. Render Graph merges and reorders passes, so
    /// a name in the timeline is an attribution hint rather than a unit of cost,
    /// whereas the difference is what a world author gets back by removing the
    /// package.
    /// </summary>
    [Serializable]
    public class VRSLTimings
    {
        public VRSLStat cpuEnabled  = new();
        public VRSLStat gpuEnabled  = new();
        public VRSLStat cpuDisabled = new();
        public VRSLStat gpuDisabled = new();

        /// <summary>GPU milliseconds attributable to the package.</summary>
        public double packageGpuMs;
        /// <summary>CPU milliseconds attributable to the package.</summary>
        public double packageCpuMs;

        public string source = nameof(VRSLTimingSource.None);
        /// <summary>Measured frames per side, after warm-up.</summary>
        public int    frameCount;

        public void ComputeDifference()
        {
            packageGpuMs = gpuEnabled.median - gpuDisabled.median;
            packageCpuMs = cpuEnabled.median - cpuDisabled.median;
        }

        /// <summary>
        /// Whether this row has GPU attribution at all.
        ///
        /// Several configurations have none — batch mode most of all, where nothing
        /// answers <c>FrameTimingManager</c> and the capture falls back to a CPU
        /// source. A row with no GPU time is not a row where the package was free.
        /// </summary>
        public bool HasGpu => gpuEnabled.median > 0.0 || gpuDisabled.median > 0.0;

        /// <summary>
        /// The package's cost on whichever clock this row actually has. GPU where
        /// there is one, CPU otherwise.
        ///
        /// Falling back matters more than it looks: verdicts decided on the GPU
        /// difference alone read every batch-mode run as unchanged, because the
        /// difference of two zeroes is zero and that is indistinguishable from a
        /// package that costs nothing.
        /// </summary>
        public double CostMs => HasGpu ? packageGpuMs : packageCpuMs;

        /// <summary>Which clock <see cref="CostMs"/> came off. Reported on every
        /// row, because a CPU-basis figure is not a GPU one and the two get quoted
        /// interchangeably otherwise.</summary>
        public string CostBasis => HasGpu ? "GPU" : "CPU";

        /// <summary>
        /// How precisely the difference is known, in milliseconds.
        ///
        /// <b>Not the interquartile range.</b> The IQR is the spread of individual
        /// frames, and individual frames scatter widely for reasons that have nothing
        /// to do with the package — an editor repaint, a background process, the GPU
        /// finding a moment to idle. Averaging that out is precisely what taking a
        /// median does, and its precision improves with the sample count. Judging a
        /// difference against the raw IQR throws away every measurement whose frames
        /// were noisy, including ones whose medians are pinned to a fortieth of that.
        ///
        /// So this is the standard error of the difference of the two medians. For a
        /// roughly normal sample, sigma is <c>IQR / 1.349</c> and the median's own
        /// standard error is <c>1.2533 * sigma / sqrt(n)</c>; the two sides combine in
        /// quadrature. At an IQR of 1.2 ms over 90 frames that is 0.12 ms a side and
        /// 0.17 ms on the difference, against 1.2 ms judged the old way.
        ///
        /// It assumes a distribution that is not pathological, which is why a negative
        /// cost is still refused outright by <see cref="Usable"/> rather than being
        /// waved through by a small error bar, and why the floor a verdict actually
        /// runs against comes from a null run rather than from this.
        /// </summary>
        public double Noise
        {
            get
            {
                var on  = HasGpu ? gpuEnabled  : cpuEnabled;
                var off = HasGpu ? gpuDisabled : cpuDisabled;
                double a = StandardError(on), b = StandardError(off);
                return Math.Sqrt(a * a + b * b);
            }
        }

        static double StandardError(VRSLStat stat)
        {
            if (stat.samples < 2 || stat.iqr <= 0.0) return 0.0;
            return 1.2533 * (stat.iqr / 1.349) / Math.Sqrt(stat.samples);
        }

        /// <summary>The raw spread of single frames, kept for the report. Descriptive,
        /// not the thing a verdict is judged against.</summary>
        public double SpreadIqr => HasGpu
            ? Math.Max(gpuEnabled.iqr, gpuDisabled.iqr)
            : Math.Max(cpuEnabled.iqr, cpuDisabled.iqr);

        /// <summary>
        /// Whether this row measured anything at all. A capture that ran over no frames,
        /// or over frames nothing timed, produces a row of zeroes that compares as
        /// unchanged against anything — which is how a harness that measures nothing
        /// reads as a harness reporting no change.
        ///
        /// <b>Both sides, and that is the whole point.</b> Cost is the enabled frame
        /// minus the disabled one, so a row whose disabled block timed nothing reports
        /// the entire frame as the package's cost — and the noise term, being the two
        /// standard errors in quadrature, collapses to the enabled side alone and stays
        /// small. The row then reads as a huge, confident regression. Everything
        /// downstream trusts this: the comparison, the derived noise floor, and the
        /// floor stored for the machine afterwards.
        /// </summary>
        public bool Measured =>
            (cpuEnabled.samples > 0 || gpuEnabled.samples > 0)
         && (cpuEnabled.median > 0.0 || gpuEnabled.median > 0.0)
         && (cpuDisabled.samples > 0 || gpuDisabled.samples > 0)
         && (cpuDisabled.median > 0.0 || gpuDisabled.median > 0.0);

        /// <summary>
        /// Whether this row's difference means anything at all.
        ///
        /// Two ways it does not. A spread as wide as the value itself is not a
        /// measurement, it is a cloud. And a package that measures as costing
        /// negative time did not make the frame faster — the difference is under the
        /// noise, or the frame was capped and what varied was idle rather than work.
        ///
        /// Reported rather than silently emitted, because "-0.216 ms" reads as a
        /// number and is not one.
        /// </summary>
        public bool Usable => Measured && CostMs > 0.0 && CostMs > Noise;

        /// <summary>The value the spread is judged against: the enabled frame on
        /// whichever clock the cost came off.</summary>
        double ReferenceMs => HasGpu ? gpuEnabled.median : cpuEnabled.median;

        /// <summary>Why <see cref="Usable"/> is false, in a sentence, or null.</summary>
        public string Unusable
        {
            get
            {
                if (!Measured) return "nothing was timed";
                if (CostMs <= 0.0)
                    return $"the package measured as costing {CostMs:F3} ms, which is not "
                         + "possible — with the package enabled the frame was no slower than "
                         + "without it. Either the difference is under the noise, or the frame "
                         + "was capped and what varied between the two halves was idle time";
                if (CostMs <= Noise)
                    return $"the package's cost ({CostMs:F3} ms) is no larger than the "
                         + $"uncertainty on it (+-{Noise:F3} ms), so it cannot be told from zero. "
                         + $"The whole frame is only {ReferenceMs:F3} ms here — there is not "
                         + "enough work in this scene, at this resolution, to measure against. "
                         + "Measure at a larger render size, or with more fixtures";
                return null;
            }
        }
    }

    /// <summary>
    /// What the package was doing while it was being timed. A frame-time change
    /// with no counter change means something outside the package moved, and that
    /// distinction is most of the value of recording them.
    /// </summary>
    [Serializable]
    public class VRSLCounters
    {
        /// <summary>Fixtures in the scene, every light path counted.</summary>
        public int   fixtures;
        /// <summary>Fixtures on the path the tile, emission and volumetric figures
        /// below describe.
        ///
        /// The same as <see cref="fixtures"/> wherever a scene carries one light
        /// path, which is nearly all of them. It differs only when a scene has both,
        /// and there it is the denominator those figures actually have: a coverage
        /// average taken from one cull pass, read against a count that includes the
        /// other path's fixtures, reports less coverage than was measured and can
        /// never reach the worst case.</summary>
        public int   measuredPathFixtures;
        public float lightsPerTileAverage;
        public int   lightsPerTileMax;
        public float emptyTilePercent;
        /// <summary>Tiles that wanted more fixtures than the per-tile cap allows,
        /// where the ones past it are dropped.</summary>
        public int   cappedTiles;
        /// <summary>Fixture-tile pairs the cap threw away across the whole frame.
        /// A count of the light that was asked for and not drawn, so anything above
        /// zero says this configuration measured a scene rendering less than it was
        /// given.</summary>
        public long  droppedFixtureTilePairs;
        public int   activeTiles;
        /// <summary>Tile grid the figures above are over. Recorded because the tile
        /// count on its own does not say what was rendered: a run labelled with a
        /// capture size can still have culled against a different one.</summary>
        public int   tilesAcross;
        public int   tilesDown;
        /// <summary>Camera the tile figures describe. One cull pass serves every
        /// camera in the frame and the last record wins, so this is the only thing
        /// that says whether they are about the view the row is labelled with.</summary>
        public string tileCamera;
        public int   stepsPerLight;
        /// <summary>Fixtures actually emitting light. A configuration where this is
        /// zero measured a dark scene, whatever else its numbers say.</summary>
        public int   emittingFixtures;
        public float peakIntensity;
        /// <summary>Channels a source is publishing, or 0 on the texture path.</summary>
        public int   channelCount;

        public bool tileCullEngaged;
        /// <summary>Set by M4. False until then, and the run notes say so rather than
        /// letting a permanently-false flag read as a regression.</summary>
        public bool normalsReuseEngaged;
        /// <summary>Set by M3. False until then, same caveat.</summary>
        public bool depthBoundEngaged;

        /// <summary>
        /// The count the coverage and emission figures are over.
        /// </summary>
        /// <remarks>
        /// Falls back to <see cref="fixtures"/> when the field is absent, which is
        /// what a run recorded before it existed deserialises to. Reading the raw
        /// field at a call site instead makes every old run report a coverage
        /// denominator of zero.
        /// </remarks>
        public int MeasuredFixtures => measuredPathFixtures > 0 ? measuredPathFixtures : fixtures;

        /// <summary>
        /// Which light path the figures above are over — "DMX" or "AudioLink".
        ///
        /// Recorded rather than inferred from the precedence rule, so a report cannot
        /// name one path while the counters were read off the other. Empty on a run
        /// captured before this existed, which reads as "not recorded" rather than as a
        /// path with no name.
        /// </summary>
        public string measuredPath = "";

        /// <summary>The path the figures are over, or a plain description when a run is
        /// too old to have recorded one.</summary>
        public string MeasuredPathName =>
            string.IsNullOrEmpty(measuredPath) ? "measured" : measuredPath;

        /// <summary>
        /// Whether the figures over one light path are being reported beside a
        /// fixture count over more than one, which happens only in a scene carrying
        /// both. A reader told this can divide the two; a reader not told cannot,
        /// and the ratio reads low without saying why.
        /// </summary>
        public bool MixedPaths => MeasuredFixtures != fixtures;
    }

    /// <summary>Supporting detail, never the headline. Merged passes make these
    /// attribution hints.</summary>
    [Serializable]
    public class VRSLPassTiming
    {
        public string   name = "";
        public VRSLStat time = new();
    }

    [Serializable]
    public class VRSLBenchmarkRow
    {
        public VRSLRowConfig        config   = new();
        public VRSLTimings          timings  = new();
        public VRSLCounters         counters = new();
        public List<VRSLPassTiming> passes   = new();
    }

    /// <summary>
    /// The machine and the configuration a run happened on.
    ///
    /// GPU timings are not comparable across machines, and a comparison that
    /// silently spans two of them costs an afternoon before anyone suspects the
    /// hardware. This block is what a comparison refuses to cross, and it sits at
    /// the top of the markdown report so a table cannot be quoted without it.
    /// </summary>
    [Serializable]
    public class VRSLBenchmarkEnvironment
    {
        public string capturedAtUtc       = "";
        public string unityVersion        = "";
        public string platform            = "";
        public string graphicsDevice      = "";
        public string graphicsApi         = "";
        public string graphicsDriver      = "";
        public string renderPipelineAsset = "";
        public string renderer            = "";
        public string depthPrimingMode    = "";
        public int    msaaSamples;
        public bool   xrActive;
        /// <summary>"Editor" or "Player". A report that does not say which invites an
        /// editor number being quoted in a results table.</summary>
        public string context             = "";

        /// <summary>"Mono" or "IL2CPP". CPU-side figures are not interchangeable
        /// between the two, and a player can be built either way from one project.
        /// Empty on a run captured before this was recorded.</summary>
        public string scriptingBackend    = "";

        /// <summary>What <see cref="scriptingBackend"/> would say for the assembly this
        /// is compiled into.</summary>
        public static string LocalScriptingBackend =>
#if ENABLE_IL2CPP
            "IL2CPP";
#elif ENABLE_MONO
            "Mono";
#else
            "unknown";
#endif

        /// <summary>
        /// The two values <see cref="context"/> takes.
        ///
        /// Named because a noise floor is filed under one of them, and the editor cannot
        /// ask <see cref="LocalContext"/> for "Player" — it would answer "Editor". Two
        /// spellings of the same word would file a floor under one key and read it back
        /// under another, which reports a floor of zero and no error at all.
        /// </summary>
        public const string EditorContext = "Editor";
        public const string PlayerContext = "Player";

        /// <summary>What <see cref="context"/> would say if this were captured now.
        /// Anything filed per context — a noise floor, for one — needs the same answer
        /// outside a run as inside one.</summary>
        public static string LocalContext => Application.isEditor ? EditorContext : PlayerContext;
        /// <summary>Filled by the editor front end. Empty in a player, which has no
        /// package folder to read and no git to ask.</summary>
        public string packageVersion      = "";
        public string gitCommit           = "";
        public int    screenWidth;
        public int    screenHeight;
        /// <summary>
        /// The size actually rendered, which is what GPU cost follows.
        ///
        /// Not the same as the screen size: the sweep renders into a fixed target so its
        /// numbers do not depend on how big somebody left the Game view, and comparing
        /// screen size would then refuse two perfectly comparable sweeps while accepting
        /// two Analyse runs taken at different window sizes. This is the field a
        /// comparison refuses on.
        /// </summary>
        public int    captureWidth;
        public int    captureHeight;
        /// <summary>VSync setting during the capture. Anything above zero caps the
        /// frame, and a capped frame does not measure work — it measures the cap.</summary>
        public int    vSyncCount;
        /// <summary>Frame-rate cap during the capture, or -1 for none.</summary>
        public int    targetFrameRate;
        /// <summary>Display refresh. A CPU frame time sitting on this interval is the
        /// tell that a run was capped whatever the two fields above say.</summary>
        public double refreshRateHz;

        public static VRSLBenchmarkEnvironment Capture()
        {
            var env = new VRSLBenchmarkEnvironment
            {
                capturedAtUtc  = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ssZ"),
                unityVersion   = Application.unityVersion,
                platform       = Application.platform.ToString(),
                graphicsDevice = SystemInfo.graphicsDeviceName,
                graphicsApi    = SystemInfo.graphicsDeviceType.ToString(),
                graphicsDriver = SystemInfo.graphicsDeviceVersion,
                context        = LocalContext,
                scriptingBackend = LocalScriptingBackend,
                screenWidth    = Screen.width,
                screenHeight   = Screen.height,
                // Defaults to the screen, which is right for anything measuring whatever
                // the camera already renders to. A caller with a fixed target overwrites it.
                captureWidth   = Screen.width,
                captureHeight  = Screen.height,
                xrActive       = UnityEngine.XR.XRSettings.enabled
                              && UnityEngine.XR.XRSettings.isDeviceActive,
                vSyncCount      = QualitySettings.vSyncCount,
                targetFrameRate = Application.targetFrameRate,
                refreshRateHz   = Screen.currentResolution.refreshRateRatio.value,
            };

            if (GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset urp)
            {
                env.renderPipelineAsset = urp.name;
                env.msaaSamples         = urp.msaaSampleCount;

                // The renderer in slot 0 rather than whichever a given camera
                // overrides to: the sweep's cameras do not override, and naming the
                // default is more useful than naming nothing.
                foreach (var data in urp.rendererDataList)
                {
                    if (data == null) continue;
                    env.renderer = data.name;
                    if (data is UniversalRendererData universal)
                        env.depthPrimingMode = universal.depthPrimingMode.ToString();
                    break;
                }
            }

            if (string.IsNullOrEmpty(env.depthPrimingMode)) env.depthPrimingMode = "unknown";
            if (string.IsNullOrEmpty(env.renderer))         env.renderer         = "unknown";
            return env;
        }

        /// <summary>Whether two runs were captured somewhere similar enough that
        /// comparing their timings means anything.</summary>
        public bool Matches(VRSLBenchmarkEnvironment other, out string difference)
        {
            difference = null;
            if (other == null) { difference = "no environment recorded"; return false; }
            if (graphicsDevice      != other.graphicsDevice)      { difference = $"GPU: '{graphicsDevice}' vs '{other.graphicsDevice}'"; return false; }
            if (graphicsApi         != other.graphicsApi)         { difference = $"graphics API: {graphicsApi} vs {other.graphicsApi}"; return false; }
            if (unityVersion        != other.unityVersion)        { difference = $"editor: {unityVersion} vs {other.unityVersion}"; return false; }
            if (renderPipelineAsset != other.renderPipelineAsset) { difference = $"pipeline asset: '{renderPipelineAsset}' vs '{other.renderPipelineAsset}'"; return false; }
            if (renderer            != other.renderer)            { difference = $"renderer: '{renderer}' vs '{other.renderer}'"; return false; }
            if (depthPrimingMode    != other.depthPrimingMode)    { difference = $"depth priming: {depthPrimingMode} vs {other.depthPrimingMode}"; return false; }
            if (msaaSamples         != other.msaaSamples)         { difference = $"MSAA: {msaaSamples}x vs {other.msaaSamples}x"; return false; }
            if (xrActive            != other.xrActive)            { difference = $"XR: {xrActive} vs {other.xrActive}"; return false; }
            if (context             != other.context)             { difference = $"context: {context} vs {other.context}"; return false; }
            // Empty means a run captured before this was recorded, which is not the
            // same as a run that disagrees — refusing on it would invalidate every
            // stored baseline for a field neither of them was ever asked about.
            if (!string.IsNullOrEmpty(scriptingBackend) && !string.IsNullOrEmpty(other.scriptingBackend)
             && scriptingBackend != other.scriptingBackend)
            { difference = $"scripting backend: {scriptingBackend} vs {other.scriptingBackend}"; return false; }
            // Zero means a run captured before the size was recorded, for the same
            // reason an empty scripting backend does — and refusing on it invalidated
            // every stored baseline written before the field existed, including the
            // committed reference against its own machine.
            if (captureWidth > 0 && captureHeight > 0
             && other.captureWidth > 0 && other.captureHeight > 0
             && (captureWidth != other.captureWidth || captureHeight != other.captureHeight))
            {
                difference = $"render size: {captureWidth}x{captureHeight} vs "
                           + $"{other.captureWidth}x{other.captureHeight}";
                return false;
            }
            return true;
        }

        public string Summary =>
            $"{graphicsDevice} ({graphicsApi}), Unity {unityVersion}, {context}"
          + (string.IsNullOrEmpty(scriptingBackend) ? ", " : $"/{scriptingBackend}, ")
          + $"{renderPipelineAsset}/{renderer}, depth priming {depthPrimingMode}, "
          + $"MSAA {msaaSamples}x, XR {(xrActive ? "on" : "off")}";
    }

    /// <summary>One run: the machine it happened on, a row per configuration, and
    /// whatever the harness had to fall back to while measuring it.</summary>
    [Serializable]
    public class VRSLBenchmarkRun
    {
        public string                   label       = "";
        public VRSLBenchmarkEnvironment environment = new();
        public List<VRSLBenchmarkRow>   rows        = new();
        /// <summary>Milliseconds a difference has to exceed before it means
        /// anything, derived from a null run — capture, change nothing, capture
        /// again — on this machine. Zero means none has been established, and a
        /// comparison then falls back to the spread the rows themselves show.</summary>
        public double                   noiseFloorMs;
        /// <summary>Fallbacks taken and features unavailable. A row's number can only
        /// be read against these.</summary>
        public List<string>             notes       = new();

        public void Note(string note)
        {
            if (!string.IsNullOrEmpty(note) && !notes.Contains(note)) notes.Add(note);
        }

        public string ToJson() => JsonUtility.ToJson(this, prettyPrint: true);

        public static VRSLBenchmarkRun FromJson(string json) =>
            JsonUtility.FromJson<VRSLBenchmarkRun>(json);
    }
}
