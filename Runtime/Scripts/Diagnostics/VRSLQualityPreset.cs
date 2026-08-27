using System.Collections.Generic;
using UnityEngine;

namespace VRSL.URP
{
    /// <summary>
    /// The quality levels the sweep iterates, until M1 lands the real thing.
    ///
    /// M1 introduces <c>VRSLQuality</c> as public API on the managers, with these
    /// values as constants in code. M0 and M1 run in parallel, so M0 cannot wait for
    /// it and two branches both adding the same public enum would collide. This
    /// writes the same table onto the manager's existing numeric fields and puts them
    /// back afterwards, and the row configuration carries the level as a string
    /// either way — so the result format does not move when M1 lands and this is
    /// deleted.
    ///
    /// <c>internal</c> on purpose, and it stays that way. It is not a settings API,
    /// nothing outside the harness should reach it, and making it public would leave
    /// a second way to set quality behind after M1 lands the first. That is also why
    /// the two managers are reached through a private shape below rather than through
    /// a shared interface on them: an interface would be public surface added for the
    /// harness's benefit, and M1 is about to give both of them the real one.
    /// </summary>
    static class VRSLQualityPreset
    {
        public enum Level
        {
            Off = 0,
            Standard = 1,
            High = 2,
        }

        public static readonly Level[] All = { Level.Off, Level.Standard, Level.High };

        /// <summary>
        /// One manager, held at a series of levels and put back afterwards.
        ///
        /// Stateful rather than a static <c>Apply</c>, and it has to be. Turning
        /// volumetrics off means clearing the shader as well as dropping the material
        /// — anything less and the next <c>OnEnable</c> rebuilds the material straight
        /// off the shader, and a capture toggles the manager dozens of times. But a
        /// caller that has cleared the shader has nothing to put back when it moves to
        /// the next level, so the original has to be remembered somewhere. Here.
        ///
        /// Measured 2026-08-24: without this the sweep ran Off first and every level
        /// after it reported zero volumetric steps, because nothing could rebuild a
        /// material from a shader reference that had been thrown away. All three
        /// levels then measured identically, which looks like a preset doing nothing
        /// rather than one doing too much.
        /// </summary>
        abstract class Target
        {
            Shader _volumetricShader;
            int    _stepCount;
            bool   _useNoise;
            float  _contactStrength;
            int    _contactSteps;
            float  _contactDistance;
            float  _contactThickness;
            bool   _strobeHeld;

            public abstract Behaviour Component          { get; }
            public abstract Shader    VolumetricShader   { get; set; }
            public abstract Material  VolumetricMaterial { get; }
            public abstract int       StepCount          { get; set; }
            public abstract bool      UseNoise           { get; set; }
            public abstract float     ContactStrength    { get; set; }
            public abstract int       ContactSteps       { get; set; }
            public abstract float     ContactDistance    { get; set; }
            public abstract float     ContactThickness   { get; set; }

            /// <summary>Every strobing fixture held fully on, where the manager has such
            /// a switch. The AudioLink path strobes off the audio rather than off a DMX
            /// channel and has no equivalent, so this is a no-op there rather than
            /// something to fake.</summary>
            public virtual bool StrobeHeld { get => false; set { } }

            /// <summary>Ask for the config to be re-uploaded. The AudioLink manager has
            /// no dirty flag — it reads these fields as it goes — so it needs nothing
            /// here.</summary>
            public virtual void MarkDirty() { }

            /// <summary>A destroyed manager compares null through the Unity operator, and
            /// a scene can be torn down mid-session.</summary>
            public bool Alive => Component != null;

            public void Capture()
            {
                _volumetricShader = VolumetricShader;
                _stepCount        = StepCount;
                _useNoise         = UseNoise;
                _contactStrength  = ContactStrength;
                _contactSteps     = ContactSteps;
                _contactDistance  = ContactDistance;
                _contactThickness = ContactThickness;
                _strobeHeld       = StrobeHeld;

                // Strobing fixtures alternate, so at any instant a random subset of the
                // rig is lit and the workload changes frame to frame. Measured
                // 2026-08-24: ten fixtures under a static Ramp reported 4, 6 and 9
                // emitting across three consecutive configurations of the same subset,
                // and lights per tile followed them. Holding every strobing fixture on
                // is what makes a configuration mean one thing.
                StrobeHeld = true;
            }

            public void Apply(Level level)
            {
                if (level == Level.Off)
                {
                    ContactStrength = 0f;

                    // Both, and neither alone is enough. Clearing the shader leaves the
                    // material the manager already built, and the pass is enqueued on
                    // the material. Dropping the material lasts only until the next
                    // OnEnable, which builds a fresh one off the shader.
                    VolumetricShader = null;
                    var material = VolumetricMaterial;
                    // Destroy rather than DestroyImmediate: this runs in play mode, and the
                    // material is one this manager built rather than an asset. Destruction
                    // is deferred to the end of the frame, which is fine because every
                    // caller settles at least one frame between applying a level and
                    // reading a counter — verified by H5, which sees zero volumetric steps
                    // at Off. Remove that settle and this becomes a race.
                    if (material != null) Object.Destroy(material);
                    MarkDirty();
                    return;
                }

                // Coming back from Off: the shader reference was thrown away, so put it
                // back before bouncing, or there is nothing to build a material from.
                if (VolumetricMaterial == null)
                {
                    VolumetricShader = _volumetricShader;
                    if (_volumetricShader != null) Bounce();
                }

                switch (level)
                {
                    case Level.Standard:
                        StepCount        = 24;
                        UseNoise         = true;
                        ContactStrength  = 1f;
                        ContactSteps     = 8;
                        ContactDistance  = 1.5f;
                        ContactThickness = 0.5f;
                        break;

                    case Level.High:
                        StepCount        = 40;
                        UseNoise         = true;
                        ContactStrength  = 1f;
                        ContactSteps     = 16;
                        ContactDistance  = 2.5f;
                        ContactThickness = 0.35f;
                        break;
                }

                MarkDirty();
            }

