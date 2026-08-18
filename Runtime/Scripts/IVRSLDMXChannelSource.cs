using Unity.Collections;

namespace VRSL.URP
{
    /// <summary>
    /// Constants shared by everything that addresses VRSL's flat channel space.
    /// </summary>
    public static class VRSLDMX
    {
        /// <summary>Flat addresses a universe occupies: 512 real slots and 8 of
        /// padding. <c>ComputeAbsoluteChannel</c> strides universes by this, not
        /// by 512, because the decode grid is 13 channels wide and 512 does not
        /// divide by 13 — each universe is padded to 40 whole rows so the next
        /// one starts on a fresh row.</summary>
        public const int SlotsPerUniverse = 520;

        /// <summary>Real DMX512 slots in a universe. Addresses
        /// <see cref="SlotsPerUniverse"/> beyond this are padding no desk can
        /// write and nothing should read.</summary>
        public const int UsableSlotsPerUniverse = 512;

        /// <summary>Flat address (1-based, as VRSL numbers channels) of slot
        /// <paramref name="slot"/> (0-based) in universe <paramref name="universe"/>
        /// (0-based).</summary>
        public static int FlatChannel(int universe, int slot)
            => universe * SlotsPerUniverse + slot + 1;
    }

    /// <summary>
    /// A run of consecutive slots from one universe, as the wire format carries
    /// them. A run rather than a whole universe, so a source sending only the
    /// channels that changed needs no separate shape.
    /// </summary>
    public struct VRSLDMXBlock
    {
        /// <summary>0-based, as an Art-Net port address counts. Universe 0 is the
        /// one VRSL calls universe 1.</summary>
        public int universe;
        /// <summary>0-based slot within the universe. Slot 0 is what a desk calls
        /// channel 1.</summary>
        public int start;
        /// <summary>Slots in this run.</summary>
        public int length;
        /// <summary>Where this run's values begin in the values array handed over
        /// alongside the blocks.</summary>
        public int valueOffset;
        /// <summary>Microseconds between these values being latched at the desk
        /// and this call. Universes are latched as their packets arrive and DMX
        /// at 44 Hz does not divide into a frame grid, so each carries its own
        /// staleness rather than sharing one per snapshot.</summary>
        public uint ageMicroseconds;
    }

    /// <summary>
    /// A supplier of raw DMX channel values, handed over as
    /// <see cref="VRSLDMXBlock"/> runs.
    ///
    /// Blocks are addressed the way a desk is — a 0-based universe and a 0-based
    /// slot within it — and the manager scatters them into VRSL's flat space,
    /// applying the 520 stride once so no source has to know about it. Values are
    /// absolute and idempotent: the manager keeps the last value it was told for
    /// every slot, so a partial snapshot corrects the slots it covers and leaves
    /// the rest alone, and a late or dropped one is corrected by the next.
    ///
    /// The pixel grid exists because a video frame was the only way to get DMX
    /// into a VRChat world. Where the values arrive as bytes already, encoding
    /// them into pixels so a shader can decode them back is a round trip that
    /// costs precision and adds the only part of the read path that can be
    /// wrong. A source implementing this hands the manager the bytes directly.
    ///
    /// The texture path is unaffected: a scene with no channel source keeps
    /// reading the CRT chain exactly as before.
    /// </summary>
    public interface IVRSLDMXChannelSource
    {
        /// <summary>How many universes this source addresses. Sizes the flat
        /// buffer, so it must cover the highest universe any block will name and
        /// should not change frame to frame — every change reallocates.</summary>
        int UniverseCount { get; }

        /// <summary>
        /// The blocks arriving this frame, or false when none are. False means
        /// "nothing new", not "stop publishing": the manager keeps the values it
        /// already holds. Returning to the CRT chain is done by clearing the
        /// manager's <c>ChannelSource</c>, which a component does in its own
        /// <c>OnDisable</c>.
        ///
        /// Both arrays are borrowed for the duration of the call and must stay
        /// valid until it returns; the manager copies out of them.
        /// </summary>
        bool TryGetBlocks(out NativeArray<VRSLDMXBlock> blocks, out int blockCount,
                          out NativeArray<byte> values);
    }
}
