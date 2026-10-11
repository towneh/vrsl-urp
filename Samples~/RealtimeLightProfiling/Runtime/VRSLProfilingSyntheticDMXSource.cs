using UnityEngine;
using VRSL.URP;

namespace VRSL.URP.Profiling
{
    /// <summary>
    /// CRT-bypass DMX source for profiling runs.
    ///
    /// Authors a small CPU-side pixel buffer that matches the format the URP
    /// compute shader and the fixture shaders consume after the existing
    /// CRT decode chain, then publishes that buffer as the four DMX globals
    /// (read by the shader-driven fixtures) and hands it to the URP manager as its Grid
    /// Textures (read by the URP compute). With this active, the GridReader
    /// camera and CustomRenderTexture chain do not need to run, so frame-to-
    /// frame variance from video decode and CRT scheduling is eliminated and
    /// the profile reflects only the cost of the lighting paths under test.
    ///
    /// Layout: 13 pixels wide × 256 rows tall. Row i corresponds to a fixture
    /// configured with useLegacySectorMode = true and sector = i (abs channel
    /// 13·i + 1). Per-channel pixel offsets follow VRSL's standard 13-channel
    /// layout (dimmer at +5, strobe at +6, R/G/B at +7/+8/+9, etc.).
    /// </summary>
    [DefaultExecutionOrder(-100)]
    [AddComponentMenu("VRSL/Profiling/Synthetic DMX Source")]
    public class VRSLProfilingSyntheticDMXSource : MonoBehaviour
    {
        const int Width  = 13;
        const int Height = 256;

        [Tooltip("RGB driven into channels +7/+8/+9 of every fixture. The compute "
               + "shader reads each colour channel as a luminance, so this is the "
               + "perceived emit colour with no further tinting.")]
        public Color fixtureColor = new Color(1f, 0.4f, 0.1f);

        [Range(0f, 1f)]
        [Tooltip("Dimmer value driven into channel +5 of every fixture (0 = off, 1 = full).")]
        public float intensity = 1f;

        [Tooltip("Animate pan/tilt with a fixed sine loop so movers exercise the "
               + "Rodrigues rotation path each frame. Disable to lock fixtures at "
               + "centre — useful for isolating per-pixel cost from movement cost.")]
        public bool animatePanTilt = true;

        [Tooltip("Pan rotation rate in degrees per second.")]
        public float panSpeed  = 90f;

        [Tooltip("Tilt rotation rate in degrees per second.")]
        public float tiltSpeed = 60f;

        [Tooltip("Sweep the zoom DMX channel (ch+4) between narrow and wide. "
               + "Has no visible effect on fixtures with enableConeWidth=false (washlights "
               + "and statics) — useful for exercising spotlight zoom motors during profiling.")]
        public bool animateZoom = true;

        [Tooltip("Zoom sweep period in seconds — full cycle from narrow to wide and back.")]
        public float zoomPeriod = 4f;

        [Tooltip("Use a square wave instead of a sine. Sine smoothly sweeps every angle "
               + "within the zoom range; square waves hold at fully narrow / fully wide for "
               + "most of each half-period with a brief linear ramp at each edge to simulate "
               + "a zoom motor moving very quickly between extremes.")]
        public bool zoomSquareWave = false;

        // Hard-coded square-wave edge ramp duration (seconds). Approximates a real
        // zoom motor sweeping quickly through the open-angle range rather than
        // teleporting between narrow and wide states.
        const float ZoomSquareTransition = 0.1f;

        Color32[] _mainPixels, _movementPixels, _strobePixels, _spinPixels;
        Texture2D _mainSrc,    _movementSrc,    _strobeSrc,    _spinSrc;
        RenderTexture _mainRT, _movementRT,     _strobeRT,     _spinRT;
        VRSLGridTextures     _gridTextures;
        // The manager handed _gridTextures, so it can be taken back before it is destroyed.
        VRSL_URPLightManager _boundManager;

