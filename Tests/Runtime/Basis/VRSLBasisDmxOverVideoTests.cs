using System.Collections;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using VRSL.URP.BasisIntegration;

namespace VRSL.URP.Tests
{
    /// <summary>
    /// Both DMX paths judged against each other rather than each against its own
    /// idea of what was sent.
    ///
    /// The fixture carries the same values twice over: VRSL's horizontal node
    /// grid burnt into the bottom of every frame, and a Truss record stamped
    /// into every access unit. <see cref="BasisVideoRenderTextureOutput"/>
    /// frames the strip into the RAW grid RT the CRT chain reads, and
    /// <see cref="BasisUserDataToVRSLDMX"/> turns the records into blocks, so a
    /// row can read the interpolation CRT and the channel buffer inside one
    /// frame and compare them.
    ///
    /// Channel c holds (c - 1 + 5 * frame) % 251, so the values carry the frame
    /// they came from. A row therefore never has to assume which frame it is
    /// looking at: it recovers the offset from the values and asks whether every
    /// channel agrees on it. An address fault breaks that agreement; a lag
    /// between the two paths moves one path's offset and not the other's.
    ///
    /// Rows N23 to N25 in TESTING.md. The fixture is 1920x1080 at 30 fps with
    /// the grid strip 1920x208 at y=864 and three universes, and it is not
    /// hosted: <c>VRSL_TRUSS_FIXTURES</c> has to point at a directory holding
    /// it.
    /// </summary>
    class VRSLBasisDmxOverVideoTests : VRSLDMXTest
    {
        const string Name       = "vrsl-dmx-marching.ts";
        const int    Universes  = 3;
        const int    Channels   = Universes * VRSLDMX.SlotsPerUniverse;   // 1560
        const int    Step       = 5;                                      // per frame
        const int    Modulus    = 251;
        const float  RealSecondsLimit = 40f;

        // The strip is 1920x208 at y=864 in a 1920x1080 frame, and the RAW grid
        // RT is 13 cells wide where the strip is 13 cells tall, so the framing
        // is a transpose: the RT's bottom-right corner samples the strip's
        // top-left. Getting this the other way round still fills the RT and
        // still lights the rig, reading every channel off the wrong fixture.
        static readonly Vector2 UvBL = new Vector2(0f, 8f / 1080f);
        static readonly Vector2 UvBR = new Vector2(0f, 216f / 1080f);
        static readonly Vector2 UvTR = new Vector2(1f, 216f / 1080f);
        static readonly Vector2 UvTL = new Vector2(1f, 8f / 1080f);

        const string RawGridRT =
            "Packages/town.mr.vrsl-urp/Runtime/Textures/RTs/DMXRTViewer-RAWValues-Horizontal.renderTexture";

        static string Fixture()
        {
            string where = System.Environment.GetEnvironmentVariable("VRSL_TRUSS_FIXTURES");
            if (string.IsNullOrEmpty(where))
                Assert.Ignore("VRSL_TRUSS_FIXTURES is unset and this fixture is not hosted.");
            if (where.StartsWith("http://") || where.StartsWith("https://"))
                return where.TrimEnd('/') + "/" + Name;
            string path = Path.GetFullPath(Path.Combine(where, Name));
            if (!File.Exists(path)) Assert.Ignore($"fixture missing: {path}");
            return path;
        }

        struct Rig
        {
            public VRSLDMXRig                     Scene;
            public BasisMediaPlayer               Player;
            public BasisUserDataToVRSLDMX         Source;
            public BasisVideoRenderTextureOutput  Video;
        }

