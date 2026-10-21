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
    /// look. Rows M5, M7 to M12 of TESTING.md.
    /// </summary>
    class VRSLCameraPolicyTests : VRSLDMXTest
    {
        const float Tau = 2f * Mathf.PI;

        static float SpinOf(VRSL_DMXURPLightManager.VRSLLightData d) => d.spotParams.w;

        static Vector3 Rgb(VRSL_DMXURPLightManager.VRSLLightData d)
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

                // A texture camera registered as the player's view is exempt from the
                // policy, and still skipped where its texture is one the manager reads.
                VRSLCameraFilter.RegisterMainView(cam);
                var registered = VRSLCameraFilter.Evaluate(cam, SecondaryCameraMode.Skip, VRSLQuality.High, null);
                Assert.IsTrue(registered.Render && registered.Volumetrics, "a registered main view renders in full under Skip");
                Assert.AreEqual(VRSLQuality.High, registered.Quality, "and at the scene's level");
                Assert.IsFalse(VRSLCameraFilter.Evaluate(cam, SecondaryCameraMode.Match, VRSLQuality.High,
                                                         new List<Texture> { target }).Render,
                    "a registered main view rendering into a texture the manager consumes is still skipped");
                VRSLCameraFilter.UnregisterMainView(cam);
                Assert.IsFalse(VRSLCameraFilter.Evaluate(cam, SecondaryCameraMode.Skip, VRSLQuality.High, null).Render,
                    "unregistering puts the policy back in charge");
            }
            finally
            {
                // Here rather than beside the assertions they follow: the filter's
                // registries are static, and a failed assertion would otherwise leave
                // a destroyed camera registered for every later row in the session.
                VRSLCameraFilter.UnregisterDataReader(cam);
                VRSLCameraFilter.UnregisterMainView(cam);
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
        public IEnumerator M5_a_mirror_under_Reduced_keeps_its_beams_at_a_lower_price_and_leaves_the_main_view_alone()
        {
            Texture2D mainMatch = null, mainReduced = null;
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
                mainMatch   = VRSLImageCompare.Read(rig.Target);
                mirrorMatch = VRSLImageCompare.Read(mirror.Target);
                Assert.AreEqual(VRSLQuality.Standard, rig.Manager.QualityFor(mirror.Camera),
                    "under Match the mirror renders at the scene's level");
                yield return CollectFromMirror(rig, mirror);
                var atMatch = rig.Manager.VolumetricStats.Last;

                rig.Manager.secondaryCameraMode = SecondaryCameraMode.Reduced;
                for (int i = 0; i < 10; i++) { yield return null; rig.RenderFrame(); }
                mainReduced   = VRSLImageCompare.Read(rig.Target);
                mirrorReduced = VRSLImageCompare.Read(mirror.Target);
                Assert.AreEqual(VRSLQuality.Low, rig.Manager.QualityFor(mirror.Camera),
                    "under Reduced a Standard scene's mirror renders at Low");
                Assert.AreEqual(VRSLQuality.Standard, rig.Manager.QualityFor(rig.Camera),
                    "the main view stays at the scene's level");
                yield return CollectFromMirror(rig, mirror);
                var atReduced = rig.Manager.VolumetricStats.Last;

                rig.Manager.secondaryCameraMode = SecondaryCameraMode.SurfaceOnly;
                for (int i = 0; i < 10; i++) { yield return null; rig.RenderFrame(); }
                mirrorSurface = VRSLImageCompare.Read(mirror.Target);

                var main = VRSLImageCompare.Compare(mainMatch, mainReduced);
                Debug.Log($"[M5] main view, Match vs Reduced: {main}");
                Assert.IsFalse(main.SizeMismatch);
                Assert.LessOrEqual(main.Max, VRSLImageCompare.Threshold,
                    $"the policy changed the main view ({main}); it is only about secondary cameras");

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
                foreach (var t in new[] { mainMatch, mainReduced, mirrorMatch, mirrorReduced, mirrorSurface })
                    if (t != null) Object.DestroyImmediate(t);
                rig.Dispose();
            }
        }

        // ── M7: four cameras, one clock ────────────────────────────────────

        [UnityTest]
        public IEnumerator M7_four_cameras_advance_spin_and_movement_at_the_one_camera_rate()
        {
            const int span = 600;                                   // 10 s of captured time
            VRSL_DMXURPLightManager.VRSLLightData[] alone = null, together = null;
            VRSL_DMXURPLightManager.VRSLLightData[] before = null;

            yield return Run(0, null, r => alone = r.ReadLights());
            // The baseline for the rate comes from the same run as the end reading, so
            // the claim rests on this rig alone rather than on two rigs starting in
            // step; the one-camera rig is compared against separately below.
            yield return Run(3, r => before = r.ReadLights(), r => together = r.ReadLights());

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

#if UNITY_EDITOR
        // ── M10: a mirror VRSL lights nothing in ───────────────────────────

        /// <summary>
        /// M10. In a mirror under <c>Skip</c>, rendered after the rig's view with the
        /// realtime fixtures on, the shader-driven fixtures glow with the grid as they do
        /// in the same mirror in a scene with no realtime fixtures. The mirror gets no VRSL
        /// lighting either way, so only the fixtures' own read of the grid differs.
        /// </summary>
        [UnityTest]
        public IEnumerator M10_shader_driven_fixtures_read_the_grid_in_a_skipped_mirror()
        {
            var prefabs = VRSLRuntimeEnableTests.ShaderDrivenPrefabs;
            var glow = new Dictionary<VRSLRuntimeEnableTests.Realtime, float[]>();
            foreach (var realtime in new[] { VRSLRuntimeEnableTests.Realtime.NeverOn,
                                             VRSLRuntimeEnableTests.Realtime.OnThroughout })
            {
                float[] lit = null, dark = null;
                yield return VRSLRuntimeEnableTests.CaptureShaderDriven(realtime, VRSLGridLayout.Horizontal, true,
                                                                       g => lit = g, SecondaryCameraMode.Skip);
                yield return VRSLRuntimeEnableTests.CaptureShaderDriven(realtime, VRSLGridLayout.Horizontal, false,
                                                                       g => dark = g, SecondaryCameraMode.Skip);
                var difference = new float[lit.Length];
                for (int i = 0; i < lit.Length; i++) difference[i] = lit[i] - dark[i];
                glow[realtime] = difference;
                Debug.Log($"[M10] Skip mirror, realtime fixtures {realtime}: glow "
                        + string.Join(", ", System.Linq.Enumerable.Select(difference, (d, i) => $"{prefabs[i]} {d:F2}")));
            }

            for (int i = 0; i < prefabs.Length; i++)
            {
                float none = glow[VRSLRuntimeEnableTests.Realtime.NeverOn][i];
                float on   = glow[VRSLRuntimeEnableTests.Realtime.OnThroughout][i];
                Assert.Greater(none, 2f,
                    $"{prefabs[i]} doesn't glow in the mirror in a scene with no realtime fixtures, "
                  + "so the row can't tell anything apart");
                Assert.GreaterOrEqual(on, 0.5f * none,
                    $"{prefabs[i]} glows {on:F2} in a skipped mirror with realtime fixtures on, against {none:F2} "
                  + "with none: it read a black grid there");
            }
        }

        /// <summary>
        /// M11. A DMX screen reader camera, rendered after the rig's view with the
        /// realtime fixtures on, is not handed the grid: the shader-driven fixtures in its
        /// view stay dark, so their glow can't reach the DMX input it renders. The same
        /// fixtures seen from a mirror under <c>Skip</c> are the control.
        /// </summary>
        [UnityTest]
        public IEnumerator M11_a_dmx_reader_camera_is_not_handed_the_grid()
        {
            var prefabs = VRSLRuntimeEnableTests.ShaderDrivenPrefabs;
            const VRSLRuntimeEnableTests.Realtime on = VRSLRuntimeEnableTests.Realtime.OnThroughout;
            var glow = new Dictionary<bool, float[]>();
            foreach (bool reader in new[] { false, true })
            {
                float[] lit = null, dark = null;
                yield return VRSLRuntimeEnableTests.CaptureShaderDriven(on, VRSLGridLayout.Horizontal, true,
                                                                       g => lit = g, SecondaryCameraMode.Skip, reader);
                yield return VRSLRuntimeEnableTests.CaptureShaderDriven(on, VRSLGridLayout.Horizontal, false,
                                                                       g => dark = g, SecondaryCameraMode.Skip, reader);
                var difference = new float[lit.Length];
                for (int i = 0; i < lit.Length; i++) difference[i] = lit[i] - dark[i];
                glow[reader] = difference;
                Debug.Log($"[M11] {(reader ? "DMX reader" : "Skip mirror")}: glow "
                        + string.Join(", ", System.Linq.Enumerable.Select(difference, (d, i) => $"{prefabs[i]} {d:F2}")));
            }

            for (int i = 0; i < prefabs.Length; i++)
            {
                Assert.Greater(glow[false][i], 2f,
                    $"{prefabs[i]} doesn't glow in the mirror, so the row can't tell anything apart");
                Assert.Less(glow[true][i], 0.1f * glow[false][i],
                    $"{prefabs[i]} glows {glow[true][i]:F2} in a DMX reader camera, against {glow[false][i]:F2} in a "
                  + "mirror: the reader was handed the grid, so the fixture's glow can feed the DMX input");
            }
        }

        /// <summary>
        /// M12. A lens flare tests its occlusion against the camera's depth texture from the
        /// transparent pass. In a camera the policy skips, nothing asks URP for that depth
        /// any earlier, so on a renderer that copies it after transparents the flare reads
        /// depth not yet written this frame and fades out. The flasher, the one of Z7's
        /// fixtures with a flare, seen from a mirror under <c>Skip</c> with the pipeline's
        /// Depth Texture on and the renderers copying depth after opaques, then after transparents, then after transparents
        /// with the mirror under <c>Match</c>, whose VRSL passes ask for depth first. The
        /// rig's depth priming doesn't move this: its prepass fills the camera's depth
        /// attachment, and the texture the flare samples still comes from the copy.
        /// </summary>
        [UnityTest]
        public IEnumerator M12_flares_in_a_skipped_mirror_need_depth_copied_after_opaques()
        {
            var prefabs = VRSLRuntimeEnableTests.ShaderDrivenPrefabs;
            int flasher = System.Array.FindIndex(prefabs, p => p.Contains("Flasher"));
            Assert.GreaterOrEqual(flasher, 0, "Z7's fixtures carry no flasher");
            const VRSLRuntimeEnableTests.Realtime none = VRSLRuntimeEnableTests.Realtime.NeverOn;
            var cases = new[]
            {
                ("Skip, after opaques",       SecondaryCameraMode.Skip,  UnityEngine.Rendering.Universal.CopyDepthMode.AfterOpaques),
                ("Skip, after transparents",  SecondaryCameraMode.Skip,  UnityEngine.Rendering.Universal.CopyDepthMode.AfterTransparents),
                ("Match, after transparents", SecondaryCameraMode.Match, UnityEngine.Rendering.Universal.CopyDepthMode.AfterTransparents),
            };
            var glow = new float[cases.Length];
            for (int c = 0; c < cases.Length; c++)
            {
                var (what, mode, copy) = cases[c];
                float[] lit = null, dark = null;
                yield return VRSLRuntimeEnableTests.CaptureShaderDriven(none, VRSLGridLayout.Horizontal, true,
                                                                       g => lit = g, mode, copyDepth: copy);
                yield return VRSLRuntimeEnableTests.CaptureShaderDriven(none, VRSLGridLayout.Horizontal, false,
                                                                       g => dark = g, mode, copyDepth: copy);
                glow[c] = lit[flasher] - dark[flasher];
                Debug.Log($"[M12] {what}: {prefabs[flasher]} glow {glow[c]:F2}");
            }

            Assert.Greater(glow[0], 1.5f * glow[1],
                $"{prefabs[flasher]} glows {glow[0]:F2} in a skipped mirror with depth copied after opaques and "
              + $"{glow[1]:F2} after transparents. If they match, a flare in a skipped camera now sees this frame's "
              + "depth on either renderer setting, and the limitation the wiki and TESTING describe is gone");
            Assert.Greater(glow[2], 1.5f * glow[1],
                $"{prefabs[flasher]} glows {glow[2]:F2} in a mirror under Match with depth copied after transparents, "
              + $"against {glow[1]:F2} under Skip: VRSL's passes on the camera no longer bring the depth copy forward");
        }
#endif
    }
}