            public void RestoreSaved()
            {
                VolumetricShader = _volumetricShader;
                StepCount        = _stepCount;
                UseNoise         = _useNoise;
                ContactStrength  = _contactStrength;
                ContactSteps     = _contactSteps;
                ContactDistance  = _contactDistance;
                ContactThickness = _contactThickness;
                StrobeHeld       = _strobeHeld;

                if (_volumetricShader != null && VolumetricMaterial == null) Bounce();
                MarkDirty();
            }

            /// <summary>Off and on again, which is what rebuilds the passes and the
            /// materials. The manager re-claims the singleton on enable, so this is safe
            /// mid-session — it was not always.</summary>
            void Bounce()
            {
                var component = Component;
                if (component == null) return;
                bool was = component.enabled;
                component.enabled = false;
                component.enabled = true;
                component.enabled = was;
            }
        }

        sealed class DmxTarget : Target
        {
            readonly VRSL_URPLightManager _m;
            public DmxTarget(VRSL_URPLightManager m) => _m = m;

            public override Behaviour Component          => _m;
            public override Shader    VolumetricShader   { get => _m.volumetricShader;       set => _m.volumetricShader = value; }
            public override Material  VolumetricMaterial => _m.VolumetricMaterial;
            public override int       StepCount          { get => _m.volumetricStepCount;    set => _m.volumetricStepCount = value; }
            public override bool      UseNoise           { get => _m.volumetricUseNoise;     set => _m.volumetricUseNoise = value; }
            public override float     ContactStrength    { get => _m.contactShadowStrength;  set => _m.contactShadowStrength = value; }
            public override int       ContactSteps       { get => _m.contactShadowSteps;     set => _m.contactShadowSteps = value; }
            public override float     ContactDistance    { get => _m.contactShadowDistance;  set => _m.contactShadowDistance = value; }
            public override float     ContactThickness   { get => _m.contactShadowThickness; set => _m.contactShadowThickness = value; }
            public override bool      StrobeHeld         { get => _m.disableStrobe;          set => _m.disableStrobe = value; }
            public override void      MarkDirty()        => _m.MarkConfigDirty();
        }

        sealed class AudioLinkTarget : Target
        {
            readonly VRSL_AudioLinkURPLightManager _m;
            public AudioLinkTarget(VRSL_AudioLinkURPLightManager m) => _m = m;

            public override Behaviour Component          => _m;
            public override Shader    VolumetricShader   { get => _m.volumetricShader;       set => _m.volumetricShader = value; }
            public override Material  VolumetricMaterial => _m.VolumetricMaterial;
            public override int       StepCount          { get => _m.volumetricStepCount;    set => _m.volumetricStepCount = value; }
            public override bool      UseNoise           { get => _m.volumetricUseNoise;     set => _m.volumetricUseNoise = value; }
            public override float     ContactStrength    { get => _m.contactShadowStrength;  set => _m.contactShadowStrength = value; }
            public override int       ContactSteps       { get => _m.contactShadowSteps;     set => _m.contactShadowSteps = value; }
            public override float     ContactDistance    { get => _m.contactShadowDistance;  set => _m.contactShadowDistance = value; }
            public override float     ContactThickness   { get => _m.contactShadowThickness; set => _m.contactShadowThickness = value; }
        }

        /// <summary>
        /// Every light manager in the scene, held at a series of levels and put back
        /// afterwards.
        ///
        /// A set rather than one manager, because a scene may carry both paths. Holding
        /// quality on the DMX manager alone there leaves the AudioLink volumetrics
        /// running at every level, so the <c>Off</c> capture is not off and the split
        /// between beams and surface lighting gets measured against a baseline that
        /// still has beams in it.
        /// </summary>
        public sealed class Session
        {
            readonly List<Target> _targets = new();
#if UNITY_EDITOR
            /// <summary>
            /// Automatic wiring resolution, held off for these managers while the session
            /// runs. Quality Off works by emptying <c>volumetricShader</c> and bouncing
            /// the manager, and resolution fills empty wiring on enable — without the
            /// hold the shader comes straight back and Off silently stops working.
            /// </summary>
            readonly List<System.IDisposable> _noAutoResolve = new();
#endif

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
#if UNITY_EDITOR
                if (dmx != null)       session._noAutoResolve.Add(VRSLWiring.SuppressAutoResolve(dmx));
                if (audioLink != null) session._noAutoResolve.Add(VRSLWiring.SuppressAutoResolve(audioLink));
#endif
                return session;
            }

            void Add(Target target)
            {
                if (target == null || !target.Alive) return;
                target.Capture();
                _targets.Add(target);
            }

            /// <summary>Put every manager at a level.</summary>
            public void Apply(Level level)
            {
                foreach (var target in _targets)
                    if (target.Alive) target.Apply(level);
            }

            /// <summary>Put everything back as it was found. A level left behind would
            /// quietly change the scene the author saved.</summary>
            public void Restore()
            {
                foreach (var target in _targets)
                    if (target.Alive) target.RestoreSaved();
#if UNITY_EDITOR
                // After the targets, so the restored shader is in place before resolution
                // is allowed to look again. A session that is never restored — the
                // benchmark rows open one on a manager they then destroy — leaves its
                // hold behind, which is why the hold is per manager and dies with it.
                foreach (var hold in _noAutoResolve) hold.Dispose();
                _noAutoResolve.Clear();
#endif
            }
        }
    }
}
