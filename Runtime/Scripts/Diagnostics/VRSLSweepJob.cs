using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace VRSL.URP
{
    /// <summary>What a sweep left behind: a run to write, or a reason there is none.</summary>
    class VRSLSweepOutcome
    {
        public VRSLBenchmarkRun run;
        public string           error;
    }

    /// <summary>
    /// The standard sweep's capture loop.
    ///
    /// In the runtime assembly because R-M0-6 asks for runs in a built player as well
    /// as in the editor, and the player is the context whose numbers get quoted — batch
    /// mode has no GPU clock, so the editor window and a built player are the only two
    /// things that can produce a GPU-basis matrix at all. Two copies of this loop would
    /// be two things to keep agreeing with each other, so there is one and each host
    /// drives it: the editor window through play mode, a player through its bootstrap.
    ///
    /// <b>The whole matrix runs inside one session.</b> The scene is built at its
    /// largest count beforehand and each configuration activates a subset of the truss
    /// rather than rebuilding — see <see cref="VRSLBenchmarkScene"/> for why that
    /// decides the shape of a sweep.
    /// </summary>
    static class VRSLSweepJob
    {
        /// <summary>
        /// Measure the whole matrix in the scene that is already loaded.
        ///
        /// <paramref name="stampEnvironment"/> fills the environment block once the
        /// session has settled. It is the caller's because the two hosts know different
        /// things: the editor can read VERSION.txt and shell out to git, and a player
        /// can only carry what was baked into it when it was built.
        /// </summary>
        /// <param name="forceOwnNormals">Draw VRSL's own normals prepass on every camera,
        /// where the policy would otherwise read URP's. The other half of a pair that
        /// measures what the reuse is worth; the run is labelled so the two are not
        /// mistaken for each other.</param>
        /// <param name="mirrors">Measure the mirror matrix instead of the standard one:
        /// one fixture count and camera variant, with none, one and three extra cameras
        /// rendering into textures at each secondary-camera policy and at Standard and
        /// High. What a world with mirrors pays, and what each policy gives back.</param>
        public static IEnumerator Run(VRSLSweepOutcome outcome, Action<VRSLBenchmarkRun> stampEnvironment,
                                      bool forceOwnNormals = false, bool mirrors = false)
        {
            var settings = new VRSLBenchmarkSettings();
            var run      = new VRSLBenchmarkRun
            {
                label = mirrors ? "mirror-sweep"
                      : forceOwnNormals ? "standard-sweep-own-normals" : "standard-sweep",
            };
            var root     = GameObject.Find(VRSLBenchmarkScene.RootName);
            var camera   = VRSLBenchmarkScene.FindCamera(root);

            if (root == null || camera == null)
            {
                outcome.error = "The sweep scene is not in the loaded scene. Build it and try again.";
                yield break;
            }

            var manager = VRSL_URPLightManager.Instance;
            if (manager == null)
            {
                outcome.error = "No VRSL DMX light manager in the sweep scene.";
                yield break;
            }

            // Registered here rather than left to the source's own OnEnable, which
            // only lands if the manager had already claimed the singleton by the time
            // it ran. It did not: the first sweep collected its fixtures, built its
            // tiles and culled them, and every tile came back empty because no channel
            // values ever reached a fixture. The PlayMode rig assigns it the same way,
            // for the same reason.
            var source = UnityEngine.Object.FindFirstObjectByType<VRSL_SyntheticDMXChannelSource>();
            if (source == null)
            {
                outcome.error = "The sweep scene has no synthetic DMX source, so the fixtures "
                              + "would have nothing to light from.";
                yield break;
            }
            manager.ChannelSource = source;
            bool forceOwnNormalsWas = manager.forceOwnNormals;
            manager.forceOwnNormals = forceOwnNormals;
            // The sweep's camera renders into a texture, which makes it a secondary
            // camera to the policy. Held at Match so a row measures the level it is
            // labelled with; the mirror matrix sets the policy per row and this is
            // what it goes back to between them.
            var policyWas = manager.secondaryCameraMode;
            manager.secondaryCameraMode = SecondaryCameraMode.Match;
            if (forceOwnNormals)
                run.Note("VRSL drew its own normals prepass on every camera (forceOwnNormals), "
                       + "so normalsReuseEngaged is false by request rather than by policy.");

            // Everything that can refuse the run happens above this line. A fixed target
            // rather than the screen, so the measurement does not depend on the size of a
            // window and whatever else is being drawn is out of the frame being timed —
            // but it is a live allocation, and a yield break past it leaks one for the
            // rest of the session.
            RenderTexture target = null;

            // From here the manager is held at a preset and a render texture is live,
            // so every exit runs through the finally below — a coroutine abandoned
            // because somebody left play mode mid-sweep included.
            // A try/catch cannot wrap a yield, so completion is tracked instead. An
            // exception anywhere inside propagates out of the coroutine, and without this
            // the host never hears: nothing is written and it waits for a result that is
            // not coming.
            //
            // This covers a throw, and only a throw. Unity does not unwind a coroutine
            // when its host is destroyed, so leaving play mode by hand reaches neither
            // the finally nor the host — the host reports that case for itself.
            bool completed = false;
            VRSLQualityPreset.Session quality = null;
            var mirrorCameras = new List<Camera>();
            // Whether anything but the sweep's own cameras rendered during a
            // configuration. Checked per configuration, since the count a mirror row
            // expects is its own.
            bool shared = false;
            try
            {
            // Allocated inside the try, and first. The finally below is the only
            // thing that gives the target back, so anything that can throw has to
            // sit after it rather than between it and the try.
            target = new RenderTexture(
                VRSLBenchmarkScene.CaptureWidth, VRSLBenchmarkScene.CaptureHeight, 24)
            { name = "VRSL sweep target" };
            camera.targetTexture = target;

            // The sweep takes the frame for itself. Every rendering camera runs the
            // whole VRSL pass chain, so anything else in the frame makes the timings a
            // sum of two views and leaves the tile counters describing whichever
            // rendered last — which is what a thirty-row sweep measured on 2026-08-31,
            // every counter belonging to a host camera nobody posed.
            //
            // Done again between configurations, because a host can bring its camera up
            // after this point. The counts and the two reports below are kept as the
            // check that it worked: suppressing and then trusting it is the shape of
            // fault this whole exercise exists to remove.
            VRSLBenchmarkScene.SuppressOtherCameras(camera);
            int camerasAtStart = VRSLBenchmarkScene.RenderingCameraCount();

            quality = VRSLQualityPreset.Session.Begin(manager);
            using var determinism = new VRSLBenchmark.DeterminismScope(settings);

            var warm = VRSLBenchmark.WarmUpSession(settings, null, run);
            while (warm.MoveNext()) yield return warm.Current;
            stampEnvironment(run);
            // The stamp reads the pipeline asset, and a host that rewrites it during
            // startup can do so after this point — so the recorded MSAA was the host's
            // latest whim rather than what the rows were measured at. Take it from what
            // the capture is holding instead. Two halves of one matched pair recorded
            // 2x and 1x while their VRSL-disabled frames agreed to 0.001 ms, which is
            // what a stamp disagreeing with its own run looks like.
            if (determinism.PinnedMsaa > 0) run.environment.msaaSamples = determinism.PinnedMsaa;
            if (determinism.PinnedPriming.HasValue)
                run.environment.depthPrimingMode = determinism.PinnedPriming.Value.ToString();
            // The size actually rendered, which is not the screen's.
            run.environment.captureWidth  = VRSLBenchmarkScene.CaptureWidth;
            run.environment.captureHeight = VRSLBenchmarkScene.CaptureHeight;

            if (mirrors)
            {
                var mirrorJob = RunMirrors(settings, run, root, camera, manager, quality,
                                           determinism, mirrorCameras, v => shared |= v);
                while (mirrorJob.MoveNext()) yield return mirrorJob.Current;
            }
            else
            {
            int done = 0;
            int total = VRSLBenchmarkScene.FixtureCounts.Length
                      * VRSLBenchmarkScene.CameraVariants.Length
                      * VRSLQualityPreset.All.Length;

            foreach (int wanted in VRSLBenchmarkScene.FixtureCounts)
            {
                // Labelled with what was activated rather than what was asked for. The
                // truss is finite, so a count past its size is clamped, and a row
                // carrying a fixture count it never ran describes a workload that does
                // not exist.
                int fixtures = VRSLBenchmarkScene.SetActiveFixtures(root, wanted);
                if (fixtures != wanted)
                    run.Note($"Asked for {wanted} fixtures and the scene could only "
                           + $"activate {fixtures}; those rows are labelled with what "
                           + "actually ran.");

                foreach (var variant in VRSLBenchmarkScene.CameraVariants)
                {
                    VRSLBenchmarkScene.PoseCamera(camera, variant);

                    foreach (var level in VRSLQualityPreset.All)
                    {
                        quality.Apply(level);

                        var config = new VRSLRowConfig
                        {
                            scene         = "standard",
                            fixtureCount  = fixtures,
                            cameraVariant = variant.ToString(),
                            quality       = level.ToString(),
                        };

                        // Deliberately a log rather than a progress bar. A progress bar
                        // repaints the editor while it is up, and the thing being
                        // measured here is how long a frame takes.
                        Debug.Log($"[VRSL sweep] {config} ({++done} of {total})");

                        // Again per configuration, both of them: a host camera that
                        // appears mid-run would otherwise own every row after it, and a
                        // host that rewrites its pipeline asset mid-run would otherwise
                        // move MSAA under the capture.
                        VRSLBenchmarkScene.SuppressOtherCameras(camera);
                        determinism.Reassert();

                        var capture = VRSLBenchmark.CaptureRow(
                            settings, config, run, expectedTileCamera: camera);
                        while (capture.MoveNext()) yield return capture.Current;
                    }
                }
            }
            }

            // Two separate claims, and the second does not imply the first. Who else
            // rendered decides what the timings are of; which camera's record survived
            // decides what the tile figures are of. A host camera that renders before
            // the sweep's leaves the counters looking local while the timings still
            // carry both.
            if (mirrors)
            {
                if (shared)
                    run.Note("Cameras other than the sweep's own and its mirrors were rendering "
                           + "during at least one configuration, so those timings are of more "
                           + "than the view each row is labelled with.");
            }
            else
                ReportOtherCameras(run, camerasAtStart);
            ReportTileCamera(run, camera);

            // Recorded from what happened rather than from what was attempted, so a
            // comparison refuses across the change instead of quietly spanning it: a
            // run that shared the frame measured more than this one did. On the mirror
            // matrix the frame is the sweep's alone when nothing but its own cameras
            // rendered; the mirrors are its own.
            run.environment.soleCamera =
                (mirrors ? !shared : VRSLBenchmarkScene.RenderingCameraCount() <= 1)
                && AllRowsUsedTheSweepsCamera(run);

            if (VRSLBenchmarkScene.SuppressedCameraCount > 0)
                run.Note($"{VRSLBenchmarkScene.SuppressedCameraCount} other camera(s) were switched off for the "
                       + "matrix, so these figures are of the sweep's own view rather than "
                       + "of every camera in the frame together. They are put back "
                       + "afterwards, and a run captured while sharing the frame is not "
                       + "comparable with this one.");

            completed = true;
            }
            finally
            {
                quality?.Restore();
                if (manager != null)
                {
                    manager.forceOwnNormals     = forceOwnNormalsWas;
                    manager.secondaryCameraMode = policyWas;
                }
                VRSLBenchmarkScene.RemoveSecondaryCameras(mirrorCameras);
                VRSLBenchmarkScene.RestoreCameras();
                camera.targetTexture = null;
                if (target != null) { target.Release(); UnityEngine.Object.DestroyImmediate(target); }
#if UNITY_EDITOR
                // Leaving play mode discards the sweep's scene, but the directional
                // lights it switched off may not have been its own. A player's copy of
                // the scene was built with them already off and holds nothing of
                // anybody else's to put back.
                VRSLBenchmarkScene.RestoreScene();
#endif
                if (!completed)
                    outcome.error = "The sweep stopped before it finished. The log holds "
                                  + "the exception; nothing was written.";
            }
            if (completed) outcome.run = run;
        }

        /// <summary>Extra cameras the mirror matrix renders beside the main one:
        /// none, one, and three, for one, two and four cameras in the frame.</summary>
        public static readonly int[] MirrorCounts = { 0, 1, 3 };

        /// <summary>The policies the mirror matrix measures, in the order the
        /// inspector lists them.</summary>
        public static readonly SecondaryCameraMode[] MirrorPolicies =
        {
            SecondaryCameraMode.Match, SecondaryCameraMode.Reduced,
            SecondaryCameraMode.SurfaceOnly, SecondaryCameraMode.Skip,
        };

        /// <summary>Fixture count and camera variant the mirror matrix holds still.
        /// One of each: the axis under test is the cameras, and the standard sweep
        /// already covers the other two.</summary>
        public const int MirrorFixtures = 50;
        public const VRSLBenchmarkScene.CameraVariant MirrorVariant =
            VRSLBenchmarkScene.CameraVariant.InsideCones;

        /// <summary>The scene levels the mirror matrix runs at. Both that have a
        /// level below them, since that is what Reduced does.</summary>
        static readonly VRSLQuality[] MirrorLevels = { VRSLQuality.Standard, VRSLQuality.High };

        /// <summary>
        /// The mirror matrix: with no mirrors, then with one and three at each policy,
        /// at each scene level. Rows carry how many cameras rendered, under which
        /// policy, and the level the mirrors rendered at, which under Reduced is one no
        /// inspector shows.
        /// </summary>
        static IEnumerator RunMirrors(VRSLBenchmarkSettings settings, VRSLBenchmarkRun run,
                                      GameObject root, Camera camera, VRSL_URPLightManager manager,
                                      VRSLQualityPreset.Session quality,
                                      VRSLBenchmark.DeterminismScope determinism,
                                      List<Camera> mirrorCameras, Action<bool> sharedFrame)
        {
            int fixtures = VRSLBenchmarkScene.SetActiveFixtures(root, MirrorFixtures);
            VRSLBenchmarkScene.PoseCamera(camera, MirrorVariant);
            run.Note($"Mirror rows: each mirror renders the rig from the far side of the truss "
                   + $"into its own {VRSLBenchmarkScene.CaptureWidth}x{VRSLBenchmarkScene.CaptureHeight} "
                   + "texture, before the main camera, so the tile figures are the main view's "
                   + "and a mirror is one more full view of the same scene.");

            int done = 0;
            int total = MirrorLevels.Length * (1 + (MirrorCounts.Length - 1) * MirrorPolicies.Length);
            var keep = new List<Camera>();

            foreach (var level in MirrorLevels)
            {
                quality.Apply(level);
                foreach (int count in MirrorCounts)
                {
                    VRSLBenchmarkScene.RemoveSecondaryCameras(mirrorCameras);
                    mirrorCameras.AddRange(VRSLBenchmarkScene.AddSecondaryCameras(root, count));
                    keep.Clear();
                    keep.Add(camera);
                    keep.AddRange(mirrorCameras);

                    // With no mirrors the policy decides nothing, so one row rather
                    // than four identical ones.
                    var policies = count == 0 ? new[] { SecondaryCameraMode.Match } : MirrorPolicies;
                    foreach (var policy in policies)
                    {
                        manager.secondaryCameraMode = policy;
                        var config = new VRSLRowConfig
                        {
                            scene            = "standard",
                            fixtureCount     = fixtures,
                            cameraVariant    = MirrorVariant.ToString(),
                            quality          = level.ToString(),
                            secondaryCameras = count,
                            secondaryPolicy  = count > 0 ? policy.ToString() : "",
                        };
                        Debug.Log($"[VRSL sweep] {config} ({++done} of {total})");

                        VRSLBenchmarkScene.SuppressOtherCameras(keep);
                        determinism.Reassert();
                        sharedFrame(VRSLBenchmarkScene.RenderingCameraCount() > keep.Count);

                        var capture = VRSLBenchmark.CaptureRow(
                            settings, config, run, expectedTileCamera: camera);
                        while (capture.MoveNext()) yield return capture.Current;

                        sharedFrame(VRSLBenchmarkScene.RenderingCameraCount() > keep.Count);
                        if (count > 0 && run.rows.Count > 0)
                        {
                            var rendered = manager.QualityFor(mirrorCameras[0]);
                            run.rows[run.rows.Count - 1].counters.secondaryQuality =
                                rendered.HasValue ? rendered.Value.ToString()
                                                  : policy == SecondaryCameraMode.Skip ? "skipped" : "";
                        }
                    }
                    // The policy the sweep's own camera measures under. Set back here
                    // rather than left at whatever the last row used, since the next
                    // count's first row is Match anyway and a Skip left behind would
                    // skip the sweep's camera too.
                    manager.secondaryCameraMode = SecondaryCameraMode.Match;
                }
            }
            VRSLBenchmarkScene.RemoveSecondaryCameras(mirrorCameras);
        }

        /// <summary>Whether every row's tile figures came from the sweep's own camera.
        /// Rows where the cull did not run are not evidence either way and are
        /// skipped.</summary>
        static bool AllRowsUsedTheSweepsCamera(VRSLBenchmarkRun run)
        {
            foreach (var row in run.rows)
                if (!string.IsNullOrEmpty(row.counters.tileCamera)
                 && !row.counters.tileCameraAsExpected) return false;
            return true;
        }

        /// <summary>
        /// Say when something other than the sweep's camera was also rendering, and so
        /// the timings are of more than the sweep's view.
        /// </summary>
        /// <param name="atStart">The count taken before the matrix, which is not
        /// enough on its own: a host that starts its own camera during the run passes
        /// that check and fails this one.</param>
        static void ReportOtherCameras(VRSLBenchmarkRun run, int atStart)
        {
            int most = Mathf.Max(atStart, VRSLBenchmarkScene.RenderingCameraCount());
            if (most <= 1) return;

            run.Note($"{most} cameras were rendering, not just the sweep's own"
                   + (atStart > 1 ? "" : " — the others came up during the run, after the "
                                       + "count taken before the matrix")
                   + ". Every VRSL pass runs once per camera, so these timings are of all "
                   + "of them together rather than of the view each row is labelled with.");
        }

        /// <summary>
        /// Say which camera produced the tile figures, and complain when it is not the
        /// sweep's own.
        /// </summary>
        /// <remarks>
        /// Measured 2026-08-31: in a player every row's counters came from the host's
        /// <c>Main Camera</c> at a 34x26 grid, while the sweep rendered its own camera
        /// into a 1920x1080 target that would have been 120x68. The figures were
        /// plausible throughout, and the camera variant — the axis one of these rows
        /// exists to separate — never reached them at all.
        /// </remarks>
        static void ReportTileCamera(VRSLBenchmarkRun run, Camera own)
        {
            // Judged on the flag the capture set by comparing camera objects, not by
            // matching the name here. Unity does not make camera names unique, so a
            // host camera called what the sweep's camera is called would read as local
            // — which is the reading this note exists to prevent.
            var foreign = new HashSet<string>();
            foreach (var row in run.rows)
            {
                if (row.counters.tileCameraAsExpected) continue;
                if (string.IsNullOrEmpty(row.counters.tileCamera)) continue;   // cull did not run
                foreign.Add(row.counters.tileCamera);
            }

            if (foreign.Count == 0) return;

            run.Note($"The lights-per-tile figures describe {string.Join(", ", foreign)}, "
                   + $"not the sweep's own {(own != null ? own.name : "camera")}. One cull "
                   + "pass serves every camera in the frame and the last record wins, so "
                   + "these counters are of a view this run did not pose — the camera "
                   + "variant on each row did not reach them. Treat the tile figures as "
                   + "being about the other camera's view of this scene.");
        }
    }
}
