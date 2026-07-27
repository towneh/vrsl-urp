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

        /// <summary>Integrate scattering into a view-aligned 3D grid once, then
        /// sample it per pixel. Cost tracks the volume's dimensions rather than
        /// the framebuffer's, and the trilinear sample needs no jitter to hide
        /// banding. Requires <c>froxelShader</c>. Scattering past
        /// <c>froxelMaxDistance</c> is not represented.</summary>
        Froxel = 2,
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
    public class VRSL_URPLightManager : MonoBehaviour, IVRSLVolumetricSource
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

        // IVRSLVolumetricSource — lets the shared froxel pass drive either manager.
        ComputeShader IVRSLVolumetricSource.FroxelShader     => froxelShader;
        Vector3Int    IVRSLVolumetricSource.FroxelResolution => froxelResolution;
        float         IVRSLVolumetricSource.FroxelMaxDistance => froxelMaxDistance;

        // ── Public API for the render passes ──────────────────────────────────
        public GraphicsBuffer  FixtureConfigBuffer { get; private set; }
        public GraphicsBuffer  LightDataBuffer     { get; private set; }
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
        VRSLFroxelPass                    _froxelPass;
        VRSLDMXLightPasses.LightingPass   _lightingPass;
        VRSLDMXLightPasses.VolumetricPass _volumetricPass;
        bool _injectionSubscribed;
        bool _warnedFroxelUnusable;

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
            if (Instance != null && Instance != this) { Destroy(this); return; }
            Instance = this;
        }

        void OnEnable()
        {
            CreateTextureHandles();
            RefreshFixtures();
            SubscribeRuntimeInjection();
            VRStageLighting_DMX_RealtimeLight.ConfigChanged += OnFixtureConfigChanged;
        }

        void OnDisable()
        {
            VRStageLighting_DMX_RealtimeLight.ConfigChanged -= OnFixtureConfigChanged;
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
            ReleaseTextureHandles();
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
            if (_configDirty)
            {
                UploadFixtureConfigs();
                _configDirty = false;
            }
        }

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
                // Intensity doubles as the active flag in the packed layout.
                float active = c.w > 0f ? 1f : 0f;
                bool lens = f.lensTransform != null;
                Vector3 lp = lens ? f.lensTransform.position : f.transform.position;
                var sr = (f.fixtureShellRenderers != null && f.fixtureShellRenderers.Length > 0)
                         ? f.fixtureShellRenderers[0] : null;
                Vector3 sp = sr != null ? sr.bounds.center : f.transform.position;
                Debug.Log($"[VRSL URP] Fixture {i} '{f.name}' (ch {f.ComputeAbsoluteChannel()}): intensity={c.w:F2} "
                        + $"rgb=({c.x:F3},{c.y:F3},{c.z:F3}) active={active:F0} "
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
                sb.AppendLine("  " + VRSLDiagnostics.ComputeStatus("Froxel compute", froxelShader,
                                     "ScatterFroxels", "IntegrateFroxels"));
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

            _computePass    ??= new VRSLDMXLightPasses.ComputePass
            {
                renderPassEvent = RenderPassEvent.BeforeRenderingOpaques,
            };
            _surfacePrepass ??= new VRSLSurfacePrepass(surfacePropertiesShader);
            _tileCullPass   ??= new VRSLTileCullPass(lightCullShader, this);
            _froxelPass     ??= new VRSLFroxelPass(this);
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
    }
}