        // Match VRSL_LocalUIControlPanel's property names so the volumetric
        // mesh fixtures (which read these _VRSLU_-prefixed globals) bind correctly.
        static readonly int IDMain          = Shader.PropertyToID("_VRSLU_DMXGridRenderTexture");
        static readonly int IDMovement      = Shader.PropertyToID("_VRSLU_DMXGridRenderTextureMovement");
        static readonly int IDStrobeTimer   = Shader.PropertyToID("_VRSLU_DMXGridStrobeTimer");
        static readonly int IDStrobeOutput  = Shader.PropertyToID("_VRSLU_DMXGridStrobeOutput");
        static readonly int IDSpinTimer     = Shader.PropertyToID("_VRSLU_DMXGridSpinTimer");

        void OnEnable()
        {
            Allocate();
            FillStaticChannels();
            FillZoom(0f);
            FillMovement(0f);
            UploadAll();
            PublishGlobals();
            BindToManager();
        }

        void OnDisable()
        {
            var mgr = UnbindFromManager();
            ReleaseRT(ref _mainRT);
            ReleaseRT(ref _movementRT);
            ReleaseRT(ref _strobeRT);
            ReleaseRT(ref _spinRT);
            ReleaseTex(ref _mainSrc);
            ReleaseTex(ref _movementSrc);
            ReleaseTex(ref _strobeSrc);
            ReleaseTex(ref _spinSrc);
            if (_gridTextures != null)
            {
                if (Application.isPlaying) Destroy(_gridTextures); else DestroyImmediate(_gridTextures);
                _gridTextures = null;
            }
            // Last, once nothing of this source is left to bind: a running manager holds
            // handles on the released textures until a bounce rebuilds them, the same
            // way BindToManager makes it take them. Releasing a handle that only wraps a
            // texture leaves the texture alone, so the order is safe.
            BounceManager(mgr);
        }

        /// <summary>Take this source's Grid Textures back off the manager it was given
        /// to. Returns that manager when it was still using them.</summary>
        VRSL_URPLightManager UnbindFromManager()
        {
            var mgr = _boundManager;
            _boundManager = null;
            if (mgr == null || _gridTextures == null || mgr.gridTextures != _gridTextures) return null;
            mgr.gridTextures = null;
            return mgr;
        }

        static void BounceManager(VRSL_URPLightManager mgr)
        {
            // Not while the scene is unloading: the manager is about to go too, and
            // allocating on a component mid-teardown is where Unity starts logging.
            if (mgr == null || !mgr.isActiveAndEnabled || !mgr.gameObject.scene.isLoaded) return;
            mgr.enabled = false;
            mgr.enabled = true;
        }

        void OnValidate()
        {
            if (_mainPixels == null || _mainSrc == null) return;
            FillStaticChannels();
            UploadStatic();
        }

        void Update()
        {
            float t = Time.time;
            if (animatePanTilt)
            {
                FillMovement(t);
                _movementSrc.SetPixels32(_movementPixels);
                _movementSrc.Apply(false);
                Graphics.Blit(_movementSrc, _movementRT);
            }
            if (animateZoom)
            {
                FillZoom(t);
                _mainSrc.SetPixels32(_mainPixels);
                _mainSrc.Apply(false);
                Graphics.Blit(_mainSrc, _mainRT);
            }
        }

        void Allocate()
        {
            int n = Width * Height;
            _mainPixels     = new Color32[n];
            _movementPixels = new Color32[n];
            _strobePixels   = new Color32[n];
            _spinPixels     = new Color32[n];

            _mainSrc     = MakeSrc("VRSL_Profiling_DMX_Main");
            _movementSrc = MakeSrc("VRSL_Profiling_DMX_Movement");
            _strobeSrc   = MakeSrc("VRSL_Profiling_DMX_Strobe");
            _spinSrc     = MakeSrc("VRSL_Profiling_DMX_Spin");

            _mainRT      = MakeRT("VRSL_Profiling_DMX_Main_RT");
            _movementRT  = MakeRT("VRSL_Profiling_DMX_Movement_RT");
            _strobeRT    = MakeRT("VRSL_Profiling_DMX_Strobe_RT");
            _spinRT      = MakeRT("VRSL_Profiling_DMX_Spin_RT");
        }

