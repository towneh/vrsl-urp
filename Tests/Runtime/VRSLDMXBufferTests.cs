using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace VRSL.URP.Tests
{
    /// <summary>
    /// The rows that judge what arrives in the channel buffer and what a fixture
    /// decodes out of it. None of them depend on elapsed time.
    ///
    /// Rows N1, N5, N6, N7 and N13 of TESTING.md.
    /// </summary>
    class VRSLDMXBufferTests : VRSLDMXTest
    {
        // Slot-within-universe, the one quantity that differs between a 520- and
        // a 512-strided packing. Ramp is a function of the flat address and reads
        // the same under either, which is why it cannot answer N6.
        static int UniverseSlotValue(int flat, int stride)
        {
            int slot = flat % stride;
            return slot >= VRSLDMX.UsableSlotsPerUniverse ? 0 : slot % 256;
        }

        static Vector3 PredictRamp(int absChannel)
            => new Vector3(VRSL_SyntheticDMXChannelSource.RampValue(absChannel + 6),
                           VRSL_SyntheticDMXChannelSource.RampValue(absChannel + 7),
                           VRSL_SyntheticDMXChannelSource.RampValue(absChannel + 8)) / 255f;

        static Vector3 PredictUniverseSlot(int absChannel, int stride)
            => new Vector3(UniverseSlotValue(absChannel + 6, stride),
                           UniverseSlotValue(absChannel + 7, stride),
                           UniverseSlotValue(absChannel + 8, stride)) / 255f;

        static Vector3 Rgb(VRSL_URPLightManager.VRSLLightData d)
            => new Vector3(d.colorAndIntensity.x, d.colorAndIntensity.y, d.colorAndIntensity.z);

        [UnityTest]
        public IEnumerator N1_every_channel_reads_back_as_published()
        {
            var rig = VRSLDMXRig.Build();
            try
            {
                yield return rig.Step(4);
                rig.Calibrate();
                rig.Source.pattern   = VRSL_SyntheticDMXChannelSource.Pattern.Ramp;
                rig.Source.universes = 4;
                yield return rig.Step(3);

                Assert.AreEqual(4 * VRSLDMX.SlotsPerUniverse, rig.Manager.ChannelCount,
                    "four universes at the 520 stride is 2080 flat addresses");

                var read = rig.ReadChannels();
                for (int i = 0; i < read.Length; i++)
                {
                    int expected = VRSL_SyntheticDMXChannelSource.RampValue(i);
                    Assert.AreEqual(expected / 255f, read[i], Half,
                        $"channel {i + 1} read back wrong. A constant offset in the channel "
                      + "number is an indexing error; a value that looks like a neighbouring "
                      + "byte is a packing error");
                }
            }
            finally { rig.Dispose(); }
        }

        [UnityTest]
        public IEnumerator N5_a_fixture_past_the_end_reads_zero_not_its_neighbour()
        {
            var rig = VRSLDMXRig.Build();
            try
            {
                yield return rig.Step(4);
                rig.Calibrate();
                rig.Source.pattern   = VRSL_SyntheticDMXChannelSource.Pattern.Ramp;
                rig.Source.universes = 1;                       // 520 slots against a patch running to 638
                yield return rig.Step(3);

                var lights = rig.ReadLights();
                var dirs   = rig.ReadDirections();

                // Sectors 0-38 sit wholly inside the published universe.
                for (int i = 0; i <= 38; i++)
                    Assert.Greater(Rgb(lights[i]).sqrMagnitude, 0f,
                        $"sector {i} is inside the published universe and should decode real colour");

                // Sector 39 spans flat 508-520, so its colour channels land in the
                // inter-universe padding no desk can address. Its pan does not,
                // which is what still separates it from a fixture past the end.
                Assert.AreEqual(Vector3.zero, Rgb(lights[39]),
                    "sector 39's colour channels are inter-universe padding and must read zero");

                // Sectors 40-49 are past the end of the buffer entirely.
                for (int i = 40; i < VRSLDMXRig.FixtureCount; i++)
                {
                    Assert.AreEqual(Vector3.zero, Rgb(lights[i]),
                        $"sector {i} is past the end and must read 0 rather than another fixture's values");
                    Assert.AreEqual(dirs[40], dirs[i],
                        $"sector {i} has no pan or tilt to read, so every out-of-range fixture "
                      + "must share one direction");
                }
                Assert.AreNotEqual(dirs[40], dirs[39],
                    "sector 39 still reads a real pan channel, so it must not share the "
                  + "out-of-range direction — that is what separates padding from absent");
            }
            finally { rig.Dispose(); }
        }

        [UnityTest]
        public IEnumerator N6_universes_stride_by_520_not_512()
        {
            var rig = VRSLDMXRig.Build();
            try
            {
                yield return rig.Step(4);
                rig.Calibrate();
                rig.Source.pattern   = VRSL_SyntheticDMXChannelSource.Pattern.UniverseSlot;
                rig.Source.universes = 2;
                yield return rig.Step(3);

                var lights = rig.ReadLights();
                int wrongUnder512 = 0;
                for (int i = 0; i < VRSLDMXRig.FixtureCount; i++)
                {
                    int ch = VRSLDMXRig.ChannelOf(i);
                    AssertNear(PredictUniverseSlot(ch, VRSLDMX.SlotsPerUniverse), Rgb(lights[i]),
                        $"fixture at ch {ch} does not match the 520-strided prediction");
                    if (!Near(Rgb(lights[i]), PredictUniverseSlot(ch, VRSLDMX.UsableSlotsPerUniverse)))
                        wrongUnder512++;
                }

                // Without this the row proves nothing: if the two models agreed
                // everywhere, matching one of them would be no evidence at all.
                Assert.Greater(wrongUnder512, 0,
                    "the 512-strided model must disagree somewhere, or this row cannot tell the two apart");

                // Legacy sector 40 is the first slot of the second universe, so it
                // has to read what sector 0 reads.
                AssertNear(Rgb(lights[0]), Rgb(lights[40]),
                    "ch 1 and ch 521 are the same slot of their respective universes");
            }
            finally { rig.Dispose(); }
        }

        [UnityTest]
        public IEnumerator N7_both_addressing_modes_agree_on_where_universe_two_begins()
        {
            var rig = VRSLDMXRig.Build();
            try
            {
                yield return rig.Step(4);
                rig.Calibrate();
                rig.Source.pattern   = VRSL_SyntheticDMXChannelSource.Pattern.UniverseSlot;
                rig.Source.universes = 2;
                yield return rig.Step(3);

                // One fixture read twice, once through each addressing mode, rather
                // than two fixtures compared with each other: nothing else about it
                // can differ between the two readings.
                var fixture = rig.Fixtures[40];
                Assert.AreEqual(521, fixture.ComputeAbsoluteChannel(), "legacy sector 40 is flat 521");
                var viaSector = Rgb(rig.ReadLights()[40]);

                fixture.useLegacySectorMode = false;
                fixture.dmxChannel  = 1;
                fixture.dmxUniverse = 2;
                rig.Manager.MarkConfigDirty();
                yield return rig.Step(3);

                Assert.AreEqual(521, fixture.ComputeAbsoluteChannel(),
                    "universe 2 channel 1 is flat 521, because a universe strides 520");
                AssertNear(viaSector, Rgb(rig.ReadLights()[40]),
                    "dmxUniverse = 2 and legacy sector 40 must land on the same slot");
            }
            finally { rig.Dispose(); }
        }

        [UnityTest]
        public IEnumerator N13_values_persist_between_frames_when_only_one_universe_arrives()
        {
            var rig = VRSLDMXRig.Build();
            try
            {
                yield return rig.Step(4);
                rig.Calibrate();
                rig.Source.pattern   = VRSL_SyntheticDMXChannelSource.Pattern.Ramp;
                rig.Source.universes = 4;
                yield return rig.Step(3);
                var whole = rig.ReadLights();

                var baseline = rig.ReadChannels();
                rig.Source.rotateUniverses = true;

                // Checked after every frame rather than after whole sweeps. A manager
                // that cleared the flat space at the start of a sweep and refilled it
                // before the last frame would pass a check that only looked at the end,
                // and that is exactly the fault this row exists to catch.
                for (int frame = 0; frame < rig.Source.universes * 3; frame++)
                {
                    yield return rig.Step(1);
                    var partial = rig.ReadChannels();
                    for (int i = 0; i < partial.Length; i++)
                        Assert.AreEqual(baseline[i], partial[i], Half,
                            $"channel {i + 1} lost its value on frame {frame} of rotation, so the "
                          + "manager is rebuilding the flat space rather than keeping what it was told");
                }

                var rotated = rig.ReadLights();
                for (int i = 0; i < VRSLDMXRig.FixtureCount; i++)
                    AssertNear(Rgb(whole[i]), Rgb(rotated[i]),
                        $"fixture at ch {VRSLDMXRig.ChannelOf(i)} decoded differently under rotation");
            }
            finally { rig.Dispose(); }
        }
    }
}
