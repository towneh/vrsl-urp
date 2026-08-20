using System;
using Unity.Collections;

namespace VRSL.URP
{
    /// <summary>
    /// Decoder for the DMX snapshot record that Truss stamps into a video stream
    /// (as SEI user data, UUID <see cref="Uuid"/>), into the blocks
    /// <see cref="IVRSLDMXChannelSource"/> hands the manager.
    ///
    /// The record is self-delimiting and self-verifying, all integers big-endian:
    /// an outer frame (magic <c>TRUSSDMX</c>, version, carrier, sequence, send
    /// time, frame index, payload length, payload, CRC32 over everything before
    /// it) around a <c>DMXS</c> payload of blocks, each a run of slots from one
    /// universe with its own age. A record that fails any check is reported and
    /// nothing from it is appended, so a consumer never sees half a snapshot or a
    /// damaged one: on a live show "arrived broken" and "did not arrive" are
    /// different faults, and the result says which.
    ///
    /// Transport-agnostic on purpose. The Basis integration feeds it from the
    /// player's user-data event; anything else that carries the same bytes can
    /// feed it too.
    /// </summary>
    public static class VRSLTrussDmx
    {
        /// <summary>The SEI user-data UUID Truss stamps on every record.</summary>
        public static readonly Guid Uuid = new Guid("b1f0a7d4-9c3e-4a52-8f61-2d7c5e0b93a8");

        public const int RecordHeaderLength  = 28;
        public const int CrcLength           = 4;
        public const int PayloadHeaderLength = 8;
        public const int BlockHeaderLength   = 10;
        public const byte RecordVersion  = 1;
        public const byte PayloadVersion = 1;

        public enum Result
        {
            Ok,
            /// <summary>Shorter than a record header.</summary>
            TooShort,
            /// <summary>Not <c>TRUSSDMX</c>.</summary>
            BadMagic,
            /// <summary>A record version this decoder does not read.</summary>
            UnsupportedVersion,
            /// <summary>The declared payload and CRC run past the end.</summary>
            Truncated,
            /// <summary>The bytes changed in transit.</summary>
            BadCrc,
            /// <summary>A payload shorter than its header.</summary>
            PayloadTooShort,
            /// <summary>A payload that is not <c>DMXS</c>: the record is someone
            /// else's, or a measurement probe rather than lighting data.</summary>
            BadPayloadMagic,
            /// <summary>A payload version this decoder does not read.</summary>
            UnsupportedPayloadVersion,
            /// <summary>A block runs past the end of the payload.</summary>
            PayloadTruncated,
        }

        /// <summary>The outer frame's fields, for diagnostics and ordering.</summary>
        public struct Header
        {
            public byte  Carrier;
            public uint  Sequence;
            public ulong SendUnixNanos;
            public uint  FrameIndex;
        }

