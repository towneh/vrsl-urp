using System.Runtime.InteropServices;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace VRSL.URP
{
    /// <summary>
    /// Singleton manager for the URP realtime light path (AudioLink data source).
    ///
    /// Discovers every VRStageLighting_AudioLink_RealtimeLight in the scene,
    /// uploads their per-frame config (position, forward direction, AudioLink params)
    /// to a GPU StructuredBuffer, and exposes the buffers and AudioLink RTHandle
    /// that the VRSLAudioLinkLightPasses pass classes drive through the render
    /// graph. The manager also subscribes to RenderPipelineManager.beginCameraRendering
    /// and enqueues those passes per camera, so no ScriptableRendererFeature is
    /// required on the URP Renderer asset.
    ///
    /// Unlike VRSL_URPLightManager (DMX path), the config buffer is re-uploaded every
    /// frame because pan/tilt transforms are animated and their world-space forward
    /// direction changes continuously.
    ///
    /// Setup: add this component to any persistent scene GameObject, then assign the
    /// VRSLAudioLinkLightUpdate compute shader and Hidden/VRSL-URP/DeferredLighting shader.
    /// </summary>
    [AddComponentMenu("VRSL/AudioLink URP Light Manager")]
    public class VRSL_AudioLinkURPLightManager : MonoBehaviour, IVRSLLightSource
    {
        public static VRSL_AudioLinkURPLightManager Instance { get; private set; }

        [Header("Compute")]
        public ComputeShader computeShader;

        [Tooltip("Assign VRSLLightCull. Builds the per-tile light list so the surface and "
               + "volumetric passes only evaluate the fixtures that reach each screen tile. "
               + "Leave empty to disable tiled culling. The passes then loop every fixture on "
               + "every pixel, which is correct but scales badly past a handful of fixtures.")]
        public ComputeShader lightCullShader;

        [Header("Lighting")]
        [Tooltip("Assign Hidden/VRSL-URP/DeferredLighting (the VRSLDeferredLighting shader asset).")]
        public Shader lightingShader;

        [Tooltip("Assign Hidden/VRSL-URP/SurfaceProperties (the VRSLSurfaceProperties shader "
               + "asset). Drives the prepass that captures each surface's albedo, smoothness and "
               + "metallic so the lighting pass can run a real BRDF. That is what keeps a lit "
               + "surface keep its texture colour instead of washing towards white. Costs one "
               + "extra opaque geometry pass. Leave empty to skip it, in which case every "
               + "surface is lit as a neutral mid-grey dielectric.")]
        public Shader surfacePropertiesShader;

        [Header("Lit surfaces")]
        [Tooltip("Which layers keep their own colour, gloss and normal maps when a fixture "
               + "lights them. Anything on a layer left out is still lit, but as a plain "
               + "mid-grey surface. Leave at Everything unless a layer is expensive to draw "
               + "and you can accept it lighting grey. When a DMX light manager is also "
               + "running and drawing fixtures through the same camera, it owns the prepass "
               + "and its own mask applies instead of this one.")]
        public LayerMask prepassLayers = ~0;

        [Range(0f, 4f)]
        [Tooltip("How bright every fixture's light is, all of them at once, on top of what "
               + "each fixture is already set to. 1 leaves them alone. The beams follow it, "
               + "since they are the same light seen in the haze; Volumetric Intensity then "
               + "scales the beams on their own. A control panel in the scene drives this for "
               + "the local user.")]
        public float lightIntensity = 1f;

        [Header("Performance")]
        [Tooltip("How much of the frame VRSL may use.\n\n"
               + "Standard suits most worlds. High marches the beams more finely and traces "
               + "contact shadows further, which costs more per pixel. Off keeps surfaces "
               + "lit and removes the beams and the shadows.\n\n"
               + "What each level costs is fixed in code, so a level costs the same in "
               + "every scene.")]
        public VRSLQuality quality = VRSLQuality.Standard;

        [Header("Contact shadows")]
        [Range(0f, 1f)]
        [Tooltip("Screen-space contact shadows. 0 disables them and compiles the trace out. "
               + "Each light marches the depth buffer from the lit pixel towards the fixture, "
               + "so cost scales with lights-per-tile times step count, the most expensive "
               + "term in the lighting loop. Off by default for that reason. "
               + "This is contact shadowing, not shadow mapping: it only sees geometry the "
               + "camera can see, and only within Distance. An avatar in a beam shadows the "
               + "floor at its feet; a wall across the room does not, and neither does an "
               + "occluder just off the edge of the screen.")]
        public float contactShadowStrength = 0f;

        [Tooltip("Assign Hidden/VRSL-URP/VolumetricLighting (the VRSLVolumetricLighting shader asset). "
               + "The raymarch runs when this is assigned and the quality level draws beams. "
               + "Emptying it and setting quality to Off both stop the pass being recorded, so "
               + "either is a way to switch beams off outright. Dropping volumetricIntensity to "
               + "0 hides them instead, and still pays for the march.")]
        public Shader volumetricShader;

        [Header("Volumetrics")]
        [Range(0f, 2f)]
        [Tooltip("Base scattering density. Lower = subtler shafts; higher = denser haze. "
               + "Tune relative to scene scale.")]
        public float volumetricDensity = 0.1f;

        [Range(-0.95f, 0.95f)]
        [Tooltip("Which way the haze throws light. 0 looks the same from anywhere. Positive "
               + "makes a beam flare when you look along it, towards the fixture, which is the "
               + "cinematic one. Negative brightens it from behind instead. Henyey–Greenstein "
               + "anisotropy, if you want to look it up.")]
        public float volumetricAnisotropy = 0.2f;

        [Tooltip("Tints the beams themselves, on top of whatever colour the fixture is "
               + "sending. White leaves them alone.")]
        [ColorUsage(showAlpha: false, hdr: false)]
        public Color volumetricTint = Color.white;

        [Range(0f, 8f)]
        [Tooltip("How strong the beams are, all of them at once, on top of what each fixture "
               + "is already doing. Drop it to 0 to take the beams out of the air without "
               + "touching the light landing on surfaces.")]
        public float volumetricIntensity = 1f;

        [Tooltip("Let the scene's own fog drive the haze. On, adding fog thickens the beams "
               + "and turning fog off hides them, so one control does the whole venue, useful "
               + "where the fog is already animated. Off, the density and tint here are what "
               + "you get.")]
        public bool coupleToSceneFog = false;

        [Header("Gobos")]
        [Tooltip("Gobo textures shared by all AudioLink fixtures. Packed into a Texture2DArray. "
               + "Each fixture selects a slot via its Gobo Index field. -1 = no gobo (open beam).")]
        public Texture2D[] goboTextures;

        [Tooltip("The dot pattern every discoball fixture in the scene throws, as a cubemap. "
               + "The one that ships is a plain mirror ball. Leave empty and a discoball "
               + "lights as an ordinary point light, with no dots.")]
        public Cubemap discoballCubemap;

        [Header("AudioLink")]
        [Tooltip("Scene-wide texture sampled by every AudioLink fixture in ColorTexture / "
               + "ColorTextureTraditional color modes. Mirrors the legacy AudioLink Static "
               + "approach where _SamplingTexture sat on the fixture material rather than per "
               + "fixture instance. Projects typically pick one palette/atlas/RT for all "
               + "their fixtures and rely on per-fixture textureSamplingCoordinates to choose "
               + "the colour. Accepts any Texture or RenderTexture; leave blank to fall back "
               + "to AudioLink's _AudioTexture atlas.")]
        public Texture samplingTexture;

        [Header("Secondary cameras")]
        [Tooltip("How VRSL lights cameras that render into a texture rather than to the "
               + "player's view: mirrors, portals, camera props. Each one pays for the whole "
               + "light path again, so this is where a world with mirrors spends or saves.\n\n"
               + "Match lights them exactly like the main view.\n"
               + "Reduced, the default, lights them one level below the scene: a scene at "
               + "High renders mirrors at Standard, and a scene at Standard renders them at "
               + "Low, a mirror-only level with beams at half the samples and no contact "
               + "shadows. Beams stay in the mirror at a lower price.\n"
               + "SurfaceOnly keeps surface lighting and drops the beams, the more expensive "
               + "of the two.\n"
               + "Skip runs nothing, and a mirror pointed at the rig shows it.\n\n"
               + "Cameras feeding VRSL's own data path are always skipped regardless of "
               + "this setting.")]
        public SecondaryCameraMode secondaryCameraMode = SecondaryCameraMode.Reduced;

        [Header("Troubleshooting")]
        [Tooltip("Draw VRSL's own normals prepass even where URP's could be read instead. "
               + "Costs an extra opaque geometry pass per camera. Turn it on if lit surfaces "
               + "look wrong in a way that changes with the camera angle, which is what a "
               + "normals texture in the wrong space looks like, and say which URP version "
               + "you are on. With both managers in a scene, set it on both: when a DMX "
               + "light manager draws the prepass for a camera, its setting decides what "
               + "that prepass does.")]
        public bool forceOwnNormals = false;

        /// <summary>x = strength, y = trace distance, z = steps, w = thickness, at the
        /// manager's own level.</summary>
        public Vector4 ContactShadowParams => ContactShadowParamsFor(Quality);

        /// <summary>The contact-shadow parameters at <paramref name="level"/>, which for
        /// a secondary camera is not necessarily the manager's own. All zero when the
        /// trace should not run, since the shader reads a step count of 0 as "skip".</summary>
        public Vector4 ContactShadowParamsFor(VRSLQualityLevel level)
        {
            if (!level.ContactShadows || contactShadowStrength <= 0f) return Vector4.zero;
            return new Vector4(contactShadowStrength, level.ContactShadowDistance,
                               level.ContactShadowSteps, level.ContactShadowThickness);
        }

        /// <summary>The level this manager is running at.</summary>
        public VRSLQualityLevel Quality => VRSLQualityLevel.For(quality);

        /// <summary>Whether the volumetric pass should run at all: a level that draws
        /// beams, and a shader to draw them with. Gates the material and the pass, so
        /// Off allocates nothing rather than allocating and drawing nothing.</summary>
        public bool VolumetricsEnabled => Quality.Volumetrics && volumetricShader != null;

        // ── Public API for the render passes ──────────────────────────────────
        public GraphicsBuffer FixtureConfigBuffer { get; private set; }
        public GraphicsBuffer LightDataBuffer     { get; private set; }
        public RTHandle       AudioLinkHandle     { get; private set; }
        public RTHandle       SamplingTextureHandle { get; private set; }
        public RenderTexture  GoboArray           { get; private set; }
        public int  FixtureCount  { get; private set; }
        public int  GoboCount     { get; private set; }
        public int  ComputeKernel { get; private set; }
        public Material LightingMaterial   { get; private set; }
        public Material VolumetricMaterial { get; private set; }
        /// <summary>The density field the raymarch samples, baked by
        /// <see cref="VRSLVolumetricNoise"/> on the first frame that needs it.</summary>
        public Texture  VolumetricNoiseTexture => _volumetricNoise;
        Texture _volumetricNoise;
        /// <summary>Counters from the raymarch, collected on request. Steps per
        /// light and lights skipped are only observable here: both are designed to
        /// leave the image alone.</summary>
        public VRSLVolumetricStatsProbe VolumetricStats { get; } = new();

        /// <summary>The clock the raymarch's dither and haze scroll run on, in
        /// seconds since level load. Passed from here rather than read as
        /// <c>_Time.y</c> in the shader so an image capture can hold it: the dither
        /// is meant to change every frame, and at the step floor that change
        /// reaches the quantised output on a few grazing pixels.</summary>
        public float VolumetricTime => VolumetricTimeOverride ?? Time.timeSinceLevelLoad;

        /// <summary>Set to hold the raymarch's clock. For captures that have to be
        /// repeatable; nothing in the light path sets it.</summary>
        internal float? VolumetricTimeOverride;

        public Vector4 VolumetricStepParams => VolumetricStepParamsFor(Quality);

        /// <summary>The step parameters at <paramref name="level"/>, which for a
        /// secondary camera is not necessarily the manager's own.</summary>
        public Vector4 VolumetricStepParamsFor(VRSLQualityLevel level) =>
            new Vector4(level.VolumetricMaxSteps, coupleToSceneFog ? 1f : 0f,
                        1f / Mathf.Max(level.VolumetricStepSpacing, 0.01f),
                        volumetricAnisotropy);
        public Vector4 VolumetricDensityParams =>
            new Vector4(volumetricDensity, VRSLQualityLevel.NoiseScale,
                        VRSLQualityLevel.NoiseScrollSpeed, VRSLQualityLevel.NoiseStrength);
        public Vector4 VolumetricFogTintParams =>
            new Vector4(volumetricTint.r, volumetricTint.g, volumetricTint.b, volumetricIntensity);
        public bool VolumetricUseNoise => Quality.VolumetricNoise;

        // ── Structs — must match VRSLLightingLibrary.hlsl exactly ─────────────
        // VRSLALFixtureConfig: 7 × float4 = 112 bytes
        [StructLayout(LayoutKind.Sequential)]
        internal struct VRSLALFixtureConfig
        {
            public Vector4 positionAndRange;  // xyz=world pos (per-frame), w=range
            public Vector4 forwardAndType;    // xyz=world forward (per-frame), w=light type
            public Vector4 intensityParams;   // x=maxIntensity, y=finalIntensity, zw=unused
            public Vector4 spotAngles;        // x=innerRatio(0..1), y=outerHalf(deg),
                                              //   z=emitterDepth(m), w=unused
            public Vector4 alParams;          // x=band, y=delay, z=bandMultiplier, w=colorMode
            public Vector4 emissionColor;     // xyz=linear RGB, w=unused
            public Vector4 reserved;
        }

        // VRSLLightData stride mirror — 4 × float4 = 64 bytes.
        // Content is written by the compute shader; we only need the size here.
        [StructLayout(LayoutKind.Sequential)]
        struct LightDataStride
        {
            Vector4 a, b, c, d;
        }

        List<VRStageLighting_AudioLink_RealtimeLight> _fixtures = new();
        RenderTexture _cachedAudioTex;
        Texture       _cachedSamplingTex;
        // A 1×1 black RenderTexture the manager owns, used as the sampling-slot
        // fallback when samplingTexture is empty. RenderTextures wrap into
        // RTHandle/TextureHandle deterministically, unlike Texture2D.blackTexture
        // (a Unity built-in shared resource).
        RenderTexture _fallbackBlackRT;

        // Render-pass instances. Allocated in OnEnable, reused across cameras and
        // frames, dropped in OnDisable. Stateless beyond renderPassEvent, the
        // ConfigureInput flags and the per-camera level, all set before each
        // enqueue, so a single instance per pass type is correct even with
        // multiple cameras.
        VRSLSurfacePrepass                      _surfacePrepass;

        // The decode is dispatched from here rather than from a graph pass: nothing
        // in it depends on the camera, so once per frame is the whole of it however
        // many cameras render. Executed immediately, ahead of whatever the first
        // camera's graph submits. Marked with the name the harness looks for.
        CommandBuffer _decodeCommands;
        int           _decodedFrame = -1;

        /// <summary>The surface prepass this manager enqueues, for its counters.</summary>
        public VRSLSurfacePrepass SurfacePrepass => _surfacePrepass;

        /// <summary>Whether the last camera this manager set up reads URP's normals
        /// texture rather than drawing its own, and why, in an author's words.</summary>
        public bool   UsesUrpNormals { get; private set; }
        public string NormalsSource  { get; private set; }

        readonly Dictionary<Camera, bool> _normalsByCamera = new();

        /// <summary>Whether <paramref name="cam"/> read URP's normals the last time
        /// this manager set it up, or false if it never has. Per camera, because two
        /// cameras in one frame can resolve to different renderers or sample counts
        /// and the manager-wide value is whichever came last.</summary>
        public bool UsesUrpNormalsFor(Camera cam)
            => cam != null && _normalsByCamera.TryGetValue(cam, out bool uses) && uses;

        readonly Dictionary<Camera, VRSLQuality> _qualityByCamera = new();

        /// <summary>The level <paramref name="cam"/> rendered at the last time this
        /// manager set it up, or null if it never has. The player's view renders at
        /// the manager's own level; a mirror renders at whatever the secondary-camera
        /// policy gave it, and this is the only record of which.</summary>
        public VRSLQuality? QualityFor(Camera cam)
            => cam != null && _qualityByCamera.TryGetValue(cam, out var level) ? level : null;
        VRSLTileCullPass                        _tileCullPass;
        VRSLAudioLinkLightPasses.LightingPass   _lightingPass;
        VRSLAudioLinkLightPasses.VolumetricPass _volumetricPass;
        bool _injectionSubscribed;

        /// <summary>Per-tile light culling for the current camera. Null until the
        /// passes are allocated, and inert when <c>lightCullShader</c> is unassigned.</summary>
        public VRSLTileCullPass TileCullPass => _tileCullPass;

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

        #if UNITY_EDITOR
        /// <summary>Fill any wiring an author has emptied. Deferred to the next
        /// editor tick — see VRSLWiring.ResolveOnValidate for why.</summary>
        void OnValidate() => VRSLWiring.ResolveOnValidate(this);
        #endif

        void OnEnable()
        {
            // Awake claims the singleton and OnDisable releases it, but Awake does not
            // run again on re-enable, so a component toggled off and on would leave
            // Instance null for the rest of the session and every render pass would
            // early-out with nothing in the log. Re-claim it here, and only when it is
            // free, so a second manager that took ownership meanwhile keeps it.
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
#if UNITY_EDITOR
            // First, and in TakeOwnership rather than OnEnable, because this is not
            // only reached from there: an owner standing down hands the singleton to
            // another running manager by calling this directly. That manager would
            // otherwise set itself up from whatever wiring it had, and everything
            // below builds the materials and passes from those fields.
            VRSLWiring.ResolveOnEnable(this);
#endif

            EnsureFallbackBlackRT();
            RefreshFixtures();
            // Allocate the sampling-texture handle eagerly so the compute pass on
            // the first render frame already has a valid binding; LateUpdate's
            // refresh logic alone can race with rendering on activation frames.
            TryRefreshSamplingTextureHandle();
            SubscribeRuntimeInjection();
        }

        void OnDisable()
        {
            // Released here and not only in OnDestroy. A disabled manager holding the
            // singleton means a second one enabled afterwards destroys itself in Awake
            // against an owner that is not running, and it is what makes the guarded
            // reclaim in OnEnable able to fire at all.
            bool wasOwner = Instance == this;
            if (wasOwner) Instance = null;
            UnsubscribeRuntimeInjection();
            _tileCullPass?.Dispose();
            _tileCullPass = null;
            // Resolves its shader in the constructor, so it has to be rebuilt
            // rather than reused — otherwise assigning a shader and re-enabling
            // the component silently changes nothing.
            _surfacePrepass = null;
            ReleaseBuffers();
            ReleaseAudioLinkHandle();
            ReleaseSamplingTextureHandle();
            ReleaseFallbackBlackRT();
            VRSLVolumetricNoise.Release(ref _volumetricNoise);

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
            foreach (var other in FindObjectsByType<VRSL_AudioLinkURPLightManager>(
                         FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (other == null || other == this || !other.isActiveAndEnabled) continue;
                // Scene teardown disables every manager in turn. Handing over then would
                // build buffers on a component that is about to be destroyed, and
                // allocating during shutdown is where Unity starts logging.
                if (!other.gameObject.scene.isLoaded) continue;
                Instance = other;
                other.TakeOwnership();
                return;
            }
        }


        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        // LateUpdate intentionally left empty — per-frame fixture-config upload
        // and RTHandle refreshes live in OnBeginCameraRendering instead. Under
        // XR's frame ordering LateUpdate can land too late relative to the
        // render-graph phase, with the GPU then consuming stale fixture data
        // (lights stuck on initial transforms, gobos not spinning, AudioLink
        // texture binding stale). Doing the work in OnBeginCameraRendering puts
        // the upload immediately before the passes are enqueued on the same
        // command buffer, so the GPU always sees this frame's state.

        // Guards UploadPerFrameState against running once per camera in
        // multi-camera setups (e.g., editor with Scene view + Game view, or a
        // scene with both a main and a UI camera). Reset on disable so the
        // first beginCameraRendering call after re-enable always uploads.
        int _lastPerFrameFrame = -1;

        // ── Public ────────────────────────────────────────────────────────────
        /// <summary>Re-scan the scene for AudioLink realtime light fixtures and rebuild GPU buffers.
        /// Call after adding or removing fixture GameObjects at runtime.</summary>
        public void RefreshFixtures()
        {
            _fixtures.Clear();
            _fixtures.AddRange(FindObjectsByType<VRStageLighting_AudioLink_RealtimeLight>(
                FindObjectsInactive.Exclude));

            FixtureCount = _fixtures.Count;
            if (FixtureCount == 0) return;

            ReleaseBuffers();

            FixtureConfigBuffer = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured,
                FixtureCount,
                Marshal.SizeOf<VRSLALFixtureConfig>());   // 112 bytes

            LightDataBuffer = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured,
                FixtureCount,
                Marshal.SizeOf<LightDataStride>());        // 64 bytes
            // A fresh buffer holds nothing, so the next camera decodes again even
            // when one already has this frame.
            _decodedFrame = -1;

            VolumetricStats.Allocate();

            if (computeShader != null)
                ComputeKernel = computeShader.FindKernel("UpdateLights");

            if (lightingShader != null && LightingMaterial == null)
                LightingMaterial = new Material(lightingShader) { hideFlags = HideFlags.HideAndDontSave };

            if (volumetricShader != null && VolumetricMaterial == null)
                VolumetricMaterial = new Material(volumetricShader) { hideFlags = HideFlags.HideAndDontSave };

            BuildGoboArray();
            TryRefreshAudioLinkHandle();
        }

        // ── Internal ──────────────────────────────────────────────────────────

        // Hosts everything that has to land on the GPU before this frame's
        // render passes execute — the fixture config buffer (animated transforms
        // and AudioLink config) and the RTHandle bindings. Called from
        // OnBeginCameraRendering, guarded by frame counter so a multi-camera
        // frame doesn't repeat the upload.
        void UploadPerFrameState()
        {
            int frame = Time.frameCount;
            if (_lastPerFrameFrame == frame) return;
            _lastPerFrameFrame = frame;

            UploadFixtureConfigs();
            TryRefreshAudioLinkHandle();
            TryRefreshSamplingTextureHandle();
            VolumetricStats.Tick();
        }

        // ── The decode ────────────────────────────────────────────────────────
        static readonly int s_FixtureCount     = Shader.PropertyToID("_FixtureCount");
        static readonly int s_GoboCount        = Shader.PropertyToID("_VRSLGoboCount");
        static readonly int s_Time             = Shader.PropertyToID("_VRSLTime");
        static readonly int s_ALFixtureConfigs = Shader.PropertyToID("_ALFixtureConfigs");
        static readonly int s_LightData        = Shader.PropertyToID("_LightData");
        static readonly int s_AudioTexture     = Shader.PropertyToID("_AudioTexture");
        static readonly int s_SamplingTexture  = Shader.PropertyToID("_VRSLALSamplingTexture");

        /// <summary>The marker the decode dispatch is recorded under, which the
        /// benchmark reads per pass.</summary>
        public const string DecodeMarker = "VRSL AudioLink Light Compute";

        /// <summary>
        /// Sample AudioLink into the light buffer, once per frame. Called from the
        /// first camera this manager renders each frame, after that frame's config
        /// upload, so a frame nobody renders decodes nothing and every camera in a
        /// frame reads one buffer.
        /// </summary>
        void DispatchDecode()
        {
            if (_decodedFrame == Time.frameCount) return;
            _decodedFrame = Time.frameCount;

            if (FixtureCount == 0
                || computeShader       == null
                || FixtureConfigBuffer == null
                || LightDataBuffer     == null
                || _cachedAudioTex     == null) return;

            // The kernel needs something in the sampling slot every dispatch. The
            // fallback, when the manager holds no sampling texture, is the AudioLink
            // atlas itself: mode-0 fixtures never read the slot, and the texture modes
            // then sample the atlas, which is the degraded path they always had.
            Texture sampling = _cachedSamplingTex != null ? _cachedSamplingTex : _cachedAudioTex;

            var cs  = computeShader;
            int k   = ComputeKernel;
            var cmd = _decodeCommands ??= new CommandBuffer { name = DecodeMarker };
            cmd.Clear();
            cmd.BeginSample(DecodeMarker);
            cmd.SetComputeIntParam(    cs,    s_FixtureCount,     FixtureCount);
            cmd.SetComputeIntParam(    cs,    s_GoboCount,        GoboCount);
            // timeSinceLevelLoad resets on scene reload, which is the desirable behaviour
            // for gobo spin — phase restarts cleanly with the scene.
            cmd.SetComputeFloatParam(  cs,    s_Time,             Time.timeSinceLevelLoad);
            cmd.SetComputeBufferParam( cs, k, s_ALFixtureConfigs, FixtureConfigBuffer);
            cmd.SetComputeBufferParam( cs, k, s_LightData,        LightDataBuffer);
            cmd.SetComputeTextureParam(cs, k, s_AudioTexture,     _cachedAudioTex);
            cmd.SetComputeTextureParam(cs, k, s_SamplingTexture,  sampling);
            cmd.DispatchCompute(cs, k, Mathf.CeilToInt(FixtureCount / 64f), 1, 1);
            cmd.EndSample(DecodeMarker);
            Graphics.ExecuteCommandBuffer(cmd);
        }

        void UploadFixtureConfigs()
        {
            if (FixtureConfigBuffer == null || FixtureCount == 0) return;

            var configs = new VRSLALFixtureConfig[FixtureCount];
            for (int i = 0; i < FixtureCount; i++)
                configs[i] = BuildConfig(_fixtures[i]);
            FixtureConfigBuffer.SetData(configs);
        }

        void BuildGoboArray()
        {
            VRSLGoboWheel.Release(ref _goboArray);
            _goboArray = VRSLGoboWheel.Build(goboTextures, out int count, out _goboWheelComplete);
            GoboArray  = _goboArray;
            GoboCount  = count;
        }

        /// <summary>Rebuild the wheel once every source is fully streamed in, if
        /// the first build caught one part-way. Cheap to ask every camera.</summary>
        void CompleteGoboArray()
        {
            if (_goboWheelComplete || !VRSLGoboWheel.Resident(goboTextures)) return;
            BuildGoboArray();
        }

        RenderTexture _goboArray;
        bool          _goboWheelComplete = true;

        VRSLALFixtureConfig BuildConfig(VRStageLighting_AudioLink_RealtimeLight f)
        {
            Vector3 pos     = f.GetWorldPosition();
            Vector3 forward = f.GetWorldForward();
            // A discoball turns about its own up axis, which is what the compute rotates
            // the dot pattern around; it has no light axis of its own.
            bool  discoball  = f.fixtureType == AudioLinkFixtureType.Discoball;
            if (discoball) forward = f.transform.up;

            int   lightType  = discoball ? 2 : f.isPointLight ? 1 : 0;
            float outerHalf  = f.spotAngle * 0.5f;
            // Inner-to-outer ratio (0..1). Wash movers keep most of the cone bright with
            // a longer soft feather at the outer edge — broad diffuse beam without reading
            // as a flat disc. Spotlights and statics use 0.5 so the falloff occupies the
            // outer half of the cone.
            float innerRatio = f.fixtureType == AudioLinkFixtureType.MoverWashlight ? 0.65f : 0.5f;

            // Emission color must be in linear space to match the lighting shader's expectation.
            Color linearEmission = f.emissionColor.linear;

            return new VRSLALFixtureConfig
            {
                positionAndRange = new Vector4(pos.x, pos.y, pos.z, f.range),
                forwardAndType   = new Vector4(forward.x, forward.y, forward.z, lightType),
                // intensityParams.y carries the combined finalIntensity × globalIntensity
                // scalar (folded CPU-side so the compute shader stays oblivious to the split).
                // intensityParams.z = AudioLink active flag (1 = sample amplitude, 0 = static full).
                intensityParams  = new Vector4(
                    f.maxIntensity * lightIntensity,
                    f.finalIntensity * f.globalIntensity,
                    f.enableAudioLink ? 1f : 0f,
                    discoball && f.discoballBeams ? 1f : 0f),
                // spotAngles.x = inner-to-outer ratio (0..1) — applied to the outer
                // half-angle in the compute shader.
                // spotAngles.z = emitter depth in metres (virtual cone-apex pushback for
                // area-emitter fixtures). 0 = point source.
                spotAngles       = new Vector4(innerRatio, outerHalf, f.emitterDepth, 0f),
                alParams         = new Vector4(
                    (int)f.band,
                    f.delay,
                    f.bandMultiplier,
                    (int)f.colorMode),
                emissionColor    = new Vector4(linearEmission.r, linearEmission.g, linearEmission.b, 0f),
                // reserved.x = gobo array index (0+ = slot); the inspector field is 1-based to
                // match the established AudioLink Static convention (1 = open beam).
                // reserved.y = gobo spin speed.
                // reserved.zw = textureSamplingCoordinates UV — sampled by the compute shader
                //               only when colorMode == ColorTexture (6).
                reserved         = new Vector4(
                    f.goboIndex - 1f,
                    discoball ? f.discoballSpinSpeed : f.goboSpinSpeed,
                    f.textureSamplingCoordinates.x,
                    f.textureSamplingCoordinates.y),
            };
        }

        void TryRefreshAudioLinkHandle()
        {
            // Prefer the live AudioLink global texture; fall back to the owned 1×1
            // black RT when it isn't published. AudioLink may not have initialized
            // yet on the first frames after a render-mode switch (observed under
            // Basis when toggling Desktop → OpenVR), and some host environments
            // never publish _AudioTexture at all. Without a fallback the compute
            // pass would gate-out and the lights would freeze on initial state.
            // With the fallback the compute runs and the audio-driven amplitude
            // term is just zero — transform tracking, gobo spin, and static
            // emission all still work.
            var tex = Shader.GetGlobalTexture("_AudioTexture") as RenderTexture
                   ?? _fallbackBlackRT;
            if (tex == _cachedAudioTex) return;

            ReleaseAudioLinkHandle();
            _cachedAudioTex = tex;
            if (_cachedAudioTex != null)
                AudioLinkHandle = RTHandles.Alloc(_cachedAudioTex);
        }

        void ReleaseAudioLinkHandle()
        {
            RTHandles.Release(AudioLinkHandle);
            AudioLinkHandle  = null;
            _cachedAudioTex  = null;
        }

        // The compute kernel needs SamplingTextureHandle bound every dispatch
        // (Unity 6 validates compute texture slots have valid handles), so we
        // always wrap *something* — the user's assigned texture if present, or
        // a 1×1 black RT we own as a no-op fallback. Mode 6 (HSV-normalised)
        // turns the black sample into white light; Mode 7 (traditional) keeps
        // it black.
        void TryRefreshSamplingTextureHandle()
        {
            Texture src = samplingTexture != null ? samplingTexture : _fallbackBlackRT;
            if (src == null) return;        // happens only if EnsureFallbackBlackRT failed
            if (src == _cachedSamplingTex) return;

            ReleaseSamplingTextureHandle();
            _cachedSamplingTex = src;
            SamplingTextureHandle = RTHandles.Alloc(src);
        }

        void ReleaseSamplingTextureHandle()
        {
            RTHandles.Release(SamplingTextureHandle);
            SamplingTextureHandle = null;
            _cachedSamplingTex    = null;
        }

        void EnsureFallbackBlackRT()
        {
            if (_fallbackBlackRT != null) return;
            _fallbackBlackRT = new RenderTexture(1, 1, 0, RenderTextureFormat.ARGB32,
                                                 RenderTextureReadWrite.Linear)
            {
                name      = "VRSL_AL_SamplingFallback_Black",
                hideFlags = HideFlags.HideAndDontSave,
            };
            _fallbackBlackRT.Create();
            // Clear once to a deterministic black (1×1 so cost is negligible).
            var prevActive = RenderTexture.active;
            RenderTexture.active = _fallbackBlackRT;
            GL.Clear(false, true, Color.black);
            RenderTexture.active = prevActive;
        }

        void ReleaseFallbackBlackRT()
        {
            if (_fallbackBlackRT == null) return;
            _fallbackBlackRT.Release();
            Object.Destroy(_fallbackBlackRT);
            _fallbackBlackRT = null;
        }

        void ReleaseBuffers()
        {
            FixtureConfigBuffer?.Release(); FixtureConfigBuffer = null;
            LightDataBuffer?.Release();     LightDataBuffer     = null;
            VRSLGoboWheel.Release(ref _goboArray); GoboArray = null;
            VolumetricStats.Dispose();
        }


        // Textures this manager consumes. A camera rendering into any of them must
        // never receive the lighting pass — see VRSLCameraFilter.
        Texture[] _ownedSources;

        Texture[] OwnedSources()
        {
            _ownedSources ??= new Texture[1];
            _ownedSources[0] = samplingTexture;
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
            sb.AppendLine($"[VRSL URP] Diagnostics — AudioLink");

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
                VRSLDiagnostics.AppendGpuResidentDrawerStatus(sb, "  ");
                sb.AppendLine("  " + VRSLDiagnostics.ComputeStatus("Decode compute", computeShader, "UpdateLights"));
                sb.AppendLine("  " + VRSLDiagnostics.ComputeStatus("Cull compute", lightCullShader, "CullLights"));
                sb.AppendLine("  Enter play mode and run this again for the rest.");
                Debug.Log(sb.ToString(), this);
                return;
            }

            if (FixtureCount == 0)
                sb.AppendLine("  Fixtures: NONE FOUND — the manager collects "
                            + "VRStageLighting_AudioLink_RealtimeLight components on enable and skips "
                            + "inactive ones. Fixtures added since then need RefreshFixtures().");
            else
                sb.AppendLine($"  Fixtures: {FixtureCount} collected");
            sb.AppendLine("  " + VRSLDiagnostics.ShaderStatus("Lighting shader", lightingShader));
            sb.AppendLine("  " + VRSLDiagnostics.ShaderStatus("Volumetric shader", volumetricShader));
            sb.AppendLine("  " + VRSLDiagnostics.SurfacePrepassStatus(surfacePropertiesShader));
            VRSLDiagnostics.AppendGpuResidentDrawerStatus(sb, "  ");
            sb.AppendLine("  Normals: " + (NormalsSource ?? "no camera has rendered yet"));
            sb.AppendLine("  " + VRSLDiagnostics.ComputeStatus("Decode compute", computeShader, "UpdateLights"));
            sb.AppendLine("  " + VRSLDiagnostics.ComputeStatus("Cull compute", lightCullShader, "CullLights"));
            sb.AppendLine("  " + VRSLDiagnostics.LightDataStatus(LightDataBuffer, FixtureCount));
            sb.AppendLine("  " + VRSLDiagnostics.TileStatus(TileCullPass, FixtureCount));
            var level = Quality;
            sb.AppendLine($"  Quality: {quality} (volumetrics {(level.Volumetrics ? $"on, {level.VolumetricMaxSteps} max steps" : "off")})");
            if (VolumetricsEnabled)
                sb.AppendLine("  " + VRSLDiagnostics.VolumetricMarchStatus(VolumetricStats, level.VolumetricMaxSteps));
            sb.AppendLine($"  Contact shadows: {(ContactShadowParams.x > 0f ? $"on (strength {contactShadowStrength:F2}, {level.ContactShadowDistance}m, {level.ContactShadowSteps} steps)" : "off")}");
            sb.AppendLine("  Secondary cameras: " + VRSLCameraFilter.Describe(secondaryCameraMode, quality));
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

            _surfacePrepass ??= new VRSLSurfacePrepass(surfacePropertiesShader);
            _tileCullPass   ??= new VRSLTileCullPass(lightCullShader, this);
            _lightingPass   ??= new VRSLAudioLinkLightPasses.LightingPass
            {
                renderPassEvent = RenderPassEvent.AfterRenderingOpaques,
            };
            _volumetricPass ??= new VRSLAudioLinkLightPasses.VolumetricPass
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
            // Re-upload and decode on the next enable's first camera render.
            _lastPerFrameFrame = -1;
            _decodedFrame      = -1;
            _decodeCommands?.Release();
            _decodeCommands = null;
        }

        void OnBeginCameraRendering(ScriptableRenderContext ctx, Camera cam)
        {
            var decision = VRSLCameraFilter.Evaluate(cam, secondaryCameraMode, quality, OwnedSources());
            if (!decision.Render) return;

            var camData = cam.GetUniversalAdditionalCameraData();
            if (camData == null) return;
            var renderer = camData.scriptableRenderer;
            if (renderer == null) return;

            UploadPerFrameState();

            // The level this camera's passes cost at. The passes are shared across
            // cameras and record for one camera at a time, so a field set here is
            // what they read for this camera and only this one.
            _qualityByCamera[cam]   = decision.Quality;
            _lightingPass.Quality   = decision.Quality;
            _volumetricPass.Quality = decision.Quality;

            // Where the normals come from is decided here, once per camera, and the
            // lighting shader samples one global name either way. Normal is asked
            // of URP only where its prepass can be drawn: on a multisampled camera
            // that is also depth primed, the request itself takes the frame down.
            // With no fixtures nothing would read it, so nothing is asked for.
            var normals = VRSLPrepassPolicy.Decide(cam, renderer, forceOwnNormals, prepassLayers);
            UsesUrpNormals = normals.UseUrpNormals;
            NormalsSource  = normals.Reason;
            _normalsByCamera[cam] = normals.UseUrpNormals;
            var input = ScriptableRenderPassInput.Depth;
            if (normals.UseUrpNormals && FixtureCount > 0)
                input |= ScriptableRenderPassInput.Normal;
            _lightingPass.ConfigureInput(input);
            _volumetricPass.ConfigureInput(input);
            _surfacePrepass.ConfigureInput(input);
            _surfacePrepass.UrpNormals = normals.UseUrpNormals;

            // Gobo wheel is a Texture2DArray, bound globally here because the
            // render graph only accepts TextureHandle.
            CompleteGoboArray();
            if (GoboArray != null)
                Shader.SetGlobalTexture("_VRSLGobos", GoboArray);
            if (discoballCubemap != null)
                Shader.SetGlobalTexture("_VRSLDiscoballCube", discoballCubemap);
            Shader.SetGlobalFloat("_VRSLDiscoballCubeBound", discoballCubemap != null ? 1f : 0f);

            // Baked here rather than on enable so a level switched at runtime from
            // Off finds one. Bound the way the gobo wheel is: it is not a graph
            // resource and nothing in the graph writes it.
            if (VolumetricsEnabled && VolumetricUseNoise)
            {
                if (_volumetricNoise == null)
                    _volumetricNoise = VRSLVolumetricNoise.Bake(computeShader, this);
                Shader.SetGlobalTexture("_VRSLVolNoise", _volumetricNoise);
            }

            // Before anything of this camera's records. The first camera of the frame
            // pays for it and the rest read what it wrote.
            DispatchDecode();

            // The surface prepass costs two opaque geometry draws and writes the
            // same targets regardless of which manager drives it, so when a DMX
            // manager is present and will draw it for this camera, it owns the
            // prepass and this one defers.
            //
            // Asked rather than inferred from Instance being non-null: Instance is
            // assigned in Awake and cleared only in OnDestroy, so a DMX manager
            // that is disabled, has no fixtures, or skips this camera still
            // answers non-null while enqueuing nothing. Deferring to it then would
            // leave the prepass enqueued by nobody and the lighting pass shading
            // against stale data. With no fixtures of its own nothing here reads
            // the targets either, so it is not drawn.
            //
            // When the DMX manager drives it, its prepassLayers apply and the mask
            // on this component does nothing; the tooltip says so, since the frame
            // will not.
            var dmxManager = VRSL_URPLightManager.Instance;
            bool dmxDrives = dmxManager != null && dmxManager.DrivesSurfacePrepass(cam);
            if (FixtureCount > 0 && !dmxDrives)
            {
                _surfacePrepass.Layers = prepassLayers;
                renderer.EnqueuePass(_surfacePrepass);
            }
            renderer.EnqueuePass(_tileCullPass);
            renderer.EnqueuePass(_lightingPass);
            if (VolumetricMaterial != null && decision.Volumetrics
             && VRSLQualityLevel.For(decision.Quality).Volumetrics)
            {
                renderer.EnqueuePass(_volumetricPass);
            }
        }

#if UNITY_EDITOR
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
    }
}
