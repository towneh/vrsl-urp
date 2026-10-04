using System;
using System.Reflection;
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
    /// moment that frame is shown, and this picks out Truss's by UUID and hands
    /// the record to <see cref="VRSLTrussRecordSource"/>, which decodes it and
    /// feeds <see cref="VRSL_URPLightManager"/>. No pixel grid, no capture
    /// camera, no decode chain: the bytes the desk sent are the bytes the
    /// fixtures read.
    /// </summary>
#if VRSL_CILBOX_PRESENT
    [Cilboxable]
#endif
    [DisallowMultipleComponent]
    [AddComponentMenu("VRSL-URP/VRSL Truss SEI DMX Output")]
    public class BasisUserDataToVRSLDMX : VRSLTrussRecordSource
    {
        [Tooltip("Player decoding the stream that carries the DMX data.")]
        public BasisMediaPlayer Player;

        BasisMediaPlayer _subscribed;
        Delegate         _handler;
        bool             _warnedNoUserData;

        // Basis's C media player has no OnUserDataReceived; the Rust one raises
        // it. Bound by name so this assembly
        // compiles against either, which it can because the handler's signature
        // is framework types only: the delegate type is the player's, the
        // parameters are not.
        static readonly EventInfo s_UserDataEvent =
            typeof(BasisMediaPlayer).GetEvent("OnUserDataReceived", BindingFlags.Instance | BindingFlags.Public);

        /// <summary>Whether the media player in this project publishes SEI user
        /// data at all. False means this source can never receive a record.</summary>
        public static bool PlayerPublishesUserData => s_UserDataEvent != null;

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
            if (s_UserDataEvent == null)
            {
                _subscribed = player;
                if (player != null && !_warnedNoUserData)
                {
                    _warnedNoUserData = true;
                    Debug.LogWarning("[VRSL URP] This Basis media player doesn't pass on stream data, so the "
                                   + "Truss SEI DMX Output will stay dark. Update Basis, or feed the manager "
                                   + "another way.",
                                     this);
                }
                return;
            }

            // By reference: a destroyed player compares equal to null the Unity
            // way while the managed object still holds the delegate.
            if (!ReferenceEquals(_subscribed, null) && _handler != null)
                s_UserDataEvent.RemoveEventHandler(_subscribed, _handler);
            _subscribed = player;
            if (_subscribed != null)
            {
                _handler ??= Delegate.CreateDelegate(
                    s_UserDataEvent.EventHandlerType, this,
                    GetType().GetMethod(nameof(OnUserData), BindingFlags.Instance | BindingFlags.NonPublic));
                s_UserDataEvent.AddEventHandler(_subscribed, _handler);
            }
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