        /// <summary>
        /// Decode one record, appending its blocks at <paramref name="blockCount"/>
        /// and their values at <paramref name="valueCount"/>, growing either array
        /// (Persistent) when it is too small, so several records delivered in one
        /// frame accumulate into one hand-over. Every check runs before anything is
        /// written: on any result but <see cref="Result.Ok"/> the counts and arrays
        /// are exactly as they were.
        ///
        /// The decoder owns both arrays: a growth allocates with
        /// <see cref="Allocator.Persistent"/> and disposes the array passed in, so a
        /// caller must not keep a second copy of either handle across a call, and
        /// disposes the arrays itself when it is done with them. Bytes after the
        /// record's CRC are ignored; the record delimits itself.
        /// </summary>
        public static Result Decode(ReadOnlySpan<byte> record, out Header header,
                                    ref NativeArray<VRSLDMXBlock> blocks, ref int blockCount,
                                    ref NativeArray<byte> values, ref int valueCount)
        {
            header = default;
            if (record.Length < RecordHeaderLength) return Result.TooShort;
            if (record[0] != (byte)'T' || record[1] != (byte)'R' || record[2] != (byte)'U' ||
                record[3] != (byte)'S' || record[4] != (byte)'S' || record[5] != (byte)'D' ||
                record[6] != (byte)'M' || record[7] != (byte)'X')
                return Result.BadMagic;
            if (record[8] != RecordVersion) return Result.UnsupportedVersion;

            int payloadLength = ReadU16(record, 26);
            int end = RecordHeaderLength + payloadLength;
            if (record.Length < end + CrcLength) return Result.Truncated;
            if (ReadU32(record, end) != Crc32(record.Slice(0, end))) return Result.BadCrc;

            var payload = record.Slice(RecordHeaderLength, payloadLength);
            var walked = WalkPayload(payload, out int count, out int total);
            if (walked != Result.Ok) return walked;

            header = new Header
            {
                Carrier       = record[9],
                Sequence      = ReadU32(record, 10),
                SendUnixNanos = ReadU64(record, 14),
                FrameIndex    = ReadU32(record, 22),
            };

            EnsureCapacity(ref blocks, blockCount + count);
            EnsureCapacity(ref values, valueCount + total);

            int at = PayloadHeaderLength;
            for (int i = 0; i < count; i++)
            {
                int length = ReadU16(payload, at + 4);
                int from   = at + BlockHeaderLength;
                payload.Slice(from, length).CopyTo(values.AsSpan().Slice(valueCount, length));
                blocks[blockCount++] = new VRSLDMXBlock
                {
                    universe        = ReadU16(payload, at),
                    start           = ReadU16(payload, at + 2),
                    length          = length,
                    valueOffset     = valueCount,
                    ageMicroseconds = ReadU32(payload, at + 6),
                };
                valueCount += length;
                at = from + length;
            }
            return Result.Ok;
        }

        /// <summary>Check the payload end to end and size what it will append.</summary>
        static Result WalkPayload(ReadOnlySpan<byte> payload, out int count, out int total)
        {
            count = 0;
            total = 0;
            if (payload.Length < PayloadHeaderLength) return Result.PayloadTooShort;
            if (payload[0] != (byte)'D' || payload[1] != (byte)'M' ||
                payload[2] != (byte)'X' || payload[3] != (byte)'S')
                return Result.BadPayloadMagic;
            if (payload[4] != PayloadVersion) return Result.UnsupportedPayloadVersion;

            count = ReadU16(payload, 6);
            int at = PayloadHeaderLength;
            for (int i = 0; i < count; i++)
            {
                if (at + BlockHeaderLength > payload.Length) return Result.PayloadTruncated;
                int length = ReadU16(payload, at + 4);
                at += BlockHeaderLength + length;
                if (at > payload.Length) return Result.PayloadTruncated;
                total += length;
            }
            return Result.Ok;
        }

        static void EnsureCapacity<T>(ref NativeArray<T> array, int needed) where T : struct
        {
            if (array.IsCreated && array.Length >= needed) return;
            int capacity = array.IsCreated ? array.Length : 0;
            if (capacity < 16) capacity = 16;
            while (capacity < needed) capacity *= 2;
            var grown = new NativeArray<T>(capacity, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            if (array.IsCreated)
            {
                NativeArray<T>.Copy(array, grown, array.Length);
                array.Dispose();
            }
            array = grown;
        }

        static int ReadU16(ReadOnlySpan<byte> b, int at) => b[at] << 8 | b[at + 1];

        static uint ReadU32(ReadOnlySpan<byte> b, int at)
            => (uint)b[at] << 24 | (uint)b[at + 1] << 16 | (uint)b[at + 2] << 8 | b[at + 3];

        static ulong ReadU64(ReadOnlySpan<byte> b, int at)
            => (ulong)ReadU32(b, at) << 32 | ReadU32(b, at + 4);

        static readonly uint[] s_crcTable = BuildCrcTable();

        static uint[] BuildCrcTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                table[n] = c;
            }
            return table;
        }

        /// <summary>CRC-32 (IEEE, reflected), the one <c>crc32fast</c> computes.</summary>
        public static uint Crc32(ReadOnlySpan<byte> data)
        {
            uint c = 0xFFFFFFFFu;
            for (int i = 0; i < data.Length; i++)
                c = s_crcTable[(c ^ data[i]) & 0xFF] ^ (c >> 8);
            return c ^ 0xFFFFFFFFu;
        }
    }
}