        static Rig Open(bool records, bool picture)
        {
            var rig = new Rig { Scene = VRSLDMXRig.Build(withSource: false) };
            var host = new GameObject("Basis Player");
            host.transform.SetParent(rig.Scene.Manager.transform.parent, false);
            rig.Player = host.AddComponent<BasisMediaPlayer>();
            rig.Player.playOnStart = false;
            rig.Player.liveness = BmLiveness.Vod;
            // A local fixture reads as a loopback-ish source to the engine's
            // gate; this opts out of it, here and only here.
            rig.Player.allowLocalAddresses = true;

            if (records)
            {
                rig.Source = host.AddComponent<BasisUserDataToVRSLDMX>();
                rig.Source.Player = rig.Player;
                rig.Source.minimumUniverses = Universes;
                // Assigned rather than left to the source's OnEnable, which only
                // lands if the manager already claimed the singleton.
                rig.Scene.Manager.ChannelSource = rig.Source;
            }

            if (picture)
            {
#if UNITY_EDITOR
                var target = UnityEditor.AssetDatabase.LoadAssetAtPath<RenderTexture>(RawGridRT);
                Assert.IsNotNull(target, $"asset not found: {RawGridRT}");
#else
                RenderTexture target = null;
                Assert.Ignore("The rig frames the grid from a package asset and is editor-only.");
#endif
                rig.Video = host.AddComponent<BasisVideoRenderTextureOutput>();
                rig.Video.Player = rig.Player;
                rig.Video.Target = target;
                rig.Video.uvBL = UvBL;
                rig.Video.uvBR = UvBR;
                rig.Video.uvTR = UvTR;
                rig.Video.uvTL = UvTL;
            }

            rig.Player.Open(Fixture());
            rig.Player.Play();
            return rig;
        }

        static void Close(Rig rig)
        {
            rig.Player.Close();
            rig.Scene.Dispose();
        }

        /// <summary>Step real frames until the records or the pictures get far
        /// enough in, or the wall clock runs out. The engine paces on wall time
        /// whatever captureDeltaTime says, so this is a real-time wait with a
        /// render each frame.</summary>
        static IEnumerator Until(Rig rig, uint records, ulong presented)
        {
            float started = Time.realtimeSinceStartup;
            int endedFor = 0;
            while ((rig.Source != null && rig.Source.RecordsDecoded < records)
                || (rig.Source == null && rig.Player.FramesPresented < presented))
            {
                Assert.AreNotEqual(BmState.Error, rig.Player.State,
                    $"the player errored ({rig.Player.ErrorCode}) before the stream got going");
                if (rig.Player.State == BmState.Ended) endedFor++;
                Assert.IsTrue(endedFor < 60 && Time.realtimeSinceStartup - started < RealSecondsLimit,
                    $"waited out the clock. Player {rig.Player.State} at "
                  + $"{rig.Player.PositionSeconds:F3} s, presented {rig.Player.FramesPresented}, "
                  + $"records {(rig.Source != null ? rig.Source.RecordsDecoded : 0)}");
                yield return rig.Scene.Step(1);
            }
        }

        /// <summary>How far a set of channels has marched, recovered from the
        /// values themselves rather than assumed from the frame count.</summary>
        static int OffsetOf(float[] values) => Mathf.RoundToInt(values[0] * 255f) % Modulus;

        /// <summary>The 13th channel of a sector that VRSL's accessor pulls a row
        /// down. <c>GetDMXValue</c> carries a hard-coded correction table over
        /// five channel ranges, so on the picture path those channels read the
        /// value 13 below their own. It belongs to VRSL's texture accessor; the
        /// channel buffer has no equivalent.</summary>
        static bool ShiftedThirteenth(int channel)
            => channel % 13 == 0
            && ((channel >= 90  && channel <= 101)
             || (channel >= 160 && channel <= 205)
             || (channel >= 326 && channel <= 404)
             || (channel >= 676 && channel <= 819)
             ||  channel >= 1339);

