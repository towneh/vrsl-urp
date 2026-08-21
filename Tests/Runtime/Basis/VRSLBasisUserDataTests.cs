using System.Collections;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using VRSL.URP.BasisIntegration;

namespace VRSL.URP.Tests
{
    /// <summary>
    /// The whole path from a stream to the channel buffer: a real
    /// <see cref="BasisMediaPlayer"/> plays a fixture whose video carries Truss
    /// DMX records, <see cref="BasisUserDataToVRSLDMX"/> turns them into blocks,
    /// and the manager's buffer is read back through the shader's own accessor.
    /// The fixture carries VRSL's Ramp pattern, so every channel is a known
    /// function of its flat address; slot s of universe u holds (u * 520 + s) % 251,
    /// universes 0-3 on every frame and universe 4 from frame 90. It is hosted at
    /// https://mr.town/vod/, and <c>VRSL_TRUSS_FIXTURES</c> points these rows at a
    /// local directory or another URL base instead.
    ///
    /// Rows B7, B8 and the dropped half of B9 in TESTING.md.
    /// </summary>
    class VRSLBasisUserDataTests : VRSLDMXTest
    {
        const int    BaseUniverses     = 4;
        const int    LateUniverseFrom  = 90;
        const int    Frames            = 300;
        const float  RealSecondsLimit  = 40f;

        const string HostedFixtures = "https://mr.town/vod";

        static string Fixture(string name)
        {
            string where = System.Environment.GetEnvironmentVariable("VRSL_TRUSS_FIXTURES");
            if (string.IsNullOrEmpty(where)) where = HostedFixtures;
            if (where.StartsWith("http://") || where.StartsWith("https://"))
                return where.TrimEnd('/') + "/" + name;
            string path = Path.GetFullPath(Path.Combine(where, name));
            Assert.IsTrue(File.Exists(path), $"fixture missing: {path}");
            return path;
        }

        struct Rig
        {
            public VRSLDMXRig              Scene;
            public BasisMediaPlayer        Player;
            public BasisUserDataToVRSLDMX  Source;
        }

        static Rig Open(string fixture, bool live = false)
        {
            var rig = new Rig { Scene = VRSLDMXRig.Build(withSource: false) };
            var host = new GameObject("Basis Player");
            host.transform.SetParent(rig.Scene.Manager.transform.parent, false);
            rig.Player = host.AddComponent<BasisMediaPlayer>();
            rig.Player.playOnStart = false;
            rig.Player.liveness = live ? BmLiveness.Live : BmLiveness.Vod;
            // A local fixture reads as a loopback-ish source to the engine's
            // gate; this opts out of it, here and only here.
            rig.Player.allowLocalAddresses = true;
            rig.Source = host.AddComponent<BasisUserDataToVRSLDMX>();
            rig.Source.Player = rig.Player;
            rig.Source.minimumUniverses = BaseUniverses;
            // Assigned rather than left to the source's OnEnable, which only
            // lands if the manager already claimed the singleton.
            rig.Scene.Manager.ChannelSource = rig.Source;
            rig.Player.Open(fixture);
            rig.Player.Play();
            return rig;
        }

        /// <summary>Step real frames until the source has decoded at least
        /// <paramref name="records"/>, or the wall clock runs out. The engine
        /// paces itself on wall time whatever <c>captureDeltaTime</c> says, so
        /// this is a real-time wait with a render each frame.</summary>
        static IEnumerator Until(Rig rig, uint records)
        {
            float started = Time.realtimeSinceStartup;
            int endedFor = 0;
            while (rig.Source.RecordsDecoded < records)
            {
                Assert.AreNotEqual(BmState.Error, rig.Player.State,
                    $"the player errored ({rig.Player.ErrorCode}) before {records} records arrived");
                // Once the session has ended nothing more is coming; a short
                // grace lets the last tick's releases land, then the row fails
                // with what the player reported rather than waiting out the clock.
                if (rig.Player.State == BmState.Ended) endedFor++;
                Assert.IsTrue(endedFor < 60 && Time.realtimeSinceStartup - started < RealSecondsLimit,
                    $"{rig.Source.RecordsDecoded} records; wanted {records}. Player {rig.Player.State} "
                  + $"at {rig.Player.PositionSeconds:F3} s of {rig.Player.DurationSeconds:F3} s, "
                  + $"presented {rig.Player.FramesPresented}, decoded {rig.Player.FramesDecoded}, "
                  + $"dropped {rig.Source.RecordsDropped}, last result {rig.Source.LastResult}");
                yield return rig.Scene.Step(1);
            }
        }

        static void AssertRamp(Rig rig, int universes, string when)
        {
            var channels = rig.Scene.ReadChannels();
            Assert.AreEqual(universes * VRSLDMX.SlotsPerUniverse, channels.Length,
                $"channel count {when}");
            for (int i = 0; i < channels.Length; i++)
            {
                float expected = VRSL_SyntheticDMXChannelSource.RampValue(i) / 255f;
                Assert.AreEqual(expected, channels[i], Half,
                    $"channel {i + 1} (universe {i / VRSLDMX.SlotsPerUniverse}, slot "
                  + $"{i % VRSLDMX.SlotsPerUniverse}) {when}");
            }
        }

