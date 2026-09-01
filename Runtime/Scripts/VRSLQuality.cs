namespace VRSL.URP
{
    /// <summary>
    /// What the package is allowed to spend on a scene.
    /// </summary>
    /// <remarks>
    /// One level in place of the numeric tunings both managers used to expose.
    /// Those set frame time directly, could not be judged by eye without a
    /// profiler, and were per scene — so every world author was handed a
    /// performance decision with no way to measure the result, and no two scenes
    /// answered it the same way.
    ///
    /// The values behind each level are constants below rather than serialised
    /// fields, which is the whole point: a hidden knob that sets frame time is
    /// still a knob.
    /// </remarks>
    public enum VRSLQuality
    {
        /// <summary>Surfaces still light. No beams in the air, no contact
        /// shadows, and nothing allocated for either.</summary>
        Off = 0,
        /// <summary>The default, and what a scene authored before this existed
        /// falls back to.</summary>
        Standard = 1,
        /// <summary>Standard's look with a finer march and a longer contact
        /// shadow trace. Costs more per pixel; changes no author-facing
        /// behaviour.</summary>
        High = 2,
    }

    /// <summary>
    /// The costs behind one <see cref="VRSLQuality"/>.
    /// </summary>
    /// <remarks>
    /// Both managers read this and neither keeps its own copy. A scene running
    /// the DMX and AudioLink paths together would otherwise be able to march at
    /// two different budgets depending on which manager owned the pass.
    /// </remarks>
    public readonly struct VRSLQualityLevel
    {
        /// <summary>Whether beams are drawn at all. False allocates no
        /// volumetric targets and records no volumetric pass.</summary>
        public readonly bool  Volumetrics;
        /// <summary>Upper bound on samples along one light's span. An upper
        /// bound rather than a count: M5 makes the number adaptive, and a span
        /// that crosses half a metre has no use for the whole budget.</summary>
        public readonly int   VolumetricMaxSteps;
        /// <summary>Metres between samples, once M5 lands the adaptive count.
        /// Carried now so that milestone does not have to reopen this table;
        /// nothing reads it yet.</summary>
        public readonly float VolumetricStepSpacing;
        public readonly bool  VolumetricNoise;
        public readonly bool  ContactShadows;
        public readonly int   ContactShadowSteps;
        /// <summary>How far along the ray towards the fixture the trace looks,
        /// in metres.</summary>
        public readonly float ContactShadowDistance;
        /// <summary>How thick a depth sample is treated as being, in metres.
        /// Too thin and a shadow breaks up; too thick and everything shadows
        /// itself.</summary>
        public readonly float ContactShadowThickness;

        VRSLQualityLevel(bool volumetrics, int maxSteps, float spacing, bool noise,
                         bool contactShadows, int contactSteps,
                         float contactDistance, float contactThickness)
        {
            Volumetrics            = volumetrics;
            VolumetricMaxSteps     = maxSteps;
            VolumetricStepSpacing  = spacing;
            VolumetricNoise        = noise;
            ContactShadows         = contactShadows;
            ContactShadowSteps     = contactSteps;
            ContactShadowDistance  = contactDistance;
            ContactShadowThickness = contactThickness;
        }

        /// <summary>
        /// Look constants that do not vary by level.
        /// </summary>
        /// <remarks>
        /// The noise fields were serialised per manager and set the look of the
        /// media rather than its price — the march samples the same texture the
        /// same number of times whatever these say. They are constants here so
        /// the two managers cannot disagree about what a beam looks like, which
        /// they could when each carried its own copy.
        /// </remarks>
        public const float NoiseScale       = 0.3f;
        public const float NoiseScrollSpeed = 0.1f;
        public const float NoiseStrength    = 0.7f;

        /// <summary>
        /// A level forced for every caller, or null. Harness only.
        /// </summary>
        /// <remarks>
        /// Not a settings path and not reachable from a scene: <c>internal</c>, held
        /// through <see cref="Force"/>'s disposable scope, and null in every build a
        /// world ships.
        ///
        /// It exists because A-M0-2 asks whether the harness reports a regression at
        /// all, and needs a lever big enough to clear the noise floor to ask it. That
        /// row measured the two obvious ones first and rejected both: clearing the cull
        /// cost 0.0015 ms, and Standard to High cost 0.089 ms against a 0.097 ms floor.
        /// Only an absurd step count moves this scene enough to see, and once the
        /// numeric fields are gone there is nothing left to set one with. Retiring the
        /// row instead would leave the harness with no check that it reports regressions.
        /// </remarks>
        internal static VRSLQualityLevel? Forced;

        /// <summary>Hold every caller at one level until disposed.</summary>
        internal static System.IDisposable Force(VRSLQualityLevel level)
        {
            var previous = Forced;
            Forced = level;
            return new Hold(previous);
        }

        sealed class Hold : System.IDisposable
        {
            readonly VRSLQualityLevel? _previous;
            public Hold(VRSLQualityLevel? previous) => _previous = previous;
            public void Dispose() => Forced = _previous;
        }

        /// <summary>This level with a different step budget, for the same row.</summary>
        internal VRSLQualityLevel WithMaxSteps(int maxSteps) =>
            new VRSLQualityLevel(Volumetrics, maxSteps, VolumetricStepSpacing, VolumetricNoise,
                                 ContactShadows, ContactShadowSteps,
                                 ContactShadowDistance, ContactShadowThickness);

        /// <summary>The costs for a level. Anything unrecognised is Standard,
        /// which is also what a scene deserialising an out-of-range value
        /// gets.</summary>
        public static VRSLQualityLevel For(VRSLQuality quality)
        {
            if (Forced.HasValue) return Forced.Value;

            switch (quality)
            {
                case VRSLQuality.Off:
                    return new VRSLQualityLevel(
                        volumetrics: false, maxSteps: 0, spacing: 0f, noise: false,
                        contactShadows: false, contactSteps: 0,
                        contactDistance: 0f, contactThickness: 0f);

                case VRSLQuality.High:
                    return new VRSLQualityLevel(
                        volumetrics: true, maxSteps: 40, spacing: 0.20f, noise: true,
                        contactShadows: true, contactSteps: 16,
                        contactDistance: 2.5f, contactThickness: 0.35f);

                default:
                    // Standard reproduces what the package shipped as its defaults, so a
                    // scene that never touched the old fields renders and costs what it
                    // did before.
                    return new VRSLQualityLevel(
                        volumetrics: true, maxSteps: 24, spacing: 0.35f, noise: true,
                        contactShadows: true, contactSteps: 8,
                        contactDistance: 1.5f, contactThickness: 0.5f);
            }
        }
    }
}
