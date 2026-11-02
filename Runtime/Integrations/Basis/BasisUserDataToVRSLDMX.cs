using System;
using UnityEngine;

namespace VRSL.URP.BasisIntegration
{
    /// <summary>
    /// Feeds the DMX channel buffer from lighting data carried inside the video
    /// itself. Truss stamps each frame's DMX snapshot into the stream as SEI user
    /// data; <see cref="BasisMediaPlayer"/> raises every such message at the
    /// moment that frame is shown, and this picks out Truss's by UUID and hands
    /// the record to <see cref="VRSLTrussRecordSource"/>, which decodes it and
    /// feeds <see cref="VRSL_DMXURPLightManager"/>. No pixel grid, no capture
    /// camera, no decode chain: the bytes the desk sent are the bytes the
    /// fixtures read.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("VRSL-URP/VRSL Truss SEI DMX Output")]
    public class BasisUserDataToVRSLDMX : VRSLTrussRecordSource
    {
        [Tooltip("Player decoding the stream that carries the DMX data.")]
        public BasisMediaPlayer Player;

        BasisMediaPlayer _subscribed;

        protected override void OnEnable()
        {
            base.OnEnable();
            Subscribe(Player);
        }

        protected override void OnDisable()
        {
            Subscribe(null);
            base.OnDisable();
        }

        // The player reference can be assigned or swapped after enable.
        protected override void Update()
        {
            if (!ReferenceEquals(Player, _subscribed)) Subscribe(Player);
            base.Update();
        }

        void Subscribe(BasisMediaPlayer player)
        {
            // By reference: a destroyed player compares equal to null the Unity
            // way while the managed object still holds the delegate.
            if (!ReferenceEquals(_subscribed, null))
                _subscribed.OnUserDataReceived -= OnUserData;
            _subscribed = player;
            if (_subscribed != null)
                _subscribed.OnUserDataReceived += OnUserData;
        }

        void OnUserData(long ptsUs, Guid uuid, ReadOnlySpan<byte> payload)
        {
            if (uuid != VRSLTrussDmx.Uuid) return;
            var result = Accept(payload);
            if (result != VRSLTrussDmx.Result.Ok && ShouldLog(result))
                Debug.LogWarning($"[VRSL URP] Dropped a DMX record from the stream: {result} "
                               + $"({payload.Length} bytes at {ptsUs} us). Further drops for "
                               + "this reason are counted, not logged.", this);
        }
    }
}
