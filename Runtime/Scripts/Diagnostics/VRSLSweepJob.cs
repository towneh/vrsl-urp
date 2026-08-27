using System;
using System.Collections;
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
        public static IEnumerator Run(VRSLSweepOutcome outcome, Action<VRSLBenchmarkRun> stampEnvironment)
        {
            var settings = new VRSLBenchmarkSettings();
            var run      = new VRSLBenchmarkRun { label = "standard-sweep" };
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
            try
            {
            // Allocated inside the try, and first. The finally below is the only
            // thing that gives the target back, so anything that can throw has to
            // sit after it rather than between it and the try.
            target = new RenderTexture(
                VRSLBenchmarkScene.CaptureWidth, VRSLBenchmarkScene.CaptureHeight, 24)
            { name = "VRSL sweep target" };
            camera.targetTexture = target;

            // Every rendering camera runs the whole VRSL pass chain, so a second one
            // doubles the cost and the counters describe whichever rendered last.
            int cameras = VRSLBenchmarkScene.RenderingCameraCount();
            if (cameras > 1)
                run.Note($"{cameras} cameras are rendering to the screen, not just the "
                       + "sweep's own. Every VRSL pass runs once per camera, so these "
                       + "figures are of all of them together and the counters describe "
                       + "whichever rendered last.");

            quality = VRSLQualityPreset.Session.Begin(manager);
            using var determinism = new VRSLBenchmark.DeterminismScope(settings);

            var warm = VRSLBenchmark.WarmUpSession(settings, null, run);
            while (warm.MoveNext()) yield return warm.Current;
            stampEnvironment(run);
            // The size actually rendered, which is not the screen's.
            run.environment.captureWidth  = VRSLBenchmarkScene.CaptureWidth;
            run.environment.captureHeight = VRSLBenchmarkScene.CaptureHeight;

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

                        var capture = VRSLBenchmark.CaptureRow(settings, config, run);
                        while (capture.MoveNext()) yield return capture.Current;
                    }
                }
            }

            completed = true;
            }
            finally
            {
                quality?.Restore();
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
    }
}
