using UnityEngine;
using UnityEngine.Animations;

namespace VRSL.URP
{
    /// <summary>
    /// Per-fixture config component for the AudioLink URP realtime light path.
    ///
    /// Attach this to each fixture that should light the scene from AudioLink's
    /// amplitude and colour data.
    /// This component is the sole authoring surface on URP fixture prefabs —
    /// no VRSLAudioLinkStatic sibling participates in the URP path.
    ///
    /// For moving-head fixtures: assign panTransform and tiltTransform.
    /// VRSL_AudioLinkURPLightManager reads their world-space transforms every frame,
    /// so animations driving the pan/tilt hierarchy automatically drive the light direction.
    /// </summary>
    [AddComponentMenu("VRSL/AudioLink Realtime Light")]
    public class VRSLAudioLinkRealtimeLight : MonoBehaviour, IVRSLRealtimeLight
    {
        // ── Fixture type ──────────────────────────────────────────────────────
        [Tooltip("Selects the fixture archetype this component represents. Drives "
               + "which inspector sections are shown — movers expose pan/tilt and "
               + "targetToFollow, spotlights expose gobo selection, statics hide "
               + "movement and gobo entirely. Pick Custom to keep every section "
               + "visible regardless of preset.")]
        public AudioLinkFixtureType fixtureType = AudioLinkFixtureType.MoverSpotlight;

        // ── AudioLink ─────────────────────────────────────────────────────────
        [Tooltip("React to AudioLink. Off, the light ignores the music and shines steadily at "
               + "its full intensity.")]
        public bool enableAudioLink = true;

        [Tooltip("Frequency band to react to.")]
        public AudioLinkBandState band = AudioLinkBandState.Bass;

        [Range(0, 127)]
        [Tooltip("Delay offset into the AudioLink history buffer (0 = most recent).")]
        public int delay = 0;

        [Range(1f, 15f)]
        [Tooltip("Multiplier applied to the raw band amplitude before driving intensity.")]
        public float bandMultiplier = 1f;

        // ── Color ─────────────────────────────────────────────────────────────
        [Tooltip("Source for the light color.\n"
               + "Emission: fixed color below.\n"
               + "ThemeColor0–3: AudioLink theme palette.\n"
               + "ColorChord: AudioLink color chord representative pixel.\n"
               + "ColorTexture: sample _AudioTexture at the UV coordinates below, "
               + "HSV-normalised to full brightness so dim atlas regions still "
               + "contribute meaningful color.\n"
               + "ColorTextureTraditional: same UV sample but returns the raw "
               + "texel without brightness normalisation.")]
        public ALRealtimeColorMode colorMode = ALRealtimeColorMode.Emission;

        [ColorUsage(false, true)]
        [Tooltip("Fixed emission color used when Color Mode is set to Emission.")]
        public Color emissionColor = Color.white;

        [Tooltip("UV coordinates within the manager's sampling texture (or AudioLink's "
               + "atlas if the manager has none assigned) to read when Color Mode is "
               + "set to ColorTexture or ColorTextureTraditional. Range [0,1] in both "
               + "axes. The texture itself is assigned scene-wide on the "
               + "VRSL_AudioLinkURPLightManager, mirroring how the legacy "
               + "_SamplingTexture sat on the fixture material rather than per-fixture.")]
        public Vector2 textureSamplingCoordinates = new Vector2(0f, 0.5f);

        // ── Light ─────────────────────────────────────────────────────────────
        [Tooltip("Peak light intensity at full AudioLink amplitude (1.0). Tune per scene scale.")]
        public float maxIntensity = 10f;

        [Tooltip("Spot angle in degrees for the light cone.")]
        public float spotAngle = 60f;

        [Tooltip("Light attenuation range. Increase for larger spaces.")]
        public float range = 20f;

        [Range(0f, 1f)]
        [Tooltip("Distance in metres from the fixture's lens back to the conceptual cone apex. "
               + "0 (default) treats the light as a point source — the cone tapers to zero at "
               + "the fixture position. A non-zero value pushes the apex behind the fixture so "
               + "the cone has finite width = emitterDepth × tan(halfAngle) at the lens, which "
               + "matches wide-aperture fixtures like LED bars and washes. Capped at 1m — "
               + "the cap accommodates narrow-cone fixtures (small half-angle needs more apex "
               + "pushback for the same visible width); the lensClip in VRSL_SpotAttenuation "
               + "still keeps contribution in the forward hemisphere of the lens.")]
        public float emitterDepth = 0f;

