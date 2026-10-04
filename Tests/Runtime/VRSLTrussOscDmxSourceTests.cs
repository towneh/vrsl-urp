using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace VRSL.URP.Tests
{
    /// <summary>
    /// The OSC lane end to end on one machine: records built byte for byte the
    /// way Truss builds them, sent as the <c>/truss/dmx</c> message
    /// <c>truss-relay --osc</c> sends, received by <see cref="VRSLTrussOscDmxSource"/>
    /// and read back from the manager's buffer through the shader's own
    /// accessor. The records carry VRSL's Ramp, so every channel is a known
    /// function of its flat address. No Basis packages needed.
    ///
    /// Rows N27 and N28 in TESTING.md.
    /// </summary>
    class VRSLTrussOscDmxSourceTests : VRSLDMXTest
    {
        const int   Universes        = 4;
        const int   Records          = 30;
        const float RealSecondsLimit = 15f;

        struct Rig
        {
            public VRSLDMXRig            Scene;
            public VRSLTrussOscDmxSource Source;
            public UdpClient             Sender;
            public IPEndPoint            To;
        }

        /// <summary>A port nothing on this machine holds right now.</summary>
        static int FreePort()
        {
            using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            return ((IPEndPoint)probe.Client.LocalEndPoint).Port;
        }

        static Rig Open(int port)
        {
            var rig = new Rig { Scene = VRSLDMXRig.Build(withSource: false) };
            var host = new GameObject("Truss OSC");
            host.transform.SetParent(rig.Scene.Manager.transform.parent, false);
            host.SetActive(false);
            rig.Source = host.AddComponent<VRSLTrussOscDmxSource>();
            rig.Source.port             = port;
            rig.Source.listenAddress    = "127.0.0.1";
            rig.Source.minimumUniverses = Universes;
            host.SetActive(true);
            // Assigned rather than left to the source's OnEnable, which only
            // lands if the manager already claimed the singleton.
            rig.Scene.Manager.ChannelSource = rig.Source;
            rig.Sender = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            rig.To     = new IPEndPoint(IPAddress.Loopback, port);
            return rig;
        }

        static void CloseRig(Rig rig)
        {
            rig.Sender?.Close();
            rig.Scene?.Dispose();
        }

        /// <summary>One OSC message: an address, a type tag string and the
        /// arguments, every field NUL-terminated or padded to four bytes.</summary>
        static byte[] Message(string address, string tags, byte[] blob)
        {
            var b = new List<byte>();
            void PutString(string s)
            {
                b.AddRange(System.Text.Encoding.ASCII.GetBytes(s));
                b.Add(0);
                while (b.Count % 4 != 0) b.Add(0);
            }
            PutString(address);
            PutString(tags);
            if (blob != null)
            {
                int n = blob.Length;
                b.Add((byte)(n >> 24)); b.Add((byte)(n >> 16)); b.Add((byte)(n >> 8)); b.Add((byte)n);
                b.AddRange(blob);
                while (b.Count % 4 != 0) b.Add(0);
            }
            return b.ToArray();
        }

        /// <summary>A record carrying the Ramp over <paramref name="universes"/>
        /// whole universes, numbered as the relay numbers a record.</summary>
        static byte[] RampRecord(int universes, uint seq)
        {
            var blocks = new VRSLTrussDmxTests.Block[universes];
            for (int u = 0; u < universes; u++)
                blocks[u] = new VRSLTrussDmxTests.Block
                {
                    universe = u, start = 0, age = 1000,
                    values = VRSLTrussDmxTests.Ramp(512, u * VRSLDMX.SlotsPerUniverse),
                };
            return VRSLTrussDmxTests.Record(VRSLTrussDmxTests.Payload(blocks), seq: seq, frame: seq, carrier: 6);
        }

        static void Send(Rig rig, byte[] datagram) => rig.Sender.Send(datagram, datagram.Length, rig.To);

        /// <summary>Step real frames until the source has decoded at least
        /// <paramref name="records"/> or counted <paramref name="ignored"/>
        /// ignored datagrams, or the wall clock runs out.</summary>
        static IEnumerator Until(Rig rig, uint records, ulong ignored = 0)
        {
            float started = Time.realtimeSinceStartup;
            while (rig.Source.RecordsDecoded + rig.Source.RecordsDropped < records
                   || rig.Source.DatagramsIgnored < ignored)
            {
                Assert.Less(Time.realtimeSinceStartup - started, RealSecondsLimit, "timed out");
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
        public IEnumerator N27_records_over_osc_light_the_rig_and_everything_else_is_ignored()
        {
            var rig = Open(FreePort());
            try
            {
                yield return rig.Scene.Step(1);
                Assert.IsTrue(rig.Source.Listening, rig.Source.LastError ?? "the source is not listening");

                for (uint seq = 0; seq < Records; seq++)
                {
                    Send(rig, Message(VRSLTrussOscDmxSource.Address, ",b", RampRecord(Universes, seq)));
                    yield return rig.Scene.Step(1);
                }
                yield return Until(rig, Records);
                rig.Scene.Calibrate();
                Assert.AreEqual(0u, rig.Source.RecordsDropped, "nothing dropped on an intact lane");
                Assert.AreEqual((ulong)Records, rig.Source.DatagramsReceived);
                Assert.AreEqual(0UL, rig.Source.DatagramsIgnored);
                Assert.AreEqual(Universes, rig.Source.UniverseCount);
                yield return rig.Scene.Step(2);
                AssertRamp(rig, Universes, "after the first records");

                // The outer frame is the lane's own: sequence and frame index
                // count the records the sender built, under the OSC carrier.
                Assert.AreEqual((uint)(Records - 1), rig.Source.LastHeader.Sequence);
                Assert.AreEqual(rig.Source.LastHeader.Sequence, rig.Source.LastHeader.FrameIndex);
                Assert.AreEqual(6, rig.Source.LastHeader.Carrier);

                // A fifth universe grows the count once it is named.
                Send(rig, Message(VRSLTrussOscDmxSource.Address, ",b", RampRecord(Universes + 1, Records)));
                yield return Until(rig, Records + 1);
                Assert.AreEqual(Universes + 1, rig.Source.UniverseCount);
                yield return rig.Scene.Step(2);
                AssertRamp(rig, Universes + 1, "after the fifth universe");

                // A record damaged after its CRC was written is dropped and
                // counted, and the buffer keeps what the intact ones said.
                var damaged = RampRecord(Universes + 1, Records + 1);
                damaged[damaged.Length - 20] ^= 0x40;
                Send(rig, Message(VRSLTrussOscDmxSource.Address, ",b", damaged));
                yield return Until(rig, Records + 2);
                Assert.AreEqual(1u, rig.Source.RecordsDropped);
                Assert.AreEqual(VRSLTrussDmx.Result.BadCrc, rig.Source.LastResult);
                yield return rig.Scene.Step(2);
                AssertRamp(rig, Universes + 1, "after a damaged record");

                // Everything that is not our message is counted and never
                // reaches the decoder: another address, another argument type,
                // a bundle, and bytes that are not OSC at all.
                uint decodedBefore = rig.Source.RecordsDecoded;
                Send(rig, Message("/other/thing", ",b", RampRecord(1, 99)));
                Send(rig, Message(VRSLTrussOscDmxSource.Address, ",i", null));
                var inner = Message(VRSLTrussOscDmxSource.Address, ",b", RampRecord(1, 100));
                var bundle = new List<byte>(System.Text.Encoding.ASCII.GetBytes("#bundle\0"));
                bundle.AddRange(new byte[8]);
                bundle.AddRange(new[] { (byte)(inner.Length >> 24), (byte)(inner.Length >> 16),
                                        (byte)(inner.Length >> 8), (byte)inner.Length });
                bundle.AddRange(inner);
                Send(rig, bundle.ToArray());
                Send(rig, new byte[] { 1, 2, 3 });
                yield return Until(rig, Records + 2, ignored: 4);
                Assert.AreEqual(4UL, rig.Source.DatagramsIgnored);
                Assert.AreEqual(decodedBefore, rig.Source.RecordsDecoded, "none of it decoded");
                Assert.AreEqual(1u, rig.Source.RecordsDropped, "and none of it counted as a drop");
            }
            finally
            {
                CloseRig(rig);
            }
        }

        [UnityTest]
        public IEnumerator N28_a_port_already_held_or_a_bad_address_is_reported_not_thrown()
        {
            int port = FreePort();
            using var holder = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
            LogAssert.Expect(LogType.Error, new Regex("cannot listen on 127\\.0\\.0\\.1:" + port
                                                   + ".*held by another program"));
            var rig = Open(port);
            try
            {
                yield return rig.Scene.Step(1);
                Assert.IsFalse(rig.Source.Listening);
                StringAssert.Contains("held by another program", rig.Source.LastError);
                Assert.AreEqual(0UL, rig.Source.DatagramsReceived);

                // A typing error in the address is told apart from a held port.
                LogAssert.Expect(LogType.Error, new Regex("not an IP address"));
                var host = new GameObject("Bad address");
                host.transform.SetParent(rig.Scene.Manager.transform.parent, false);
                host.SetActive(false);
                var bad = host.AddComponent<VRSLTrussOscDmxSource>();
                bad.listenAddress = "not-an-address";
                bad.port          = FreePort();
                host.SetActive(true);
                yield return rig.Scene.Step(1);
                Assert.IsFalse(bad.Listening);
                StringAssert.Contains("not an IP address", bad.LastError);
            }
            finally
            {
                CloseRig(rig);
            }
        }
    }
}
