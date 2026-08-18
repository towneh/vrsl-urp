using Unity.Collections;

namespace VRSL.URP
{
    /// <summary>
    /// A supplier of raw DMX channel values, one byte per channel.
    ///
    /// <b>Element 0 is VRSL flat address 1, not real DMX channel 1 of universe 1
    /// in the way a desk means it.</b> Fixtures resolve their patch through
    /// <c>ComputeAbsoluteChannel</c>, which strides universes by 520, not 512:
    /// the grid is 13 channels wide, 512 does not divide by 13, so each universe
    /// is padded to 40 whole rows and the next one starts on a fresh row.
    /// Addresses 513-520 of each universe are padding nothing reads.
    ///
    /// A source holding real universes must therefore place universe <i>u</i>
    /// (0-based) slot <i>s</i> (0-based) at element <c>u * 520 + s</c>. Packing
    /// them 512 apart puts every fixture from the second universe onward 8
    /// channels early, 16 by the third, which reads as plausible-but-wrong
    /// colour rather than as an obvious failure.
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
        /// <summary>
        /// The current channel values, or false when this source has nothing to
        /// offer this frame. The array is borrowed for the duration of the call
        /// and must stay valid until it returns; the manager copies from it.
        /// </summary>
        bool TryGetChannels(out NativeArray<byte> channels, out int channelCount);
    }
}