        /// <summary>The 1-based channels not holding what the offset says they
        /// should, by more than <paramref name="tolerance"/> DMX units.
        ///
        /// Channels whose true value sits near the wrap go unjudged: the ramp is
        /// modular, so a damped read across the wrap lands between 250 and 0 and
        /// means nothing. Nor is padding judged, which nobody writes.</summary>
        static List<int> Disagreeing(float[] values, int offset, int count,
                                     float tolerance, string what)
        {
            var wrong = new List<int>();
            int judged = 0;
            float worst = 0f;
            for (int i = 0; i < count; i++)
            {
                if (i % VRSLDMX.SlotsPerUniverse >= VRSLDMX.UsableSlotsPerUniverse) continue;
                int expected = (i + offset) % Modulus;
                if (expected > Modulus - 1 - 4 * Step || expected < 4 * Step) continue;
                judged++;
                float delta = Mathf.Abs(values[i] * 255f - expected);
                if (delta > worst) worst = delta;
                if (delta > tolerance) wrong.Add(i + 1);
            }
            Assert.Greater(judged, count / 2,
                $"{what}: too few channels were judgeable, which means the offset is wrong");
            Debug.Log($"{what}: offset {offset}, {judged} judged, worst {worst:F1}, "
                    + $"{wrong.Count} outside {tolerance}");
            return wrong;
        }

        /// <summary>Which judged channels the picture path is expected to get
        /// wrong, so a row can assert the set rather than wave a tolerance at
        /// it.</summary>
        static List<int> ExpectedWrong(int offset, int count)
        {
            var wrong = new List<int>();
            for (int i = 0; i < count; i++)
            {
                if (i % VRSLDMX.SlotsPerUniverse >= VRSLDMX.UsableSlotsPerUniverse) continue;
                int expected = (i + offset) % Modulus;
                if (expected > Modulus - 1 - 4 * Step || expected < 4 * Step) continue;
                if (ShiftedThirteenth(i + 1)) wrong.Add(i + 1);
            }
            return wrong;
        }

        static void AssertPictureReadsTheRamp(float[] grid, int offset, string when)
        {
            var wrong = Disagreeing(grid, offset, Channels, 4f, "picture " + when);
            CollectionAssert.AreEqual(ExpectedWrong(offset, Channels), wrong,
                "the channels the picture path gets wrong are not the ones VRSL's own "
              + "correction table accounts for, so the framing or the addressing is at fault");
            foreach (int channel in wrong)
                Assert.AreEqual((channel - 1 - 13 + offset) % Modulus,
                                grid[channel - 1] * 255f, 2f,
                    $"channel {channel} is shifted, but not onto the row below it");
        }

        /// <summary>Where the values actually land, read at three points along
        /// the chain: the RAW grid RT the framing writes, and the channel the
        /// shader's accessor returns for it. Not an assertion — it is here to
        /// tell a framing fault apart from a decode-chain one, because both
        /// show up as "the numbers are wrong" at the accessor.</summary>
        [UnityTest]
        public IEnumerator Dump_where_the_picture_lands()
        {
            var rig = Open(records: true, picture: true);
            try
            {
                yield return Until(rig, 30, 0);
                yield return rig.Scene.Step(30);

                var buffer = rig.Scene.ReadChannels();
                var grid   = rig.Scene.ReadGridChannels(Channels);
                int offset = OffsetOf(buffer);

                var raw = rig.Video.Target;
                var shot = new Texture2D(raw.width, raw.height, TextureFormat.RGBA32, false);
                var was = RenderTexture.active;
                RenderTexture.active = raw;
                shot.ReadPixels(new Rect(0, 0, raw.width, raw.height), 0, 0);
                shot.Apply(false);
                RenderTexture.active = was;

                int cw = raw.width / 13, ch = raw.height / 120;
                var report = new System.Text.StringBuilder();
                report.AppendLine($"RAW RT {raw.width}x{raw.height}, cells {cw}x{ch}; "
                                + $"records offset {offset}; grid texture "
                                + $"{rig.Scene.Manager.dmxMainTexture.width}x"
                                + $"{rig.Scene.Manager.dmxMainTexture.height}");
                report.AppendLine("ch  want   rawRT  accessor");
                for (int c = 1; c <= 40; c++)
                {
                    int cx = (c - 1) % 13, cy = (c - 1) / 13;
                    var px = shot.GetPixel(cx * cw + cw / 2, cy * ch + ch / 2);
                    report.AppendLine($"{c,3} {(c - 1 + offset) % Modulus,6} "
                                    + $"{px.r * 255f,7:F0} {grid[c - 1] * 255f,9:F0}");
                }
                Debug.Log(report.ToString());
                Object.DestroyImmediate(shot);
            }
            finally { Close(rig); }
        }