        Texture2D MakeSrc(string n) => new Texture2D(Width, Height, TextureFormat.RGBA32, false, true)
        {
            name       = n,
            filterMode = FilterMode.Point,
            wrapMode   = TextureWrapMode.Clamp,
            hideFlags  = HideFlags.DontSave,
        };

        RenderTexture MakeRT(string n)
        {
            var rt = new RenderTexture(Width, Height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear)
            {
                name             = n,
                filterMode       = FilterMode.Point,
                wrapMode         = TextureWrapMode.Clamp,
                useMipMap        = false,
                autoGenerateMips = false,
                hideFlags        = HideFlags.DontSave,
            };
            rt.Create();
            return rt;
        }

        // 13-channel pixel layout per row (column = absChannel % 13, with col 0
        // standing in for col 13 from the formal mapping):
        //   0  pan coarse    1  pan fine     2  tilt coarse  3  tilt fine
        //   4  cone width    5  dimmer       6  strobe       7  R
        //   8  G             9  B           10  gobo spin   11  gobo select
        //  12  reserved
        void FillStaticChannels()
        {
            byte intB = ToByte(intensity);
            byte rB   = ToByte(fixtureColor.r);
            byte gB   = ToByte(fixtureColor.g);
            byte bB   = ToByte(fixtureColor.b);
            var  white = new Color32(255, 255, 255, 255);

            for (int row = 0; row < Height; row++)
            {
                int o = row * Width;
                // ch+4 (cone width) is owned by FillZoom — written once at init
                // and re-written every frame when animateZoom is on.
                _mainPixels[o + 5] = new Color32(intB, intB, intB, 255);   // dimmer
                _mainPixels[o + 7] = new Color32(rB,   rB,   rB,   255);   // R as luminance
                _mainPixels[o + 8] = new Color32(gB,   gB,   gB,   255);   // G as luminance
                _mainPixels[o + 9] = new Color32(bB,   bB,   bB,   255);   // B as luminance

                _strobePixels[o + 6] = white;                              // strobe gate fully open
            }
        }

        void FillZoom(float t)
        {
            float zoom01;
            if (animateZoom && zoomPeriod > 0f)
            {
                if (zoomSquareWave)
                {
                    // Snap between fully narrow (0) and fully wide (1) each half-period,
                    // with a brief linear ramp at the edges to approximate a zoom motor
                    // moving very quickly rather than teleporting between states.
                    float cyclePos   = (t / zoomPeriod) % 1f;
                    bool  highHalf   = cyclePos >= 0.5f;
                    float halfPos    = (highHalf ? cyclePos - 0.5f : cyclePos) * 2f; // [0,1) within current half
                    float halfPeriod = zoomPeriod * 0.5f;
                    float ramp = halfPeriod > 0f
                        ? Mathf.Clamp01(halfPos * halfPeriod / ZoomSquareTransition)
                        : 1f;
                    zoom01 = highHalf ? ramp : 1f - ramp;
                }
                else
                {
                    float phase = t * 2f * Mathf.PI / zoomPeriod;
                    zoom01 = Mathf.Sin(phase) * 0.5f + 0.5f;
                }
            }
            else
            {
                zoom01 = 0.5f;
            }
            byte zoomB = ToByte(zoom01);
            var  c     = new Color32(zoomB, zoomB, zoomB, 255);
            for (int row = 0; row < Height; row++)
                _mainPixels[row * Width + 4] = c;
        }

        void FillMovement(float t)
        {
            for (int row = 0; row < Height; row++)
            {
                int   o     = row * Width;
                float phase = row * 0.13f;
                float pan01 = animatePanTilt
                            ? Mathf.Sin(t * Mathf.Deg2Rad * panSpeed  + phase)         * 0.5f + 0.5f
                            : 0.5f;
                float tilt01 = animatePanTilt
                            ? Mathf.Cos(t * Mathf.Deg2Rad * tiltSpeed + phase * 0.7f)  * 0.5f + 0.5f
                            : 0.5f;

                byte panB  = ToByte(pan01);
                byte tiltB = ToByte(tilt01);
                _movementPixels[o + 0] = new Color32(panB,  panB,  panB,  255);
                _movementPixels[o + 2] = new Color32(tiltB, tiltB, tiltB, 255);
            }
        }

