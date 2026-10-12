using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace VRSL.URP.Tests
{
    /// <summary>
    /// The scene the DMX buffer rows are judged against, built in code.
    ///
    /// It deliberately does not use the profiling sample: that lives under
    /// <c>Samples~</c> and only exists once a developer has imported it, which
    /// makes it unavailable to a headless run. The patch it produces is the same
    /// one the rows were written against — fixture <i>i</i> at legacy sector
    /// <i>i</i>, so absolute channel <c>13i + 1</c>.
    ///
    /// <b>Time is captured, not measured.</b> <see cref="Time.captureDeltaTime"/>
    /// makes every frame advance exactly <see cref="FrameDelta"/> of game time
    /// however fast the machine actually renders, so a wait is a frame count
    /// rather than a stopwatch reading. The movement and spin rows depend on
    /// that: judged against wall-clock they are only reproducible to a second or
    /// two, which is coarser than the effects they are trying to separate.
    /// </summary>
    sealed class VRSLDMXRig : IDisposable
    {
        public const float FrameDelta   = 1f / 60f;
        public const int   FixtureCount = 50;
        /// <summary>Metres between fixtures on the truss. Wider than any offset
        /// between a fixture's root and the point the compute lights from, which
        /// is what lets a nearest match identify them.</summary>
        public const float Spacing      = 2f;

        const string RootName      = "VRSL DMX Test Rig";
        const string Pkg           = "Packages/town.mr.vrsl-urp/";
        const string ManagerPrefab = Pkg + "Runtime/Prefabs/DMX/VRSL-DMX-LightManager.prefab";
        const string FixturePrefab = Pkg + "Runtime/Prefabs/DMX/VRSL-DMX-Mover-Spotlight-13CH.prefab";

        /// <summary>The camera's render target, for rows that read the frame back.</summary>
        public RenderTexture Target => _target;
        /// <summary>The rig's camera, disabled: it renders only through
        /// <see cref="Step"/> and <see cref="RenderFrame"/>.</summary>
        public Camera Camera => _camera;
        /// <summary>The plane the beams land on.</summary>
        public GameObject Floor { get; private set; }

        /// <summary>A camera rendering into its own texture, the way a mirror or a
        /// camera prop does. Disabled like the main camera: it renders through
        /// <see cref="RenderFrame"/> while <see cref="Renders"/> is set, and through
        /// <see cref="Render"/> on its own.</summary>
        public sealed class SecondaryCamera
        {
            public Camera        Camera;
            public RenderTexture Target;
            /// <summary>Whether <see cref="RenderFrame"/> renders it this frame. A
            /// row about a mirror that comes and goes flips this.</summary>
            public bool          Renders = true;
        }

        /// <summary>Secondary cameras added with <see cref="AddSecondaryCamera"/>.</summary>
        public readonly List<SecondaryCamera> Secondaries = new();

        public VRSL_URPLightManager             Manager { get; private set; }
        public VRSL_SyntheticDMXChannelSource   Source  { get; private set; }
        public readonly List<VRStageLighting_DMX_RealtimeLight> Fixtures = new();

        GameObject    _root;
        Camera        _camera;
        RenderTexture _target;
        int           _msaa;
        UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset _asset;
        int           _assetMsaaWas;
        GPUResidentDrawerMode? _drawer;
        GPUResidentDrawerMode  _drawerWas;
        float?        _renderScale;
        float         _renderScaleWas;
        UnityEngine.Rendering.Universal.DepthPrimingMode? _priming;
        readonly Dictionary<UnityEngine.Rendering.Universal.UniversalRendererData,
                            UnityEngine.Rendering.Universal.DepthPrimingMode> _primingWas = new();
        float         _captureWas;
        bool          _forceLoadWas;
        List<Light>   _lightsWereOn;
        UnityEngine.Rendering.AmbientMode _ambientModeWas;
        Color         _ambientLightWas;
        Material      _skyboxWas;
        Light         _sunWas;
        bool          _fogWas;
        int           _validateKernel = -1;
        GraphicsBuffer _dummyChannels;
        int[]         _map;

        /// <summary>Absolute channel of fixture <paramref name="i"/> under the
        /// rig's patch. Mirrors <c>ComputeAbsoluteChannel</c> for legacy sector
        /// mode so a prediction never has to ask the component it is checking.</summary>
        public static int ChannelOf(int i) => i * 13 + 1;

        // VRSL's 13-channel layout, in one place. Spelling these offsets out per
        // test class is how a row ends up asserting confidently against the wrong
        // channel after the layout moves.
        public static int DimmerChannel(int abs)     => abs + 5;
        public static int StrobeChannel(int abs)     => abs + 6;
        public static int RedChannel(int abs)        => abs + 7;
        public static int SpinChannel(int abs)       => abs + 10;
        /// <summary>Channel 13 of the fixture's own sector, which is where the
        /// movement damping reads its smoothness from.</summary>
        public static int SmoothnessChannel(int abs) => ((abs - 1) / 13 + 1) * 13;

        /// <summary>The value the Ramp pattern puts on a 1-based channel.
        /// <c>RampValue</c> is indexed from 0, and mixing the two up shifts every
        /// prediction by one channel.</summary>
        public static float RampAt(int channel)
            => VRSL_SyntheticDMXChannelSource.RampValue(channel - 1) / 255f;

        /// <summary>Render target edge in pixels. The correctness rows only need a
        /// camera that renders at all, so they take the default; the benchmark rows
        /// raise it, because per-pixel cost is the thing they are measuring and at
        /// 256 square there is not enough of it to clear the noise.</summary>
        public const int TargetSize = 256;

        /// <summary>
        /// Which shape of frame a frozen capture produces. Part of the name every
        /// stored capture and reference frame is filed under, so a change to what
        /// the freeze does, what the source publishes or how the rig is lit makes
        /// the rows seed afresh rather than report the old frame as a regression.
        /// Bump it with any such change. 2: beams aimed down at intensity 60 under
        /// the static Fixtures pattern, host lighting removed.
        /// </summary>
        public const int CaptureVersion = 2;

        /// <summary>The name a stored capture or reference frame of this shape is
        /// filed under.</summary>
        public static string CaptureName(string scene) => $"{scene}-v{CaptureVersion}";

        /// <summary>The depth-priming mode every Universal Renderer is held at while
        /// the rig stands, unless <see cref="Build"/> was told to hold none. Forced is
        /// the shipped desktop configuration of the host this suite runs in.</summary>
        public const UnityEngine.Rendering.Universal.DepthPrimingMode DefaultPriming =
            UnityEngine.Rendering.Universal.DepthPrimingMode.Forced;

        /// <summary>The GPU Resident Drawer mode the pipeline asset is held at while
        /// the rig stands, unless <see cref="Build"/> was told to hold none. Disabled,
        /// so the floor's own material reaches the surface prepass: a mesh the drawer
        /// batches lights as the neutral fallback instead (row S15), and the host
        /// switches the drawer on and off with its quality tier at moments of its
        /// own, which is one way a frozen frame stops reproducing.</summary>
        public const GPUResidentDrawerMode DefaultDrawer = GPUResidentDrawerMode.Disabled;

        /// <param name="msaa">Samples on the render target. A camera rendering into a
        /// texture takes its MSAA from the texture rather than from the pipeline
        /// asset, so this, not the asset, is what the rig's frame is multisampled at.</param>
        /// <param name="priming">Depth priming, held on every Universal Renderer at
        /// every render and put back on dispose; null holds nothing. Held per render
        /// rather than set once because the host rewrites the renderer's mode at a
        /// moment of its own after start-up, and a row that set it once measured
        /// whatever the host left: two captures meant to differ in priming came back
        /// bit-identical, and the frame a stored capture was seeded from stopped
        /// reproducing on the same code.</param>
        /// <param name="drawer">GPU Resident Drawer mode, held on the pipeline asset at
        /// every render and put back on dispose; null holds nothing. Setting the
        /// asset's mode recreates the pipeline, so it is written only when it
        /// differs.</param>
        /// <param name="renderScale">Render scale, held on the pipeline asset at every
        /// render and put back on dispose; null holds nothing. Basis sets it from the
        /// user's resolution setting.</param>
        public static VRSLDMXRig Build(int fixtures = FixtureCount, bool withSource = true,
                                       int targetSize = TargetSize, int msaa = 1,
                                       UnityEngine.Rendering.Universal.DepthPrimingMode? priming = DefaultPriming,
                                       GPUResidentDrawerMode? drawer = DefaultDrawer,
                                       float? renderScale = null)
        {
            VRSLDMXRig building = null;
            try
            {
            var rig = building = new VRSLDMXRig();
            rig._captureWas = Time.captureDeltaTime;
            Time.captureDeltaTime = FrameDelta;
            rig._priming = priming;
            if (priming.HasValue && GraphicsSettings.currentRenderPipeline
                    is UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset urpAsset)
            {
                foreach (var data in urpAsset.rendererDataList)
                    if (data is UnityEngine.Rendering.Universal.UniversalRendererData universal)
                        rig._primingWas[universal] = universal.depthPrimingMode;
            }
            rig.HoldPriming();

            // Every mip of every texture, for the rig's lifetime. The host streams
            // mips, and the rig's camera is disabled, so nothing ever tells the
            // streamer what this view needs: which mips are resident is then a
            // function of what ran before. That was the run-shape difference the
            // image rows carried for weeks, a soft gobo disc against a sharp one,
            // and the fixture bodies' textures beside it.
            rig._forceLoadWas = Texture.streamingTextureForceLoadAll;
            Texture.streamingTextureForceLoadAll = true;

            // The frame is the package's, not the host scene's. Whatever sun,
            // skybox and ambient the test scene carries is switched off for the
            // rig's lifetime and put back on dispose. Found by capturing the rig
            // with the manager off: the package moved 228 pixels by at most 3 of
            // 255, while a host highlight on the floor moved 2782 by 48, and which
            // rows had run first decided whether that highlight was there at all.
            rig._lightsWereOn = new List<Light>();
            foreach (var light in UnityEngine.Object.FindObjectsByType<Light>(
                         FindObjectsInactive.Exclude))
            {
                if (light == null || !light.enabled) continue;
                light.enabled = false;
                rig._lightsWereOn.Add(light);
            }
            rig._ambientModeWas  = RenderSettings.ambientMode;
            rig._ambientLightWas = RenderSettings.ambientLight;
            rig._skyboxWas       = RenderSettings.skybox;
            rig._sunWas          = RenderSettings.sun;
            rig._fogWas          = RenderSettings.fog;
            RenderSettings.ambientMode  = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = Color.black;
            RenderSettings.skybox       = null;
            RenderSettings.sun          = null;
            RenderSettings.fog          = false;

            // Anything a previous test left behind, usually because it failed
            // before its finally ran. The cost of not clearing it is silent and
            // confusing rather than loud: the manager is a singleton, so a second
            // one destroys itself in Awake, and every read then comes back zero
            // with nothing in the log to say why.
            foreach (var stale in UnityEngine.Object.FindObjectsByType<GameObject>(
                         FindObjectsInactive.Include))
                if (stale != null && stale.name == RootName)
                    UnityEngine.Object.DestroyImmediate(stale);
            Assert(VRSL_URPLightManager.Instance == null,
                "a light manager outside the rig still holds the singleton, so the rig's own "
              + "manager would destroy itself on Awake");

            rig._root = new GameObject(RootName);

            // A render target, because batch mode has no display to render to and
            // a camera without one produces nothing.
            rig._target = new RenderTexture(targetSize, targetSize, 24)
            {
                name         = "VRSL test target",
                antiAliasing = Mathf.Max(1, msaa),
            };
            // The target's count only reaches the frame while the pipeline asset
            // allows MSAA at all, and a host that rewrites its asset per quality
            // tier does so at a moment of its own: it read 2x as a row began and 1x
            // by its first frame. Held to the asked count on every render, put
            // back on dispose.
            rig._msaa  = msaa;
            rig._asset = GraphicsSettings.currentRenderPipeline
                             as UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset;
            rig._assetMsaaWas = rig._asset != null ? rig._asset.msaaSampleCount : 0;
            rig._drawer    = drawer;
            rig._drawerWas = rig._asset != null ? rig._asset.gpuResidentDrawerMode : GPUResidentDrawerMode.Disabled;
            rig.HoldDrawer();
            rig._renderScale    = renderScale;
            rig._renderScaleWas = rig._asset != null ? rig._asset.renderScale : 1f;
            rig.HoldRenderScale();
            rig._camera = new GameObject("Camera").AddComponent<Camera>();
            rig._camera.transform.SetParent(rig._root.transform, false);
            // Aimed across the near end of the truss and down at the floor, so several
            // beams land somewhere visible. Pointed along the horizon it saw one fixture
            // and empty space.
            var eye = new Vector3(6f, 4f, -12f);
            rig._camera.transform.SetPositionAndRotation(
                eye, Quaternion.LookRotation(new Vector3(10f, 0f, 4f) - eye, Vector3.up));
            rig._camera.clearFlags      = CameraClearFlags.SolidColor;
            rig._camera.backgroundColor = Color.black;
            rig._camera.targetTexture   = rig._target;
            // The player's view of the rig, as far as the policy for secondary cameras
            // is concerned, even though it renders into a texture: every row measures
            // the level it sets, and a mirror row adds its own secondary cameras.
            VRSLCameraFilter.RegisterMainView(rig._camera);
            // Rendering is driven from Step and RenderFrame only. Left enabled, the
            // player loop renders it too and the DMX pass runs twice per frame. That
            // happens to be harmless today because the integrators advance from
            // LateUpdate and the pass is a pure function of the buffers, but the rows
            // would drift by a factor of two the moment anything advanced per render,
            // and they would still produce smooth plausible directions while doing it.
            rig._camera.enabled         = false;

            // A floor for the beams to land on.
            //
            // Without one the fixtures hung over empty space and the rig rendered an
            // almost entirely black frame — measured at 0.09% of pixels lit, peak
            // brightness 59 of 255. Everything downstream inherited that. The volumetric
            // march ran on a couple of hundred pixels, so its step count was invisible
            // and what looked like the cost of volumetrics was really the pass's fixed
            // overhead; clearing the tile cull cost 0.0015 ms because there was nothing
            // to light either way; and "identical image" rows were comparing two black
            // rectangles. A rig that renders nothing cannot be asked what rendering
            // costs.
            var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
            floor.name = "Floor";
            rig.Floor  = floor;
            floor.transform.SetParent(rig._root.transform, false);
            floor.transform.localPosition = new Vector3(fixtures * Spacing * 0.5f, 0f, 0f);
            floor.transform.localScale    = new Vector3(fixtures * Spacing * 0.2f + 4f, 1f, 6f);

            var fixtureSrc = Load<GameObject>(FixturePrefab);
            for (int i = 0; i < fixtures; i++)
            {
                var go = UnityEngine.Object.Instantiate(fixtureSrc, rig._root.transform);
                go.name = $"Fixture ({i:000})";
                go.transform.localPosition = new Vector3(i * Spacing, 5.6f, 0f);
                // Pointed at the floor, the way a hung fixture is, so its beam lands
                // somewhere rather than running off to the far plane.
                go.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
                // Legacy sector mode walks the flat space directly, which is what
                // every row's prediction assumes. The universe field only exists
                // on the non-legacy path and is exercised separately.
                foreach (var f in go.GetComponentsInChildren<VRStageLighting_DMX_RealtimeLight>())
                {
                    f.useLegacySectorMode = true;
                    f.sector = i;
                    rig.Fixtures.Add(f);
                }
            }

            var mgrGo = UnityEngine.Object.Instantiate(Load<GameObject>(ManagerPrefab), rig._root.transform);
            mgrGo.name = "Light Manager";
            rig.Manager = mgrGo.GetComponent<VRSL_URPLightManager>();
            // Instantiating an active prefab runs Awake and OnEnable before this
            // returns, so the manager collected whatever existed at that moment.
            // The fixtures above already do, but a caller adding more later has
            // to say so.
            rig.Manager.RefreshFixtures();
            ClearDmxTextures(rig.Manager);

            if (withSource)
            {
                var srcGo = new GameObject("Synthetic DMX Source");
                srcGo.transform.SetParent(rig._root.transform, false);
                rig.Source = srcGo.AddComponent<VRSL_SyntheticDMXChannelSource>();
                // Assigned rather than left to the source's own OnEnable, which
                // only lands if the manager already claimed the singleton.
                rig.Manager.ChannelSource = rig.Source;
                rig.Manager.SetDmxInput((int)VRSLDmxInput.StreamData);
            }
            return rig;
            }
            catch
            {
                // Otherwise the caller has no handle to clean up with and the
                // leftovers break every test that follows.
                building?.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Learn which light-data slot belongs to which fixture.
        ///
        /// Identity cannot come from collection order: <c>FindObjectsByType</c>
        /// does not promise one, and in practice it is neither scene order nor
        /// stable between runs. It comes from the x coordinate instead, which the
        /// rig gives every fixture as a distinct multiple of <see cref="Spacing"/>.
        /// The emitter sits below the fixture root by the lens offset, so only x
        /// is compared — the offset is vertical and identical for all of them.
        ///
        /// Call it after stepping at least one frame: it reads light data the
        /// compute pass writes while rendering.
        /// </summary>
        public void Calibrate()
        {
            var raw = ReadRaw();
            Assert(raw.Length >= Fixtures.Count,
                $"the manager collected {raw.Length} fixtures but the rig built {Fixtures.Count}");

            bool anyWritten = false;
            foreach (var d in raw)
                if (d.positionAndRange != Vector4.zero) { anyWritten = true; break; }
            Assert(anyWritten,
                "every light data entry is zero, so the DMX compute pass never wrote any. "
              + "That is a camera that did not render rather than anything to do with the "
              + "channel buffer");

            _map = new int[Fixtures.Count];
            var claimed = new HashSet<int>();
            for (int i = 0; i < Fixtures.Count; i++)
            {
                float want = Fixtures[i].transform.position.x;
                int found = -1;
                for (int r = 0; r < raw.Length; r++)
                {
                    if (claimed.Contains(r)) continue;
                    if (Mathf.Abs(raw[r].positionAndRange.x - want) > Spacing * 0.25f) continue;
                    found = r;
                    break;
                }
                Assert(found >= 0,
                    $"no unclaimed light data sits near x = {want} for the fixture at "
                  + $"ch {ChannelOf(i)}");
                claimed.Add(found);
                _map[i] = found;
            }
        }

        static T Load<T>(string path) where T : UnityEngine.Object
        {
#if UNITY_EDITOR
            var asset = UnityEditor.AssetDatabase.LoadAssetAtPath<T>(path);
            if (asset == null) throw new InvalidOperationException($"Asset not found: {path}");
            return asset;
#else
            throw new InvalidOperationException("The DMX rig builds from package assets and is editor-only.");
#endif
        }

        /// <summary>
        /// Add a camera rendering into its own texture, posed on the far side of the
        /// truss looking back at where the beams land, so a mirror row sees the cones.
        /// </summary>
        public SecondaryCamera AddSecondaryCamera(int targetSize = TargetSize,
                                                  Vector3? eye = null, Vector3? lookAt = null)
        {
            var target = new RenderTexture(targetSize, targetSize, 24)
            {
                name         = $"VRSL test secondary {Secondaries.Count}",
                antiAliasing = Mathf.Max(1, _msaa),
            };
            var cam = new GameObject($"Secondary Camera {Secondaries.Count}").AddComponent<Camera>();
            cam.transform.SetParent(_root.transform, false);
            var at   = eye    ?? new Vector3(6f, 4f, 16f);
            var look = lookAt ?? new Vector3(10f, 0f, 4f);
            cam.transform.SetPositionAndRotation(at, Quaternion.LookRotation(look - at, Vector3.up));
            cam.clearFlags      = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.black;
            cam.targetTexture   = target;
            cam.enabled         = false;
            var secondary = new SecondaryCamera { Camera = cam, Target = target };
            Secondaries.Add(secondary);
            return secondary;
        }

        /// <summary>Render one camera and nothing else. For a row that wants a
        /// frame, or a frame of counters, of a single view.</summary>
        public void Render(Camera camera)
        {
            if (_asset != null && _msaa > 1 && _asset.msaaSampleCount != _msaa)
                _asset.msaaSampleCount = _msaa;
            HoldPriming();
            HoldDrawer();
            HoldRenderScale();
            camera.Render();
        }

        /// <summary>The priming mode the rig is holding, or null.</summary>
        public UnityEngine.Rendering.Universal.DepthPrimingMode? Priming => _priming;

        /// <summary>The GPU Resident Drawer mode the rig is holding, or null.</summary>
        public GPUResidentDrawerMode? Drawer => _drawer;

        void HoldDrawer()
        {
            if (!_drawer.HasValue || _asset == null) return;
            if (_asset.gpuResidentDrawerMode != _drawer.Value)
                _asset.gpuResidentDrawerMode = _drawer.Value;
        }

        void HoldRenderScale()
        {
            if (!_renderScale.HasValue || _asset == null) return;
            if (_asset.renderScale != _renderScale.Value)
                _asset.renderScale = _renderScale.Value;
        }

        void HoldPriming()
        {
            if (!_priming.HasValue) return;
            foreach (var renderer in _primingWas.Keys)
                if (renderer != null && renderer.depthPrimingMode != _priming.Value)
                    renderer.depthPrimingMode = _priming.Value;
        }

        /// <summary>Advance exactly <paramref name="frames"/> frames, which is
        /// <c>frames * FrameDelta</c> of game time whatever the machine does.</summary>
        /// <b>Yield this straight from a test method.</b> The test runner drives
        /// one level of nesting, so a helper that yields this instead of yielding
        /// <c>null</c> itself silently advances no frames at all — and an unrendered
        /// frame reads exactly like a carrier that delivered nothing.
        public IEnumerator Step(int frames)
        {
            for (int i = 0; i < frames; i++)
            {
                yield return null;
                // Driven explicitly rather than left to the player loop. Batch mode
                // renders nothing on its own, and the light data buffer is written
                // by the DMX pass, which only runs while a camera is rendering — so
                // without this every row reads a buffer of zeros and blames the
                // carrier. The camera is disabled, so this is the only render.
                RenderFrame();
            }
        }

        /// <summary>Render one frame: the main camera, then every secondary camera
        /// that is set to render. For helpers that cannot yield <see cref="Step"/>
        /// because they are already a level deep.</summary>
        public void RenderFrame() => RenderFrame(mainCamera: true);

        /// <param name="mainCamera">False leaves the main camera out, for a frame
        /// where only a mirror renders.</param>
        public void RenderFrame(bool mainCamera)
        {
            if (mainCamera) Render(_camera);
            foreach (var secondary in Secondaries)
                if (secondary.Renders) Render(secondary.Camera);
        }

        /// <summary>
        /// Freeze everything that integrates over time, so two captures taken at
        /// different points in a session render the same frame.
        ///
        /// Three things move on their own. Strobe alternates, and the manager's own
        /// toggle holds every strobing fixture on. Gobo spin is a pure integrator that
        /// never settles, so two captures a few hundred frames apart have the gobo at a
        /// different angle. Movement damping converges towards its target rather than
        /// reaching it, so it never quite arrives either.
        ///
        /// <b>Movement is frozen rather than warmed up.</b> Leaving it to settle was
        /// tried and does not hold: two captures within one run came back identical
        /// while two captures in different runs differed on 1341 pixels, because
        /// convergence is asymptotic and any one-frame difference in how the run
        /// started propagates for ever. Setting both smoothing bounds to zero makes the
        /// damping term reach its target on the first frame instead, which is exact and
        /// the same every run.
        ///
        /// Without this an image row compares two frames of the same scene at
        /// different moments and reports a difference that is entirely the clock.
        /// </summary>
        public void FreezeForImageCapture()
        {
            PointBeamsDown();
            // Bright enough to read. At the prefab's 10 the package moved 228 pixels
            // of a 256-square frame by at most 3 of 255, so an image row was
            // guarding a frame the package barely touched.
            foreach (var fixture in Fixtures) fixture.maxIntensity = 60f;
            Manager.disableStrobe = true;
            foreach (var fixture in Fixtures) fixture.enableGoboSpin = false;
            // smoothing of 0 gives lerp(previous, target, 1 - pow(0, dt)) = target.
            Manager.movementSmoothingMax = 0f;
            Manager.movementSmoothingMin = 0f;
            // The raymarch's dither and haze scroll are phased by time. At the
            // step floor the dither reaches the quantised output on a few grazing
            // pixels, so two captures at different frames differed by one 8-bit
            // step on two or three of them.
            Manager.VolumetricTimeOverride = 0f;

            Manager.MarkConfigDirty();
        }


        /// <summary>
        /// Start the CRT decode chain from black.
        ///
        /// The chain's textures are assets, and their contents live for the
        /// session: a row that drives the texture path writes the raw grid and
        /// every CRT downstream of it, and the next rig's fixture bodies read the
        /// leftovers through the <c>_VRSLU_DMX*</c> globals. A rig built first in a
        /// session sees the assets as imported instead. Clearing the live layout's
        /// five and whatever their materials sample puts both on the same footing.
        /// </summary>
        static void ClearDmxTextures(VRSL_URPLightManager manager) => ClearGridTextures(manager.LiveTextures);

        /// <summary>Clear one layout's decode chain to black, and whatever its
        /// materials sample, the video grid included.</summary>
        public static void ClearGridTextures(VRSLGridTextures.Set live)
        {
            var seen = new HashSet<RenderTexture>();
            void Clear(RenderTexture rt)
            {
                if (rt == null || !seen.Add(rt)) return;
                if (rt is CustomRenderTexture crt && crt.material != null)
                    foreach (var name in crt.material.GetTexturePropertyNames())
                        Clear(crt.material.GetTexture(name) as RenderTexture);
                var previous = RenderTexture.active;
                RenderTexture.active = rt;
                GL.Clear(false, true, Color.black);
                RenderTexture.active = previous;
            }
            Clear(live.main);
            Clear(live.movement);
            Clear(live.strobe);
            Clear(live.strobeTimer);
            Clear(live.spinTimer);
        }

        /// <summary>
        /// Put <paramref name="values"/> (channel 1 first, 0..1) on the video grid a
        /// layout's decode chain reads: the texture its main decode texture's material
        /// samples. Each channel fills the block of video texels behind the decode
        /// texel the fixtures read it from (<see cref="CellsOf"/>), so the layout's own
        /// chain decodes it, strobe timing and output included, the way it decodes video.
        /// </summary>
        public static void PaintVideoGrid(VRSLGridTextures.Set set, VRSLGridLayout layout, float[] values)
        {
            var main = set.main as CustomRenderTexture;
            Assert(main != null && main.material != null, $"the {layout} main texture is not a decode CRT");
            var video = main.material.GetTexture("_DMXTexture") as RenderTexture;
            Assert(video != null, $"the {layout} decode chain reads no video grid");
            Assert(video.width % main.width == 0 && video.height % main.height == 0,
                $"the {layout} video grid ({video.width}x{video.height}) is not a whole multiple of "
              + $"its decode texture ({main.width}x{main.height})");
            int sx = video.width / main.width, sy = video.height / main.height;

            var cells  = CellsOf(values.Length, layout, main.width, main.height);
            var pixels = new Color[video.width * video.height];
            for (int i = 0; i < values.Length; i++)
            {
                var grey = new Color(values[i], values[i], values[i], 1f);
                for (int dy = 0; dy < sy; dy++)
                    for (int dx = 0; dx < sx; dx++)
                        pixels[(cells[i].y * sy + dy) * video.width + cells[i].x * sx + dx] = grey;
            }

            var source = new Texture2D(video.width, video.height, TextureFormat.RGBAFloat, false, true)
                         { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            try
            {
                source.SetPixels(pixels);
                source.Apply(false);
                Graphics.Blit(source, video);
            }
            finally { UnityEngine.Object.DestroyImmediate(source); }
        }

        /// <summary>
        /// Point every fixture straight down, as hung.
        ///
        /// The movers pan and tilt to whatever their channels say, and under the
        /// synthetic patterns that sends the beams off sideways at shallow angles,
        /// mostly clear of the floor the camera looks at. Measured: of some ten
        /// thousand marched pixels, 326 rays crossed any cone, every one of them a
        /// grazing edge. A frame with no beam in it cannot guard the beams, so a
        /// capture aims them at the floor first. Rows about movement leave pan and
        /// tilt on, since the beam direction is their only observable.
        /// </summary>
        public void PointBeamsDown()
        {
            foreach (var fixture in Fixtures)
            {
                fixture.enablePanTilt   = false;
                // The authored cone rather than the zoom channel, which under the
                // synthetic patterns narrows most cones to a few degrees.
                fixture.enableConeWidth = false;
            }
            Manager.MarkConfigDirty();
        }

        /// <summary>Frames covering the given span of game time.</summary>
        public static int Frames(float seconds) => Mathf.RoundToInt(seconds / FrameDelta);

        /// <summary>Decoded per-fixture light data as the compute pass wrote it,
        /// which is the order <c>RefreshFixtures</c> happened to collect in.
        /// <c>FindObjectsByType</c> does not promise one, and in practice it is
        /// neither the scene order nor stable between runs.</summary>
        public VRSL_URPLightManager.VRSLLightData[] ReadRaw()
        {
            var buffer = Manager.LightDataBuffer;
            Assert(buffer != null, "the manager has no light data buffer");
            var data = new VRSL_URPLightManager.VRSLLightData[Manager.FixtureCount];
            buffer.GetData(data);
            return data;
        }

        /// <summary>The same data reordered to match <see cref="Fixtures"/>, so a
        /// row can say "fixture 39" and mean the one patched at sector 39.</summary>
        public VRSL_URPLightManager.VRSLLightData[] ReadLights()
        {
            Assert(_map != null, "the rig was read before Calibrate() ran");
            var raw = ReadRaw();
            var ordered = new VRSL_URPLightManager.VRSLLightData[_map.Length];
            for (int i = 0; i < _map.Length; i++) ordered[i] = raw[_map[i]];
            return ordered;
        }

        /// <summary>Beam direction per fixture, the only observable the movement
        /// damping has: pan and tilt are stored nowhere readable and survive only
        /// as the direction the compute derives from them.</summary>
        public Vector3[] ReadDirections()
        {
            var lights = ReadLights();
            var dirs = new Vector3[lights.Length];
            for (int i = 0; i < lights.Length; i++)
            {
                var d = lights[i].directionAndType;
                dirs[i] = new Vector3(d.x, d.y, d.z);
            }
            return dirs;
        }

        /// <summary>Every channel read back through the shader's own accessor,
        /// scaled 0..1. This is the read path a fixture uses, not a copy of the
        /// bytes the CPU uploaded, so it catches an indexing or packing mistake
        /// that a CPU-side comparison cannot see.</summary>
        public float[] ReadChannels()
        {
            int count = Manager.ChannelCount;
            Assert(count > 0, "no channel source is publishing");
            var cs = Manager.computeShader;
            if (_validateKernel < 0) _validateKernel = cs.FindKernel("ValidateChannels");

            cs.SetBuffer(_validateKernel, "_VRSLU_DMXChannels", Manager.ChannelBuffer);
            cs.SetInt("_VRSLU_DMXChannelCount", count);
            // The kernel names the grid texture in the branch it is not taking,
            // and an unbound texture is an error at dispatch even so. A manager
            // with no grid assigned is a legitimate buffer-only scene, so it gets
            // something to bind rather than an assertion.
            cs.SetTexture(_validateKernel, "_DMXMainTex",
                          (Texture)Manager.LiveTextures.main ?? Texture2D.blackTexture);
            return Validate(cs, count);
        }

        /// <summary>
        /// For channels 1..<paramref name="count"/>, the texel of a
        /// <paramref name="width"/> by <paramref name="height"/> grid in
        /// <paramref name="layout"/> that the fixtures' include reads it from. Found on
        /// the GPU by the GridCells test shader, so positions exactly on a texel edge
        /// land where the sampler puts them.
        /// </summary>
        public static Vector2Int[] CellsOf(int count, VRSLGridLayout layout, int width, int height)
        {
            var shader = Shader.Find("Hidden/VRSL-URP/Tests/GridCells");
            Assert(shader != null, "the GridCells test shader is not in this project");

            // Every texel holds its own coordinates, in Unity's convention, which is the
            // one the decode samples in.
            var index = new Texture2D(width, height, TextureFormat.RGBAFloat, false, true)
                        { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            var coords = new Color[width * height];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    coords[y * width + x] = new Color(x, y, 0f, 1f);
            index.SetPixels(coords);
            index.Apply(false);

            var target   = new RenderTexture(count, 1, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
            var readback = new Texture2D(count, 1, TextureFormat.RGBAFloat, false, true);
            var material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            var cmd      = new CommandBuffer { name = "VRSL test grid cells" };
            try
            {
                target.Create();
                material.SetTexture("_VRSLTestIndex", index);
                material.SetVector("_VRSLDMXTexelSize", new Vector4(1f / width, 1f / height, width, height));
                material.SetInteger("_VRSLU_DMXGridLayout", (int)layout);
                cmd.SetRenderTarget(target);
                cmd.DrawProcedural(Matrix4x4.identity, material, 0, MeshTopology.Triangles, 3);
                Graphics.ExecuteCommandBuffer(cmd);

                var previous = RenderTexture.active;
                RenderTexture.active = target;
                readback.ReadPixels(new Rect(0, 0, count, 1), 0, 0, false);
                readback.Apply(false);
                RenderTexture.active = previous;

                var found = readback.GetPixels();
                var cells = new Vector2Int[count];
                for (int i = 0; i < count; i++)
                    cells[i] = new Vector2Int(Mathf.RoundToInt(found[i].r), Mathf.RoundToInt(found[i].g));
                return cells;
            }
            finally
            {
                cmd.Release();
                target.Release();
                UnityEngine.Object.DestroyImmediate(target);
                UnityEngine.Object.DestroyImmediate(readback);
                UnityEngine.Object.DestroyImmediate(index);
                UnityEngine.Object.DestroyImmediate(material);
            }
        }

        /// <summary>A grid painted from channel values, and what a fixture reads from it.</summary>
        public sealed class PaintedGrid : IDisposable
        {
            /// <summary>The grid, grey, so the decode's luminance returns each value.</summary>
            public RenderTexture Texture;
            /// <summary>The texel each channel is read from, channel 1 first.</summary>
            public Vector2Int[] Cells;
            /// <summary>What each channel reads: its own value, or the value of whichever
            /// channel sharing its texel was painted last.</summary>
            public float[] Reads;
            /// <summary>Channels whose texel another channel reads too. The layout cannot
            /// carry them separately, whatever decodes it.</summary>
            public List<int> Shared = new();

            public void Dispose()
            {
                if (Texture == null) return;
                Texture.Release();
                UnityEngine.Object.DestroyImmediate(Texture);
                Texture = null;
            }
        }

        /// <summary>
        /// A <paramref name="width"/> by <paramref name="height"/> grid in
        /// <paramref name="layout"/> carrying <paramref name="values"/> (channel 1 first,
        /// 0..1) in the texels the fixtures read them from (<see cref="CellsOf"/>).
        /// </summary>
        public static PaintedGrid PaintGrid(float[] values, VRSLGridLayout layout, int width, int height)
            => PaintGrid(values, CellsOf(values.Length, layout, width, height), layout, width, height);

        /// <summary>
        /// For channels 1..<paramref name="count"/>, the texel each one occupies: the column of
        /// its slot and the row of its sector, taken from channels no reader can place wrongly
        /// (slot 1 of every sector, and every slot of a reference sector of the same parity),
        /// so it doesn't depend on how a reader turns the rest into a cell.
        /// </summary>
        public static Vector2Int[] OwnCellsOf(int count, VRSLGridLayout layout, int width, int height)
        {
            var found = CellsOf(count, layout, width, height);
            var own   = new Vector2Int[count];
            for (int i = 0; i < count; i++)
            {
                int sector = i / 13, slot = i % 13;
                // A Legacy row holds two sectors side by side, and from sector 40 on the
                // columns shift; a reference sector matches both.
                int reference = (layout == VRSLGridLayout.Legacy && sector >= 40 ? 40 : 0) + (sector & 1);
                own[i] = new Vector2Int(found[reference * 13 + slot].x, found[sector * 13].y);
            }
            return own;
        }

        /// <summary>A grid carrying <paramref name="values"/> in the given texels.</summary>
        public static PaintedGrid PaintGrid(float[] values, Vector2Int[] cells, VRSLGridLayout layout,
                                            int width, int height)
        {
            var pixels = new Color[width * height];
            var owner  = new Dictionary<Vector2Int, int>();
            var grid   = new PaintedGrid { Cells = cells, Reads = new float[values.Length] };
            for (int i = 0; i < values.Length; i++)
            {
                var cell = cells[i];
                Assert(cell.x >= 0 && cell.x < width && cell.y >= 0 && cell.y < height,
                    $"channel {i + 1} reads from outside the grid, at {cell}");
                if (owner.TryGetValue(cell, out int other))
                {
                    if (!grid.Shared.Contains(other + 1)) grid.Shared.Add(other + 1);
                    grid.Shared.Add(i + 1);
                }
                owner[cell] = i;
                pixels[cell.y * width + cell.x] = new Color(values[i], values[i], values[i], 1f);
            }
            for (int i = 0; i < values.Length; i++)
                grid.Reads[i] = pixels[cells[i].y * width + cells[i].x].r;

            var source = new Texture2D(width, height, TextureFormat.RGBAFloat, false, true);
            try
            {
                source.SetPixels(pixels);
                source.Apply(false);
                grid.Texture = new RenderTexture(width, height, 0, RenderTextureFormat.ARGBFloat,
                                                 RenderTextureReadWrite.Linear)
                {
                    name       = $"Painted {layout} grid",
                    filterMode = FilterMode.Point,
                    wrapMode   = TextureWrapMode.Clamp,
                };
                grid.Texture.Create();
                Graphics.CopyTexture(source, grid.Texture);
            }
            finally { UnityEngine.Object.DestroyImmediate(source); }
            grid.Shared.Sort();
            return grid;
        }

        /// <summary>Channels 1..<paramref name="count"/> read back through the
        /// shader's own accessor forced onto the CRT texture path, whatever the
        /// channel buffer is doing.
        ///
        /// <c>_VRSLU_DMXChannelCount</c> is bound zero here rather than from the
        /// manager, which is what makes <c>MainChannel</c> take its texture
        /// branch even while a source is publishing. A row can therefore read
        /// both feeds within one frame and compare them against each other
        /// rather than against two separate moments in the stream.
        ///
        /// The values come from the interpolation CRT, so they are damped: this
        /// is what a fixture reads, not the raw grid bytes.</summary>
        public float[] ReadGridChannels(int count)
        {
            var cs = Manager.computeShader;
            Assert(cs != null, "the manager has no compute shader");
            var grid = Manager.LiveTextures.main;
            Assert(grid != null, "the manager has no DMX grid texture");
            if (_validateKernel < 0) _validateKernel = cs.FindKernel("ValidateChannels");

            // The kernel declares the channel buffer whether or not its branch is
            // taken, and an unbound StructuredBuffer is not safe to dispatch with.
            _dummyChannels ??= new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, sizeof(uint));

            cs.SetBuffer(_validateKernel, "_VRSLU_DMXChannels",
                         Manager.ChannelBuffer ?? _dummyChannels);
            cs.SetInt("_VRSLU_DMXChannelCount", 0);
            cs.SetInt("_VRSLU_DMXGridLayout", (int)(Manager.GridLayout ?? VRSLGridLayout.Horizontal));
            cs.SetTexture(_validateKernel, "_DMXMainTex", grid);
            cs.SetVector("_VRSLDMXTexelSize", new Vector4(
                1f / grid.width, 1f / grid.height, grid.width, grid.height));
            return Validate(cs, count);
        }

        /// <summary>Run the validation kernel over channels 1..<paramref name="count"/>
        /// and bring the answers back. The caller binds whichever source it wants
        /// read; this is the part both readers share, so a change to the kernel's
        /// contract lands in one place.</summary>
        float[] Validate(ComputeShader cs, int count)
        {
            var readback = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, sizeof(float));
            var values = new float[count];
            try
            {
                cs.SetBuffer(_validateKernel, "_VRSLU_ValidationOut", readback);
                cs.SetInt("_VRSLU_ValidationStart", 1);
                cs.SetInt("_VRSLU_ValidationCount", count);
                cs.Dispatch(_validateKernel, Mathf.CeilToInt(count / 64f), 1, 1);
                readback.GetData(values);
            }
            finally { readback.Release(); }
            return values;
        }

        static void Assert(bool condition, string what)
        {
            if (!condition) throw new InvalidOperationException($"Test rig: {what}.");
        }

        public void Dispose()
        {
            // Restoring this matters beyond the test: left set, captureDeltaTime
            // decouples the whole editor from real time.
            Time.captureDeltaTime = _captureWas;
            Texture.streamingTextureForceLoadAll = _forceLoadWas;
            if (_lightsWereOn != null)
            {
                foreach (var light in _lightsWereOn) if (light != null) light.enabled = true;
                _lightsWereOn = null;
                RenderSettings.ambientMode  = _ambientModeWas;
                RenderSettings.ambientLight = _ambientLightWas;
                RenderSettings.skybox       = _skyboxWas;
                RenderSettings.sun          = _sunWas;
                RenderSettings.fog          = _fogWas;
            }
            VRSLCameraFilter.UnregisterMainView(_camera);
            if (_root != null) UnityEngine.Object.DestroyImmediate(_root);
            _root = null;
            if (_asset != null && _assetMsaaWas > 0) _asset.msaaSampleCount = _assetMsaaWas;
            if (_asset != null && _drawer.HasValue && _asset.gpuResidentDrawerMode != _drawerWas)
                _asset.gpuResidentDrawerMode = _drawerWas;
            _drawer = null;
            if (_asset != null && _renderScale.HasValue) _asset.renderScale = _renderScaleWas;
            _renderScale = null;
            _asset = null;
            foreach (var pair in _primingWas)
                if (pair.Key != null) pair.Key.depthPrimingMode = pair.Value;
            _primingWas.Clear();
            _priming = null;
            if (_target != null) { _target.Release(); UnityEngine.Object.DestroyImmediate(_target); }
            _target = null;
            foreach (var secondary in Secondaries)
                if (secondary.Target != null)
                {
                    secondary.Target.Release();
                    UnityEngine.Object.DestroyImmediate(secondary.Target);
                }
            Secondaries.Clear();
            _dummyChannels?.Release();
            _dummyChannels = null;
        }
    }
}
