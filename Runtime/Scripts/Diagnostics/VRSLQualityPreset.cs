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
    /// a second way to set quality behind after M1 lands the first.
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
        /// A manager held at a series of levels, and put back afterwards.
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
        public sealed class Session
        {
            VRSL_URPLightManager _manager;
            Shader _volumetricShader;
            int    _stepCount;
            bool   _useNoise;
            float  _contactStrength;
            int    _contactSteps;
            float  _contactDistance;
            float  _contactThickness;
            bool   _disableStrobe;

            public static Session Begin(VRSL_URPLightManager manager)
            {
                // Apply and Restore both tolerate a null manager, so Begin has to as well
                // — otherwise the one entry point throws while the two that follow it are
                // carefully defensive.
                if (manager == null) return new Session();

                var session = new Session
                {
                    _manager           = manager,
                    _volumetricShader  = manager.volumetricShader,
                    _stepCount         = manager.volumetricStepCount,
                    _useNoise          = manager.volumetricUseNoise,
                    _contactStrength   = manager.contactShadowStrength,
                    _contactSteps      = manager.contactShadowSteps,
                    _contactDistance   = manager.contactShadowDistance,
                    _contactThickness  = manager.contactShadowThickness,
                    _disableStrobe     = manager.disableStrobe,
                };

                // Strobing fixtures alternate, so at any instant a random subset of the
                // rig is lit and the workload changes frame to frame. Measured
                // 2026-08-24: ten fixtures under a static Ramp reported 4, 6 and 9
                // emitting across three consecutive configurations of the same subset,
                // and lights per tile followed them. Holding every strobing fixture on
                // is what makes a configuration mean one thing.
                manager.disableStrobe = true;
                return session;
            }

            /// <summary>Put the manager at a level.</summary>
            public void Apply(Level level)
            {
                if (_manager == null) return;

                if (level == Level.Off)
                {
                    _manager.contactShadowStrength = 0f;

                    // Both, and neither alone is enough. Clearing the shader leaves the
                    // material the manager already built, and the pass is enqueued on
                    // the material. Dropping the material lasts only until the next
                    // OnEnable, which builds a fresh one off the shader.
                    _manager.volumetricShader = null;
                    var material = _manager.VolumetricMaterial;
                    // Destroy rather than DestroyImmediate: this runs in play mode, and the
                    // material is one this manager built rather than an asset. Destruction
                    // is deferred to the end of the frame, which is fine because every
                    // caller settles at least one frame between applying a level and
                    // reading a counter — verified by H5, which sees zero volumetric steps
                    // at Off. Remove that settle and this becomes a race.
                    if (material != null) Object.Destroy(material);
                    _manager.MarkConfigDirty();
                    return;
                }

                // Coming back from Off: the shader reference was thrown away, so put it
                // back before bouncing, or there is nothing to build a material from.
                if (_manager.VolumetricMaterial == null)
                {
                    _manager.volumetricShader = _volumetricShader;
                    if (_volumetricShader != null) Bounce(_manager);
                }

                switch (level)
                {
                    case Level.Standard:
                        _manager.volumetricStepCount    = 24;
                        _manager.volumetricUseNoise     = true;
                        _manager.contactShadowStrength  = 1f;
                        _manager.contactShadowSteps     = 8;
                        _manager.contactShadowDistance  = 1.5f;
                        _manager.contactShadowThickness = 0.5f;
                        break;

                    case Level.High:
                        _manager.volumetricStepCount    = 40;
                        _manager.volumetricUseNoise     = true;
                        _manager.contactShadowStrength  = 1f;
                        _manager.contactShadowSteps     = 16;
                        _manager.contactShadowDistance  = 2.5f;
                        _manager.contactShadowThickness = 0.35f;
                        break;
                }

                _manager.MarkConfigDirty();
            }

            /// <summary>Put everything back as it was found. A level left behind would
            /// quietly change the scene the author saved.</summary>
            public void Restore()
            {
                if (_manager == null) return;
                _manager.volumetricShader        = _volumetricShader;
                _manager.volumetricStepCount     = _stepCount;
                _manager.volumetricUseNoise      = _useNoise;
                _manager.contactShadowStrength   = _contactStrength;
                _manager.contactShadowSteps      = _contactSteps;
                _manager.contactShadowDistance   = _contactDistance;
                _manager.contactShadowThickness  = _contactThickness;
                _manager.disableStrobe           = _disableStrobe;

                if (_volumetricShader != null && _manager.VolumetricMaterial == null)
                    Bounce(_manager);
                _manager.MarkConfigDirty();
            }
        }

        /// <summary>Off and on again, which is what rebuilds the passes and the
        /// materials. The manager re-claims the singleton on enable, so this is safe
        /// mid-session — it was not always.</summary>
        static void Bounce(VRSL_URPLightManager manager)
        {
            bool was = manager.enabled;
            manager.enabled = false;
            manager.enabled = true;
            manager.enabled = was;
        }
    }
}
