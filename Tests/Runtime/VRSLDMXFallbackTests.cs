using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace VRSL.URP.Tests
{
    /// <summary>
    /// What a scene does when a channel source is present, and what it goes back
    /// to when there is not one. N4 is the regression that matters most: every
    /// existing scene is a scene with no channel source.
    ///
    /// Rows N2, N3 and N4 of TESTING.md.
    /// </summary>
    class VRSLDMXFallbackTests : VRSLDMXTest
    {
        /// <summary>
        /// Decoded colour only, deliberately not the intensity in <c>w</c>.
        ///
        /// On the no-source path <c>w</c> carries a strobe multiplier read straight
        /// from the CRT, and these rows compare samples taken seconds apart, so a
        /// scene whose chain is running would differ by sample time rather than by
        /// anything the row is asking about. The manager's Disable Strobe is no help:
        /// <c>StrobeValue</c> checks the channel count before it checks that flag, so
        /// with no source publishing the flag is never reached.
        ///
        /// What is given up is intensity coverage in N4. The colour is the decode of
        /// the channel data, which is what the handover can actually break.
        /// </summary>
        static Vector3[] Colours(VRSL_URPLightManager.VRSLLightData[] d)
            => d.Select(x => new Vector3(x.colorAndIntensity.x,
                                         x.colorAndIntensity.y,
                                         x.colorAndIntensity.z)).ToArray();

        /// <summary>Colour arrives through a half, so nothing here compares exactly.
        /// Well below a byte step, so it still separates one channel from its
        /// neighbour.</summary>
        const float SameDecode = 1e-4f;

        [UnityTest]
        public IEnumerator N2_fixtures_light_and_move_from_the_buffer_alone()
        {
            var rig = VRSLDMXRig.Build();
            try
            {
                yield return rig.Step(4);
                rig.Calibrate();
                rig.Source.pattern   = VRSL_SyntheticDMXChannelSource.Pattern.Fixtures;
                rig.Source.universes = 4;
                yield return rig.Step(VRSLDMXRig.Frames(2f));

                var first = rig.ReadLights();
                var dirsA = rig.ReadDirections();
                yield return rig.Step(VRSLDMXRig.Frames(2f));
                var dirsB = rig.ReadDirections();

                int lit = Enumerable.Range(0, VRSLDMXRig.FixtureCount)
                                    .Count(i => first[i].colorAndIntensity.w > 0f);
                Assert.Greater(lit, VRSLDMXRig.FixtureCount / 2,
                    $"only {lit} fixtures lit from the buffer with no CRT chain feeding anything");

                int moved = Enumerable.Range(0, VRSLDMXRig.FixtureCount)
                                      .Count(i => Vector3.Distance(dirsA[i], dirsB[i]) > 1e-3f);
                Assert.Greater(moved, VRSLDMXRig.FixtureCount / 2,
                    $"only {moved} fixtures changed direction; colour, spin, strobe and movement "
                  + "all read the buffer now, so the pattern's pan and tilt should reach them");
            }
            finally { rig.Dispose(); }
        }

        [UnityTest]
        public IEnumerator N3_clearing_the_source_returns_to_the_texture_path_within_a_frame()
        {
            var rig = VRSLDMXRig.Build();
            try
            {
                yield return rig.Step(4);
                rig.Calibrate();
                rig.Source.pattern   = VRSL_SyntheticDMXChannelSource.Pattern.Ramp;
                rig.Source.universes = 4;
                yield return rig.Step(VRSLDMXRig.Frames(1f));

                Assert.Greater(rig.Manager.ChannelCount, 0, "the source never started publishing");
                var published = Colours(rig.ReadLights());

                rig.Source.enabled = false;         // the component clears the manager in OnDisable
                yield return rig.Step(2);

                Assert.AreEqual(0, rig.Manager.ChannelCount,
                    "the manager is still publishing after its source was disabled");
                Assert.AreEqual(0, rig.Manager.UniverseCount);

                var after = Colours(rig.ReadLights());
                bool changed = Enumerable.Range(0, VRSLDMXRig.FixtureCount)
                                         .Any(i => Vector3.Distance(published[i], after[i]) > SameDecode);
                Assert.IsTrue(changed,
                    "the decode did not move when the source went away, so the fixtures latched "
                  + "at the buffer's last values instead of falling back");
            }
            finally { rig.Dispose(); }
        }

        [UnityTest]
        public IEnumerator N4_a_scene_with_no_source_decodes_as_it_did_before_one_was_attached()
        {
            var rig = VRSLDMXRig.Build();
            try
            {
                // Calibration needs a source to identify fixtures by, so the
                // no-source baseline is taken by putting it down again afterwards.
                yield return rig.Step(4);
                rig.Calibrate();
                rig.Manager.ChannelSource = null;
                yield return rig.Step(VRSLDMXRig.Frames(1f));

                Assert.AreEqual(0, rig.Manager.ChannelCount, "nothing should be publishing now");
                var baseline = Colours(rig.ReadLights());

                // Hand over, let it drive the whole patch, then take it away again.
                // Comparing before against after inside one session is the check a
                // cross-branch capture was reaching for, without needing the other
                // branch to have a working scene to capture from.
                rig.Manager.ChannelSource = rig.Source;
                yield return rig.Step(VRSLDMXRig.Frames(1f));

                Assert.Greater(rig.Manager.ChannelCount, 0, "the source never took over");
                var driven = Colours(rig.ReadLights());
                Assert.IsTrue(
                    Enumerable.Range(0, VRSLDMXRig.FixtureCount)
                              .Any(i => Vector3.Distance(baseline[i], driven[i]) > SameDecode),
                    "attaching a source changed nothing, so this row is not testing the handover");

                rig.Manager.ChannelSource = null;
                yield return rig.Step(VRSLDMXRig.Frames(1f));

                var restored = Colours(rig.ReadLights());
                for (int i = 0; i < VRSLDMXRig.FixtureCount; i++)
                    Assert.Less(Vector3.Distance(baseline[i], restored[i]), SameDecode,
                        $"fixture {i} did not return to its no-source decode. Every existing scene "
                      + "is a no-source scene, so this is the regression that matters most");
            }
            finally { rig.Dispose(); }
        }
    }
}
