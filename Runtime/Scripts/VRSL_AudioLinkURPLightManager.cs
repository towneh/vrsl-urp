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
    public class VRSL_AudioLinkURPLightManager : MonoBehaviour, IVRSLVolumetricSource
    {
        public static VRSL_AudioLinkURPLightManager Instance { get; private set; }

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

        [Tooltip("Assign VRSLFroxelVolumetric. Required for the Froxel volumetric mode; "
               + "ignored by the Half and Full raymarch modes.")]
        public ComputeShader froxelShader;

        [Tooltip("Froxel mode only. Volume dimensions per eye. Larger is sharper and costs more, but "
               + "unlike the raymarch modes the cost does not track screen resolution. "
               + "Depth slices are spread exponentially, so most land near the camera. "
               + "Clamped to 8-256 on X and Y and 8-128 on Z when consumed, since a zero "
               + "or negative axis would produce an invalid volume.")]
        public Vector3Int froxelResolution = new Vector3Int(160, 90, 64);

        [Range(4f, 200f)]
        [Tooltip("Froxel mode only. How far the volume reaches, in metres. Scattering beyond this is not "
               + "represented, so set it to roughly the depth of the space rather than the "
               + "camera far plane — every slice spent past the back wall is wasted.")]
        public float froxelMaxDistance = 64f;

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
        [Tooltip("Number of integration steps along each view ray. Higher = smoother, more cost. "
               + "Cost scales linearly with step count and active fixture count. "
               + "Half and Full only — Froxel mode gets its depth resolution from Froxel Resolution's Z instead.")]
        public int volumetricStepCount = 32;

        [Range(0f, 2f)]
        [Tooltip("Base scattering density. Lower = subtler shafts; higher = denser haze. "
               + "Tune relative to scene scale.")]
        public float volumetricDensity = 0.1f;

        [Range(-0.95f, 0.95f)]
        [Tooltip("Henyey–Greenstein anisotropy. 0 = isotropic; positive values brighten when "
               + "looking down the beam; negative values back-scatter.")]
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
        [Tooltip("Gobo textures shared by all AudioLink fixtures. Packed into a Texture2DArray. "
               + "Each fixture selects a slot via its Gobo Index field. -1 = no gobo (open beam).")]
        public Texture2D[] goboTextures;

        [Header("Color Sampling")]
        [Tooltip("Scene-wide texture sampled by every AudioLink fixture in ColorTexture / "
               + "ColorTextureTraditional color modes. Mirrors the legacy AudioLink Static "
               + "approach where _SamplingTexture sat on the fixture material rather than per "
               + "fixture instance — projects typically pick one palette/atlas/RT for all "
               + "their fixtures and rely on per-fixture textureSamplingCoordinates to choose "
               + "the colour. Accepts any Texture or RenderTexture; leave blank to fall back "
               + "to AudioLink's _AudioTexture atlas.")]
        public Texture samplingTexture;

        [Header("Cameras")]
        [Tooltip("How VRSL treats cameras that render into a texture rather than to the "
               + "player's view — mirrors, portals, camera props. Full lights them like the "
               + "main view, which is the default because beams in a mirror are a large part "
               + "of a stage look. SurfaceOnly keeps surface lighting but drops the "
               + "volumetric raymarch, the more expensive of the two. Skip runs nothing. "
               + "Cameras feeding VRSL's own data path are always skipped regardless of "
               + "this setting.")]
        public SecondaryCameraMode secondaryCameraMode = SecondaryCameraMode.Full;

        /// <summary>x = strength, y = trace distance, z = steps, w = thickness.</summary>
        public Vector4 ContactShadowParams =>
            new Vector4(contactShadowStrength, contactShadowDistance,
                        contactShadowSteps, contactShadowThickness);

        // IVRSLVolumetricSource — lets the shared froxel pass drive either manager.
        ComputeShader IVRSLVolumetricSource.FroxelShader     => froxelShader;
        Vector3Int    IVRSLVolumetricSource.FroxelResolution => froxelResolution;
        float         IVRSLVolumetricSource.FroxelMaxDistance => froxelMaxDistance;

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

        public Vector4 VolumetricStepParams =>
            new Vector4(volumetricStepCount, coupleToSceneFog ? 1f : 0f, 0f, volumetricAnisotropy);
        public Vector4 VolumetricDensityParams =>
            new Vector4(volumetricDensity, volumetricNoiseScale,
                        volumetricNoiseScrollSpeed, volumetricNoiseStrength);
        public Vector4 VolumetricFogTintParams =>
            new Vector4(volumetricTint.r, volumetricTint.g, volumetricTint.b, volumetricIntensity);
        public bool VolumetricUseNoise => volumetricUseNoise;
        bool IVRSLVolumetricSource.VolumetricCoupleToSceneFog => coupleToSceneFog;
        public bool VolumetricUseFullRes => volumetricResolution == VolumetricResolution.Full;
        /// <summary>Froxel mode replaces the raymarch entirely rather than
        /// layering on top of it, so the raymarch pass sits out when it's on.</summary>
        public bool VolumetricUseFroxel  => volumetricResolution == VolumetricResolution.Froxel;

        /// <summary>Froxel mode selected <i>and</i> able to run. The raymarch pass
        /// stands down on this rather than on the mode alone: standing down on the
        /// mode while the froxel pass declined to record would leave neither
        /// running, which is silent — no cones, no error, nothing to search for.</summary>
        public bool VolumetricFroxelActive =>
            VolumetricUseFroxel && _froxelPass != null && _froxelPass.IsUsable;


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
        // frames, dropped in OnDisable. Stateless beyond renderPassEvent and
        // ConfigureInput flags, so a single instance per pass type is correct
        // even with multiple cameras.
        VRSLAudioLinkLightPasses.ComputePass    _computePass;
        VRSLSurfacePrepass                      _surfacePrepass;
        VRSLTileCullPass                        _tileCullPass;
        VRSLFroxelPass                          _froxelPass;
        VRSLAudioLinkLightPasses.LightingPass   _lightingPass;
        VRSLAudioLinkLightPasses.VolumetricPass _volumetricPass;
        bool _injectionSubscribed;
        bool _warnedFroxelUnusable;

        /// <summary>Per-tile light culling for the current camera. Null until the
        /// passes are allocated, and inert when <c>lightCullShader</c> is unassigned.</summary>
        public VRSLTileCullPass TileCullPass => _tileCullPass;

        // ── Lifecycle ─────────────────────────────────────────────────────────
        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(this); return; }
            Instance = this;
        }

        void OnEnable()
        {
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
            UnsubscribeRuntimeInjection();
            _tileCullPass?.Dispose();
            _tileCullPass = null;
            // These two resolve their shader and kernels in their constructors, so
            // they have to be rebuilt rather than reused — otherwise assigning a
            // shader and re-enabling the component, which is what the froxel
            // warning tells you to do, silently changes nothing.
            _surfacePrepass = null;
            _froxelPass?.Dispose();
            _froxelPass     = null;
            _warnedFroxelUnusable = false;
            ReleaseBuffers();
            ReleaseAudioLinkHandle();
            ReleaseSamplingTextureHandle();
            ReleaseFallbackBlackRT();
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
            _goboArray = VRSLGoboWheel.Build(goboTextures, out int count);
            GoboArray  = _goboArray;
            GoboCount  = count;
        }

        RenderTexture _goboArray;

        VRSLALFixtureConfig BuildConfig(VRStageLighting_AudioLink_RealtimeLight f)
        {
            Vector3 pos     = f.GetWorldPosition();
            Vector3 forward = f.GetWorldForward();

            int   lightType  = f.isPointLight ? 1 : 0;
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
                    f.maxIntensity,
                    f.finalIntensity * f.globalIntensity,
                    f.enableAudioLink ? 1f : 0f,
                    0f),
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
                    f.goboSpinSpeed,
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
                sb.AppendLine("  " + VRSLDiagnostics.ComputeStatus("Decode compute", computeShader, "UpdateLights"));
                sb.AppendLine("  " + VRSLDiagnostics.ComputeStatus("Cull compute", lightCullShader, "CullLights"));
                sb.AppendLine("  " + VRSLDiagnostics.ComputeStatus("Froxel compute", froxelShader,
                                     "ScatterFroxels", "IntegrateFroxels"));
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
            sb.AppendLine("  " + VRSLDiagnostics.ComputeStatus("Decode compute", computeShader, "UpdateLights"));
            sb.AppendLine("  " + VRSLDiagnostics.ComputeStatus("Cull compute", lightCullShader, "CullLights"));
            sb.AppendLine("  " + VRSLDiagnostics.LightDataStatus(LightDataBuffer, FixtureCount));
            sb.AppendLine("  " + VRSLDiagnostics.TileStatus(TileCullPass, FixtureCount));
            sb.Append($"  Volumetric mode: {volumetricResolution}");
            if (volumetricResolution == VolumetricResolution.Froxel)
            {
                var effective = VRSLFroxelPass.ClampResolution(froxelResolution);
                sb.Append($" ({effective.x}x{effective.y}x{effective.z}, {froxelMaxDistance}m)");
                if (effective != froxelResolution)
                    sb.Append($" — CLAMPED from {froxelResolution.x}x{froxelResolution.y}"
                            + $"x{froxelResolution.z}");
            }
            sb.AppendLine();
            if (volumetricResolution == VolumetricResolution.Froxel)
                sb.AppendLine("  " + VRSLDiagnostics.ComputeStatus("Froxel compute", froxelShader,
                                     "ScatterFroxels", "IntegrateFroxels"));
            if (volumetricResolution == VolumetricResolution.Froxel)
                sb.AppendLine("  " + VRSLDiagnostics.FroxelVolumeStatus(_froxelPass?.Volume));
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

            _computePass    ??= new VRSLAudioLinkLightPasses.ComputePass
            {
                renderPassEvent = RenderPassEvent.BeforeRenderingOpaques,
            };
            _surfacePrepass ??= new VRSLSurfacePrepass(surfacePropertiesShader);
            _tileCullPass   ??= new VRSLTileCullPass(lightCullShader, this);
            _froxelPass     ??= new VRSLFroxelPass(this);
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
            // Re-upload on the next enable's first camera render.
            _lastPerFrameFrame = -1;
        }

        void OnBeginCameraRendering(ScriptableRenderContext ctx, Camera cam)
        {
            var decision = VRSLCameraFilter.Evaluate(cam, secondaryCameraMode, OwnedSources());
            if (decision == VRSLCameraDecision.Skip) return;

            var camData = cam.GetUniversalAdditionalCameraData();
            if (camData == null) return;
            var renderer = camData.scriptableRenderer;
            if (renderer == null) return;

            UploadPerFrameState();

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
            // The surface prepass costs two opaque geometry draws and writes the
            // same targets regardless of which manager drives it, so when a DMX
            // manager is present it owns the prepass and this one defers.
            //
            // isActiveAndEnabled, not just a null check: Instance is assigned in
            // Awake and cleared only in OnDestroy, so a DMX manager that is merely
            // disabled still answers non-null while having unsubscribed from
            // beginCameraRendering. Deferring to it then would leave the prepass
            // enqueued by nobody and the lighting pass shading against stale data.
            var dmxManager = VRSL_URPLightManager.Instance;
            if (dmxManager == null || !dmxManager.isActiveAndEnabled)
                renderer.EnqueuePass(_surfacePrepass);
            renderer.EnqueuePass(_tileCullPass);
            renderer.EnqueuePass(_lightingPass);
            if (VolumetricMaterial != null && decision == VRSLCameraDecision.Full)
            {
                // Inside the block so the message can't claim a fallback that
                // isn't going to happen — with no volumetric shader, or on a
                // secondary camera set to SurfaceOnly, neither pass runs.
                if (VolumetricUseFroxel && !VolumetricFroxelActive && !_warnedFroxelUnusable)
                {
                    _warnedFroxelUnusable = true;
                    Debug.LogWarning(
                        "[VRSL] Volumetric resolution is set to Froxel but the froxel compute "
                        + "isn't usable — assign froxelShader (VRSLFroxelVolumetric). Falling "
                        + "back to the raymarch. The kernels resolve when the manager enables, "
                        + "so disable and re-enable the component after assigning it.", this);
                }

                if (VolumetricFroxelActive)
                    renderer.EnqueuePass(_froxelPass);
                else
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