        [UnityTest]
        public IEnumerator N23_the_records_carry_the_frame_they_came_from()
        {
            var rig = Open(records: true, picture: false);
            try
            {
                yield return Until(rig, 30, 0);
                yield return rig.Scene.Step(2);

                Assert.AreEqual(0u, rig.Source.RecordsDropped, "nothing dropped on an intact stream");
                Assert.AreEqual(Universes, rig.Source.UniverseCount, "the stream names three universes");

                uint before = rig.Source.RecordsDecoded;
                var channels = rig.Scene.ReadChannels();
                Assert.AreEqual(before, rig.Source.RecordsDecoded,
                    "a record landed while the channels were being read, so they are from two frames");
                Assert.AreEqual(Channels, channels.Length, "channel count");

                int offset = OffsetOf(channels);
                CollectionAssert.IsEmpty(
                    Disagreeing(channels, offset, Channels, Half * 255f, "records"),
                    "every channel must agree on one offset, or the addressing is wrong");

                // The offset says which frame the values came from, and the
                // record's own header says which frame carried them. They are the
                // same claim from two directions. Both are read modulo 251,
                // which the ramp wraps on: five marches the ramp and five is
                // coprime with 251, so 201 walks it back.
                int implied = offset * 201 % Modulus;
                int header  = (int)(rig.Source.LastHeader.FrameIndex % Modulus);
                Debug.Log($"N23: offset {offset} implies frame {implied}; "
                        + $"header says {rig.Source.LastHeader.FrameIndex}; "
                        + $"{rig.Source.RecordsDecoded} records decoded");
                // The source decodes a record and the manager consumes its blocks
                // on the next frame, so the buffer is allowed to sit one record
                // behind the newest header. Two would mean one went unread.
                int behind = (header - implied + Modulus) % Modulus;
                Assert.LessOrEqual(behind, 1,
                    $"the channel buffer is {behind} frames behind the newest record's header");
            }
            finally { Close(rig); }
        }

        [UnityTest]
        public IEnumerator N24_the_picture_carries_the_same_ramp_through_the_crt_chain()
        {
            var rig = Open(records: false, picture: true);
            try
            {
                yield return Until(rig, 0, 30);
                // The interpolation CRT damps, so give it frames to settle onto a
                // steadily marching input before reading.
                yield return rig.Scene.Step(30);

                var grid = rig.Scene.ReadGridChannels(Channels);
                AssertPictureReadsTheRamp(grid, OffsetOf(grid), "alone");
            }
            finally { Close(rig); }
        }

        [UnityTest]
        public IEnumerator N25_both_paths_agree_within_one_frame_of_each_other()
        {
            var rig = Open(records: true, picture: true);
            try
            {
                yield return Until(rig, 30, 0);
                yield return rig.Scene.Step(30);

                uint before = rig.Source.RecordsDecoded;
                var buffer = rig.Scene.ReadChannels();
                var grid   = rig.Scene.ReadGridChannels(Channels);
                Assert.AreEqual(before, rig.Source.RecordsDecoded,
                    "a record landed between the two reads, so they are from two frames");

                int fromRecords = OffsetOf(buffer);
                int fromPicture = OffsetOf(grid);
                // Offsets are modular, so the distance between them is too.
                int apart = ((fromPicture - fromRecords) % Modulus + Modulus) % Modulus;
                if (apart > Modulus / 2) apart -= Modulus;
                float frames = apart / (float)Step;
                Debug.Log($"N25: records at offset {fromRecords}, picture at {fromPicture}, "
                        + $"{frames:F1} frames apart");

                CollectionAssert.IsEmpty(
                    Disagreeing(buffer, fromRecords, Channels, Half * 255f, "records"),
                    "the record path disagrees with itself");
                AssertPictureReadsTheRamp(grid, fromPicture, "beside the records");
                Assert.LessOrEqual(Mathf.Abs(frames), 2f,
                    "the two paths are further apart in time than the CRT's damping accounts for");
            }
            finally { Close(rig); }
        }
    }
}