        [Tooltip("Emit as a point light instead of a spot.")]
        public bool isPointLight = false;

        [Min(1)]
        [Tooltip("Which slot of the scene's gobo wheel this light projects, 1 for the first. On "
               + "the default wheel 1 is an open beam, 2 to 8 are upstream VRSL's shaped gobos "
               + "and 9 is the Club Orion logo. A slot past the end of the wheel shows the last "
               + "one.")]
        public int goboIndex = 1;

        [Range(-10f, 10f)]
        [Tooltip("How fast the gobo turns: 20° a second for each unit, so 1 turns it once "
               + "every 18 seconds and 10 about once every 2 seconds. Positive turns it "
               + "clockwise, negative anti-clockwise, 0 holds it still.")]
        public float goboSpinSpeed = 0f;

        // ──────────────────────────────────────────────────────────────────────────
        // Discoball
        // ──────────────────────────────────────────────────────────────────────────
        [Range(-180f, 180f)]
        [Tooltip("How fast the ball turns, in degrees per second. Negative turns it the other "
               + "way. The dots sweep the room at this rate; a real ball on a motor does "
               + "about 8.")]
        public float discoballSpinSpeed = 8.2f;

        [Tooltip("Draw the dots in the haze as well as on surfaces. Hundreds of thin beams, "
               + "which looks the part in a dark room and costs a cubemap fetch per raymarch "
               + "step for this one light across everything its range covers. Off, the dots "
               + "land on surfaces only.")]
        public bool discoballBeams = false;

        [Range(0f, 1f)]
        [Tooltip("User-side intensity cap, equivalent to Final Intensity on shader fixtures.")]
        public float finalIntensity = 1f;

        [Range(0f, 1f)]
        [Tooltip("Global intensity scalar applied on top of Final Intensity. Useful for "
               + "scene-wide dimming without adjusting every fixture individually.")]
        public float globalIntensity = 1f;

        bool IVRSLRealtimeLight.IsDiscoball => fixtureType == AudioLinkFixtureType.Discoball;
        float IVRSLRealtimeLight.GlobalIntensity { get => globalIntensity; set => globalIntensity = value; }

        // ── Pan / Tilt ────────────────────────────────────────────────────────
        [Tooltip("Enable per-frame world transform read for moving-head direction. "
               + "The animation system rotates the transforms; this component reads the result.")]
        public bool enablePanTilt = false;

        [Tooltip("Transform rotated on Y for pan. Its world position is used as the light origin.")]
        public Transform panTransform;

        [Tooltip("Transform rotated on X for tilt. Its world-space forward is sent to the GPU as the light direction.")]
        public Transform tiltTransform;

        [Tooltip("Optional follow target. When assigned, the AimConstraint on tiltTransform "
               + "is rebound to this Transform at Start, so the fixture aims at it instead "
               + "of the prefab's per-instance PanTiltTarget child. Useful when many movers "
               + "should track a shared target (e.g., a stage mark, a performer rig, or a "
               + "DoublePanTiltTarget driven by an animation). Leave empty to keep the "
               + "prefab's default aim source (the per-instance PanTiltTarget).")]
        public Transform targetToFollow;

        [Tooltip("Optional child Transform marking the fixture's lens, used as the cone "
               + "origin when pan/tilt is disabled or panTransform is unassigned. Leave "
               + "empty when the prefab origin already coincides with the lens (typical "
               + "for static par lights and blinders). Pan/tilt-driven movers should "
               + "continue to assign panTransform — that takes precedence and is read "
               + "every frame so the cone tracks the rotating head. On a discoball this is "
               + "the ball: the dots come from its centre and it turns with them.")]
        public Transform lensTransform;

