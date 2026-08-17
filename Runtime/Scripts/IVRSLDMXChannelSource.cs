using Unity.Collections;

namespace VRSL.URP
{
    /// <summary>
    /// A supplier of raw DMX channel values, one byte per channel, indexed from
    /// zero (so DMX channel 1 is element 0).
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
