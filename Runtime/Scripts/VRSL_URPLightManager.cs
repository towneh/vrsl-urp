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
    public class VRSL_URPLightManager : MonoBehaviour
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

        [Header("Lighting")]
        [Tooltip("Assign Hidden/VRSL-URP/DeferredLighting (the VRSLDeferredLighting shader asset).")]
        public Shader lightingShader;

        [Range(0f, 1f)]
        [Tooltip("Modulates each light's surface contribution by the pre-light scene colour, "
               + "as an albedo proxy. 0 = pure additive (existing behaviour — light is added on "
               + "top of the surface unmodulated, can read as washed-out under bright spots). "
               + "1 = pure multiplicative against the pre-light frame (light picks up the "
               + "surface's hue and dark surfaces stay dark — physically closer to reflectance, "
               + "but loses contribution on near-black surfaces). Tune to taste; 0.4–0.6 is a "
               + "reasonable starting point in ambient-dominated stage scenes. When > 0 the "
               + "lighting pass adds an extra fullscreen blit that captures the pre-VRSL camera "
               + "colour into a private RT (~0.1 ms on desktop, more under SPSI VR); skipped "
               + "entirely at 0 so the cost is opt-in.")]
        public float albedoTintStrength = 0f;

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
               + "Cost scales linearly with step count and active fixture count.")]
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

        [Header("Debug")]
        [Tooltip("Log fixture collection and DMX global / CRT publishing to the Console on enable. "
               + "Use to confirm the manager found your fixtures and is feeding the _VRSLU_* globals "
               + "from the right CRTs.")]
        public bool outputDebugLogs = false;

        // ── Public API for the render passes ──────────────────────────────────
        public GraphicsBuffer  FixtureConfigBuffer { get; private set; }
        public GraphicsBuffer  LightDataBuffer     { get; private set; }
        public RTHandle        DMXMainHandle       { get; private set; }
        public RTHandle        DMXMovementHandle   { get; private set; }
        public RTHandle        DMXStrobeHandle     { get; private set; }
        public RTHandle        DMXSpinTimerHandle  { get; private set; }
        public Texture2DArray  GoboArray           { get; private set; }
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

        const int GoboResolution = 256;

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

        // 5 × float4 = 80 bytes
        [StructLayout(LayoutKind.Sequential)]
        internal struct VRSLLightData
        {
            public Vector4 positionAndRange;
            public Vector4 directionAndType;
            public Vector4 colorAndIntensity;
            public Vector4 spotCosines;
            public Vector4 goboAndSpin;
        }

        List<VRStageLighting_DMX_RealtimeLight> _fixtures = new();
        bool _configDirty = true;

        // Render-pass instances. Allocated in OnEnable, reused across cameras and
        // frames, dropped in OnDisable. Stateless beyond renderPassEvent and
        // ConfigureInput flags, so a single instance per pass type is correct
        // even with multiple cameras.
        VRSLDMXLightPasses.ComputePass    _computePass;
        VRSLNormalsPrepass                _normalsPrepass;
        VRSLDMXLightPasses.LightingPass   _lightingPass;
        VRSLDMXLightPasses.VolumetricPass _volumetricPass;
        bool _injectionSubscribed;

#if UNITY_EDITOR
        // Called by Unity when the component is first added or the context-menu Reset is chosen.
        void Reset() => LoadDefaultGoboWheel();

        [ContextMenu("Load Default Gobo Wheel")]
        void LoadDefaultGoboWheel()
        {
            const string folder =
                "Packages/net.towneh.vrsl-urp/Runtime/Textures/MoverLightTextures/GOBO/IndividualGobos";

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
                Marshal.SizeOf<VRSLLightData>());       // 80 bytes

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
            int n = Mathf.Min(FixtureCount, 12);
            for (int i = 0; i < n; i++)
            {
                var f = _fixtures[i];
                Vector4 c = data[i].colorAndIntensity;
                Vector4 p = data[i].positionAndRange;
                bool lens = f.lensTransform != null;
                Vector3 lp = lens ? f.lensTransform.position : f.transform.position;
                var sr = (f.fixtureShellRenderers != null && f.fixtureShellRenderers.Length > 0)
                         ? f.fixtureShellRenderers[0] : null;
                Vector3 sp = sr != null ? sr.bounds.center : f.transform.position;
                Debug.Log($"[VRSL URP] Fixture {i} (ch {f.ComputeAbsoluteChannel()}): intensity={c.w:F2} "
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
            if (GoboArray != null) { Object.Destroy(GoboArray); GoboArray = null; }

            GoboCount = goboTextures != null ? goboTextures.Length : 0;
            if (GoboCount == 0) return;

            GoboArray = new Texture2DArray(GoboResolution, GoboResolution, GoboCount,
                TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };

            var tmp      = RenderTexture.GetTemporary(GoboResolution, GoboResolution, 0,
                               RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            var readback = new Texture2D(GoboResolution, GoboResolution, TextureFormat.RGBA32, false);
            var prevRT   = RenderTexture.active;

            for (int i = 0; i < GoboCount; i++)
            {
                if (goboTextures[i] == null) continue;
                Graphics.Blit(goboTextures[i], tmp);
                RenderTexture.active = tmp;
                readback.ReadPixels(new Rect(0, 0, GoboResolution, GoboResolution), 0, 0);
                readback.Apply();
                GoboArray.SetPixels(readback.GetPixels(), i);
            }

            RenderTexture.active = prevRT;
            Object.Destroy(readback);
            RenderTexture.ReleaseTemporary(tmp);
            GoboArray.Apply();
        }

        void ReleaseBuffers()
        {
            FixtureConfigBuffer?.Release(); FixtureConfigBuffer = null;
            LightDataBuffer?.Release();     LightDataBuffer     = null;
            if (GoboArray != null) { Object.Destroy(GoboArray); GoboArray = null; }
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
            _normalsPrepass ??= new VRSLNormalsPrepass();
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
            if (cam == null) return;
            // Reflection probes and editor preview cameras render through the same
            // pipeline event but don't want stage-light passes — would cost dispatch
            // and pollute reflection captures.
            if (cam.cameraType == CameraType.Reflection
             || cam.cameraType == CameraType.Preview) return;

            var camData = cam.GetUniversalAdditionalCameraData();
            if (camData == null) return;
            var renderer = camData.scriptableRenderer;
            if (renderer == null) return;

            // VRSLNormalsPrepass writes _VRSLNormalsTexture into a VRSL-owned
            // non-MSAA RT before opaque rendering; the lighting shader samples
            // that global. The lighting and volumetric passes only need depth
            // from URP, so neither requests Normal here. The albedo-tint path
            // captures its own opaque snapshot inside LightingPass rather than
            // reading URP's _CameraOpaqueTexture, so Color isn't requested
            // either — URP 17 render graph mode doesn't always honour
            // ConfigureInput(Color) for our injection point.
            _lightingPass.ConfigureInput(ScriptableRenderPassInput.Depth);
            _volumetricPass.ConfigureInput(ScriptableRenderPassInput.Depth);

            // Gobo wheel is a Texture2DArray, bound globally here because the
            // render graph only accepts TextureHandle.
            if (GoboArray != null)
                Shader.SetGlobalTexture("_VRSLGobos", GoboArray);

            renderer.EnqueuePass(_computePass);
            renderer.EnqueuePass(_normalsPrepass);
            renderer.EnqueuePass(_lightingPass);
            if (VolumetricMaterial != null)
                renderer.EnqueuePass(_volumetricPass);
        }
    }
}
