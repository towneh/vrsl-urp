using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;

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

        public VRSL_URPLightManager             Manager { get; private set; }
        public VRSL_SyntheticDMXChannelSource   Source  { get; private set; }
        public readonly List<VRStageLighting_DMX_RealtimeLight> Fixtures = new();

        static readonly List<string> s_vrslErrors = new();

        GameObject    _root;
        Camera        _camera;
        RenderTexture _target;
        float         _captureWas;
        int           _validateKernel = -1;
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

        public static VRSLDMXRig Build(int fixtures = FixtureCount, bool withSource = true)
        {
            VRSLDMXRig building = null;
            try
            {
            // Set here rather than in a [SetUp]: for a [UnityTest] the runner
            // opens its log scope after setup has run, so the flag has to be set
            // from inside the test body to take effect.
            //
            // The project these run in is a Basis client, and its own systems log
            // errors about a missing scene once they have been ticking a while.
            // Without this the rows that step thousands of frames fail for a reason
            // that has nothing to do with them, while the short ones pass — which
            // reads as flakiness rather than as an unrelated log.
            // The host project logs errors of its own, and the framework fails any
            // test that sees one, so whichever row is running at that moment loses.
            // Swapping Debug.unityLogger.logHandler does not catch them — the host
            // logs through a logger of its own — so the framework's blanket switch is
            // the only lever. What it costs is that a VRSL error would no longer fail
            // a row by itself, so those are collected below and judged in teardown.
            LogAssert.ignoreFailingMessages = true;

            var rig = building = new VRSLDMXRig();
            Application.logMessageReceived += rig.OnLog;
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
            rig._target = new RenderTexture(256, 256, 24) { name = "VRSL test target" };
            rig._camera = new GameObject("Camera").AddComponent<Camera>();
            rig._camera.transform.SetParent(rig._root.transform, false);
            rig._camera.transform.position = new Vector3(0f, 2f, -12f);
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

            var fixtureSrc = Load<GameObject>(FixturePrefab);
            for (int i = 0; i < fixtures; i++)
            {
                var go = UnityEngine.Object.Instantiate(fixtureSrc, rig._root.transform);
                go.name = $"Fixture ({i:000})";
                go.transform.localPosition = new Vector3(i * Spacing, 5.6f, 0f);
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
        /// <see cref="Step"/> because they are already a level deep.
        ///
        /// Re-asserts the log suppression as well, because the runner resets it
        /// around each yield and this is the other path frames are advanced by. Left
        /// only in <see cref="Step"/>, the gap was every frame a sampling helper
        /// drove — which is why the strobe rows, with the longest sampling loops,
        /// were the ones that kept catching the host's error.</summary>
        public void RenderFrame()
        {
            LogAssert.ignoreFailingMessages = true;
            _camera.Render();
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

            var readback = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, sizeof(float));
            var values = new float[count];
            try
            {
                cs.SetBuffer(_validateKernel, "_VRSLU_DMXChannels", Manager.ChannelBuffer);
                cs.SetInt("_VRSLU_DMXChannelCount", count);
                cs.SetBuffer(_validateKernel, "_VRSLU_ValidationOut", readback);
                cs.SetInt("_VRSLU_ValidationStart", 1);
                cs.SetInt("_VRSLU_ValidationCount", count);
                cs.Dispatch(_validateKernel, Mathf.CeilToInt(count / 64f), 1, 1);
                readback.GetData(values);
            }
            finally { readback.Release(); }
            return values;
        }

        void OnLog(string message, string stack, LogType type)
        {
            if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert) return;
            // Matched on the package's own log prefix, and deliberately not on the
            // stack: the host's errors are raised during the render this rig drives,
            // so their stack runs through it and a stack match flags every one of them
            // as ours.
            if (message.Contains("[VRSL"))
                s_vrslErrors.Add($"{type}: {message}");
        }

        /// <summary>Errors VRSL itself logged while a rig was alive. The host
        /// project's are not here; theirs are what the suppression is for. Static
        /// because the teardown that judges them outlives any one rig.</summary>
        public static IReadOnlyList<string> CollectedErrors => s_vrslErrors;

        public static void ClearCollectedErrors() => s_vrslErrors.Clear();

        static void Assert(bool condition, string what)
        {
            if (!condition) throw new InvalidOperationException($"Test rig: {what}.");
        }

        public void Dispose()
        {
            // Restoring this matters beyond the test: left set, captureDeltaTime
            // decouples the whole editor from real time.
            Application.logMessageReceived -= OnLog;
            Time.captureDeltaTime = _captureWas;
            if (_root != null) UnityEngine.Object.DestroyImmediate(_root);
            _root = null;
            if (_target != null) { _target.Release(); UnityEngine.Object.DestroyImmediate(_target); }
            _target = null;
        }
    }
}
