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
        const string ManagerPrefab = Pkg + "Runtime/Prefabs/DMX/Horizontal Mode/DMX-13CH-URP-Fixtures/VRSL-DMX-URP-LightManager-Horizontal.prefab";
        const string FixturePrefab = Pkg + "Runtime/Prefabs/DMX/Horizontal Mode/DMX-13CH-URP-Fixtures/VRSL-DMX-Mover-Spotlight-H-13CH-URP.prefab";

        /// <summary>The camera's render target, for rows that read the frame back.</summary>
        public RenderTexture Target => _target;

        public VRSL_URPLightManager             Manager { get; private set; }
        public VRSL_SyntheticDMXChannelSource   Source  { get; private set; }
        public readonly List<VRStageLighting_DMX_RealtimeLight> Fixtures = new();

        GameObject    _root;
        Camera        _camera;
        RenderTexture _target;
        float         _captureWas;
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

        public static VRSLDMXRig Build(int fixtures = FixtureCount, bool withSource = true,
                                       int targetSize = TargetSize)
        {
            VRSLDMXRig building = null;
            try
            {
            var rig = building = new VRSLDMXRig();
            rig._captureWas = Time.captureDeltaTime;
            Time.captureDeltaTime = FrameDelta;

            // Anything a previous test left behind, usually because it failed
            // before its finally ran. The cost of not clearing it is silent and
            // confusing rather than loud: the manager is a singleton, so a second
            // one destroys itself in Awake, and every read then comes back zero
            // with nothing in the log to say why.
            foreach (var stale in UnityEngine.Object.FindObjectsByType<GameObject>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (stale != null && stale.name == RootName)
                    UnityEngine.Object.DestroyImmediate(stale);
            Assert(VRSL_URPLightManager.Instance == null,
                "a light manager outside the rig still holds the singleton, so the rig's own "
              + "manager would destroy itself on Awake");

            rig._root = new GameObject(RootName);

            // A render target, because batch mode has no display to render to and
            // a camera without one produces nothing.
            rig._target = new RenderTexture(targetSize, targetSize, 24) { name = "VRSL test target" };
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

            if (withSource)
            {
                var srcGo = new GameObject("Synthetic DMX Source");
                srcGo.transform.SetParent(rig._root.transform, false);
                rig.Source = srcGo.AddComponent<VRSL_SyntheticDMXChannelSource>();
                // Assigned rather than left to the source's own OnEnable, which
                // only lands if the manager already claimed the singleton.
                rig.Manager.ChannelSource = rig.Source;
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
                _camera.Render();
            }
        }

        /// <summary>Render one frame. For helpers that cannot yield
        /// <see cref="Step"/> because they are already a level deep.</summary>
        public void RenderFrame()
        {
            _camera.Render();
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
                          (Texture)Manager.dmxMainTexture ?? Texture2D.blackTexture);
            return Validate(cs, count);
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
            var grid = Manager.dmxMainTexture;
            Assert(grid != null, "the manager has no DMX grid texture");
            if (_validateKernel < 0) _validateKernel = cs.FindKernel("ValidateChannels");

            // The kernel declares the channel buffer whether or not its branch is
            // taken, and an unbound StructuredBuffer is not safe to dispatch with.
            _dummyChannels ??= new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, sizeof(uint));

            cs.SetBuffer(_validateKernel, "_VRSLU_DMXChannels",
                         Manager.ChannelBuffer ?? _dummyChannels);
            cs.SetInt("_VRSLU_DMXChannelCount", 0);
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
            if (_root != null) UnityEngine.Object.DestroyImmediate(_root);
            _root = null;
            if (_target != null) { _target.Release(); UnityEngine.Object.DestroyImmediate(_target); }
            _target = null;
            _dummyChannels?.Release();
            _dummyChannels = null;
        }
    }
}