        void UploadAll()
        {
            _mainSrc    .SetPixels32(_mainPixels);     _mainSrc    .Apply(false);
            _movementSrc.SetPixels32(_movementPixels); _movementSrc.Apply(false);
            _strobeSrc  .SetPixels32(_strobePixels);   _strobeSrc  .Apply(false);
            _spinSrc    .SetPixels32(_spinPixels);     _spinSrc    .Apply(false);

            Graphics.Blit(_mainSrc,     _mainRT);
            Graphics.Blit(_movementSrc, _movementRT);
            Graphics.Blit(_strobeSrc,   _strobeRT);
            Graphics.Blit(_spinSrc,     _spinRT);
        }

        void UploadStatic()
        {
            _mainSrc  .SetPixels32(_mainPixels);   _mainSrc  .Apply(false);
            _strobeSrc.SetPixels32(_strobePixels); _strobeSrc.Apply(false);
            Graphics.Blit(_mainSrc,   _mainRT);
            Graphics.Blit(_strobeSrc, _strobeRT);
        }

        void PublishGlobals()
        {
            Shader.SetGlobalTexture(IDMain,         _mainRT);
            Shader.SetGlobalTexture(IDMovement,     _movementRT);
            Shader.SetGlobalTexture(IDStrobeTimer,  _strobeRT);
            Shader.SetGlobalTexture(IDStrobeOutput, _strobeRT);
            Shader.SetGlobalTexture(IDSpinTimer,    _spinRT);
        }

        // Manager allocates RTHandles in OnEnable, so swap its Grid Textures and bounce the
        // component to force a re-allocation against our synthetic textures.
        // No-op when URP isn't installed — the legacy mesh-shader path reads the
        // global textures published in PublishGlobals directly, no manager rebind
        // needed.
        void BindToManager()
        {
            var mgr = FindAnyObjectByType<VRSL_URPLightManager>();
            if (mgr == null) return;

            // The same set for every layout, so the manager reads the synthetic grid
            // whichever layout it is set to.
            if (_gridTextures == null)
            {
                _gridTextures = ScriptableObject.CreateInstance<VRSLGridTextures>();
                _gridTextures.name      = "VRSL_Profiling_GridTextures";
                _gridTextures.hideFlags = HideFlags.DontSave;
            }
            var set = new VRSLGridTextures.Set
            {
                main        = _mainRT,
                movement    = _movementRT,
                strobe      = _strobeRT,
                strobeTimer = _strobeRT,
                spinTimer   = _spinRT,
            };
            _gridTextures.horizontal = _gridTextures.vertical = _gridTextures.legacy = set;

            bool wasEnabled = mgr.enabled;
            mgr.enabled = false;
            mgr.gridTextures = _gridTextures;
            _boundManager    = mgr;
            if (wasEnabled) mgr.enabled = true;
        }

        /// <summary>Re-publish globals and rebind to the URP manager. Call after
        /// adding a manager to the scene at runtime, or after replacing fixtures
        /// in a way that triggered the manager to re-allocate handles.</summary>
        public void Rebind()
        {
            if (_mainRT == null) return;
            PublishGlobals();
            BindToManager();
        }

        static byte ToByte(float v) => (byte)Mathf.Clamp(Mathf.RoundToInt(v * 255f), 0, 255);

        static void ReleaseRT(ref RenderTexture rt)
        {
            if (rt == null) return;
            rt.Release();
            if (Application.isPlaying) Destroy(rt); else DestroyImmediate(rt);
            rt = null;
        }

        static void ReleaseTex(ref Texture2D t)
        {
            if (t == null) return;
            if (Application.isPlaying) Destroy(t); else DestroyImmediate(t);
            t = null;
        }
    }
}
