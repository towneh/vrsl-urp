using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;

namespace VRSL.URP.Tests
{
    /// <summary>
    /// The Truss record decoder against records built the way Truss builds them,
    /// byte for byte: the outer frame's fields, the CRC, the block walk, and what
    /// is (and is not) appended when a record fails a check.
    ///
    /// Row N15 of TESTING.md.
    /// </summary>
    class VRSLTrussDmxTests
    {
        // Nothing here yields, so the host cannot log inside a row; the frames
        // between rows are still this fixture's, so it is quietened like the rest.
        [OneTimeSetUp]
        public void QuietTheHost() => VRSLHostQuiet.Silence();

        [OneTimeTearDown]
        public void LetTheHostSpeak() => VRSLHostQuiet.Restore();

        struct Block
        {
            public int universe, start; public uint age; public byte[] values;
        }

        static void PutU16(List<byte> b, int v) { b.Add((byte)(v >> 8)); b.Add((byte)v); }
        static void PutU32(List<byte> b, uint v)
        {
            b.Add((byte)(v >> 24)); b.Add((byte)(v >> 16)); b.Add((byte)(v >> 8)); b.Add((byte)v);
        }
        static void PutU64(List<byte> b, ulong v) { PutU32(b, (uint)(v >> 32)); PutU32(b, (uint)v); }

        /// <summary>A <c>DMXS</c> payload, as Truss's <c>payload::encode</c> lays it out.</summary>
        static byte[] Payload(params Block[] blocks)
        {
            var b = new List<byte> { (byte)'D', (byte)'M', (byte)'X', (byte)'S', 1, 0x01 };
            PutU16(b, blocks.Length);
            foreach (var blk in blocks)
            {
                PutU16(b, blk.universe);
                PutU16(b, blk.start);
                PutU16(b, blk.values.Length);
                PutU32(b, blk.age);
                b.AddRange(blk.values);
            }
            return b.ToArray();
        }

        /// <summary>A <c>TRUSSDMX</c> record around a payload, CRC included, as
        /// Truss's <c>Record::encode</c> lays it out.</summary>
        static byte[] Record(byte[] payload, uint seq = 7, uint frame = 42,
                             ulong sendNanos = 1_700_000_000_000_000_000UL, byte carrier = 1,
                             byte version = 1)
        {
            var b = new List<byte>();
            b.AddRange(System.Text.Encoding.ASCII.GetBytes("TRUSSDMX"));
            b.Add(version);
            b.Add(carrier);
            PutU32(b, seq);
            PutU64(b, sendNanos);
            PutU32(b, frame);
            PutU16(b, payload.Length);
            b.AddRange(payload);
            PutU32(b, VRSLTrussDmx.Crc32(b.ToArray()));
            return b.ToArray();
        }

        static byte[] Ramp(int length, int from = 0)
        {
            var v = new byte[length];
            for (int i = 0; i < length; i++) v[i] = (byte)((from + i) % 251);
            return v;
        }

        NativeArray<VRSLDMXBlock> _blocks;
        NativeArray<byte>         _values;
        int _blockCount, _valueCount;

        [TearDown]
        public void Dispose()
        {
            if (_blocks.IsCreated) _blocks.Dispose();
            if (_values.IsCreated) _values.Dispose();
            _blocks = default; _values = default;
            _blockCount = 0; _valueCount = 0;
        }

        VRSLTrussDmx.Result Decode(byte[] record, out VRSLTrussDmx.Header header)
            => VRSLTrussDmx.Decode(record, out header, ref _blocks, ref _blockCount,
                                   ref _values, ref _valueCount);

        [Test]
        public void Crc32_matches_the_reference_vector()
        {
            // The check value every CRC-32/IEEE implementation agrees on.
            Assert.AreEqual(0xCBF43926u,
                            VRSLTrussDmx.Crc32(System.Text.Encoding.ASCII.GetBytes("123456789")));
        }

