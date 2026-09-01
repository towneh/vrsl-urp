using System.Collections.Generic;
using UnityEngine;

namespace VRSL.URP
{
    /// <summary>
    /// Every light manager in the scene, held at a series of quality levels and put
    /// back afterwards.
    /// </summary>
    /// <remarks>
    /// A set rather than one manager, because a scene may carry both paths. Holding
    /// quality on the DMX manager alone there leaves the AudioLink volumetrics running
    /// at every level, so the <c>Off</c> capture is not off and the split between beams
    /// and surface lighting gets measured against a baseline that still has beams in it.
    ///
    /// <c>internal</c> on purpose. <see cref="VRSLQuality"/> is the settings API;
    /// this is a capture-and-restore around it, and making it public would leave a
    /// second way to set quality.
    /// </remarks>
    static class VRSLQualityPreset
    {
        public static readonly VRSLQuality[] All =
            { VRSLQuality.Off, VRSLQuality.Standard, VRSLQuality.High };

        /// <summary>One manager's quality and strobe state, remembered and restored.</summary>
        abstract class Target
        {
            VRSLQuality _quality;
            bool        _strobeHeld;

            public abstract Behaviour   Component { get; }
            public abstract VRSLQuality Quality   { get; set; }

            /// <summary>Every strobing fixture held fully on, where the manager has such
            /// a switch. The AudioLink path strobes off the audio rather than off a DMX
            /// channel and has no equivalent, so this is a no-op there rather than
            /// something to fake.</summary>
            public virtual bool StrobeHeld { get => false; set { } }

            /// <summary>Ask for the config to be re-uploaded. The AudioLink manager has
            /// no dirty flag — it reads its fields as it goes — so it needs nothing
            /// here.</summary>
            public virtual void MarkDirty() { }

            /// <summary>A destroyed manager compares null through the Unity operator, and
            /// a scene can be torn down mid-session.</summary>
            public bool Alive => Component != null;

            public void Capture()
            {
                _quality    = Quality;
                _strobeHeld = StrobeHeld;

                // Strobing fixtures alternate, so at any instant a random subset of the
                // rig is lit and the workload changes frame to frame. Measured
                // 2026-08-24: ten fixtures under a static Ramp reported 4, 6 and 9
                // emitting across three consecutive configurations of the same subset,
                // and lights per tile followed them. Holding every strobing fixture on
                // is what makes a configuration mean one thing.
                StrobeHeld = true;
            }

            public void Apply(VRSLQuality quality)
            {
                Quality = quality;
                MarkDirty();
            }

            public void RestoreSaved()
            {
                Quality    = _quality;
                StrobeHeld = _strobeHeld;
                MarkDirty();
            }
        }

        sealed class DmxTarget : Target
        {
            readonly VRSL_URPLightManager _m;
            public DmxTarget(VRSL_URPLightManager m) => _m = m;

            public override Behaviour   Component  => _m;
            public override VRSLQuality Quality    { get => _m.quality;       set => _m.quality = value; }
            public override bool        StrobeHeld { get => _m.disableStrobe; set => _m.disableStrobe = value; }
            public override void        MarkDirty() => _m.MarkConfigDirty();
        }

        sealed class AudioLinkTarget : Target
        {
            readonly VRSL_AudioLinkURPLightManager _m;
            public AudioLinkTarget(VRSL_AudioLinkURPLightManager m) => _m = m;

            public override Behaviour   Component => _m;
            public override VRSLQuality Quality   { get => _m.quality; set => _m.quality = value; }
        }

        public sealed class Session
        {
            readonly List<Target> _targets = new();

            public static Session Begin(VRSL_URPLightManager manager) => Begin(manager, null);

            public static Session Begin(VRSL_URPLightManager dmx,
                                        VRSL_AudioLinkURPLightManager audioLink)
            {
                // Apply and Restore both tolerate an empty set, so Begin has to as well —
                // otherwise the one entry point throws while the two that follow it are
                // carefully defensive.
                var session = new Session();
                session.Add(dmx != null ? new DmxTarget(dmx) : null);
                session.Add(audioLink != null ? new AudioLinkTarget(audioLink) : null);
                return session;
            }

            void Add(Target target)
            {
                if (target == null || !target.Alive) return;
                target.Capture();
                _targets.Add(target);
            }

            /// <summary>Put every manager at a level.</summary>
            public void Apply(VRSLQuality quality)
            {
                foreach (var target in _targets)
                    if (target.Alive) target.Apply(quality);
            }

            /// <summary>Put everything back as it was found. A level left behind would
            /// quietly change the scene the author saved.</summary>
            public void Restore()
            {
                foreach (var target in _targets)
                    if (target.Alive) target.RestoreSaved();
            }
        }
    }
}
