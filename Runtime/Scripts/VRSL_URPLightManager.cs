using System.Runtime.InteropServices;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace VRSL.URP
{
    /// <summary>Render resolution for the VRSL volumetric raymarch pass.
    /// Half is the default — half-resolution raymarch with bilateral upsample,
    /// suited to live VR. Full runs the raymarch at the camera target resolution
    /// and additively blends the result; ~4× per-pixel cost but no upsample
    /// artefacts, suited to cinematic capture and high-perf desktop targets.
    /// </summary>
    public enum VolumetricResolution
    {
        Half = 0,
        Full = 1,
    }

    /// <summary>
    /// Singleton manager for the URP realtime light path (DMX data source).
    ///
    /// Collects every VRStageLighting_DMX_RealtimeLight in the scene, uploads
    /// their static configuration to a GPU StructuredBuffer once (and again
    /// whenever a fixture's settings change), and exposes the persistent
    /// GraphicsBuffers and DMX texture RTHandles that the VRSLDMXLightPasses
    /// pass classes drive through the render graph. The manager also subscribes
    /// to RenderPipelineManager.beginCameraRendering and enqueues those passes
    /// per camera, so no ScriptableRendererFeature is required on the URP
    /// Renderer asset.
    ///
    /// Setup: add this component to any scene object, assign the three VRSL
    /// CustomRenderTextures, and assign the VRSLDMXLightUpdate compute shader.
    /// </summary>
    [AddComponentMenu("VRSL/URP Light Manager")]
    public class VRSL_URPLightManager : MonoBehaviour, IVRSLLightSource
    {
        public static VRSL_URPLightManager Instance { get; private set; }

        // ── Inspector ─────────────────────────────────────────────────────────
        [Header("DMX Render Textures")]
        public RenderTexture dmxMainTexture;
        public RenderTexture dmxMovementTexture;
        public RenderTexture dmxStrobeTexture;
        [Tooltip("StrobeTimings CRT producing the strobe phase. Published as the "
               + "_VRSLU_DMXGridStrobeTimer global, which the StrobeOutput CRT's decode shader "
               + "samples to compute the strobe gate. Leave empty if strobe isn't used.")]
        public RenderTexture dmxStrobeTimerTexture;
        [Tooltip("SpinnerTimer CRT (the CRT fed by DMXRTShader-SpinnerTimer). The URP path "
               + "samples its accumulated phase to drive gobo spin, matching the volumetric "
               + "shader's getGoboSpinSpeed() so rate changes don't cause visible jumps.")]
        public RenderTexture dmxSpinTimerTexture;

        [Header("Compute")]
        public ComputeShader computeShader;

        [Tooltip("Assign VRSLLightCull. Builds the per-tile light list so the surface and "
               + "volumetric passes only evaluate the fixtures that reach each screen tile. "
               + "Leave empty to disable tiled culling — the passes then loop every fixture on "
               + "every pixel, which is correct but scales badly past a handful of fixtures.")]
        public ComputeShader lightCullShader;

        [Header("Lighting")]
        [Tooltip("Assign Hidden/VRSL-URP/DeferredLighting (the VRSLDeferredLighting shader asset).")]
        public Shader lightingShader;

        [Tooltip("Assign Hidden/VRSL-URP/SurfaceProperties (the VRSLSurfaceProperties shader "
               + "asset). Drives the prepass that captures each surface's albedo, smoothness and "
               + "metallic so the lighting pass can run a real BRDF — that is what makes a lit "
               + "surface keep its texture colour instead of washing towards white. Costs one "
               + "extra opaque geometry pass. Leave empty to skip it, in which case every "
               + "surface is lit as a neutral mid-grey dielectric.")]
        public Shader surfacePropertiesShader;

        [Header("Contact Shadows")]
        [Range(0f, 1f)]
        [Tooltip("Screen-space contact shadows. 0 disables them and compiles the trace out. "
               + "Each light marches the depth buffer from the lit pixel towards the fixture, "
               + "so cost scales with lights-per-tile times step count — the most expensive "
               + "term in the lighting loop. Off by default for that reason. "
               + "This is contact shadowing, not shadow mapping: it only sees geometry the "
               + "camera can see, and only within Distance. An avatar in a beam shadows the "
               + "floor at its feet; a wall across the room does not, and neither does an "
               + "occluder just off the edge of the screen.")]
        public float contactShadowStrength = 0f;

        [Range(0.05f, 10f)]
        [Tooltip("How far along the ray to the fixture the trace runs, in metres. Longer "
               + "catches larger gaps but spreads the same step count thinner, so thin "
               + "geometry starts leaking light.")]
        public float contactShadowDistance = 1.5f;

        [Range(4, 32)]
        [Tooltip("Depth samples per light. Higher is more reliable on thin occluders and "
               + "costs linearly.")]
        public int contactShadowSteps = 8;

        [Range(0.05f, 5f)]
        [Tooltip("How thick a depth-buffer surface is treated as being, in metres. A depth "
               + "buffer records surfaces rather than solids, so without this bound distant "
               + "background would shadow everything in front of it.")]
        public float contactShadowThickness = 0.5f;

        [Header("Volumetric")]
        [Tooltip("Assign Hidden/VRSL-URP/VolumetricLighting (the VRSLVolumetricLighting shader asset). "
               + "The volumetric raymarch pass runs whenever this is assigned — there is no "
               + "separate enable toggle since the URP prefab path has no legacy mesh-cone "
               + "shader to fall back to. To silence cones at runtime, drive volumetricIntensity "
               + "to 0 instead.")]
        public Shader volumetricShader;

        [Tooltip("Render resolution for the raymarch. Half is half-res with bilateral upsample "
               + "(default; right for live VR). Full runs the raymarch at the camera target "
               + "resolution and additively blends — ~4× per-pixel cost, no upsample artefacts, "
               + "suited to cinematic capture or high-perf desktop targets.")]
        public VolumetricResolution volumetricResolution = VolumetricResolution.Half;

        [Range(8, 64)]
        [Tooltip("Integration steps across each light's span of the view ray. Higher = smoother, "
               + "more cost; cost scales linearly with this and with lights-per-tile. The span is "
               + "bounded to the cone rather than the whole ray, so every step lands inside the "
               + "beam and low counts go further than they otherwise would — 16 is often enough. "
               + "Raise it for wide cones, long beams or dense haze, where each step covers more "
               + "distance.")]
        public int volumetricStepCount = 24;

        [Range(0f, 2f)]
        [Tooltip("Base scattering density. Lower = subtler shafts; higher = denser haze. "
               + "Tune relative to scene scale.")]
        public float volumetricDensity = 0.1f;

        [Range(-0.95f, 0.95f)]
        [Tooltip("Henyey–Greenstein anisotropy. 0 = isotropic (cones look the same from any "
               + "angle); positive values brighten when looking down the beam; negative values "
               + "back-scatter.")]
        public float volumetricAnisotropy = 0.2f;

        [Tooltip("Colour tint applied to the accumulated in-scattering. White = no tint.")]
        [ColorUsage(showAlpha: false, hdr: false)]
        public Color volumetricTint = Color.white;

        [Range(0f, 8f)]
        [Tooltip("Global intensity multiplier for the volumetric contribution. Multiplies on "
               + "top of the per-light intensity already encoded in _VRSLLights.")]
        public float volumetricIntensity = 1f;

        [Tooltip("Couple density and tint to URP scene fog. When on, density is multiplied by "
               + "unity_FogParams.x (the scene fog coefficient) and tint by unity_FogColor — so "
               + "raising scene fog density brightens the shafts and turning fog off hides them. "
               + "When off, the manager's density and tint values are used directly. Most useful "
               + "when the project drives haze level globally from a URP VolumeProfile.")]
        public bool coupleToSceneFog = false;

        [Header("Volumetric — Modulated Density")]
        [Tooltip("Multiply density by 3D world-space noise to approximate dusty stage haze. "
               + "When off, the noise code is compiled out of the shader and there is no cost. "
               + "Adds roughly 5–10% to the raymarch pass on desktop VR when on.")]
        public bool volumetricUseNoise = true;

        [Range(0.05f, 2f)]
        [Tooltip("Spatial frequency of the dust noise in world units. Higher = finer patches; "
               + "lower = larger blobs.")]
        public float volumetricNoiseScale = 0.3f;

        [Range(0f, 2f)]
        [Tooltip("Vertical drift speed of the noise in world units per second. 0 = static.")]
        public float volumetricNoiseScrollSpeed = 0.1f;

        [Range(0f, 1f)]
        [Tooltip("How strongly the noise modulates density. 0 = clean uniform; "
               + "1 = density drops to zero in the darkest patches.")]
        public float volumetricNoiseStrength = 0.7f;

        [Header("Gobo Wheel")]
        [Tooltip("Gobo textures available to all DMX fixtures. Packed into a shared Texture2DArray. "
               + "DMX channel +11 selects the slot (0 = open/no gobo). Order matches DMX value range.")]
        public Texture2D[] goboTextures;

        [Header("Cameras")]
        [Tooltip("How VRSL treats cameras that render into a texture rather than to the "
               + "player's view — mirrors, portals, camera props. Full lights them like the "
               + "main view, which is the default because beams in a mirror are a large part "
               + "of a stage look. SurfaceOnly keeps surface lighting but drops the "
               + "volumetric raymarch, the more expensive of the two. Skip runs nothing. "
               + "Cameras feeding VRSL's own DMX readers are always skipped regardless of "
               + "this setting.")]
        public SecondaryCameraMode secondaryCameraMode = SecondaryCameraMode.Full;

        [Header("Debug")]
        [Tooltip("Log fixture collection and DMX global / CRT publishing to the Console on enable. "
               + "Use to confirm the manager found your fixtures and is feeding the _VRSLU_* globals "
               + "from the right CRTs.")]
        public bool outputDebugLogs = false;

        /// <summary>x = strength, y = trace distance, z = steps, w = thickness.</summary>
        public Vector4 ContactShadowParams =>
            new Vector4(contactShadowStrength, contactShadowDistance,
                        contactShadowSteps, contactShadowThickness);

        public enum StrobeRate
        {
            /// <summary>Phase is time times one of three fixed frequencies, picked by
            /// DMX thresholds at 0.2 and 0.5. Stateless, and what the shipped Horizontal
            /// and Vertical CRT materials do.</summary>
            StaticFrequencies = 0,
            /// <summary>Phase integrates at the channel value times the maximum
            /// frequency. What the legacy CRT material does.</summary>
            Dynamic = 1,
        }

        [Header("Strobe (channel-source path only)")]
        [Tooltip("How the strobe rate is derived when a channel source is publishing. "
               + "The CRT chain keeps using whatever its own material says; these settings "
               + "exist because a scene driven from a channel source may have no CRT "
               + "material to read. Defaults match the shipped Horizontal material.")]
        public StrobeRate strobeRate = StrobeRate.StaticFrequencies;

        [Tooltip("Static mode: the rate below a channel value of 0.2. Kept to mirror the "
               + "CRT material, but unreachable: the same 0.2 threshold that selects this "
               + "rate also holds the fixture fully on, so its phase never reaches the "
               + "output. Changing it has no effect, in the CRT chain either.")]
        public float strobeLowFrequency  = 25f;
        [Tooltip("Static mode: the rate between 0.2 and 0.5.")]
        public float strobeMedFrequency  = 45f;
        [Tooltip("Static mode: the rate above 0.5.")]
        public float strobeHighFrequency = 65f;
        [Tooltip("Dynamic mode: phase integrates at the channel value times this.")]
        public float maxStrobeFrequency  = 185f;
        [Tooltip("Hold every strobing fixture fully on. Mirrors the control panel's "
               + "global strobe disable, which only reaches the CRT materials.")]
        public bool  disableStrobe;

        [Header("Movement smoothing (channel-source path only)")]
        [Tooltip("Smoothing when a fixture's own smoothness channel reads 0. Defaults match "
               + "the shipped movement CRT material. Unlike colour, this is kept on the buffer "
               + "path: the amount is chosen per fixture from channel 13 of its sector, which "
               + "is a lighting decision rather than a filter hiding capture noise.")]
        public float movementSmoothingMax = 0.32258067f;
        [Tooltip("Smoothing when that channel reads full. Lower means the head snaps sooner.")]
        public float movementSmoothingMin = 0.16129033f;

        // ── Public API for the render passes ──────────────────────────────────
        public GraphicsBuffer  FixtureConfigBuffer { get; private set; }
        public GraphicsBuffer  LightDataBuffer     { get; private set; }
        /// <summary>Raw DMX channel bytes, four per word. Always allocated, so the
        /// compute can read it unconditionally; <see cref="ChannelCount"/> is 0 when
        /// no source is feeding it and the compute falls back to the CRT textures.</summary>
        public GraphicsBuffer  ChannelBuffer       { get; private set; }
        public int             ChannelCount        { get; private set; }
        /// <summary>Accumulated gobo-spin phase, one float per channel, integrated by
        /// the AdvanceState kernel while a channel source is publishing. Meaningless
        /// and untouched when <see cref="ChannelCount"/> is 0, where the compute reads
        /// the SpinnerTimer CRT instead.</summary>
        public GraphicsBuffer  SpinPhaseBuffer     { get; private set; }
        /// <summary>Strobe phase for the dynamic rate mode, one float per channel.
        /// Static mode derives its phase from time and leaves this untouched.</summary>
        public GraphicsBuffer  StrobePhaseBuffer   { get; private set; }
        /// <summary>Damped movement values, one float per channel, the buffer-path
        /// equivalent of the movement interpolation CRT.</summary>
        public GraphicsBuffer  MovementBuffer      { get; private set; }
        /// <summary>Seconds of show time each universe advanced this frame, one float
        /// per universe. Movement damps against this rather than against the frame
        /// delta, so a universe delivered irregularly is smoothed over the interval
        /// its data actually spans.</summary>
        public GraphicsBuffer  UniverseStepBuffer  { get; private set; }
        /// <summary>Universes the flat channel space covers. Zero when nothing is
        /// publishing.</summary>
        public int             UniverseCount       { get; private set; }

        /// <summary>The flat channel space as the manager holds it on the CPU, one byte
        /// per slot at the <see cref="VRSLDMX.SlotsPerUniverse"/> stride. These are the
        /// bytes a source published, with no smoothing applied — the buffer path has no
        /// CRT to read them back through. Empty when nothing is publishing. Borrowed:
        /// the manager rewrites it on the next upload.</summary>
        public System.ReadOnlySpan<byte> PublishedChannels =>
            ChannelCount > 0 && _flat != null ? _flat : System.Array.Empty<byte>();

        /// <summary>Show time each universe advanced this frame, one entry per universe
        /// — the CPU side of <see cref="UniverseStepBuffer"/>. Zero for a universe that
        /// received no block this frame. Empty when nothing is publishing.</summary>
        public System.ReadOnlySpan<float> UniverseSteps =>
            UniverseCount > 0 && _universeStep != null
                ? _universeStep
                : System.Array.Empty<float>();

        /// <summary>When <paramref name="universe"/>'s most recent values were latched at
        /// the desk, on the same clock as <c>Time.timeAsDouble</c>, so subtracting it from
        /// that gives staleness including the transport age the source reported. False for
        /// a universe outside the published range or one not yet heard from. Only advances
        /// on a block that moves the clock forward, so a source repeating a timestamp reads
        /// as going stale rather than as current.</summary>
        public bool TryGetUniverseLatchTime(int universe, out double latchedAt)
        {
            latchedAt = 0.0;
            // StopPublishing zeroes the counts and leaves these arrays holding the last
            // patch, so the count is what says whether any of it still means anything.
            if (_dataTime == null || _dataTimeSeen == null ||
                universe < 0 || universe >= UniverseCount ||
                universe >= _dataTime.Length || !_dataTimeSeen[universe])
                return false;
            latchedAt = _dataTime[universe];
            return true;
        }
        public RTHandle        DMXMainHandle       { get; private set; }
        public RTHandle        DMXMovementHandle   { get; private set; }
        public RTHandle        DMXStrobeHandle     { get; private set; }
        public RTHandle        DMXSpinTimerHandle  { get; private set; }
        public RenderTexture   GoboArray           { get; private set; }
        public int  FixtureCount   { get; private set; }
        public int  GoboCount      { get; private set; }
        public int  ComputeKernel  { get; private set; }
        public Material LightingMaterial   { get; private set; }
        public Material VolumetricMaterial { get; private set; }

        // Volumetric shader parameter packing — read by VRSLDMXLightPasses.VolumetricPass
        // each frame and uploaded as global vectors before the raymarch pass.
        public Vector4 VolumetricStepParams =>
            new Vector4(volumetricStepCount, coupleToSceneFog ? 1f : 0f, 0f, volumetricAnisotropy);
        public Vector4 VolumetricDensityParams =>
            new Vector4(volumetricDensity, volumetricNoiseScale,
                        volumetricNoiseScrollSpeed, volumetricNoiseStrength);
        public Vector4 VolumetricFogTintParams =>
            new Vector4(volumetricTint.r, volumetricTint.g, volumetricTint.b, volumetricIntensity);
        public bool VolumetricUseNoise => volumetricUseNoise;
        public bool VolumetricUseFullRes => volumetricResolution == VolumetricResolution.Full;

        // ── Structs — must match VRSLLightingLibrary.hlsl exactly ─────────────
        // 8 × float4 = 128 bytes
        [StructLayout(LayoutKind.Sequential)]
        internal struct VRSLFixtureConfig
        {
            public Vector4 positionAndRange;    // xyz=pos,     w=range
            public Vector4 forwardAndType;      // xyz=forward, w=lightType(0=spot,1=point)
            public Vector4 rightAndMaxIntensity;// xyz=local +X in world space (tilt axis), w=maxIntensity
            public Vector4 spotAngles;          // x=innerRatio(0..1), y=maxOuterHalf(deg),
                                               //   z=finalIntensity,    w=minOuterHalf(deg)
            public Vector4 dmxChannel;          // x=absChannel, y=enableStrobe,
                                               //   z=enablePanTilt, w=enableFineChannels
            public Vector4 panSettings;         // x=maxMinPan, y=panOffset, z=invertPan, w=enableGoboSpin
            public Vector4 tiltSettings;        // x=maxMinTilt,y=tiltOffset,z=invertTilt,w=enableGobo
            public Vector4 extras;              // x=emitterDepth(m), yzw=reserved
        }

        // 4 × float4 = 64 bytes — must match VRSLLightData in VRSLLightingLibrary.hlsl,
        // including the two packed slots (see the accessors there).
        [StructLayout(LayoutKind.Sequential)]
        internal struct VRSLLightData
        {
            public Vector4 positionAndRange;
            public Vector4 directionAndType;
            public Vector4 colorAndIntensity;
            public Vector4 spotParams;
        }

        List<VRStageLighting_DMX_RealtimeLight> _fixtures = new();
        bool _configDirty = true;

        // Render-pass instances. Allocated in OnEnable, reused across cameras and
        // frames, dropped in OnDisable. Stateless beyond renderPassEvent and
        // ConfigureInput flags, so a single instance per pass type is correct
        // even with multiple cameras.
        VRSLDMXLightPasses.ComputePass    _computePass;
        VRSLSurfacePrepass                _surfacePrepass;
        VRSLTileCullPass                  _tileCullPass;
        VRSLDMXLightPasses.LightingPass   _lightingPass;
        VRSLDMXLightPasses.VolumetricPass _volumetricPass;
        bool _injectionSubscribed;

        /// <summary>Per-tile light culling for the current camera. Null until the
        /// passes are allocated, and inert when <c>lightCullShader</c> is unassigned.</summary>
        public VRSLTileCullPass TileCullPass => _tileCullPass;

#if UNITY_EDITOR
        // Called by Unity when the component is first added or the context-menu Reset is chosen.
        void Reset() => LoadDefaultGoboWheel();

        [ContextMenu("Load Default Gobo Wheel")]
        void LoadDefaultGoboWheel()
        {
            const string folder =
                "Packages/town.mr.vrsl-urp/Runtime/Textures/MoverLightTextures/GOBO/IndividualGobos";

            var guids = UnityEditor.AssetDatabase.FindAssets("t:Texture2D", new[] { folder });
            var list  = new List<Texture2D>();
            foreach (var guid in guids)
            {
                var tex = UnityEditor.AssetDatabase.LoadAssetAtPath<Texture2D>(
                    UnityEditor.AssetDatabase.GUIDToAssetPath(guid));
                if (tex != null) list.Add(tex);
            }

            // Default gobo first (matches shader slot 1 = lowest DMX values), then alphabetically
            list.Sort((a, b) =>
            {
                bool aD = a.name.Contains("Default");
                bool bD = b.name.Contains("Default");
                if (aD != bD) return aD ? -1 : 1;
                return string.Compare(a.name, b.name, System.StringComparison.Ordinal);
            });

            goboTextures = list.ToArray();
            UnityEditor.EditorUtility.SetDirty(this);
        }
#endif

        // ── Lifecycle ─────────────────────────────────────────────────────────
        void Awake()
        {
            // A component that starts switched off still gets Awake, but never OnEnable
            // or OnDisable — so a claim made here would never be released, and the
            // manager that is actually running would destroy itself as a duplicate
            // against an owner that does nothing. Awake runs only on an active
            // GameObject, so `enabled` on its own is the whole condition.
            if (!enabled) return;

            if (Instance != null && Instance != this) { Destroy(this); return; }
            Instance = this;
        }

        void OnEnable()
        {
            // Awake claims the singleton and OnDisable releases it, but Awake does not
            // run again on re-enable, so a component toggled off and on used to leave
            // Instance null for the rest of the session. Everything that reaches the
            // manager goes through Instance — the render passes, and any
            // IVRSLDMXChannelSource registering itself — so the whole DMX path went
            // quiet with no error. Re-claim it here, and only when it is free, so a
            // second manager that took ownership meanwhile keeps it.
            if (Instance == null) Instance = this;
            // A manager that does not own the singleton must not set itself up. Nothing
            // downstream checks ownership — OnBeginCameraRendering least of all — so a
            // non-owner that subscribed would enqueue a second set of passes and write
            // the same shader globals, which is duplicated work and wrong lighting
            // rather than a harmless spare component.
            if (Instance != this) return;
            TakeOwnership();
        }

        /// <summary>Set up as the manager everything downstream reaches through
        /// <see cref="Instance"/>. Called on enable, and on being handed the singleton
        /// by an owner that is standing down.</summary>
        void TakeOwnership()
        {
            CreateTextureHandles();
            RefreshFixtures();
            // After RefreshFixtures, which releases the fixture buffers and would
            // once have taken these with it. Allocated up front so the compute pass
            // has a valid binding on the first frame, before any LateUpdate runs.
            EnsureChannelBuffer(0);
            EnsureSpinPhaseBuffer(0);
            EnsureStrobePhaseBuffer(0);
            EnsureMovementBuffer(0);
            EnsureUniverseStepBuffer(0);
            SubscribeRuntimeInjection();
            VRStageLighting_DMX_RealtimeLight.ConfigChanged += OnFixtureConfigChanged;
        }

        void OnDisable()
        {
            // Released here, not only in OnDestroy. Without it a disabled manager goes on
            // owning the singleton, so a second one destroys itself in Awake against an
            // owner that is not running — and the guarded reclaim in OnEnable can never
            // fire, because Instance is never null for it to claim.
            bool wasOwner = Instance == this;
            if (wasOwner) Instance = null;
            VRStageLighting_DMX_RealtimeLight.ConfigChanged -= OnFixtureConfigChanged;
            UnsubscribeRuntimeInjection();
            _tileCullPass?.Dispose();
            _tileCullPass = null;
            // Resolves its shader in the constructor, so it has to be rebuilt
            // rather than reused — otherwise assigning a shader and re-enabling
            // the component silently changes nothing.
            _surfacePrepass = null;
            ReleaseBuffers();
            ReleaseChannelBuffers();
            ReleaseTextureHandles();

            // Last, so this one has finished tearing down before another starts
            // building. Both hold their own buffers, but the shipped state should
            // never be two managers with resources allocated at once.
            if (wasOwner) HandOverOwnership();
        }
        /// <summary>
        /// Give the singleton to another manager that is still running.
        ///
        /// Nothing re-runs <c>OnEnable</c> on a component that is already enabled, so a
        /// manager that stood down because something else owned the singleton would stay
        /// stood down for the rest of the session once that owner was switched off. The
        /// scene would then hold an enabled manager, no owner, and no lighting.
        /// </summary>
        void HandOverOwnership()
        {
            // The source published to this manager by name, so it goes across with the
            // singleton. Left behind, the new owner starts with none and its first
            // UploadChannels stops publishing — and the source's own OnDisable, which
            // looks the manager up through Instance, would no longer recognise itself
            // and would leave a dangling reference behind on this one.
            var source = ChannelSource;
            foreach (var other in FindObjectsByType<VRSL_URPLightManager>(
                         FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (other == null || other == this || !other.isActiveAndEnabled) continue;
                // Scene teardown disables every manager in turn. Handing over then would
                // build buffers on a component that is about to be destroyed, and
                // allocating during shutdown is where Unity starts logging.
                if (!other.gameObject.scene.isLoaded) continue;
                Instance = other;
                other.ChannelSource = source;
                other.TakeOwnership();
                return;
            }
        }


        // Inspector tweak on any VRStageLighting_DMX_RealtimeLight in the scene.
        // Mark dirty so the next LateUpdate re-uploads the config buffer with the
        // new field values (emitterDepth, range, spot angles, etc.).
        void OnFixtureConfigChanged() => MarkConfigDirty();

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        void LateUpdate()
        {
            // Ownership gate, same rule OnEnable applies to setting up. Nothing below
            // checks it, and UploadChannels with no source reaches StopPublishing, which
            // rebinds the channel buffer and zeroes the channel-count global. A manager
            // that is enabled but does not own the singleton would therefore blank the
            // owner's DMX data every frame, with no error and no visible cause.
            if (Instance != this) return;

            if (_configDirty)
            {
                UploadFixtureConfigs();
                _configDirty = false;
            }
            UploadChannels();
            AdvanceState();
        }

        // ── DMX channels as bytes ─────────────────────────────────────────────
        static readonly int s_DMXChannels     = Shader.PropertyToID("_VRSLU_DMXChannels");
        static readonly int s_DMXChannelCount = Shader.PropertyToID("_VRSLU_DMXChannelCount");

        // A Raw buffer holds four channels per 32-bit word, so channel n lives at
        // byte n and the shader picks its byte out of the word. One word is the
        // smallest allocation that keeps the binding valid when nothing is
        // publishing: an unbound buffer is undefined to read on some backends,
        // and the compute reads it before it checks the count.
        static readonly int s_DMXSpinPhase   = Shader.PropertyToID("_VRSLU_DMXSpinPhase");
        static readonly int s_DeltaTime      = Shader.PropertyToID("_VRSLU_DeltaTime");
        static readonly int s_DMXStrobePhase = Shader.PropertyToID("_VRSLU_DMXStrobePhase");
        static readonly int s_StrobeStatic   = Shader.PropertyToID("_VRSLU_StrobeStatic");
        static readonly int s_StrobeFreqs    = Shader.PropertyToID("_VRSLU_StrobeFreqs");
        static readonly int s_TimeY          = Shader.PropertyToID("_VRSLU_TimeY");
        static readonly int s_StrobeDisabled = Shader.PropertyToID("_VRSLU_StrobeDisabled");
        static readonly int s_DMXMovement    = Shader.PropertyToID("_VRSLU_DMXMovement");
        static readonly int s_MoveSmooth     = Shader.PropertyToID("_VRSLU_MoveSmooth");
        static readonly int s_UniverseStep   = Shader.PropertyToID("_VRSLU_UniverseStep");
        static readonly int s_UniverseCount  = Shader.PropertyToID("_VRSLU_UniverseCount");
        static readonly int s_SlotsPerUni    = Shader.PropertyToID("_VRSLU_SlotsPerUniverse");
        int _moveKernel = -1;
        int _advanceKernel = -1;
        int _strobeKernel  = -1;

        Vector4 StrobeFreqs => new Vector4(strobeLowFrequency, strobeMedFrequency,
                                           strobeHighFrequency, maxStrobeFrequency);

        void EnsureSpinPhaseBuffer(int channelCount)
        {
            int count = Mathf.Max(1, channelCount);
            if (SpinPhaseBuffer != null && SpinPhaseBuffer.count == count) return;
            SpinPhaseBuffer?.Release();
            // Cleared on allocation so a resize starts from zero phase rather than
            // from whatever the driver left there.
            SpinPhaseBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, sizeof(float));
            SpinPhaseBuffer.SetData(new float[count]);
        }

        // Always allocated, even in static mode where nothing writes it: the compute
        // declares the buffer and an unbound resource is undefined to read on some
        // backends, the same reason the channel buffer is never left unallocated.
        void EnsureMovementBuffer(int channelCount)
        {
            int count = Mathf.Max(1, channelCount);
            if (MovementBuffer != null && MovementBuffer.count == count) return;
            MovementBuffer?.Release();
            MovementBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, sizeof(float));
            MovementBuffer.SetData(new float[count]);
        }

        void EnsureStrobePhaseBuffer(int channelCount)
        {
            int count = Mathf.Max(1, channelCount);
            if (StrobePhaseBuffer != null && StrobePhaseBuffer.count == count) return;
            StrobePhaseBuffer?.Release();
            StrobePhaseBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, sizeof(float));
            StrobePhaseBuffer.SetData(new float[count]);
        }

        void EnsureUniverseStepBuffer(int universeCount)
        {
            int count = Mathf.Max(1, universeCount);
            if (UniverseStepBuffer != null && UniverseStepBuffer.count == count) return;
            UniverseStepBuffer?.Release();
            UniverseStepBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, sizeof(float));
            UniverseStepBuffer.SetData(new float[count]);
        }

        // Integrators advance here rather than in the render pass, and the difference
        // is not cosmetic: the pass runs once per camera, so phase would accumulate
        // twice per frame in stereo and again for every mirror in the scene, making
        // the spin rate a function of how many cameras are looking.
        void AdvanceState()
        {
            if (computeShader == null || ChannelCount == 0 || SpinPhaseBuffer == null) return;
            if (_advanceKernel < 0) _advanceKernel = computeShader.FindKernel("AdvanceState");

            computeShader.SetBuffer(_advanceKernel, s_DMXChannels,     ChannelBuffer);
            computeShader.SetInt(   s_DMXChannelCount,                 ChannelCount);
            computeShader.SetBuffer(_advanceKernel, s_DMXSpinPhase,    SpinPhaseBuffer);
            computeShader.SetFloat( s_DeltaTime,                       Time.deltaTime);
            computeShader.Dispatch(_advanceKernel, Mathf.CeilToInt(ChannelCount / 64f), 1, 1);

            if (MovementBuffer != null && UniverseStepBuffer != null)
            {
                if (_moveKernel < 0) _moveKernel = computeShader.FindKernel("AdvanceMovement");
                computeShader.SetBuffer(_moveKernel, s_DMXChannels,   ChannelBuffer);
                computeShader.SetBuffer(_moveKernel, s_DMXMovement,   MovementBuffer);
                computeShader.SetBuffer(_moveKernel, s_UniverseStep,  UniverseStepBuffer);
                computeShader.SetInt(   s_UniverseCount,              UniverseCount);
                computeShader.SetInt(   s_SlotsPerUni,                VRSLDMX.SlotsPerUniverse);
                computeShader.SetVector(s_MoveSmooth,
                    new Vector4(movementSmoothingMax, movementSmoothingMin, 0f, 0f));
                computeShader.Dispatch(_moveKernel, Mathf.CeilToInt(ChannelCount / 64f), 1, 1);
            }

            // Static mode has no integral to advance; its phase is a function of time,
            // computed where it is read. Skipping the dispatch is the whole saving.
            if (strobeRate != StrobeRate.Dynamic || StrobePhaseBuffer == null) return;
            if (_strobeKernel < 0) _strobeKernel = computeShader.FindKernel("AdvanceStrobe");
            computeShader.SetBuffer(_strobeKernel, s_DMXChannels,     ChannelBuffer);
            computeShader.SetBuffer(_strobeKernel, s_DMXStrobePhase,  StrobePhaseBuffer);
            computeShader.SetVector(s_StrobeFreqs,                    StrobeFreqs);
            computeShader.Dispatch(_strobeKernel, Mathf.CeilToInt(ChannelCount / 64f), 1, 1);
        }

        void EnsureChannelBuffer(int channelCount)
        {
            int words = Mathf.Max(1, (channelCount + 3) / 4);
            if (ChannelBuffer != null && ChannelBuffer.count == words) return;
            ChannelBuffer?.Release();
            ChannelBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Raw, words, 4);
            Shader.SetGlobalBuffer(s_DMXChannels, ChannelBuffer);
        }

        void UploadChannels()
        {
            int universes = ChannelSource != null ? ChannelSource.UniverseCount : 0;
            if (universes <= 0) { StopPublishing(); return; }

            int count = universes * VRSLDMX.SlotsPerUniverse;
            EnsureFlatSpace(universes);
            EnsureChannelBuffer(count);
            EnsureSpinPhaseBuffer(count);
            EnsureStrobePhaseBuffer(count);
            EnsureMovementBuffer(count);
            EnsureUniverseStepBuffer(universes);

            ScatterBlocks(universes);

            // SetData wants whole elements, so the tail of the last word is padded
            // rather than partially written. Those bytes are past the count the
            // shader honours, so their content never reaches a fixture.
            int words = (count + 3) / 4;
            if (_channelWords == null || _channelWords.Length != words)
                _channelWords = new uint[words];
            for (int i = 0; i < words; i++)
            {
                int b = i * 4;
                uint w = 0;
                for (int k = 0; k < 4; k++)
                {
                    int idx = b + k;
                    if (idx < count) w |= (uint)_flat[idx] << (k * 8);
                }
                _channelWords[i] = w;
            }
            ChannelBuffer.SetData(_channelWords);
            UniverseStepBuffer.SetData(_universeStep);

            UniverseCount = universes;
            ChannelCount  = count;
            Shader.SetGlobalInt(s_DMXChannelCount, count);
        }

        void StopPublishing()
        {
            EnsureChannelBuffer(0);
            EnsureSpinPhaseBuffer(0);
            EnsureStrobePhaseBuffer(0);
            EnsureMovementBuffer(0);
            EnsureUniverseStepBuffer(0);
            UniverseCount = 0;
            ChannelCount  = 0;
            Shader.SetGlobalInt(s_DMXChannelCount, 0);
        }

        // The flat space persists between frames. Values are absolute and a block is
        // a run rather than a whole universe, so a partial snapshot corrects the slots
        // it covers and leaves the rest holding what they were last told — which is
        // what makes sending only the changed channels possible without a second
        // format. Reallocating clears it, so a source that resizes starts dark rather
        // than half-remembering an older patch.
        void EnsureFlatSpace(int universes)
        {
            int count = universes * VRSLDMX.SlotsPerUniverse;
            if (_flat != null && _flat.Length == count) return;
            _flat         = new byte[count];
            _universeStep = new float[universes];
            _dataTime     = new double[universes];
            _dataTimeSeen = new bool[universes];
        }

        void ScatterBlocks(int universes)
        {
            System.Array.Clear(_universeStep, 0, _universeStep.Length);

            if (ChannelSource == null ||
                !ChannelSource.TryGetBlocks(out var blocks, out int blockCount, out var values) ||
                blockCount <= 0)
                return;

            blockCount = Mathf.Min(blockCount, blocks.Length);
            // Double, because a universe's clock is differenced against the last frame's
            // and a float loses the millisecond somewhere in the second hour of a show.
            double now = Time.timeAsDouble;

            for (int i = 0; i < blockCount; i++)
            {
                var b = blocks[i];
                if (b.universe < 0 || b.universe >= universes ||
                    b.length <= 0 || b.start < 0 || b.valueOffset < 0)
                {
                    // Silent, a producer bug reaches a lighting designer as stale or
                    // dark fixtures rather than as a source fault.
                    if (outputDebugLogs)
                        Debug.LogWarning($"[VRSL URP] Dropped DMX block {i}: universe={b.universe} "
                                       + $"start={b.start} length={b.length} offset={b.valueOffset}, "
                                       + $"against {universes} universe(s).", this);
                    continue;
                }

                // A run may not reach into the padding between universes, which no
                // desk can address. Clamping rather than wrapping keeps a producer
                // bug inside the universe that caused it.
                int length = Mathf.Min(b.length, VRSLDMX.UsableSlotsPerUniverse - b.start);
                length = Mathf.Min(length, values.Length - b.valueOffset);
                if (outputDebugLogs && length != b.length)
                    Debug.LogWarning($"[VRSL URP] Truncated DMX block {i} on universe "
                                   + $"{b.universe} from {b.length} to {length} slot(s).", this);
                if (length <= 0) continue;

                int at = b.universe * VRSLDMX.SlotsPerUniverse + b.start;
                for (int s = 0; s < length; s++) _flat[at + s] = values[b.valueOffset + s];

                // Each universe advances its own clock, which runs at the time its
                // values were latched rather than at the time they were rendered.
                double dataTime = now - b.ageMicroseconds * 1e-6;
                if (!_dataTimeSeen[b.universe])
                {
                    _dataTimeSeen[b.universe] = true;
                    _dataTime[b.universe]     = dataTime - Time.deltaTime;
                }
                double step = dataTime - _dataTime[b.universe];
                if (step <= 0.0) continue;

                // Added rather than assigned. A universe can arrive as several runs in
                // one frame, each carrying its own age, and the clock has to advance
                // across all of them: assigning would leave only the gap between the
                // last two and silently damp over less show time than the data spans.
                // The step is cleared per frame above, so this stays frame-local.
                //
                // Capped after the addition so a universe resuming after a stall damps
                // as if a second had passed rather than snapping, which is the
                // difference between a head catching up and a head teleporting.
                _universeStep[b.universe] =
                    (float)System.Math.Min(_universeStep[b.universe] + step, MaxUniverseStep);
                _dataTime[b.universe]     = dataTime;
            }
        }

        const double MaxUniverseStep = 1.0;

        uint[]   _channelWords;
        byte[]   _flat;
        float[]  _universeStep;
        double[] _dataTime;
        bool[]   _dataTimeSeen;

        // ── Public ────────────────────────────────────────────────────────────
        /// <summary>Re-scan the scene for VRStageLighting_DMX_RealtimeLight components
        /// and rebuild the GPU buffers. Call after adding/removing fixtures at runtime.</summary>
        public void RefreshFixtures()
        {
            _fixtures.Clear();
            _fixtures.AddRange(FindObjectsByType<VRStageLighting_DMX_RealtimeLight>(
                FindObjectsInactive.Exclude));

            FixtureCount = _fixtures.Count;
            if (outputDebugLogs)
            {
                if (FixtureCount == 0)
                    Debug.LogWarning("[VRSL URP] No VRStageLighting_DMX_RealtimeLight fixtures found in the scene.", this);
                else
                    Debug.Log($"[VRSL URP] Collected {FixtureCount} DMX realtime light fixture(s).", this);
            }
            if (FixtureCount == 0) return;

            ReleaseBuffers();
            FixtureConfigBuffer = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured,
                FixtureCount,
                Marshal.SizeOf<VRSLFixtureConfig>());   // 112 bytes

            LightDataBuffer = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured,
                FixtureCount,
                Marshal.SizeOf<VRSLLightData>());       // 64 bytes

            if (computeShader != null)
                ComputeKernel = computeShader.FindKernel("UpdateLights");

            if (lightingShader != null && LightingMaterial == null)
                LightingMaterial = new Material(lightingShader) { hideFlags = HideFlags.HideAndDontSave };

            if (volumetricShader != null && VolumetricMaterial == null)
                VolumetricMaterial = new Material(volumetricShader) { hideFlags = HideFlags.HideAndDontSave };

            BuildGoboArray();
            _configDirty = true;
        }

        /// <summary>Mark config dirty so it is re-uploaded next LateUpdate.</summary>
        public void MarkConfigDirty() => _configDirty = true;

        /// <summary>
        /// Where DMX channel values come from, when they arrive as bytes rather
        /// than as a video frame. Null leaves the CRT decode chain in charge, which
        /// is the unmodified behaviour.
        /// </summary>
        public IVRSLDMXChannelSource ChannelSource
        {
            get => _channelSource;
            set
            {
                if (ReferenceEquals(_channelSource, value)) return;
                _channelSource = value;
                // The flat space holds whatever the previous source last said. Keeping
                // it across a handover would leave a new source publishing one universe
                // on top of another source's patch, which reads as a plausible cue.
                _flat = null;
            }
        }

        IVRSLDMXChannelSource _channelSource;

        // ── Internal ──────────────────────────────────────────────────────────
        void UploadFixtureConfigs()
        {
            if (FixtureConfigBuffer == null || FixtureCount == 0) return;

            var configs = new VRSLFixtureConfig[FixtureCount];
            for (int i = 0; i < FixtureCount; i++)
                configs[i] = BuildConfig(_fixtures[i]);

            FixtureConfigBuffer.SetData(configs);
        }

        VRSLFixtureConfig BuildConfig(VRStageLighting_DMX_RealtimeLight f)
        {
            // Light origin priority:
            //  1. lensTransform — explicit per-fixture anchor (truss-clamp prefabs etc.).
            //  2. fixture-body mesh centre (fixtureShellRenderers[0].bounds.center) — the
            //     geometry this light drives. Robust when the component transform sits away
            //     from the lit mesh, e.g. every fixture parked at a shared root while the bar
            //     sub-meshes are spread out (the renderer's transform is the root too, so its
            //     bounds centre is the only reliable per-bar position).
            //  3. the component's own transform.
            Vector3 pos;
            if (f.lensTransform != null)
                pos = f.lensTransform.position;
            else if (f.fixtureShellRenderers != null && f.fixtureShellRenderers.Length > 0
                     && f.fixtureShellRenderers[0] != null)
                pos = f.fixtureShellRenderers[0].bounds.center;
            else
                pos = f.transform.position;
            pos += f.lightOriginOffset;

            // Use the fixture's declared local light axis (defaults to forward for moving heads).
            // Par cans and similar fixtures whose lens faces local +Y use Vector3.up here.
            Vector3 localDir    = f.localLightDirection.sqrMagnitude > 0f
                                      ? f.localLightDirection.normalized
                                      : Vector3.forward;
            Vector3 baseForward = f.transform.TransformDirection(localDir);
            // Local +X in world space — used by the compute shader as the tilt rotation axis.
            // The volumetric mover shader rotates object-space X by the tilt matrix; we need the
            // same axis in world space since the compute shader has no ObjectToWorld matrix.
            Vector3 baseRight   = f.transform.right;

            // StaticPointLight emits omnidirectionally regardless of the isPointLight
            // toggle (which the inspector hides for that archetype).
            int   lightType    = (f.isPointLight || f.fixtureType == DMXFixtureType.StaticPointLight) ? 1 : 0;
            float outerHalf    = f.maxSpotAngle * 0.5f;
            // When enableConeWidth is false, collapse minOuter == outerHalf so the compute
            // shader's lerp over DMX ch+4 is a no-op and the cone stays fixed at maxSpotAngle.
            float minOuterHalf = f.enableConeWidth ? f.minSpotAngle * 0.5f : outerHalf;
            // Inner-to-outer ratio (0..1). Wash movers keep most of the cone bright with
            // a longer soft feather at the outer edge — broad diffuse beam without reading
            // as a flat disc. Spotlights and statics use 0.5 so the falloff occupies the
            // outer half of the cone. The compute shader applies this ratio against the
            // dynamic outer half-angle (which DMX ch+4 lerps between min and max), so the
            // inner cone tracks any cone-width changes the fixture makes at runtime.
            float innerRatio   = f.fixtureType == DMXFixtureType.MoverWashlight ? 0.65f : 0.5f;

            return new VRSLFixtureConfig
            {
                positionAndRange     = new Vector4(pos.x, pos.y, pos.z, f.range),
                forwardAndType       = new Vector4(baseForward.x, baseForward.y, baseForward.z, lightType),
                rightAndMaxIntensity = new Vector4(baseRight.x, baseRight.y, baseRight.z, f.maxIntensity),
                // spotAngles.x = inner-to-outer ratio (0..1) — applied to the dynamic
                // outer half-angle in the compute shader so it tracks ch+4 cone width.
                // spotAngles.y = max outer half-angle, spotAngles.w = min outer half-angle.
                // spotAngles.z carries the combined finalIntensity × globalIntensity scalar
                // (folded CPU-side so the compute shader stays oblivious to the split).
                spotAngles        = new Vector4(innerRatio, outerHalf,
                                                f.finalIntensity * f.globalIntensity, minOuterHalf),
                dmxChannel        = new Vector4(
                    f.ComputeAbsoluteChannel(),
                    f.enableStrobe       ? 1f : 0f,
                    f.enablePanTilt      ? 1f : 0f,
                    f.enableFineChannels ? 1f : 0f),
                panSettings  = new Vector4(
                    f.maxMinPan,
                    f.panOffset,
                    f.invertPan      ? 1f : 0f,
                    f.enableGoboSpin ? 1f : 0f),
                // Subtract 90° from tiltOffset: baseForward for moving heads already points
                // world -Y (via the 90° X root rotation), so the Rodrigues default is not re-applied.
                tiltSettings = new Vector4(
                    f.maxMinTilt,
                    f.tiltOffset - 90f,
                    f.invertTilt ? 1f : 0f,
                    f.enableGobo ? 1f : 0f),
                // extras.y carries the 5-channel-mode flag the compute uses to pick
                // the compressed static DMX layout instead of the 13-channel layout.
                // extras.z carries _CurveMod so the point light reproduces the body-glow
                // surface's non-linear dimmer response (kept in sync via the shell MPB).
                extras       = new Vector4(f.emitterDepth, f.use5ChannelMode ? 1f : 0f,
                                           f.curveMod, 0f),
            };
        }

        void CreateTextureHandles()
        {
            ReleaseTextureHandles();
            if (dmxMainTexture      != null) DMXMainHandle      = RTHandles.Alloc(dmxMainTexture);
            if (dmxMovementTexture  != null) DMXMovementHandle  = RTHandles.Alloc(dmxMovementTexture);
            if (dmxStrobeTexture    != null) DMXStrobeHandle    = RTHandles.Alloc(dmxStrobeTexture);
            if (dmxSpinTimerTexture != null) DMXSpinTimerHandle = RTHandles.Alloc(dmxSpinTimerTexture);
            PublishDMXGlobals();
        }

        // The DMX grid CRTs are bound to the compute by the render passes, but fixture-body
        // surface shaders sample them as _VRSLU_* globals instead. Publish them from the same
        // references so the manager alone drives both the render-pass lights and the surface
        // emissive — no control panel needed. Also force each CustomRenderTexture into Realtime
        // update mode, the role VRSL_LocalUIControlPanel.EnableCRTS used to fill, so the decode
        // chain keeps producing live data without the panel present.
        static readonly int s_DMXGrid            = Shader.PropertyToID("_VRSLU_DMXGridRenderTexture");
        static readonly int s_DMXGridMovement    = Shader.PropertyToID("_VRSLU_DMXGridRenderTextureMovement");
        static readonly int s_DMXGridStrobe      = Shader.PropertyToID("_VRSLU_DMXGridStrobeOutput");
        static readonly int s_DMXGridStrobeTimer = Shader.PropertyToID("_VRSLU_DMXGridStrobeTimer");
        static readonly int s_DMXGridSpin        = Shader.PropertyToID("_VRSLU_DMXGridSpinTimer");
        static readonly int s_DMXGridTexelSize   = Shader.PropertyToID("_VRSLDMXTexelSize");

        void PublishDMXGlobals()
        {
            PublishDMX("Color/Intensity", s_DMXGrid,            dmxMainTexture);
            PublishDMX("Movement",        s_DMXGridMovement,    dmxMovementTexture);
            PublishDMX("Strobe Output",   s_DMXGridStrobe,      dmxStrobeTexture);
            PublishDMX("Strobe Timings",  s_DMXGridStrobeTimer, dmxStrobeTimerTexture);
            PublishDMX("Spin Timer",      s_DMXGridSpin,        dmxSpinTimerTexture);

            // The surface decode (IndustryRead in VRSL-DMXFunctions-URP.hlsl) maps a channel to a
            // grid texel from a texel size. Publish it as our own global _VRSLDMXTexelSize — the
            // same name and value VRSLDMXLightPasses feeds the compute — rather than relying on the
            // texture's auto _TexelSize, which Unity manages/overwrites for a SetGlobalTexture-bound
            // texture (often with a wrong size). Without a correct texel size the surface's row UV
            // drifts with the texture aspect, worsening down the grid, so the highest-patched bars
            // decode the wrong (often black) cell while the render-pass light stays correct.
            if (dmxMainTexture != null)
                Shader.SetGlobalVector(s_DMXGridTexelSize, new Vector4(
                    1f / dmxMainTexture.width, 1f / dmxMainTexture.height,
                    dmxMainTexture.width,      dmxMainTexture.height));
        }

        void PublishDMX(string label, int globalId, RenderTexture tex)
        {
            if (tex == null)
            {
                if (outputDebugLogs)
                    Debug.LogWarning($"[VRSL URP] DMX {label}: no CRT assigned.", this);
                return;
            }
            Shader.SetGlobalTexture(globalId, tex);
            bool setRealtime = false;
            if (tex is CustomRenderTexture crt && crt.updateMode != CustomRenderTextureUpdateMode.Realtime)
            {
                crt.updateMode = CustomRenderTextureUpdateMode.Realtime;
                setRealtime = true;
            }
            if (outputDebugLogs)
                Debug.Log($"[VRSL URP] DMX {label}: {tex.name}{(setRealtime ? " (set Realtime)" : "")}", this);
        }

        // Reads the compute's decoded per-fixture light data back to the CPU and logs it.
        // Right-click the component (in Play mode) to run. intensity > 0 means the channel
        // data reached the fixture and decoded — so a dark scene is a rendering problem.
        // intensity 0 across fixtures means the patched channels decode to zero — a channel
        // alignment / 5CH-layout / show-is-dark problem, not a rendering one.
        [ContextMenu("Log Decoded Fixture Light Data")]
        public void LogFixtureLightData()
        {
            if (LightDataBuffer == null || FixtureCount == 0)
            {
                Debug.LogWarning("[VRSL URP] No light buffer / fixtures — enter Play mode first.", this);
                return;
            }
            var data = new VRSLLightData[FixtureCount];
            LightDataBuffer.GetData(data);
            int n = FixtureCount;
            for (int i = 0; i < n; i++)
            {
                var f = _fixtures[i];
                Vector4 c = data[i].colorAndIntensity;
                Vector4 p = data[i].positionAndRange;
                // spotParams.w is the gobo spin angle in radians, already wrapped to
                // [-2pi, 2pi]. Logged because it is the only observable of the phase
                // integrator, and a rotating gobo looks the same whether the phase
                // came from the CRT or the compute.
                float spin = data[i].spotParams.w;
                // Pan and tilt are not stored anywhere readable; they survive only as
                // the beam direction the compute derives from them. Logged because it
                // is the sole observable of the movement damping.
                Vector4 dir = data[i].directionAndType;
                // Intensity doubles as the active flag in the packed layout.
                float active = c.w > 0f ? 1f : 0f;
                bool lens = f.lensTransform != null;
                Vector3 lp = lens ? f.lensTransform.position : f.transform.position;
                var sr = (f.fixtureShellRenderers != null && f.fixtureShellRenderers.Length > 0)
                         ? f.fixtureShellRenderers[0] : null;
                Vector3 sp = sr != null ? sr.bounds.center : f.transform.position;
                Debug.Log($"[VRSL URP] Fixture {i} '{f.name}' (ch {f.ComputeAbsoluteChannel()}): intensity={c.w:F2} "
                        + $"rgb=({c.x:F3},{c.y:F3},{c.z:F3}) active={active:F0} spin={spin:F4} dir=({dir.x:F3},{dir.y:F3},{dir.z:F3}) "
                        + $"lightPos=({p.x:F1},{p.y:F1},{p.z:F1}) lensSet={lens} "
                        + $"lensPos=({lp.x:F1},{lp.y:F1},{lp.z:F1}) shellCtr=({sp.x:F1},{sp.y:F1},{sp.z:F1})",
                          f);
            }
        }

        void ReleaseTextureHandles()
        {
            RTHandles.Release(DMXMainHandle);      DMXMainHandle      = null;
            RTHandles.Release(DMXMovementHandle);  DMXMovementHandle  = null;
            RTHandles.Release(DMXStrobeHandle);    DMXStrobeHandle    = null;
            RTHandles.Release(DMXSpinTimerHandle); DMXSpinTimerHandle = null;
        }

        void BuildGoboArray()
        {
            VRSLGoboWheel.Release(ref _goboArray);
            _goboArray = VRSLGoboWheel.Build(goboTextures, out int count);
            GoboArray  = _goboArray;
            GoboCount  = count;
        }

        RenderTexture _goboArray;

        void ReleaseBuffers()
        {
            FixtureConfigBuffer?.Release(); FixtureConfigBuffer = null;
            LightDataBuffer?.Release();     LightDataBuffer     = null;
            VRSLGoboWheel.Release(ref _goboArray); GoboArray = null;
        }

        // Deliberately not part of ReleaseBuffers. That one is fixture-scoped and
        // RefreshFixtures calls it, which is public and meant to be called at
        // runtime when fixtures come and go. Releasing the channel buffers there
        // would null them under a live channel source, and the render pass would
        // drop the whole DMX compute until the next LateUpdate re-allocated —
        // a frame of darkness triggered by adding a fixture.
        void ReleaseChannelBuffers()
        {
            ChannelBuffer?.Release();       ChannelBuffer       = null;
            SpinPhaseBuffer?.Release();     SpinPhaseBuffer     = null;
            StrobePhaseBuffer?.Release();   StrobePhaseBuffer   = null;
            MovementBuffer?.Release();      MovementBuffer      = null;
            UniverseStepBuffer?.Release();  UniverseStepBuffer  = null;
            ChannelCount  = 0;
            UniverseCount = 0;
            _flat = null;
            _advanceKernel = -1;
            _strobeKernel  = -1;
            _moveKernel    = -1;
        }


        // Textures this manager consumes. A camera rendering into any of them must
        // never receive the lighting pass — see VRSLCameraFilter.
        Texture[] _ownedSources;

        Texture[] OwnedSources()
        {
            _ownedSources ??= new Texture[5];
            _ownedSources[0] = dmxMainTexture;
            _ownedSources[1] = dmxMovementTexture;
            _ownedSources[2] = dmxStrobeTexture;
            _ownedSources[3] = dmxStrobeTimerTexture;
            _ownedSources[4] = dmxSpinTimerTexture;
            return _ownedSources;
        }


        /// <summary>
        /// Prints a health report to the Console. Right-click the component in
        /// play mode. Answers, in order, the questions that "nothing is lit"
        /// leaves open: did the shaders compile, did the decode produce data, is
        /// culling keeping it, and is the prepass feeding the BRDF.
        /// </summary>
        [ContextMenu("VRSL Diagnostics")]
        public void LogDiagnostics()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"[VRSL URP] Diagnostics — DMX");

            // Everything below is populated on enable, which for a MonoBehaviour
            // without ExecuteAlways never happens in the editor. Reporting the
            // resulting zeroes as findings would send anyone reading this after
            // the wrong problem.
            if (!Application.isPlaying)
            {
                sb.AppendLine("  NOT IN PLAY MODE — the manager initialises on enable, which "
                            + "doesn't happen in the editor, so fixture, light-data and tile "
                            + "figures would all read empty regardless. Shader assignment is "
                            + "still meaningful:");
                sb.AppendLine("  " + VRSLDiagnostics.ShaderStatus("Lighting shader", lightingShader));
                sb.AppendLine("  " + VRSLDiagnostics.ShaderStatus("Volumetric shader", volumetricShader));
                sb.AppendLine("  " + VRSLDiagnostics.SurfacePrepassStatus(surfacePropertiesShader));
                sb.AppendLine("  " + VRSLDiagnostics.ComputeStatus("Decode compute", computeShader, "UpdateLights"));
                sb.AppendLine("  " + VRSLDiagnostics.ComputeStatus("Cull compute", lightCullShader, "CullLights"));
                sb.AppendLine("  Enter play mode and run this again for the rest.");
                Debug.Log(sb.ToString(), this);
                return;
            }

            if (FixtureCount == 0)
                sb.AppendLine("  Fixtures: NONE FOUND — the manager collects "
                            + "VRStageLighting_DMX_RealtimeLight components on enable and skips "
                            + "inactive ones. Fixtures added since then need RefreshFixtures().");
            else
                sb.AppendLine($"  Fixtures: {FixtureCount} collected");
            sb.AppendLine("  " + VRSLDiagnostics.ShaderStatus("Lighting shader", lightingShader));
            sb.AppendLine("  " + VRSLDiagnostics.ShaderStatus("Volumetric shader", volumetricShader));
            sb.AppendLine("  " + VRSLDiagnostics.SurfacePrepassStatus(surfacePropertiesShader));
            sb.AppendLine("  " + VRSLDiagnostics.ComputeStatus("Decode compute", computeShader, "UpdateLights"));
            sb.AppendLine("  " + VRSLDiagnostics.ComputeStatus("Cull compute", lightCullShader, "CullLights"));
            sb.AppendLine("  " + VRSLDiagnostics.LightDataStatus(LightDataBuffer, FixtureCount));
            sb.AppendLine("  " + VRSLDiagnostics.TileStatus(TileCullPass, FixtureCount));
            sb.AppendLine($"  Volumetric mode: {volumetricResolution}");
            sb.AppendLine($"  Contact shadows: {(contactShadowStrength > 0f ? $"on (strength {contactShadowStrength:F2}, {contactShadowDistance}m, {contactShadowSteps} steps)" : "off")}");
            sb.AppendLine($"  Secondary cameras: {secondaryCameraMode}");
            Debug.Log(sb.ToString(), this);
        }

        // ── Runtime pass injection ────────────────────────────────────────────
        // Drives the URP render passes via RenderPipelineManager.beginCameraRendering
        // so the package works without any ScriptableRendererFeature authoring on
        // the URP Renderer asset. Pass instances are allocated once on enable and
        // re-enqueued per camera each frame.
        void SubscribeRuntimeInjection()
        {
            if (_injectionSubscribed) return;

            _computePass    ??= new VRSLDMXLightPasses.ComputePass
            {
                renderPassEvent = RenderPassEvent.BeforeRenderingOpaques,
            };
            _surfacePrepass ??= new VRSLSurfacePrepass(surfacePropertiesShader);
            _tileCullPass   ??= new VRSLTileCullPass(lightCullShader, this);
            _lightingPass   ??= new VRSLDMXLightPasses.LightingPass
            {
                renderPassEvent = RenderPassEvent.AfterRenderingOpaques,
            };
            _volumetricPass ??= new VRSLDMXLightPasses.VolumetricPass
            {
                renderPassEvent = (RenderPassEvent)((int)RenderPassEvent.AfterRenderingOpaques + 1),
            };

            RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
            _injectionSubscribed = true;
        }

        void UnsubscribeRuntimeInjection()
        {
            if (!_injectionSubscribed) return;
            RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
            _injectionSubscribed = false;
        }

        void OnBeginCameraRendering(ScriptableRenderContext ctx, Camera cam)
        {
            var decision = VRSLCameraFilter.Evaluate(cam, secondaryCameraMode, OwnedSources());
            if (decision == VRSLCameraDecision.Skip) return;

            var camData = cam.GetUniversalAdditionalCameraData();
            if (camData == null) return;
            var renderer = camData.scriptableRenderer;
            if (renderer == null) return;

            // VRSLSurfacePrepass writes _VRSLNormalsTexture, _VRSLAlbedoTexture and
            // _VRSLMaterialTexture into VRSL-owned non-MSAA RTs before opaque
            // rendering; the lighting shader samples those globals. The lighting
            // and volumetric passes only need depth from URP, so neither requests
            // Normal or Color here.
            _lightingPass.ConfigureInput(ScriptableRenderPassInput.Depth);
            _volumetricPass.ConfigureInput(ScriptableRenderPassInput.Depth);

            // Gobo wheel is a Texture2DArray, bound globally here because the
            // render graph only accepts TextureHandle.
            if (GoboArray != null)
                Shader.SetGlobalTexture("_VRSLGobos", GoboArray);

            renderer.EnqueuePass(_computePass);
            // The surface prepass output is identical whichever manager drives it,
            // so with both active only one enqueues it. The DMX manager takes
            // priority; VRSL_AudioLinkURPLightManager defers when it sees one.
            renderer.EnqueuePass(_surfacePrepass);
            renderer.EnqueuePass(_tileCullPass);
            renderer.EnqueuePass(_lightingPass);
            if (VolumetricMaterial != null && decision == VRSLCameraDecision.Full)
            {
                renderer.EnqueuePass(_volumetricPass);
            }
        }
    }
}