        [Test]
        public void A_full_universe_and_a_partial_run_round_trip()
        {
            var record = Record(Payload(
                new Block { universe = 0, start = 0,  age = 1500, values = Ramp(512) },
                new Block { universe = 3, start = 10, age = 22,   values = Ramp(5, 100) }),
                seq: 99, frame: 1234, sendNanos: 5, carrier: 1);

            var r = Decode(record, out var header);

            Assert.AreEqual(VRSLTrussDmx.Result.Ok, r);
            Assert.AreEqual(99u, header.Sequence);
            Assert.AreEqual(1234u, header.FrameIndex);
            Assert.AreEqual(5UL, header.SendUnixNanos);
            Assert.AreEqual(1, header.Carrier);
            Assert.AreEqual(2, _blockCount);
            Assert.AreEqual(517, _valueCount);

            var a = _blocks[0];
            Assert.AreEqual(0, a.universe);
            Assert.AreEqual(0, a.start);
            Assert.AreEqual(512, a.length);
            Assert.AreEqual(0, a.valueOffset);
            Assert.AreEqual(1500u, a.ageMicroseconds);
            for (int i = 0; i < 512; i++)
                Assert.AreEqual((byte)(i % 251), _values[a.valueOffset + i], $"slot {i}");

            var b = _blocks[1];
            Assert.AreEqual(3, b.universe);
            Assert.AreEqual(10, b.start);
            Assert.AreEqual(5, b.length);
            Assert.AreEqual(512, b.valueOffset);
            Assert.AreEqual(22u, b.ageMicroseconds);
            for (int i = 0; i < 5; i++)
                Assert.AreEqual((byte)((100 + i) % 251), _values[b.valueOffset + i]);
        }

        [Test]
        public void Records_accumulate_and_the_arrays_grow_to_fit()
        {
            // Start below one block's worth so every growth path runs.
            _blocks = new NativeArray<VRSLDMXBlock>(1, Allocator.Persistent);
            _values = new NativeArray<byte>(4, Allocator.Persistent);

            for (int n = 0; n < 5; n++)
            {
                var r = Decode(Record(Payload(
                    new Block { universe = n, start = n, age = 0, values = Ramp(100, n) })), out _);
                Assert.AreEqual(VRSLTrussDmx.Result.Ok, r, $"record {n}");
            }

            Assert.AreEqual(5, _blockCount);
            Assert.AreEqual(500, _valueCount);
            for (int n = 0; n < 5; n++)
            {
                Assert.AreEqual(n, _blocks[n].universe);
                Assert.AreEqual(n * 100, _blocks[n].valueOffset);
                Assert.AreEqual((byte)(n % 251), _values[_blocks[n].valueOffset]);
                Assert.AreEqual((byte)((n + 99) % 251), _values[_blocks[n].valueOffset + 99]);
            }
        }

        [Test]
        public void An_empty_snapshot_is_a_valid_record_that_appends_nothing()
        {
            Assert.AreEqual(VRSLTrussDmx.Result.Ok, Decode(Record(Payload()), out _));
            Assert.AreEqual(0, _blockCount);
            Assert.AreEqual(0, _valueCount);
        }

        [Test]
        public void A_damaged_record_is_refused_and_leaves_the_arrays_untouched()
        {
            Assert.AreEqual(VRSLTrussDmx.Result.Ok, Decode(Record(Payload(
                new Block { universe = 0, start = 0, age = 0, values = Ramp(8) })), out _));

            var record = Record(Payload(
                new Block { universe = 1, start = 0, age = 0, values = Ramp(8, 50) }));
            // Payload: header 0-7, block header 8-17, values from 18. Flip a
            // bit in the first value, so the row proves the CRC covers the
            // values and not only the framing.
            record[VRSLTrussDmx.RecordHeaderLength + VRSLTrussDmx.PayloadHeaderLength
                   + VRSLTrussDmx.BlockHeaderLength] ^= 0x40;

            Assert.AreEqual(VRSLTrussDmx.Result.BadCrc, Decode(record, out var header));
            Assert.AreEqual(default(VRSLTrussDmx.Header), header);
            Assert.AreEqual(1, _blockCount);
            Assert.AreEqual(8, _valueCount);
            Assert.AreEqual(0, _blocks[0].universe);
        }

