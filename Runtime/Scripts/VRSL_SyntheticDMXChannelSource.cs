using Unity.Collections;
using UnityEngine;

namespace VRSL.URP
{
    /// <summary>
    /// A lighting desk that is not there: generates DMX channel values on the
    /// CPU and feeds them to <see cref="VRSL_URPLightManager"/> as bytes.
    ///
    /// It exists so the buffer path can be brought up and checked without a
    /// video stream, a network, or a real desk. The ramp pattern in particular
    /// is what the validation menu item compares against: every channel holds a
    /// known function of its own index, so a value read back through the shader
    /// proves the packing and the indexing rather than merely proving something
    /// arrived.
    /// </summary>
    [AddComponentMenu("VRSL-URP/Synthetic DMX Channel Source")]
    [DefaultExecutionOrder(-100)]
    public class VRSL_SyntheticDMXChannelSource : MonoBehaviour, IVRSLDMXChannelSource
    {
        public enum Pattern
        {
            /// <summary>Channel n holds (n mod 251). Prime, so the value does not
            /// repeat on any 13-channel fixture boundary and a misread by a whole
            /// fixture is visible rather than plausible.</summary>
            Ramp,
            /// <summary>Every channel holds the same value, swept over time.</summary>
            Sweep,
            /// <summary>13-channel fixtures lit and moving, for looking at rather
            /// than measuring.</summary>
            Fixtures,
        }

        [Tooltip("Universes to generate. 512 channels each.")]
        [Range(1, 32)]
        public int universes = 4;

        public Pattern pattern = Pattern.Ramp;

        [Tooltip("Sweep and Fixtures animate at this rate. Ramp is static, which is "
               + "what makes it usable as a reference.")]
        public float speed = 0.25f;

        NativeArray<byte> _channels;

        public int ChannelCount => universes * 512;

        /// <summary>The value the Ramp pattern puts in a given zero-based channel.
        /// The validation harness compares against this, so it lives here rather
        /// than being written out twice.</summary>
        public static byte RampValue(int channelIndex) => (byte)(channelIndex % 251);

        void OnEnable()
        {
            Allocate();
            var mgr = VRSL_URPLightManager.Instance;
            if (mgr != null) mgr.ChannelSource = this;
        }

        void OnDisable()
        {
            var mgr = VRSL_URPLightManager.Instance;
            if (mgr != null && ReferenceEquals(mgr.ChannelSource, this)) mgr.ChannelSource = null;
            if (_channels.IsCreated) _channels.Dispose();
        }

        void Allocate()
        {
            if (_channels.IsCreated && _channels.Length == ChannelCount) return;
            if (_channels.IsCreated) _channels.Dispose();
            _channels = new NativeArray<byte>(ChannelCount, Allocator.Persistent,
                                              NativeArrayOptions.ClearMemory);
        }

        void Update()
        {
            Allocate();
            switch (pattern)
            {
                case Pattern.Ramp:     FillRamp();     break;
                case Pattern.Sweep:    FillSweep();    break;
                case Pattern.Fixtures: FillFixtures(); break;
            }
        }

        void FillRamp()
        {
            for (int i = 0; i < _channels.Length; i++) _channels[i] = RampValue(i);
        }

        void FillSweep()
        {
            byte v = (byte)(Mathf.PingPong(Time.time * speed * 255f, 255f));
            for (int i = 0; i < _channels.Length; i++) _channels[i] = v;
        }

        // Channel offsets follow VRSL's 13-channel layout: pan, pan fine, tilt,
        // tilt fine, zoom, dimmer, strobe, R, G, B, gobo spin, gobo, reserved.
        void FillFixtures()
        {
            for (int i = 0; i < _channels.Length; i++) _channels[i] = 0;

            int fixtures = _channels.Length / 13;
            float t = Time.time * speed;
            for (int f = 0; f < fixtures; f++)
            {
                int b = f * 13;
                float phase = t + f * 0.13f;
                _channels[b + 0]  = (byte)(Mathf.Sin(phase) * 127f + 128f);        // pan
                _channels[b + 2]  = (byte)(Mathf.Cos(phase * 0.7f) * 127f + 128f); // tilt
                _channels[b + 4]  = 128;                                           // zoom
                _channels[b + 5]  = 255;                                           // dimmer
                _channels[b + 7]  = (byte)(Mathf.Sin(phase) * 127f + 128f);        // red
                _channels[b + 8]  = (byte)(Mathf.Sin(phase + 2.1f) * 127f + 128f); // green
                _channels[b + 9]  = (byte)(Mathf.Sin(phase + 4.2f) * 127f + 128f); // blue
            }
        }

        public bool TryGetChannels(out NativeArray<byte> channels, out int channelCount)
        {
            channels = _channels;
            channelCount = _channels.IsCreated ? _channels.Length : 0;
            return channelCount > 0;
        }
    }
}
