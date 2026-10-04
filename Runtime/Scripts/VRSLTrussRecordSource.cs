using System;
using Unity.Collections;
using UnityEngine;

namespace VRSL.URP
{
    /// <summary>
    /// Feeds the DMX channel buffer from Truss records, whichever way they
    /// arrived. A record is the desk's DMX snapshot as Truss frames it, whether
    /// a stream carried it as SEI user data or the relay sent it over OSC, so
    /// one decoder and one hand-over serve both.
    ///
    /// A subclass owns the delivery and nothing else. It hands each record's
    /// bytes to <see cref="Accept"/> on the main thread; this decodes them,
    /// counts what happened, grows the universe count as blocks name higher
    /// universes, and hands the blocks to <see cref="VRSL_URPLightManager"/>
    /// as an <see cref="IVRSLDMXChannelSource"/>. No pixel grid, no capture
    /// camera, no decode chain: the bytes the desk sent are the bytes the
    /// fixtures read.
    ///
    /// Values are absolute, so a dropped record is corrected by the next one. A
    /// record that fails its CRC or its framing is dropped rather than applied,
    /// and counted, since a damaged snapshot on a rig looks like a cue.
    /// </summary>
    public abstract class VRSLTrussRecordSource : MonoBehaviour, IVRSLDMXChannelSource
    {
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
        VRSL_URPLightManager _manager;

        protected virtual void OnEnable()
        {
            _named       = 0;
            _blockCount  = 0;
            _valueCount  = 0;
            _logged      = 0;
            RecordsDecoded = 0;
            RecordsDropped = 0;
            LastResult     = VRSLTrussDmx.Result.Ok;
            Bind(VRSL_URPLightManager.Instance);
        }

        protected virtual void OnDisable()
        {
            Bind(null);
            if (_values.IsCreated) _values.Dispose();
            if (_blocks.IsCreated) _blocks.Dispose();
        }

        // The manager can enable after this does or be replaced.
        protected virtual void Update()
        {
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

        /// <summary>
        /// Decode one record into the blocks waiting for the next hand-over.
        /// Main thread only: the arrays are the ones the manager copies from.
        /// Returns why the record was dropped, or <see cref="VRSLTrussDmx.Result.Ok"/>,
        /// and keeps the counts either way; a subclass that wants to log a drop
        /// asks <see cref="ShouldLog"/> so each reason logs once.
        /// </summary>
        protected VRSLTrussDmx.Result Accept(ReadOnlySpan<byte> record)
        {
            if (_blockCount >= MaxPendingBlocks)
            {
                _blockCount = 0;
                _valueCount = 0;
            }

            int before = _blockCount;
            var result = VRSLTrussDmx.Decode(record, out var header,
                                             ref _blocks, ref _blockCount,
                                             ref _values, ref _valueCount);
            LastResult = result;
            if (result != VRSLTrussDmx.Result.Ok)
            {
                RecordsDropped++;
                return result;
            }

            RecordsDecoded++;
            LastHeader = header;
            for (int i = before; i < _blockCount; i++)
                if (_blocks[i].universe >= _named)
                    _named = _blocks[i].universe + 1;
            return result;
        }

        /// <summary>Whether a drop for <paramref name="result"/> is the first
        /// of its reason since enable, with <see cref="logDrops"/> on. True at
        /// most once per reason, so a damaged stream cannot log at frame rate.</summary>
        protected bool ShouldLog(VRSLTrussDmx.Result result)
        {
            if (!logDrops) return false;
            int bit = 1 << (int)result;
            if ((_logged & bit) != 0) return false;
            _logged |= bit;
            return true;
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
