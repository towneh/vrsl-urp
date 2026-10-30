#if UNITY_EDITOR
using System.Collections;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace VRSL.URP.Tests
{
    /// <summary>
    /// The per-light beam switch: a light with its beam off lights surfaces as before
    /// and the volumetric pass skips it before any stepping. Rows V18 to V22 of
    /// TESTING.md; V23 is the AudioLink light's, in the AudioLink test assembly.
    /// </summary>
    class VRSLBeamSwitchTests : VRSLDMXTest
    {
        const int ImageSize    = 512;
        const int WarmUpFrames = 4;

        static bool OutOfHaze(VRSL_DMXURPLightManager.VRSLLightData l)
            => Mathf.Repeat(l.directionAndType.w, 8f) >= 4f;

        static float GoboSlice(VRSL_DMXURPLightManager.VRSLLightData l)
            => Mathf.Floor(l.directionAndType.w * 0.125f) - 1f;

        static float LightType(VRSL_DMXURPLightManager.VRSLLightData l)
            => Mathf.Repeat(l.directionAndType.w, 4f);

        static void SetBeams(VRSLDMXRig rig, bool on)
        {
            foreach (var f in rig.Fixtures) f.Beams = on;
        }

        /// <summary>One frame of counters from the rig's manager. Two ticks from
        /// request to result; three frames leaves a margin.</summary>
        static IEnumerator Collect(VRSLDMXRig rig)
        {
            rig.Manager.VolumetricStats.Request();
            yield return null; rig.RenderFrame();
            yield return null; rig.RenderFrame();
            yield return null; rig.RenderFrame();
        }

        static IEnumerator WarmUp(VRSLDMXRig rig)
        {
            for (int i = 0; i < WarmUpFrames; i++) { yield return null; rig.RenderFrame(); }
        }

        static string Counters(VRSLVolumetricStats s)
            => $"[valid {s.Valid}, frame {s.Frame}, pixels {s.Pixels}, marched {s.LightsMarched}, "
             + $"steps {s.Steps}, skipped {s.LightsSkipped}]";

        /// <summary>V18. With every beam off the frame is the surface-only frame: the
        /// floor, the gobos and the colours as they were, nothing in the air. The
        /// surface-only reference is the same rig with the beam strength at a value
        /// the visibility bound rejects outright, so both frames run the same passes
        /// at the same level.</summary>
        [UnityTest]
        public IEnumerator V18_a_light_with_its_beam_off_lights_the_floor_as_before_and_adds_nothing_in_the_haze()
        {
            Texture2D beamsOn = null, beamsOff = null, surfaceOnly = null;
            var rig = VRSLDMXRig.Build(targetSize: ImageSize);
            try
            {
                rig.Source.pattern = VRSL_SyntheticDMXChannelSource.Pattern.Fixtures;
                rig.Source.speed   = 0f;
                rig.Manager.enabled = false;
                rig.Manager.enabled = true;
                rig.Manager.quality = VRSLQuality.Standard;
                rig.FreezeForImageCapture();

                yield return WarmUp(rig);
                beamsOn = VRSLImageCompare.Read(rig.Target);

                SetBeams(rig, false);
                yield return WarmUp(rig);
                beamsOff = VRSLImageCompare.Read(rig.Target);

                SetBeams(rig, true);
                rig.Manager.volumetricIntensity = 1e-12f;
                yield return WarmUp(rig);
                surfaceOnly = VRSLImageCompare.Read(rig.Target);

                var beams = VRSLImageCompare.Compare(beamsOn, surfaceOnly);
                Debug.Log($"[V18] beams on against surface only: {beams}");
                Assert.IsFalse(beams.SizeMismatch);
                Assert.Greater(beams.DifferingPixels, beams.TotalPixels / 100,
                    $"the beams changed under 1% of the frame ({beams}), so the row cannot see a beam");

                var off = VRSLImageCompare.Compare(beamsOff, surfaceOnly);
                Debug.Log($"[V18] beams off against surface only: {off}");
                Assert.IsFalse(off.SizeMismatch);
                Assert.LessOrEqual(off.Max, VRSLImageCompare.Threshold,
                    $"with every beam off the frame is not the surface-only frame ({off}): a beam is "
                  + "still drawn, or switching it off changed how the light lands");
            }
            finally
            {
                rig.Dispose();
                if (beamsOn != null)     Object.Destroy(beamsOn);
                if (beamsOff != null)    Object.Destroy(beamsOff);
                if (surfaceOnly != null) Object.Destroy(surfaceOnly);
            }
        }

        /// <summary>V19. A light with its beam off never reaches the march: nothing
        /// marched, no steps, and nothing put down to the visibility bound either,
        /// since the light was never a candidate. Switched back on, it is marched again.</summary>
        [UnityTest]
        public IEnumerator V19_a_light_with_its_beam_off_is_never_marched()
        {
            var rig = VRSLDMXRig.Build();
            try
            {
                rig.PointBeamsDown();
                rig.FreezeForImageCapture();
                rig.Source.pattern = VRSL_SyntheticDMXChannelSource.Pattern.Ramp;
                yield return rig.Step(8);

                yield return Collect(rig);
                var lit = rig.Manager.VolumetricStats.Last;
                Assert.IsTrue(lit.Valid && lit.LightsMarched > 0, "the lit rig marched nothing " + Counters(lit));

                SetBeams(rig, false);
                yield return Collect(rig);
                var off = rig.Manager.VolumetricStats.Last;
                Assert.IsTrue(off.Valid && off.Frame > lit.Frame, "the beams-off frame was not collected");
                Assert.AreEqual(0, off.LightsMarched, $"{off.LightsMarched} light(s) with the beam off were marched " + Counters(off));
                Assert.AreEqual(0, off.Steps, "steps were taken for lights with the beam off " + Counters(off));
                Assert.AreEqual(0, off.LightsSkipped,
                    "lights with the beam off reached the visibility bound, so they paid for the span and "
                  + "the estimate before being dropped " + Counters(off));
                Assert.Greater(off.Pixels, 0, "no pixel was counted, so the pass did not run " + Counters(off));

                SetBeams(rig, true);
                yield return Collect(rig);
                var again = rig.Manager.VolumetricStats.Last;
                Assert.AreEqual(lit.LightsMarched + lit.LightsSkipped, again.LightsMarched + again.LightsSkipped,
                    "with the beams back on a different number of lights reached the loop " + Counters(again));
            }
            finally { rig.Dispose(); }
        }

        /// <summary>V20. Set while the world runs, the switch is in the light record
        /// after one frame, both ways, and leaves the type and the gobo slice alone.</summary>
        [UnityTest]
        public IEnumerator V20_the_switch_reaches_the_light_record_the_next_frame()
        {
            using var rig = VRSLDMXRig.Build();
            rig.Source.pattern = VRSL_SyntheticDMXChannelSource.Pattern.Fixtures;
            rig.Source.speed   = 0f;
            yield return rig.Step(4);

            var before = rig.ReadRaw();
            Assert.Greater(before.Length, 0, "the rig has no lights");
            foreach (var l in before) Assert.IsFalse(OutOfHaze(l), $"a light starts out of the haze: {l.directionAndType}");

            SetBeams(rig, false);
            yield return rig.Step(1);
            var off = rig.ReadRaw();
            for (int i = 0; i < off.Length; i++)
            {
                Assert.IsTrue(OutOfHaze(off[i]), $"light {i} is still in the haze a frame after its beam went off: {off[i].directionAndType}");
                Assert.AreEqual(LightType(before[i]), LightType(off[i]), 1e-3f, $"light {i} changed type");
                Assert.AreEqual(GoboSlice(before[i]), GoboSlice(off[i]), 1e-3f, $"light {i} changed gobo");
            }

            SetBeams(rig, true);
            yield return rig.Step(1);
            foreach (var l in rig.ReadRaw())
                Assert.IsFalse(OutOfHaze(l), $"a light is still out of the haze a frame after its beam came back: {l.directionAndType}");
        }

        /// <summary>V21. Every shipped DMX realtime light has its beam on, including
        /// those saved before the switch existed, which carry no value for it.</summary>
        [Test]
        public void V21_every_shipped_DMX_realtime_light_has_its_beam_on()
        {
            int lights = 0, unsaved = 0;
            var key = new Regex(@"^\s+beams:", RegexOptions.Multiline);
            foreach (var guid in UnityEditor.AssetDatabase.FindAssets("t:Prefab", new[] { "Packages/town.mr.vrsl-urp/Runtime/Prefabs" }))
            {
                string path = UnityEditor.AssetDatabase.GUIDToAssetPath(guid);
                var prefab  = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(path);
                var found   = prefab.GetComponentsInChildren<VRSLDMXRealtimeLight>(true);
                if (found.Length == 0) continue;
                if (!key.IsMatch(System.IO.File.ReadAllText(System.IO.Path.GetFullPath(path)))) unsaved++;
                foreach (var f in found)
                {
                    lights++;
                    Assert.IsTrue(f.Beams, $"{path}: {f.name} ships with its beam off");
                }
            }
            Debug.Log($"[V21] {lights} realtime light(s), {unsaved} prefab(s) saved without the field");
            Assert.Greater(lights, 0, "no shipped prefab carries a DMX realtime light");
            Assert.Greater(unsaved, 0,
                "every prefab now saves the field, so this row no longer sees a light saved before it "
              + "existed; point it at a scene saved by an older release");
        }

        /// <summary>V22. A discoball follows Dots In The Haze and nothing else: off it
        /// is not marched, on it is, and the beam switch on the same light changes
        /// neither.</summary>
        [UnityTest]
        public IEnumerator V22_a_discoball_follows_its_own_switch()
        {
            using var rig = VRSLDMXRig.Build(fixtures: 1);
            rig.Source.pattern = VRSL_SyntheticDMXChannelSource.Pattern.Fixtures;
            rig.Source.speed   = 0f;
            rig.Manager.quality = VRSLQuality.Standard;
            rig.FreezeForImageCapture();
            foreach (var f in rig.Fixtures) f.maxIntensity = 0f;
            var ball = VRSLDiscoballTests.Hang(rig, spinDegPerSec: 0f);
            yield return rig.Step(4);

            yield return Collect(rig);
            var dotsOff = rig.Manager.VolumetricStats.Last;
            Assert.IsTrue(dotsOff.Valid, "no counters came back " + Counters(dotsOff));
            Assert.AreEqual(0, dotsOff.LightsMarched, "the ball was marched with its dots out of the haze " + Counters(dotsOff));

            ball.discoballBeams = true;
            rig.Manager.MarkConfigDirty();
            yield return Collect(rig);
            var dotsOn = rig.Manager.VolumetricStats.Last;
            Assert.Greater(dotsOn.LightsMarched, 0, "the ball was not marched with its dots in the haze " + Counters(dotsOn));

            ball.Beams = false;
            yield return Collect(rig);
            var beamOff = rig.Manager.VolumetricStats.Last;
            Assert.AreEqual(dotsOn.LightsMarched, beamOff.LightsMarched,
                "the beam switch changed a discoball's march; the ball follows Dots In The Haze " + Counters(beamOff));

            ball.discoballBeams = false;
            ball.Beams = true;
            rig.Manager.MarkConfigDirty();
            yield return Collect(rig);
            var back = rig.Manager.VolumetricStats.Last;
            Assert.AreEqual(0, back.LightsMarched, "the beam switch put a discoball's dots in the haze " + Counters(back));
        }
    }
}
#endif