        // ── Fixture shell driving ─────────────────────────────────────────────
        [Tooltip("The fixture's body renderers, such as the lamp lens. Each one glows with "
               + "this fixture's AudioLink band and colour. The emission colour, colour mode "
               + "and intensities set here replace the material's own on these, so change "
               + "them here rather than on the material.")]
        public MeshRenderer[] fixtureShellRenderers;

        MaterialPropertyBlock _shellProps;

        /// <summary>
        /// Fires when any VRSLAudioLinkRealtimeLight is switched on or off, by
        /// its component or its GameObject. The manager subscribes and collects its
        /// fixtures again once, before the next frame renders, however many switched.
        /// </summary>
        internal static event System.Action EnabledChanged;

        void OnEnable()  => EnabledChanged?.Invoke();
        void OnDisable() => EnabledChanged?.Invoke();

        void Start()
        {
            DriveFixtureShells();
            ApplyTargetToFollow();
        }

        // When targetToFollow is set, rebind the AimConstraint on tiltTransform so the
        // fixture aims at the supplied Transform rather than the prefab's per-instance
        // PanTiltTarget. No-op when targetToFollow is null — the prefab's existing
        // AimConstraint source (PanTiltTarget) is kept.
        public void ApplyTargetToFollow()
        {
            if (targetToFollow == null || tiltTransform == null) return;
            var aim = tiltTransform.GetComponent<AimConstraint>();
            if (aim == null) return;
            var src = new ConstraintSource
            {
                sourceTransform = targetToFollow,
                weight = 1f,
            };
            if (aim.sourceCount > 0) aim.SetSource(0, src);
            else                     aim.AddSource(src);
        }

        // Pushes a MaterialPropertyBlock to the body MeshRenderers so the legacy
        // fixture-mesh shader (which samples AudioLink data via material properties)
        // sees the right per-instance configuration. Used on URP-only prefab
        // variants where this component is the sole authoring surface.
        public void DriveFixtureShells()
        {
            if (fixtureShellRenderers == null || fixtureShellRenderers.Length == 0) return;

            if (_shellProps == null) _shellProps = new MaterialPropertyBlock();

            // Map colorMode to the AudioLink Static body shader's sampling flags
            // (ThemeColorSampling / ColorChord / ColorTexture / Emission are
            // mutually exclusive). The two ColorTexture variants share the
            // sampling toggle but differ on the traditional flag.
            int themeColorTarget    = 0;
            int enableThemeColor    = 0;
            int enableColorChord    = 0;
            int enableTextureSample = 0;
            int useTraditional      = 0;
            if (colorMode >= ALRealtimeColorMode.ThemeColor0
                && colorMode <= ALRealtimeColorMode.ThemeColor3)
            {
                enableThemeColor = 1;
                // The body shader numbers the theme colours from 1, as the Static's field does.
                themeColorTarget = (int)colorMode - (int)ALRealtimeColorMode.ThemeColor0 + 1;
            }
            else if (colorMode == ALRealtimeColorMode.ColorChord)
            {
                enableColorChord = 1;
            }
            else if (colorMode == ALRealtimeColorMode.ColorTexture)
            {
                enableTextureSample = 1;
            }
            else if (colorMode == ALRealtimeColorMode.ColorTextureTraditional)
            {
                enableTextureSample = 1;
                useTraditional      = 1;
            }

            _shellProps.SetFloat("_EnableAudioLink",          enableAudioLink ? 1f : 0f);
            _shellProps.SetFloat("_Band",                     (float)(int)band);
            _shellProps.SetFloat("_Delay",                    delay);
            _shellProps.SetFloat("_BandMultiplier",           bandMultiplier);
            _shellProps.SetColor("_Emission",                 emissionColor);
            _shellProps.SetFloat("_GlobalIntensity",          globalIntensity);
            _shellProps.SetFloat("_FinalIntensity",           finalIntensity);
            _shellProps.SetFloat("_SpinSpeed",                goboSpinSpeed);
            _shellProps.SetInt(  "_EnableSpin",               goboSpinSpeed != 0f ? 1 : 0);
            _shellProps.SetInt(  "_ProjectionSelection",      goboIndex);
            _shellProps.SetInt(  "_EnableColorTextureSample", enableTextureSample);
            _shellProps.SetInt(  "_UseTraditionalSampling",   useTraditional);
            _shellProps.SetInt(  "_EnableThemeColorSampling", enableThemeColor);
            _shellProps.SetInt(  "_ThemeColorTarget",         themeColorTarget);
            _shellProps.SetInt(  "_EnableColorChord",         enableColorChord);
            _shellProps.SetFloat("_TextureColorSampleX",      textureSamplingCoordinates.x);
            _shellProps.SetFloat("_TextureColorSampleY",      textureSamplingCoordinates.y);

            for (int i = 0; i < fixtureShellRenderers.Length; i++)
            {
                if (fixtureShellRenderers[i] != null)
                    fixtureShellRenderers[i].SetPropertyBlock(_shellProps);
            }
        }