        [Test]
        public void Framing_faults_are_told_apart()
        {
            var good = Record(Payload(new Block { universe = 0, start = 0, age = 0, values = Ramp(8) }));

            Assert.AreEqual(VRSLTrussDmx.Result.TooShort, Decode(new byte[27], out _));

            var magic = (byte[])good.Clone();
            magic[0] = (byte)'t';
            Assert.AreEqual(VRSLTrussDmx.Result.BadMagic, Decode(magic, out _));

            Assert.AreEqual(VRSLTrussDmx.Result.UnsupportedVersion,
                            Decode(Record(Payload(), version: 2), out _));

            var cut = new byte[good.Length - 3];
            Array.Copy(good, cut, cut.Length);
            Assert.AreEqual(VRSLTrussDmx.Result.Truncated, Decode(cut, out _));

            // A probe record: valid frame, body that is not a DMXS payload.
            Assert.AreEqual(VRSLTrussDmx.Result.BadPayloadMagic,
                            Decode(Record(Ramp(24)), out _));
            Assert.AreEqual(VRSLTrussDmx.Result.PayloadTooShort,
                            Decode(Record(new byte[] { (byte)'D', (byte)'M', (byte)'X' }), out _));

            var versioned = Payload();
            versioned[4] = 2;
            Assert.AreEqual(VRSLTrussDmx.Result.UnsupportedPayloadVersion,
                            Decode(Record(versioned), out _));

            Assert.AreEqual(0, _blockCount, "nothing refused was appended");
        }

        [Test]
        public void Bytes_after_the_crc_are_not_the_records_concern()
        {
            // An SEI payload can carry padding after the record; the record
            // delimits itself, so what follows its CRC is ignored rather than
            // refused.
            var record = new List<byte>(Record(Payload(
                new Block { universe = 0, start = 0, age = 0, values = Ramp(4) })));
            record.AddRange(new byte[] { 0x80, 0x00, 0x00, 0x00, 0x00 });

            Assert.AreEqual(VRSLTrussDmx.Result.Ok, Decode(record.ToArray(), out _));
            Assert.AreEqual(1, _blockCount);
            Assert.AreEqual(4, _valueCount);
        }

        [Test]
        public void A_zero_length_block_is_valid_and_appends_an_empty_run()
        {
            var r = Decode(Record(Payload(
                new Block { universe = 2, start = 7, age = 9, values = new byte[0] },
                new Block { universe = 2, start = 8, age = 9, values = Ramp(3) })), out _);

            Assert.AreEqual(VRSLTrussDmx.Result.Ok, r);
            Assert.AreEqual(2, _blockCount);
            Assert.AreEqual(3, _valueCount);
            Assert.AreEqual(0, _blocks[0].length);
            Assert.AreEqual(0, _blocks[0].valueOffset);
            Assert.AreEqual(3, _blocks[1].length);
            Assert.AreEqual(0, _blocks[1].valueOffset, "an empty run takes no values");
        }

        [Test]
        public void A_run_past_the_end_of_its_universe_is_the_managers_to_clamp()
        {
            // The decoder reads the format and nothing else. The manager owns
            // the universe's bounds and clamps a run that passes them (and says
            // so when its logs are on); a decoder refusing the whole record for
            // one long block would drop every other universe in the snapshot.
            var r = Decode(Record(Payload(
                new Block { universe = 0, start = 510, age = 0, values = Ramp(4) })), out _);

            Assert.AreEqual(VRSLTrussDmx.Result.Ok, r);
            Assert.AreEqual(1, _blockCount);
            Assert.AreEqual(510, _blocks[0].start);
            Assert.AreEqual(4, _blocks[0].length);
        }

        [Test]
        public void A_block_running_past_its_payload_refuses_the_whole_record()
        {
            // Two blocks declared, the second's values clipped off: the CRC is
            // computed over what is there, so only the walk can catch it.
            var payload = new List<byte>(Payload(
                new Block { universe = 0, start = 0, age = 0, values = Ramp(4) },
                new Block { universe = 1, start = 0, age = 0, values = Ramp(4) }));
            payload.RemoveRange(payload.Count - 2, 2);

            Assert.AreEqual(VRSLTrussDmx.Result.PayloadTruncated,
                            Decode(Record(payload.ToArray()), out _));
            Assert.AreEqual(0, _blockCount, "the intact first block is not applied either");

            // Header declared, no values at all.
            var headerOnly = new List<byte>(Payload(
                new Block { universe = 0, start = 0, age = 0, values = Ramp(4) }));
            headerOnly.RemoveRange(headerOnly.Count - 4 - 10, 14);
            headerOnly[7] = 1;
            Assert.AreEqual(VRSLTrussDmx.Result.PayloadTruncated,
                            Decode(Record(headerOnly.ToArray()), out _));
        }
    }
}
