using Unity.Collections;
using UnityEngine;

namespace VRSL.URP
{
    /// <summary>
    /// A lighting desk that is not there: generates DMX channel values on the
    /// CPU and hands them to <see cref="VRSL_URPLightManager"/> as blocks.
    ///
    /// It exists so the buffer path can be brought up and checked without a
    /// video stream, a network, or a real desk. The ramp pattern in particular
    /// is what the validation menu item compares against: every channel holds a
    /// known function of its own address, so a value read back through the
    /// shader proves the packing and the indexing rather than merely proving
    /// something arrived.
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
            /// <summary>Every channel holds its own slot number within its universe,
            /// so the pattern repeats every universe. Ramp is a function of the flat
            /// address and therefore reads the same whichever stride a source packed
            /// with; this is keyed on the slot, so it is the only pattern that can
            /// tell a 520-strided buffer from a 512-strided one.</summary>
            UniverseSlot,
        }

        [Tooltip("Universes to generate. Each publishes its 512 real slots; the 8 "
               + "addresses of padding VRSL leaves between universes are nobody's to "
               + "write, so nothing is sent for them.")]
        [Range(1, 32)]
        public int universes = 4;

        public Pattern pattern = Pattern.Ramp;

        [Tooltip("Sweep and Fixtures animate at this rate. Ramp is static, which is "
               + "what makes it usable as a reference.")]
        public float speed = 0.25f;

        [Header("Delivery")]
        [Tooltip("Publish one universe per frame in rotation rather than all of them "
               + "every frame, the way the relay latch rotates under budget pressure. "
               + "Each universe then advances its own damping by the real interval "
               + "between its snapshots instead of by a frame.")]
        public bool rotateUniverses;

        [Tooltip("Staleness to report on every block, in milliseconds. A constant age "
               + "shifts each universe timeline by the same amount and so changes no "
               + "damping step; it is here to prove the manager carries age through "
               + "without it destabilising anything.")]
        [Range(0f, 500f)]
        public float simulatedAgeMs;

        NativeArray<byte>         _values;
        NativeArray<VRSLDMXBlock> _blocks;
        int                       _blockCount;

        /// <summary>Kept for callers that predicted addresses from this type.
        /// The stride belongs to VRSL flat space, not to any one source.</summary>
        public const int SlotsPerUniverse = VRSLDMX.SlotsPerUniverse;

        public int UniverseCount => universes;

        /// <summary>Frames this source has published since it was enabled. A rotating
        /// source covers the flat space only after a full rotation, and nothing else
        /// can tell a consumer how far through that it is: frames since play started
        /// counts frames before the source existed.</summary>
        public int PublishedFrames { get; private set; }

        /// <summary>The value the Ramp pattern puts at a given zero-based flat
        /// address. The validation harness compares against this, so it lives here
        /// rather than being written out twice. Padding reads 0 because no block
        /// covers it.</summary>
        public static byte RampValue(int flatAddress)
        {
            int slot = flatAddress % VRSLDMX.SlotsPerUniverse;
            if (slot >= VRSLDMX.UsableSlotsPerUniverse) return 0;
            return (byte)(flatAddress % 251);
        }

        void OnEnable()
        {
            PublishedFrames = 0;
            Allocate();
            var mgr = VRSL_URPLightManager.Instance;
            if (mgr != null) mgr.ChannelSource = this;
        }

        void OnDisable()
        {
            var mgr = VRSL_URPLightManager.Instance;
            if (mgr != null && ReferenceEquals(mgr.ChannelSource, this)) mgr.ChannelSource = null;
            if (_values.IsCreated) _values.Dispose();
            if (_blocks.IsCreated) _blocks.Dispose();
        }

        int ValueCount => universes * VRSLDMX.UsableSlotsPerUniverse;

        void Allocate()
        {
            if (_values.IsCreated && _values.Length == ValueCount) return;
            if (_values.IsCreated) _values.Dispose();
            if (_blocks.IsCreated) _blocks.Dispose();
            _values = new NativeArray<byte>(ValueCount, Allocator.Persistent,
                                            NativeArrayOptions.ClearMemory);
            _blocks = new NativeArray<VRSLDMXBlock>(universes, Allocator.Persistent,
                                                    NativeArrayOptions.ClearMemory);
        }

        void Update()
        {
            Allocate();

            int first = 0, count = universes;
            if (rotateUniverses)
            {
                first = universes > 0 ? Time.frameCount % universes : 0;
                count = 1;
            }

            uint age = (uint)Mathf.RoundToInt(simulatedAgeMs * 1000f);
            float t = Time.time * speed;

            _blockCount = 0;
            for (int i = 0; i < count; i++)
            {
                int u  = first + i;
                int at = u * VRSLDMX.UsableSlotsPerUniverse;
                for (int s = 0; s < VRSLDMX.UsableSlotsPerUniverse; s++)
                    _values[at + s] = Value(u * VRSLDMX.SlotsPerUniverse + s, s, t);

                _blocks[_blockCount++] = new VRSLDMXBlock
                {
                    universe        = u,
                    start           = 0,
                    length          = VRSLDMX.UsableSlotsPerUniverse,
                    valueOffset     = at,
                    ageMicroseconds = age,
                };
            }
            PublishedFrames++;
        }

        byte Value(int flat, int slot, float t)
        {
            switch (pattern)
            {
                case Pattern.Sweep:        return (byte)Mathf.PingPong(t * 255f, 255f);
                case Pattern.Fixtures:     return FixtureValue(flat, t);
                case Pattern.UniverseSlot: return (byte)(slot % 256);
                default:                   return RampValue(flat);
            }
        }

        // Channel offsets follow VRSL's 13-channel layout: pan, pan fine, tilt,
        // tilt fine, zoom, dimmer, strobe, R, G, B, gobo spin, gobo, reserved.
        static byte FixtureValue(int flat, float t)
        {
            float phase = t + (flat / 13) * 0.13f;
            switch (flat % 13)
            {
                case 0:  return (byte)(Mathf.Sin(phase) * 127f + 128f);          // pan
                case 2:  return (byte)(Mathf.Cos(phase * 0.7f) * 127f + 128f);   // tilt
                case 4:  return 128;                                             // zoom
                case 5:  return 255;                                             // dimmer
                case 7:  return (byte)(Mathf.Sin(phase) * 127f + 128f);          // red
                case 8:  return (byte)(Mathf.Sin(phase + 2.1f) * 127f + 128f);   // green
                case 9:  return (byte)(Mathf.Sin(phase + 4.2f) * 127f + 128f);   // blue
                default: return 0;
            }
        }

        public bool TryGetBlocks(out NativeArray<VRSLDMXBlock> blocks, out int blockCount,
                                 out NativeArray<byte> values)
        {
            blocks     = _blocks;
            values     = _values;
            blockCount = _blocks.IsCreated ? _blockCount : 0;
            return blockCount > 0;
        }
    }
}