        [UnityTest]
        public IEnumerator B7_B8_records_arrive_in_order_and_the_buffer_reads_the_ramp()
        {
            var rig = Open(Fixture("truss-dmx-ramp.ts"));
            try
            {
                yield return Until(rig, 30);
                rig.Scene.Calibrate();
                Assert.AreEqual(0u, rig.Source.RecordsDropped, "nothing dropped on an intact stream");
                Assert.AreEqual(BaseUniverses, rig.Source.UniverseCount,
                    "before frame 90 the stream names four universes");
                yield return rig.Scene.Step(2);
                AssertRamp(rig, BaseUniverses, "before the fifth universe");

                yield return Until(rig, LateUniverseFrom + 30);
                Assert.AreEqual(BaseUniverses + 1, rig.Source.UniverseCount,
                    "the fifth universe grew the count once it was named");
                Assert.AreEqual(0u, rig.Source.RecordsDropped);
                yield return rig.Scene.Step(2);
                AssertRamp(rig, BaseUniverses + 1, "after the fifth universe");

                // Sequence and frame index ride the outer frame; they track the
                // video frame, so the newest header names a frame at or past
                // what has been decoded.
                Assert.GreaterOrEqual(rig.Source.LastHeader.FrameIndex + 1, rig.Source.RecordsDecoded);
                Assert.AreEqual(rig.Source.LastHeader.FrameIndex, rig.Source.LastHeader.Sequence);
                Assert.AreEqual(1, rig.Source.LastHeader.Carrier);

                yield return Until(rig, Frames);
                Assert.AreEqual(0u, rig.Source.RecordsDropped, "nothing dropped across the whole file");
            }
            finally
            {
                rig.Player.Close();
                rig.Scene.Dispose();
            }
        }

        [UnityTest]
        public IEnumerator B9_damaged_records_are_dropped_and_counted_while_the_rest_light_the_rig()
        {
            var rig = Open(Fixture("truss-dmx-ramp-damaged.ts"));
            try
            {
                // 270 intact of 300: every tenth record has a value bit flipped
                // after its CRC was written. LastResult is overwritten by the
                // next good record, so it is sampled every frame along the way;
                // records arrive at 30 Hz and frames step faster than that.
                bool sawBadCrc = false;
                float started = Time.realtimeSinceStartup;
                while (rig.Source.RecordsDecoded < Frames - Frames / 10)
                {
                    Assert.AreNotEqual(BmState.Error, rig.Player.State, "the player errored");
                    Assert.Less(Time.realtimeSinceStartup - started, RealSecondsLimit, "timed out");
                    yield return rig.Scene.Step(1);
                    if (rig.Source.LastResult == VRSLTrussDmx.Result.BadCrc) sawBadCrc = true;
                }
                rig.Scene.Calibrate();
                Assert.AreEqual((uint)(Frames / 10), rig.Source.RecordsDropped,
                    "every tenth record fails its CRC and is counted");
                Assert.IsTrue(sawBadCrc, "a drop was observed as a CRC failure while playing");
                yield return rig.Scene.Step(2);
                AssertRamp(rig, BaseUniverses + 1, "with a tenth of the records dropped");
            }
            finally
            {
                rig.Player.Close();
                rig.Scene.Dispose();
            }
        }

        /// <summary>
        /// The same checks against a live lane rather than a file: Art-Net into a
        /// Truss relay, the relay's RTMP into a server, the server's RTSP into the
        /// player. The sender carries the same Ramp over five universes, so what
        /// arrives is compared the same way. Skipped unless VRSL_TRUSS_LIVE_URL names
        /// the lane; BasisApps/basis-truss-live brings one up on this machine.
        /// </summary>
        [UnityTest]
        public IEnumerator B11_the_live_lane_delivers_what_the_fixture_does()
        {
            string url = System.Environment.GetEnvironmentVariable("VRSL_TRUSS_LIVE_URL");
            if (string.IsNullOrEmpty(url))
                Assert.Ignore("VRSL_TRUSS_LIVE_URL is not set; no live lane to play");

            var rig = Open(url, live: true);
            try
            {
                yield return Until(rig, 30);
                rig.Scene.Calibrate();
                uint at30 = rig.Source.RecordsDecoded;
                float t30 = Time.realtimeSinceStartup;

                // Five seconds of the lane: the record rate is the video frame
                // rate, and nothing arrives damaged on a path that copies the
                // stream through.
                yield return Until(rig, at30 + 150);
                float seconds = Time.realtimeSinceStartup - t30;
                float perSecond = (rig.Source.RecordsDecoded - at30) / seconds;
                Assert.That(perSecond, Is.InRange(20f, 45f),
                    $"{perSecond:F1} records/s; the lane runs at the video's 30 fps");
                Assert.AreEqual(0u, rig.Source.RecordsDropped, "nothing dropped on a remuxing path");
                Assert.AreEqual(BaseUniverses + 1, rig.Source.UniverseCount,
                    "the sender carries five universes");
                yield return rig.Scene.Step(2);
                AssertRamp(rig, BaseUniverses + 1, "from the live lane");

                // The relay stamps its own send time; a block's age is how long
                // before that the universe was last heard from, and at 40 Hz
                // Art-Net against 30 fps video that is well under a frame.
                Assert.Less(rig.Source.LastHeader.SendUnixNanos, ulong.MaxValue);
            }
            finally
            {
                rig.Player.Close();
                rig.Scene.Dispose();
            }
        }
    }
}
