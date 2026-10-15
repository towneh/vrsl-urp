using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace VRSL.URP.Tests
{
    /// <summary>
    /// What a scene does with a channel source under each kind of DMX input: read
    /// alone under Stream data, dark when it goes, and on standby under a grid. N4 is
    /// the regression that matters most: every existing scene reads a grid.
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
        static Vector3[] Colours(VRSL_DMXURPLightManager.VRSLLightData[] d)
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
        public IEnumerator N3_clearing_the_source_under_stream_data_darkens_the_lights_within_a_frame()
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
                Assert.IsTrue(rig.ReadLights().Any(l => l.colorAndIntensity.w > 0f),
                    "nothing lit from the source, so going dark would prove nothing");

                rig.Source.enabled = false;         // the component clears the manager in OnDisable
                yield return rig.Step(2);

                Assert.AreEqual(0, rig.Manager.ChannelCount,
                    "the manager is still publishing after its source was disabled");
                Assert.AreEqual(0, rig.Manager.UniverseCount);

                var after = rig.ReadLights();
                for (int i = 0; i < VRSLDMXRig.FixtureCount; i++)
                    Assert.AreEqual(Vector4.zero, after[i].colorAndIntensity,
                        $"fixture {i} still lights with Stream data chosen and nothing sending it: "
                      + "it latched at the buffer's last values or fell back to a grid");
            }
            finally { rig.Dispose(); }
        }

        [UnityTest]
        public IEnumerator N4_a_grid_input_decodes_the_same_with_a_source_on_standby_as_with_none()
        {
            var rig = VRSLDMXRig.Build();
            try
            {
                // Calibration needs a source to identify fixtures by, so the grid
                // baseline is taken afterwards, with the source still attached.
                yield return rig.Step(4);
                rig.Calibrate();
                rig.Manager.SetDmxInput((int)VRSLDmxInput.HorizontalGrid);
                yield return rig.Step(VRSLDMXRig.Frames(1f));

                Assert.AreEqual(0, rig.Manager.ChannelCount, "the source is published on a grid input");
                Assert.IsTrue(ReferenceEquals(rig.Manager.ChannelSource, rig.Source),
                    "choosing a grid detached the source rather than leaving it on standby");
                var baseline = Colours(rig.ReadLights());

                // Every existing scene is a scene with no source, so the standby source
                // going away must change nothing either.
                rig.Manager.ChannelSource = null;
                yield return rig.Step(VRSLDMXRig.Frames(1f));
                var none = Colours(rig.ReadLights());
                for (int i = 0; i < VRSLDMXRig.FixtureCount; i++)
                    Assert.Less(Vector3.Distance(baseline[i], none[i]), SameDecode,
                        $"fixture {i} decodes differently with the source detached than on standby");

                // Stream data, then the grid again. Comparing before against after inside
                // one session is the check a cross-branch capture was reaching for,
                // without needing the other branch to have a working scene to capture from.
                rig.Manager.ChannelSource = rig.Source;
                rig.Manager.SetDmxInput((int)VRSLDmxInput.StreamData);
                yield return rig.Step(VRSLDMXRig.Frames(1f));

                Assert.Greater(rig.Manager.ChannelCount, 0, "the source never took over");
                var driven = Colours(rig.ReadLights());
                Assert.IsTrue(
                    Enumerable.Range(0, VRSLDMXRig.FixtureCount)
                              .Any(i => Vector3.Distance(baseline[i], driven[i]) > SameDecode),
                    "Stream data changed nothing, so this row is not testing the switch");

                rig.Manager.SetDmxInput((int)VRSLDmxInput.HorizontalGrid);
                yield return rig.Step(VRSLDMXRig.Frames(1f));

                var restored = Colours(rig.ReadLights());
                for (int i = 0; i < VRSLDMXRig.FixtureCount; i++)
                    Assert.Less(Vector3.Distance(baseline[i], restored[i]), SameDecode,
                        $"fixture {i} did not return to its grid decode. Every existing scene "
                      + "reads a grid, so this is the regression that matters most");
            }
            finally { rig.Dispose(); }
        }
    }
}
