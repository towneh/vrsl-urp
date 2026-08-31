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
    /// Rows N23 to N26 in TESTING.md. The fixture is 1920x1080 at 30 fps with
    /// the grid strip 1920x208 at y=864 and three universes. It is hosted at
    /// https://www.mr.town/vod/, and <c>VRSL_TRUSS_FIXTURES</c> points these rows
    /// at a local directory or another URL base instead.
    /// </summary>
    class VRSLBasisDmxOverVideoTests : VRSLDMXTest
    {
        const string Name       = "vrsl-dmx-marching.ts";
        const int    Universes  = 3;
        const int    Channels   = Universes * VRSLDMX.SlotsPerUniverse;   // 1560
        const int    Step       = 5;                                      // per frame
        const int    Modulus    = 251;
        const float  RealSecondsLimit = 40f;

        const string HostedFixtures = "https://www.mr.town/vod";

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
            if (string.IsNullOrEmpty(where)) where = HostedFixtures;
            if (where.StartsWith("http://") || where.StartsWith("https://"))
                return where.TrimEnd('/') + "/" + Name;
            string path = Path.GetFullPath(Path.Combine(where, Name));
            Assert.IsTrue(File.Exists(path), $"fixture missing: {path}");
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
            // The scene root and the captureDeltaTime override outlive a failed
            // close otherwise, and every row after this one inherits them.
            try { rig.Player.Close(); }
            finally { rig.Scene.Dispose(); }
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
        /// values themselves rather than assumed from the frame count.
        ///
        /// Every channel implies an offset and the answer is the one most of them
        /// imply. One channel would do on the record path, where the values are
        /// exact, but not on the picture path: those come off the interpolation
        /// CRT damped and any single channel can sit a unit or two out. An offset
        /// recovered one too low reports every channel as wrong, which points
        /// away from the cause.</summary>
        static int OffsetOf(float[] values, int count)
        {
            var votes = new Dictionary<int, int>();
            int best = 0, bestVotes = 0;
            for (int i = 0; i < count && i < values.Length; i++)
            {
                if (i % VRSLDMX.SlotsPerUniverse >= VRSLDMX.UsableSlotsPerUniverse) continue;
                int implied = ((Mathf.RoundToInt(values[i] * 255f) - i) % Modulus + Modulus) % Modulus;
                votes.TryGetValue(implied, out int n);
                votes[implied] = ++n;
                if (n > bestVotes) { bestVotes = n; best = implied; }
            }
            Assert.Greater(bestVotes, 0, "no channels to recover an offset from");
            return best;
        }

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

        /// <summary>Whether channel <paramref name="i"/> (0-based) can be judged
        /// at all. Padding cannot, since nobody writes it. Nor can a channel whose
        /// true value sits near the wrap: the ramp is modular, so a damped read
        /// across the wrap lands between 250 and 0 and means nothing.
        ///
        /// The measured set and the expected set both come through this one
        /// predicate. Comparing them says something only while the two filters
        /// agree, and a second copy would drift into a failure that reads as a
        /// framing fault.</summary>
        static bool Judgeable(int i, int offset)
        {
            if (i % VRSLDMX.SlotsPerUniverse >= VRSLDMX.UsableSlotsPerUniverse) return false;
            int expected = (i + offset) % Modulus;
            return expected <= Modulus - 1 - 4 * Step && expected >= 4 * Step;
        }

        /// <summary>The 1-based channels not holding what the offset says they
        /// should, by more than <paramref name="tolerance"/> DMX units.</summary>
        static List<int> Disagreeing(float[] values, int offset, int count,
                                     float tolerance, string what)
        {
            Assert.GreaterOrEqual(values.Length, count,
                $"{what}: {values.Length} channels were read where {count} were expected");
            var wrong = new List<int>();
            int judged = 0;
            float worst = 0f;
            for (int i = 0; i < count; i++)
            {
                if (!Judgeable(i, offset)) continue;
                int expected = (i + offset) % Modulus;
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
                if (Judgeable(i, offset) && ShiftedThirteenth(i + 1)) wrong.Add(i + 1);
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

        /// <summary>The cell each channel occupies in the RAW grid RT, read back
        /// from the texture itself.</summary>
        static float[] ReadRawGrid(RenderTexture raw)
        {
            var shot = new Texture2D(raw.width, raw.height, TextureFormat.RGBA32, false);
            try
            {
                // Leaving the active target on the grid RT would change what every
                // later ReadPixels in the run comes back with.
                var was = RenderTexture.active;
                try
                {
                    RenderTexture.active = raw;
                    shot.ReadPixels(new Rect(0, 0, raw.width, raw.height), 0, 0);
                    shot.Apply(false);
                }
                finally { RenderTexture.active = was; }

                int cw = raw.width / 13, ch = raw.height / 120;
                var values = new float[Channels];
                for (int i = 0; i < Channels; i++)
                {
                    // The RT is 13 cells across where the strip is 13 cells tall,
                    // and GetPixel counts rows from the bottom the way the accessor
                    // does. Cell centres, clear of any bleed at the edges.
                    int cx = i % 13, cy = i / 13;
                    values[i] = shot.GetPixel(cx * cw + cw / 2, cy * ch + ch / 2).r;
                }
                return values;
            }
            finally { Object.DestroyImmediate(shot); }
        }

        /// <summary>What the framing put in the RAW grid RT, before the CRT chain
        /// has touched it.
        ///
        /// This is the row that tells a framing fault from a decode-chain one.
        /// Both show up at the accessor as "the numbers are wrong", and N24 cannot
        /// separate them; here the values have been through the blit and nothing
        /// else. Note there are no exceptions to make room for: VRSL's correction
        /// table lives in the accessor, so every cell must hold its own channel.
        ///
        /// The RT leads the interpolation CRT by a frame, which is the CRT's own
        /// latency, so the offset is recovered here rather than shared with the
        /// rows that read through the accessor.</summary>
        [UnityTest]
        public IEnumerator N26_the_framing_lands_the_grid_before_the_decode_chain()
        {
            var rig = Open(records: false, picture: true);
            try
            {
                yield return Until(rig, 0, 30);
                yield return rig.Scene.Step(30);

                var values = ReadRawGrid(rig.Video.Target);
                int offset = OffsetOf(values, Channels);
                CollectionAssert.IsEmpty(
                    Disagreeing(values, offset, Channels, 2f, "raw grid"),
                    "the RAW grid RT does not hold the ramp, so the fault is in the "
                  + "framing rather than in the decode chain: the strip is mispositioned, "
                  + "or the corner UVs transpose it the wrong way");
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

                int offset = OffsetOf(channels, Channels);
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
                // The manager consumes blocks once per frame and records arrive as
                // the engine releases them, so a few can be decoded but not yet
                // handed over when the row reads. That gap is sampling, not
                // delivery: the source accumulates them and the next hand-over
                // applies all of them in order. The claim that carries weight is
                // the one above — the buffer holds one frame's values and not a
                // mixture. This only has to catch the buffer running ahead of what
                // was ever decoded, or falling wholesale behind.
                int behind = (header - implied + Modulus) % Modulus;
                Assert.LessOrEqual(behind, 10,
                    $"the channel buffer is {behind} frames behind the newest record's "
                  + "header, which is more than a hand-over boundary accounts for");
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
                AssertPictureReadsTheRamp(grid, OffsetOf(grid, Channels), "alone");
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

                int fromRecords = OffsetOf(buffer, Channels);
                int fromPicture = OffsetOf(grid, Channels);
                // Offsets are modular, so the distance between them is too.
                int apart = ((fromPicture - fromRecords) % Modulus + Modulus) % Modulus;
                if (apart > Modulus / 2) apart -= Modulus;
                float frames = apart / (float)Step;
                Debug.Log($"N25: records at offset {fromRecords}, picture at {fromPicture}, "
                        + $"{frames:F1} frames apart");

                // ReadChannels binds the manager's own channel count, so it takes
                // whichever branch MainChannel would take for a fixture. With both
                // feeds live it comes back exact, including on the channels the
                // texture accessor shifts, which is what says the records win.
                CollectionAssert.IsEmpty(
                    Disagreeing(buffer, fromRecords, Channels, Half * 255f, "records"),
                    "with both feeds live the fixtures must read the records, and this "
                  + "is the grid's shifted channels showing up in what they read");
                AssertPictureReadsTheRamp(grid, fromPicture, "beside the records");
                Assert.LessOrEqual(Mathf.Abs(frames), 2f,
                    "the two paths are further apart in time than the CRT's damping accounts for");
            }
            finally { Close(rig); }
        }
    }
}
