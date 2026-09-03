using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace VRSL.URP.Tests
{
    /// <summary>
    /// The secondary-camera rows: what a mirror is lit at, what a mirror costs, and
    /// that the decode and the integrators stay once per frame however many cameras
    /// look. Rows M5, M7, M8 and M9 of TESTING.md.
    /// </summary>
    class VRSLCameraPolicyTests : VRSLDMXTest
    {
        const float Tau = 2f * Mathf.PI;

        static float SpinOf(VRSL_URPLightManager.VRSLLightData d) => d.spotParams.w;

        static Vector3 Rgb(VRSL_URPLightManager.VRSLLightData d)
            => new Vector3(d.colorAndIntensity.x, d.colorAndIntensity.y, d.colorAndIntensity.z);

        /// <summary>Copied from N8. The gobo spin rate a channel implies, in rad/s;
        /// null below the 0.01 gate where the kernel resets the phase.</summary>
        static float? SpinRate(int absChannel)
        {
            float dmx = VRSLDMXRig.RampAt(VRSLDMXRig.SpinChannel(absChannel));
            if (dmx < 0.01f) return null;
            return dmx > 0.5f ? -4f * (dmx - 0.5f) : 4f * dmx;
        }

        // ── M9: the policy, as a function ──────────────────────────────────

        static readonly VRSLQuality[] SceneLevels =
            { VRSLQuality.Off, VRSLQuality.Standard, VRSLQuality.High };

        [Test]
        public void M9_each_policy_value_maps_onto_the_decision_it_names()
        {
            var go     = new GameObject("M9 camera");
            var cam    = go.AddComponent<Camera>();
            var target = new RenderTexture(16, 16, 0);
            cam.enabled       = false;
            cam.targetTexture = target;
            try
            {
                foreach (var scene in SceneLevels)
                {
                    var match = VRSLCameraFilter.Evaluate(cam, SecondaryCameraMode.Match, scene, null);
                    Assert.IsTrue(match.Render && match.Volumetrics, $"Match renders everything at {scene}");
                    Assert.AreEqual(scene, match.Quality, "Match keeps the scene's level");

                    var surface = VRSLCameraFilter.Evaluate(cam, SecondaryCameraMode.SurfaceOnly, scene, null);
                    Assert.IsTrue(surface.Render && !surface.Volumetrics, $"SurfaceOnly lights surfaces only at {scene}");
                    Assert.AreEqual(scene, surface.Quality, "SurfaceOnly keeps the scene's level");

                    var skip = VRSLCameraFilter.Evaluate(cam, SecondaryCameraMode.Skip, scene, null);
                    Assert.IsFalse(skip.Render, $"Skip renders nothing at {scene}");

                    var reduced = VRSLCameraFilter.Evaluate(cam, SecondaryCameraMode.Reduced, scene, null);
                    Assert.IsTrue(reduced.Render && reduced.Volumetrics, $"Reduced keeps the beams at {scene}");
                    Assert.AreEqual(VRSLQualityLevel.Below(scene), reduced.Quality,
                        "Reduced is one level below the scene");

                    // The player's view is never a secondary camera, whatever the policy.
                    cam.targetTexture = null;
                    foreach (SecondaryCameraMode mode in System.Enum.GetValues(typeof(SecondaryCameraMode)))
                    {
                        var main = VRSLCameraFilter.Evaluate(cam, mode, scene, null);
                        Assert.IsTrue(main.Render && main.Volumetrics, $"the main view renders in full under {mode}");
                        Assert.AreEqual(scene, main.Quality, $"the main view keeps the scene's level under {mode}");
                    }
                    cam.targetTexture = target;
                }

                // One below each level. Standard steps to Low rather than Off: a mirror
                // with no beams in it is what Reduced exists to avoid.
                Assert.AreEqual(VRSLQuality.Standard, VRSLQualityLevel.Below(VRSLQuality.High));
                Assert.AreEqual(VRSLQuality.Low,      VRSLQualityLevel.Below(VRSLQuality.Standard));
                Assert.AreEqual(VRSLQuality.Off,      VRSLQualityLevel.Below(VRSLQuality.Off));
                Assert.AreEqual(VRSLQuality.Low,      VRSLQualityLevel.Below(VRSLQuality.Low));

                // Low is a beam-drawing level that costs less than Standard on every axis.
                var low = VRSLQualityLevel.For(VRSLQuality.Low);
                var std = VRSLQualityLevel.For(VRSLQuality.Standard);
                Assert.IsTrue(low.Volumetrics, "Low draws beams");
                Assert.Less(low.VolumetricMaxSteps, std.VolumetricMaxSteps, "Low takes fewer steps");
                Assert.Greater(low.VolumetricStepSpacing, std.VolumetricStepSpacing, "Low samples more sparsely");
                Assert.IsFalse(low.ContactShadows, "Low traces no contact shadows");
                Assert.IsFalse(System.Array.IndexOf(VRSLQualityPreset.All, VRSLQuality.Low) >= 0,
                    "Low is not a scene level, so the sweep must not measure it as one");

                // The always-skipped cases are unchanged by the policy.
                cam.cameraType = CameraType.Reflection;
                Assert.IsFalse(VRSLCameraFilter.Evaluate(cam, SecondaryCameraMode.Match, VRSLQuality.High, null).Render,
                    "a reflection probe is skipped under every policy");
                cam.cameraType = CameraType.Game;
                Assert.IsFalse(VRSLCameraFilter.Evaluate(cam, SecondaryCameraMode.Match, VRSLQuality.High,
                                                         new List<Texture> { target }).Render,
                    "a camera rendering into a texture the manager consumes is skipped");
                VRSLCameraFilter.RegisterDataReader(cam);
                Assert.IsFalse(VRSLCameraFilter.Evaluate(cam, SecondaryCameraMode.Match, VRSLQuality.High, null).Render,
                    "a registered data reader is skipped");
                VRSLCameraFilter.UnregisterDataReader(cam);
            }
            finally
            {
                target.Release();
                Object.DestroyImmediate(target);
                Object.DestroyImmediate(go);
            }
        }

        // ── M5: a mirror under Reduced ─────────────────────────────────────

        /// <summary>Warm frames before a capture, as the image rows use.</summary>
        const int WarmUpFrames = 120;

        /// <summary>One frame of counters from the mirror alone. Two ticks from
        /// request to result; three frames leaves a margin. The main camera sits
        /// out so the counters describe one view.</summary>
        static IEnumerator CollectFromMirror(VRSLDMXRig rig, VRSLDMXRig.SecondaryCamera mirror)
        {
            rig.Manager.VolumetricStats.Request();
            for (int i = 0; i < 3; i++)
            {
                yield return null;
                rig.Render(mirror.Camera);
            }
        }

        [UnityTest]
        public IEnumerator M5_a_mirror_under_Reduced_keeps_its_beams_at_a_lower_price()
        {
            // The rig's own camera renders into a texture, so to the policy it is a
            // secondary camera as well: this row is about the mirror, and the main
            // view's independence from the policy is M9's claim (a camera with no
            // target keeps the scene's level under every value) and M5's by hand.
            Texture2D mirrorMatch = null, mirrorReduced = null, mirrorSurface = null;
            var rig = VRSLDMXRig.Build(targetSize: 512);
            try
            {
                var mirror = rig.AddSecondaryCamera(512);

                rig.Source.pattern = VRSL_SyntheticDMXChannelSource.Pattern.Fixtures;
                rig.Source.speed   = 0f;
                rig.Manager.enabled = false;
                rig.Manager.enabled = true;
                rig.Manager.quality             = VRSLQuality.Standard;
                rig.Manager.secondaryCameraMode = SecondaryCameraMode.Match;
                rig.FreezeForImageCapture();

                for (int i = 0; i < WarmUpFrames; i++) { yield return null; rig.RenderFrame(); }
                mirrorMatch = VRSLImageCompare.Read(mirror.Target);
                Assert.AreEqual(VRSLQuality.Standard, rig.Manager.QualityFor(mirror.Camera),
                    "under Match the mirror renders at the scene's level");
                yield return CollectFromMirror(rig, mirror);
                var atMatch = rig.Manager.VolumetricStats.Last;

                rig.Manager.secondaryCameraMode = SecondaryCameraMode.Reduced;
                for (int i = 0; i < 10; i++) { yield return null; rig.RenderFrame(); }
                mirrorReduced = VRSLImageCompare.Read(mirror.Target);
                Assert.AreEqual(VRSLQuality.Low, rig.Manager.QualityFor(mirror.Camera),
                    "under Reduced a Standard scene's mirror renders at Low");
                yield return CollectFromMirror(rig, mirror);
                var atReduced = rig.Manager.VolumetricStats.Last;

                rig.Manager.secondaryCameraMode = SecondaryCameraMode.SurfaceOnly;
                for (int i = 0; i < 10; i++) { yield return null; rig.RenderFrame(); }
                mirrorSurface = VRSLImageCompare.Read(mirror.Target);

                var beams = VRSLImageCompare.Compare(mirrorReduced, mirrorSurface);
                Debug.Log($"[M5] mirror, Reduced vs SurfaceOnly: {beams}");
                Assert.IsFalse(beams.SizeMismatch);
                Assert.Greater(beams.DifferingPixels, 0,
                    "the mirror at Reduced looks the same as with no beams at all, so Reduced "
                  + "dropped them rather than drawing them cheaper");

                var price = VRSLImageCompare.Compare(mirrorMatch, mirrorReduced);
                Debug.Log($"[M5] mirror, Match vs Reduced: {price}");

                // Cheaper, by the counters: the same lights marched over the same pixels,
                // at fewer steps each. No fixture goes missing, because the level changes
                // how a light is stepped and not which lights reach a tile.
                Assert.IsTrue(atMatch.Valid && atReduced.Valid, "no frame of counters came back");
                Assert.Greater(atMatch.LightsMarched, 0, "the mirror saw no beam under Match");
                Assert.AreEqual(atMatch.LightsMarched, atReduced.LightsMarched,
                    atMatch.LightsMarched * 0.01 + 1,
                    "Reduced marched a different set of lights in the mirror; a missing fixture "
                  + "is exactly what the policy must not cause");
                Assert.Less(atReduced.StepsPerLight, atMatch.StepsPerLight,
                    $"Reduced took {atReduced.StepsPerLight:F1} steps per light against Match's "
                  + $"{atMatch.StepsPerLight:F1}, so the mirror is not cheaper");
                Assert.LessOrEqual(atReduced.StepsPerLight, VRSLQualityLevel.For(VRSLQuality.Low).VolumetricMaxSteps,
                    "the mirror marched past Low's ceiling");
                Debug.Log($"[M5] steps per light: Match {atMatch.StepsPerLight:F2}, Reduced {atReduced.StepsPerLight:F2}; "
                        + $"lights marched {atMatch.LightsMarched} / {atReduced.LightsMarched}");
            }
            finally
            {
                foreach (var t in new[] { mirrorMatch, mirrorReduced, mirrorSurface })
                    if (t != null) Object.DestroyImmediate(t);
                rig.Dispose();
            }
        }

        // ── M7: four cameras, one clock ────────────────────────────────────

        [UnityTest]
        public IEnumerator M7_four_cameras_advance_spin_and_movement_at_the_one_camera_rate()
        {
            const int span = 600;                                   // 10 s of captured time
            VRSL_URPLightManager.VRSLLightData[] alone = null, together = null;
            VRSL_URPLightManager.VRSLLightData[] before = null;

            yield return Run(0, r => before = r.ReadLights(), r => alone = r.ReadLights());
            yield return Run(3, null, r => together = r.ReadLights());

            float elapsed = span * VRSLDMXRig.FrameDelta;
            int checkedCount = 0;
            for (int i = 0; i < VRSLDMXRig.FixtureCount; i++)
            {
                int ch = VRSLDMXRig.ChannelOf(i);
                float? rate = SpinRate(ch);
                if (rate == null) continue;

                // The rate its channel asks for, as N8 judges it. An integrator that had
                // drifted into a per-camera pass would advance four times as far.
                float moved    = Mathf.Repeat(SpinOf(together[i]) - SpinOf(before[i]), Tau);
                float expected = Mathf.Repeat(rate.Value * elapsed, Tau);
                float residual = Mathf.Min(Mathf.Abs(moved - expected), Tau - Mathf.Abs(moved - expected));
                Assert.Less(residual, 0.01f,
                    $"ch {ch} advanced {moved:F4} rad in {elapsed:F2} s with four cameras rendering, "
                  + $"expected {expected:F4}. Four times the expected advance is an integrator "
                  + "running once per camera");
                checkedCount++;
            }
            Assert.Greater(checkedCount, 40, "too few fixtures spin for this row to mean anything");

            // And the same frame of light data as one camera produces, movement included.
            for (int i = 0; i < VRSLDMXRig.FixtureCount; i++)
            {
                var a = alone[i]; var b = together[i];
                Assert.AreEqual(a.directionAndType.x, b.directionAndType.x, 1e-3f, $"fixture {i} direction x");
                Assert.AreEqual(a.directionAndType.y, b.directionAndType.y, 1e-3f, $"fixture {i} direction y");
                Assert.AreEqual(a.directionAndType.z, b.directionAndType.z, 1e-3f, $"fixture {i} direction z");
                Assert.AreEqual(SpinOf(a), SpinOf(b), 1e-3f, $"fixture {i} spin phase");
                Assert.IsTrue(Near(Rgb(a), Rgb(b)), $"fixture {i} colour");
            }

            IEnumerator Run(int secondaries, System.Action<VRSLDMXRig> atStart, System.Action<VRSLDMXRig> atEnd)
            {
                var rig = VRSLDMXRig.Build();
                try
                {
                    for (int i = 0; i < secondaries; i++) rig.AddSecondaryCamera(64);
                    // Stepped here rather than through rig.Step: already a level deep.
                    for (int f = 0; f < 4; f++) { yield return null; rig.RenderFrame(); }
                    rig.Calibrate();
                    rig.Source.pattern   = VRSL_SyntheticDMXChannelSource.Pattern.Ramp;
                    rig.Source.universes = 4;
                    for (int f = 0; f < 5; f++) { yield return null; rig.RenderFrame(); }
                    atStart?.Invoke(rig);
                    for (int f = 0; f < span; f++) { yield return null; rig.RenderFrame(); }
                    atEnd(rig);
                }
                finally { rig.Dispose(); }
            }
        }

        // ── M8: a mirror that comes and goes ───────────────────────────────

        [UnityTest]
        public IEnumerator M8_a_mirror_appearing_mid_cue_reads_this_frames_data_and_draws_the_rig()
        {
            var rig = VRSLDMXRig.Build();
            try
            {
                var mirror = rig.AddSecondaryCamera(128);
                yield return rig.Step(4);
                rig.Calibrate();

                // Every channel the same value, moving far each frame, so a decode one
                // frame stale reads as a different colour rather than as a near miss.
                // Strobe and spin off: under Sweep those channels move too.
                rig.Source.pattern    = VRSL_SyntheticDMXChannelSource.Pattern.Sweep;
                rig.Source.speed      = 30f;
                rig.Source.universes  = 4;
                rig.Manager.disableStrobe = true;
                foreach (var f in rig.Fixtures) f.enableGoboSpin = false;
                rig.Manager.MarkConfigDirty();
                yield return rig.Step(3);

                int mirrorFrames = 0;
                for (int frame = 0; frame < 45; frame++)
                {
                    // Main only, mirror only, both — so the mirror is sometimes the first
                    // camera of the frame and sometimes the only one.
                    bool main   = frame % 3 != 1;
                    mirror.Renders = frame % 3 != 0;
                    yield return null;
                    rig.RenderFrame(mainCamera: main);

                    // The bytes the manager uploaded last, which are the ones this frame's
                    // decode must reflect whichever camera triggered it.
                    var published = rig.Manager.PublishedChannels;
                    Assert.Greater(published.Length, 0, "nothing is publishing");
                    var lights = rig.ReadLights();
                    for (int i = 0; i < VRSLDMXRig.FixtureCount; i += 7)
                    {
                        int ch = VRSLDMXRig.ChannelOf(i);
                        float expected = published[VRSLDMXRig.RedChannel(ch) - 1] / 255f;
                        Assert.AreEqual(expected, Rgb(lights[i]).x, Half,
                            $"frame {frame} ({(main ? "main" : "no main")}, {(mirror.Renders ? "mirror" : "no mirror")}): "
                          + $"fixture {i} decoded {Rgb(lights[i]).x:F4} against the published {expected:F4}. "
                          + "A one-frame-old value is a decode that did not run for this frame's cameras");
                    }

                    if (!mirror.Renders) continue;
                    mirrorFrames++;
                    var frameTex = VRSLImageCompare.Read(mirror.Target);
                    try
                    {
                        int lit = 0;
                        foreach (var px in frameTex.GetPixels32())
                            if (px.r > 8 || px.g > 8 || px.b > 8) lit++;
                        Assert.Greater(lit, 0,
                            $"frame {frame}: the mirror rendered black on the frame it appeared, so "
                          + "either the fixture bodies lost their grid textures or the light path skipped it");
                    }
                    finally { Object.DestroyImmediate(frameTex); }
                }
                Assert.Greater(mirrorFrames, 20, "the mirror hardly rendered, so the row judged little");
            }
            finally { rig.Dispose(); }
        }
    }
}