        // ─────────────────────────────────────────────────────────────────────

        /// <summary>World-space position to use for this light (called per-frame by the manager).</summary>
        public Vector3 GetWorldPosition()
        {
            if (enablePanTilt && panTransform != null)
                return panTransform.position;
            if (lensTransform != null)
                return lensTransform.position;
            return transform.position;
        }

        /// <summary>World-space forward direction for this light (called per-frame by the manager).</summary>
        public Vector3 GetWorldForward()
        {
            if (enablePanTilt && tiltTransform != null)
                return tiltTransform.forward;
            return transform.forward;
        }

        // Upstream's mover prefabs ship these cone widths, and this package's the matching spot angles.
        internal const float LegacySpotlightConeWidth = 2.25f, SpotlightSpotAngle = 25f;
        internal const float LegacyWashlightConeWidth = 2.7f,  WashlightSpotAngle = 60f;
        internal const float NarrowestLegacySpotAngle = 5f,    WidestLegacySpotAngle = 179f;

        /// <summary>
        /// The spot angle matching a cone width from the original VRSL's AudioLink movers
        /// (its <c>ConeWidth</c>, 0 to 5.5). Scaled so the original prefab's width gives this
        /// prefab's angle, and kept between 5° and 179°. Fixtures whose cone width did nothing
        /// in the original (pars, blinders, discoballs) get their current <see cref="spotAngle"/>.
        /// Assign the result to <see cref="spotAngle"/>; it takes effect the same frame.
        /// </summary>
        public float SpotAngleForLegacyConeWidth(float coneWidth)
        {
            switch (fixtureType)
            {
                case AudioLinkFixtureType.MoverSpotlight:
                    return ScaledSpotAngle(coneWidth, LegacySpotlightConeWidth, SpotlightSpotAngle);
                case AudioLinkFixtureType.MoverWashlight:
                    return ScaledSpotAngle(coneWidth, LegacyWashlightConeWidth, WashlightSpotAngle);
                default:
                    return spotAngle;
            }
        }

        static float ScaledSpotAngle(float coneWidth, float legacyDefault, float urpDefault) =>
            Mathf.Clamp(urpDefault * coneWidth / legacyDefault, NarrowestLegacySpotAngle, WidestLegacySpotAngle);

    }

    /// <summary>Fixture archetype for <see cref="VRSLAudioLinkRealtimeLight"/>.
    /// Drives inspector field visibility — movers expose pan/tilt, spotlights expose gobo
    /// selection, static fixtures hide both. Custom keeps every section visible for
    /// authoring novel fixtures that don't fit the four built-in shapes.</summary>
    public enum AudioLinkFixtureType
    {
        MoverSpotlight = 0,
        MoverWashlight = 1,
        StaticBlinder  = 2,
        StaticParLight = 3,
        Custom         = 4,
        /// <summary>A point light whose dots come from the manager's discoball cubemap,
        /// spinning about the fixture's up axis. Brightness follows the band like any
        /// other AudioLink fixture.</summary>
        Discoball      = 5,
    }

    /// <summary>Color source for <see cref="VRSLAudioLinkRealtimeLight"/>.</summary>
    public enum ALRealtimeColorMode
    {
        Emission                = 0,
        ThemeColor0             = 1,
        ThemeColor1             = 2,
        ThemeColor2             = 3,
        ThemeColor3             = 4,
        ColorChord              = 5,
        ColorTexture            = 6,
        ColorTextureTraditional = 7,
    }
}
