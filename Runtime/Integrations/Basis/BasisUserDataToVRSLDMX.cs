using System;
using Unity.Collections;
using UnityEngine;
#if VRSL_CILBOX_PRESENT
using Cilbox;
#endif

namespace VRSL.URP.BasisIntegration
{
    /// <summary>
    /// Feeds the DMX channel buffer from lighting data carried inside the video
    /// itself. Truss stamps each frame's DMX snapshot into the stream as SEI user
    /// data; <see cref="BasisMediaPlayer"/> raises every such message at the
    /// moment that frame is shown, and this picks out Truss's by UUID, decodes
    /// the record, and hands the blocks to <see cref="VRSL_URPLightManager"/> as
    /// an <see cref="IVRSLDMXChannelSource"/>. No pixel grid, no capture camera,
    /// no decode chain: the bytes the desk sent are the bytes the fixtures read.
    ///
    /// Values are absolute, so a dropped record is corrected by the next one. A
    /// record that fails its CRC or its framing is dropped rather than applied,
    /// and counted, since a damaged snapshot on a rig looks like a cue.
    /// </summary>
#if VRSL_CILBOX_PRESENT
    [Cilboxable]
#endif
    [DisallowMultipleComponent]
    [AddComponentMenu("VRSL-URP/VRSL Truss SEI DMX Output")]
    public class BasisUserDataToVRSLDMX : MonoBehaviour, IVRSLDMXChannelSource
    {
        [Tooltip("Player decoding the stream that carries the DMX data.")]
        public BasisMediaPlayer Player;

        [Tooltip("Universes to size the channel buffer for before any data arrives. "
               + "The count grows when a block names a higher universe; each growth "
               + "reallocates the manager's buffers, so set this to the show's size to "
               + "avoid a resize on the first cue.")]
        [Range(1, 32)]
        public int minimumUniverses = 1;

        [Tooltip("Log the first record dropped for each reason. Quiet by default: a "
               + "damaged stream would otherwise log at frame rate.")]
        public bool logDrops;

        /// <summary>Records decoded and handed on since this was enabled.</summary>
        public uint RecordsDecoded { get; private set; }
        /// <summary>Records that failed a check and were not applied.</summary>
        public uint RecordsDropped { get; private set; }
        /// <summary>Why the most recent record was dropped, or Ok.</summary>
        public VRSLTrussDmx.Result LastResult { get; private set; }
        /// <summary>The outer frame of the most recent decoded record.</summary>
        public VRSLTrussDmx.Header LastHeader { get; private set; }

        // Read live rather than latched at enable, so a value assigned from code
        // after AddComponent, or raised at runtime, takes effect. The grown
        // count never shrinks below what a block has named.
        public int UniverseCount => Mathf.Max(_named, Mathf.Max(1, minimumUniverses));

        // Pending blocks accumulate across every record delivered between two
        // hand-overs, since a snapshot may carry only the channels that changed
        // and dropping an intermediate one would lose them; the manager applies
        // them in order, so a later block wins. Past this many without a
        // hand-over nobody is reading, and holding more would only delay live
        // data behind stale.
        const int MaxPendingBlocks = 4096;

        NativeArray<VRSLDMXBlock> _blocks;
        NativeArray<byte>         _values;
        int  _blockCount;
        int  _valueCount;
        // Highest universe a block has named, plus one.
        int  _named;
        // One bit per Result, so each reason logs once.
        int  _logged;
        BasisMediaPlayer _subscribed;
        VRSL_URPLightManager _manager;

        void OnEnable()
        {
            _named       = 0;
            _blockCount  = 0;
            _valueCount  = 0;
            _logged      = 0;
            RecordsDecoded = 0;
            RecordsDropped = 0;
            LastResult     = VRSLTrussDmx.Result.Ok;
            Subscribe(Player);
            Bind(VRSL_URPLightManager.Instance);
        }

        void OnDisable()
        {
            Bind(null);
            Subscribe(null);
            if (_values.IsCreated) _values.Dispose();
            if (_blocks.IsCreated) _blocks.Dispose();
        }

        // The player reference can be assigned or swapped after enable, and
        // the manager can enable after this does or be replaced.
        void Update()
        {
            if (!ReferenceEquals(Player, _subscribed)) Subscribe(Player);
            var mgr = VRSL_URPLightManager.Instance;
            if (!ReferenceEquals(mgr, _manager)) Bind(mgr);
        }

        // Takes the slot on a manager once, when it becomes the current one,
        // rather than every frame: another source enabled later is entitled
        // to take it over, the same way this takes over an earlier one.
        void Bind(VRSL_URPLightManager manager)
        {
            if (_manager != null && ReferenceEquals(_manager.ChannelSource, this))
                _manager.ChannelSource = null;
            _manager = manager;
            if (_manager != null) _manager.ChannelSource = this;
        }

        void Subscribe(BasisMediaPlayer player)
        {
            // By reference: a destroyed player compares equal to null the Unity
            // way while the managed object still holds the delegate.
            if (!ReferenceEquals(_subscribed, null)) _subscribed.UserDataReceived -= OnUserData;
            _subscribed = player;
            if (_subscribed != null) _subscribed.UserDataReceived += OnUserData;
        }

        void OnUserData(long ptsUs, Guid uuid, ReadOnlySpan<byte> payload)
        {
            if (uuid != VRSLTrussDmx.Uuid) return;

            if (_blockCount >= MaxPendingBlocks)
            {
                _blockCount = 0;
                _valueCount = 0;
            }

            int before = _blockCount;
            var result = VRSLTrussDmx.Decode(payload, out var header,
                                             ref _blocks, ref _blockCount,
                                             ref _values, ref _valueCount);
            LastResult = result;
            if (result != VRSLTrussDmx.Result.Ok)
            {
                RecordsDropped++;
                int bit = 1 << (int)result;
                if (logDrops && (_logged & bit) == 0)
                {
                    _logged |= bit;
                    Debug.LogWarning($"[VRSL URP] Dropped a DMX record from the stream: {result} "
                                   + $"({payload.Length} bytes at {ptsUs} us). Further drops for "
                                   + "this reason are counted, not logged.", this);
                }
                return;
            }

            RecordsDecoded++;
            LastHeader = header;
            for (int i = before; i < _blockCount; i++)
                if (_blocks[i].universe >= _named)
                    _named = _blocks[i].universe + 1;
        }

        public bool TryGetBlocks(out NativeArray<VRSLDMXBlock> blocks, out int blockCount,
                                 out NativeArray<byte> values)
        {
            blocks     = _blocks;
            values     = _values;
            blockCount = _blockCount;
            if (blockCount == 0) return false;
            // Handed over once. The manager copies out during this call, so the
            // counts can go now: a frame with nothing new must answer false, or
            // a stalled stream would re-apply the same blocks, and with them the
            // same ages, every frame.
            _blockCount = 0;
            _valueCount = 0;
            return true;
        }
    }
}
