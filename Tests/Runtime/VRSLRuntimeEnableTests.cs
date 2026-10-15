#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace VRSL.URP.Tests
{
    /// <summary>
    /// Realtime fixtures switched on and off while the world runs, with no call to
    /// <c>RefreshFixtures()</c>: the DMX manager follows them by itself, collecting once
    /// a frame however many switch and not at all in a frame where none did.
    ///
    /// Rows Z1 to Z7 of TESTING.md, DMX path. The AudioLink path of Z1 to Z6 is in
    /// <c>VRSLAudioLinkRuntimeEnableTests</c>.
    /// </summary>
    class VRSLRuntimeEnableTests : VRSLDMXTest
    {
        const string FixturePrefab =
            "Packages/town.mr.vrsl-urp/Runtime/Prefabs/DMX/VRSL-DMX-Mover-Spotlight-13CH.prefab";

        /// <summary>The prefab instance a rig fixture belongs to: what a world switches
        /// to hide the fixture.</summary>
        static GameObject InstanceOf(VRSLDMXRig rig, VRStageLighting_DMX_RealtimeLight f)
        {
            var root = rig.Floor.transform.parent;
            var t = f.transform;
            while (t.parent != null && t.parent != root) t = t.parent;
            return t.gameObject;
        }

        /// <summary>Whether the light data the last frame decoded holds a light at
        /// <paramref name="x"/>. Read by position because a re-collect reorders the
        /// slots, and with the buffer gone nothing is drawn at all.</summary>
        static bool Drawn(VRSLDMXRig rig, float x)
        {
            var m = rig.Manager;
            if (m.LightDataBuffer == null || m.FixtureCount == 0) return false;
            var data = new VRSL_DMXURPLightManager.VRSLLightData[m.FixtureCount];
            m.LightDataBuffer.GetData(data);
            foreach (var d in data)
                if (Mathf.Abs(d.positionAndRange.x - x) < VRSLDMXRig.Spacing * 0.25f) return true;
            return false;
        }

        static void AssertHeld(VRSLDMXRig rig, int count, float x, bool drawn, string when)
        {
            Assert.AreEqual(count, rig.Manager.FixtureCount, $"fixtures the manager holds {when}");
            Assert.AreEqual(drawn, Drawn(rig, x),
                drawn ? $"the fixture should be drawn {when}" : $"the fixture should not be drawn {when}");
        }

        /// <summary>Row Z1. A fixture's GameObject switched off, then on.</summary>
        [UnityTest]
        public IEnumerator Z1_a_fixture_switched_off_and_on_is_followed()
        {
            var rig = VRSLDMXRig.Build();
            try
            {
                yield return rig.Step(4);
                var f = rig.Fixtures[7];
                float x = f.transform.position.x;
                AssertHeld(rig, VRSLDMXRig.FixtureCount, x, true, "before the switch");

                var go = InstanceOf(rig, f);
                go.SetActive(false);
                yield return rig.Step(1);
                AssertHeld(rig, VRSLDMXRig.FixtureCount - 1, x, false, "the frame after it was switched off");

                go.SetActive(true);
                yield return rig.Step(1);
                AssertHeld(rig, VRSLDMXRig.FixtureCount, x, true, "the frame after it was switched back on");
            }
            finally { rig.Dispose(); }
        }

        /// <summary>Row Z2. The same through a parent the fixture sits under.</summary>
        [UnityTest]
        public IEnumerator Z2_a_fixture_under_a_parent_switched_off_and_on_is_followed()
        {
            var rig = VRSLDMXRig.Build();
            try
            {
                var f = rig.Fixtures[12];
                var go = InstanceOf(rig, f);
                var parent = new GameObject("Room");
                parent.transform.SetParent(go.transform.parent, false);
                go.transform.SetParent(parent.transform, true);
                yield return rig.Step(4);
                float x = f.transform.position.x;
                AssertHeld(rig, VRSLDMXRig.FixtureCount, x, true, "before the switch");

                parent.SetActive(false);
                yield return rig.Step(1);
                AssertHeld(rig, VRSLDMXRig.FixtureCount - 1, x, false, "the frame after its parent was switched off");

                parent.SetActive(true);
                yield return rig.Step(1);
                AssertHeld(rig, VRSLDMXRig.FixtureCount, x, true, "the frame after its parent was switched back on");
            }
            finally { rig.Dispose(); }
        }

        /// <summary>Row Z3. Seventy fixtures switched one by one in a single frame, the
        /// way a world's room culling does it: one scan and one rebuild that frame, and
        /// nothing in the frames after.</summary>
        [UnityTest]
        public IEnumerator Z3_seventy_switched_in_one_frame_are_collected_once()
        {
            const int Extra = 70;
            var rig = VRSLDMXRig.Build();
            try
            {
                var holder = new GameObject("Main room");
                holder.SetActive(false);
                holder.transform.SetParent(rig.Floor.transform.parent, false);
                var prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(FixturePrefab);
                Assert.IsNotNull(prefab, FixturePrefab);
                var extra = new List<GameObject>();
                for (int i = 0; i < Extra; i++)
                {
                    var go = Object.Instantiate(prefab, holder.transform);
                    go.transform.localPosition = new Vector3(i * VRSLDMXRig.Spacing, 5.6f, 12f);
                    extra.Add(go);
                }
                yield return rig.Step(2);
                holder.SetActive(true);
                yield return rig.Step(3);
                Assert.AreEqual(VRSLDMXRig.FixtureCount + Extra, rig.Manager.FixtureCount,
                    "the room's fixtures, switched on with their parent, should be collected");

                int scans = rig.Manager.FixtureScans, rebuilds = rig.Manager.FixtureRebuilds;
                foreach (var go in extra) go.SetActive(false);
                yield return rig.Step(3);
                Assert.AreEqual(VRSLDMXRig.FixtureCount, rig.Manager.FixtureCount, "after seventy switched off");
                Assert.AreEqual(1, rig.Manager.FixtureScans - scans, "scans for seventy switched off in one frame");
                Assert.AreEqual(1, rig.Manager.FixtureRebuilds - rebuilds, "rebuilds for seventy switched off in one frame");

                scans = rig.Manager.FixtureScans; rebuilds = rig.Manager.FixtureRebuilds;
                foreach (var go in extra) go.SetActive(true);
                yield return rig.Step(3);
                Assert.AreEqual(VRSLDMXRig.FixtureCount + Extra, rig.Manager.FixtureCount, "after seventy switched on");
                Assert.AreEqual(1, rig.Manager.FixtureScans - scans, "scans for seventy switched on in one frame");
                Assert.AreEqual(1, rig.Manager.FixtureRebuilds - rebuilds, "rebuilds for seventy switched on in one frame");
            }
            finally { rig.Dispose(); }
        }

        /// <summary>Row Z4. The last fixture switched off leaves the manager holding no
        /// fixture buffers; switched back on, it is drawn.</summary>
        [UnityTest]
        public IEnumerator Z4_the_last_fixture_off_releases_the_buffers()
        {
            var rig = VRSLDMXRig.Build(fixtures: 1);
            try
            {
                yield return rig.Step(4);
                var f = rig.Fixtures[0];
                float x = f.transform.position.x;
                AssertHeld(rig, 1, x, true, "before the switch");

                var go = InstanceOf(rig, f);
                go.SetActive(false);
                yield return rig.Step(1);
                Assert.AreEqual(0, rig.Manager.FixtureCount, "fixtures held with the last one off");
                Assert.IsNull(rig.Manager.LightDataBuffer, "light data held with no fixtures");
                Assert.IsNull(rig.Manager.FixtureConfigBuffer, "fixture configs held with no fixtures");

                go.SetActive(true);
                yield return rig.Step(1);
                AssertHeld(rig, 1, x, true, "the frame after it was switched back on");
            }
            finally { rig.Dispose(); }
        }

        /// <summary>Row Z5. Frames in which nothing switched cost no scan and no rebuild,
        /// and neither does moving a fixture to a new address.</summary>
        [UnityTest]
        public IEnumerator Z5_quiet_frames_and_a_readdress_cost_no_scan()
        {
            var rig = VRSLDMXRig.Build();
            try
            {
                yield return rig.Step(4);
                int scans = rig.Manager.FixtureScans, rebuilds = rig.Manager.FixtureRebuilds;
                var buffer = rig.Manager.LightDataBuffer;
                yield return rig.Step(10);
                Assert.AreEqual(scans, rig.Manager.FixtureScans, "scans in ten frames where nothing switched");
                Assert.AreEqual(rebuilds, rig.Manager.FixtureRebuilds, "rebuilds in ten frames where nothing switched");
                Assert.AreSame(buffer, rig.Manager.LightDataBuffer, "the light data was rebuilt with nothing switched");

                var f = rig.Fixtures[3];
                f.sector = 20;
                f.ApplyAddress();
                yield return rig.Step(2);
                Assert.AreEqual(scans, rig.Manager.FixtureScans, "scans after a fixture moved to a new address");
                Assert.AreEqual(rebuilds, rig.Manager.FixtureRebuilds, "rebuilds after a fixture moved to a new address");
                Assert.AreSame(buffer, rig.Manager.LightDataBuffer, "the light data was rebuilt after a fixture moved to a new address");
            }
            finally { rig.Dispose(); }
        }

        /// <summary>Row Z6. A fixture whose component is switched off, on a GameObject
        /// left on, stops being drawn; switched back on, it is drawn again.</summary>
        [UnityTest]
        public IEnumerator Z6_a_switched_off_component_is_not_drawn()
        {
            var rig = VRSLDMXRig.Build();
            try
            {
                yield return rig.Step(4);
                var f = rig.Fixtures[20];
                float x = f.transform.position.x;
                AssertHeld(rig, VRSLDMXRig.FixtureCount, x, true, "before the switch");

                f.enabled = false;
                yield return rig.Step(1);
                AssertHeld(rig, VRSLDMXRig.FixtureCount - 1, x, false, "the frame after its component was switched off");

                f.enabled = true;
                yield return rig.Step(1);
                AssertHeld(rig, VRSLDMXRig.FixtureCount, x, true, "the frame after its component was switched back on");
            }
            finally { rig.Dispose(); }
        }

        // ── Shader-driven fixtures with no realtime fixture left ──────────────

        const string Prefabs = "Packages/town.mr.vrsl-urp/Runtime/Prefabs/DMX/";

        /// <summary>Fixtures that read the grid in their own shaders and cast no light.</summary>
        static readonly string[] ShaderDriven =
        {
            "VRSL-DMX-Static-Flasher-1CH.prefab",
            "VRSL-DMX-Static-LightBar-13CH.prefab",
            "VRSL-DMX-Static-MultiLightBar-15CH.prefab",
            "VRSL-DMX-Static-6x4StrobeLight-5CH.prefab",
        };

        internal enum Realtime { OnThroughout, NeverOn, SwitchedOff }

        internal static string[] ShaderDrivenPrefabs => ShaderDriven;

        /// <summary>
        /// The rig and <see cref="ShaderDriven"/> on a grid painted in <paramref name="layout"/>,
        /// bright or black, captured from a camera in front of the shader-driven ones.
        /// <paramref name="glow"/> gets each one's mean brightness (0 to 255) over its own
        /// pixels on screen. With <paramref name="mirror"/> that camera is a mirror under
        /// that policy, rendered after the rig's view; with <paramref name="dataReader"/> it
        /// is registered as a DMX screen reader, also rendered after; otherwise it is the
        /// player's view.
        /// </summary>
        internal static IEnumerator CaptureShaderDriven(Realtime realtime, VRSLGridLayout layout, bool bright,
                                                        System.Action<float[]> glow,
                                                        SecondaryCameraMode? mirror = null,
                                                        bool dataReader = false)
        {
            using var rig = VRSLDMXRig.Build();
            var values = VRSLGridDecodeTests.ImageValues(VRSLDMXRig.ChannelOf(VRSLDMXRig.FixtureCount - 1) + 25);
            if (!bright) values = new float[values.Length];
            var size    = VRSLGridDecodeTests.SizeOf(layout);
            var painted = VRSLDMXRig.PaintGrid(values, layout, size.x, size.y);
            var white   = VRSLGridDecodeTests.Solid(Color.white);
            var grid    = ScriptableObject.CreateInstance<VRSLGridTextures>();
            Camera reader = null;
            try
            {
                var placed = new List<GameObject>();
                for (int i = 0; i < ShaderDriven.Length; i++)
                {
                    var prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(Prefabs + ShaderDriven[i]);
                    Assert.IsNotNull(prefab, $"{ShaderDriven[i]} is not in the package");
                    var go = Object.Instantiate(prefab, rig.Floor.transform.parent);
                    go.transform.localPosition = new Vector3(2f + i * 1.6f, 1.5f, -6f);
                    foreach (var s in go.GetComponentsInChildren<VRStageLighting_DMX_Static>(true))
                    {
                        s.useLegacySectorMode = true;
                        s.sector = 2 + i;
                    }
                    placed.Add(go);
                }
                if (realtime == Realtime.NeverOn)
                    foreach (var f in rig.Fixtures) f.enabled = false;

                grid.horizontal = grid.vertical = grid.legacy = new VRSLGridTextures.Set
                    { main = painted.Texture, movement = painted.Texture, strobe = white, spinTimer = painted.Texture };
                rig.Manager.ChannelSource = null;
                rig.Manager.enabled       = false;
                rig.Manager.gridTextures  = grid;
                rig.Manager.dmxInput      = (VRSLDmxInput)layout;
                if (mirror.HasValue) rig.Manager.secondaryCameraMode = mirror.Value;
                rig.Manager.enabled       = true;
                rig.Manager.RefreshFixtures();
                rig.FreezeForImageCapture();

                var at   = new Vector3(4.4f, 3f, -14f);
                var view = rig.AddSecondaryCamera(targetSize: 512, eye: at, lookAt: new Vector3(at.x, 1.5f, -6f));
                if (dataReader)           VRSLCameraFilter.RegisterDataReader(view.Camera);
                else if (!mirror.HasValue) VRSLCameraFilter.RegisterMainView(view.Camera);
                reader = dataReader ? view.Camera : null;

                for (int i = 0; i < 4; i++)
                {
                    yield return null;
                    rig.RenderFrame();
                }
                if (realtime == Realtime.SwitchedOff)
                    foreach (var f in rig.Fixtures) f.enabled = false;
                // Well past the frame the manager notices: the grid has to stay readable,
                // not only survive the frame the count reaches zero.
                for (int i = 0; i < 20; i++)
                {
                    yield return null;
                    rig.RenderFrame();
                }
                Assert.AreEqual(realtime == Realtime.OnThroughout ? VRSLDMXRig.FixtureCount : 0,
                                rig.Manager.FixtureCount, $"realtime fixtures the manager holds ({realtime})");

                var frame = VRSLImageCompare.Read(view.Target);
                try
                {
                    var pixels = frame.GetPixels32();
                    var means  = new float[placed.Count];
                    for (int i = 0; i < placed.Count; i++)
                        means[i] = MeanGlow(pixels, frame.width, frame.height, ScreenRect(view.Camera, placed[i]));
                    glow(means);
                }
                finally { Object.DestroyImmediate(frame); }
            }
            finally
            {
                if (reader != null) VRSLCameraFilter.UnregisterDataReader(reader);
                painted.Dispose();
                Object.DestroyImmediate(white);
                Object.DestroyImmediate(grid);
            }
        }

        /// <summary>The pixels <paramref name="go"/>'s renderers cover on <paramref name="cam"/>'s target.</summary>
        static RectInt ScreenRect(Camera cam, GameObject go)
        {
            var renderers = go.GetComponentsInChildren<Renderer>();
            Assert.IsNotEmpty(renderers, $"{go.name} has no renderers");
            var b = renderers[0].bounds;
            foreach (var r in renderers) b.Encapsulate(r.bounds);
            Vector2 min = new(float.MaxValue, float.MaxValue), max = new(float.MinValue, float.MinValue);
            for (int c = 0; c < 8; c++)
            {
                var corner = new Vector3((c & 1) == 0 ? b.min.x : b.max.x,
                                         (c & 2) == 0 ? b.min.y : b.max.y,
                                         (c & 4) == 0 ? b.min.z : b.max.z);
                Vector2 p = cam.WorldToScreenPoint(corner);
                min = Vector2.Min(min, p);
                max = Vector2.Max(max, p);
            }
            int x0 = Mathf.Clamp(Mathf.FloorToInt(min.x), 0, cam.pixelWidth  - 1);
            int y0 = Mathf.Clamp(Mathf.FloorToInt(min.y), 0, cam.pixelHeight - 1);
            int x1 = Mathf.Clamp(Mathf.CeilToInt(max.x),  x0 + 1, cam.pixelWidth);
            int y1 = Mathf.Clamp(Mathf.CeilToInt(max.y),  y0 + 1, cam.pixelHeight);
            return new RectInt(x0, y0, x1 - x0, y1 - y0);
        }

        static float MeanGlow(Color32[] pixels, int width, int height, RectInt rect)
        {
            double sum = 0;
            for (int y = rect.yMin; y < rect.yMax; y++)
            for (int x = rect.xMin; x < rect.xMax; x++)
            {
                var p = pixels[y * width + x];
                sum += Mathf.Max(p.r, Mathf.Max(p.g, p.b));
            }
            return (float)(sum / (rect.width * rect.height));
        }

        /// <summary>
        /// Row Z7. Shader-driven fixtures go on reading the grid once every realtime
        /// fixture has been switched off, as they do in a scene that never had one. Each
        /// fixture's glow is its mean brightness on a bright grid less its mean on a black
        /// one, over its own pixels: with realtime fixtures on, their light reaches the
        /// shader-driven ones too, so that case is only a check that the frame works.
        /// </summary>
        [UnityTest]
        public IEnumerator Z7_shader_driven_fixtures_read_the_grid_with_every_realtime_fixture_off(
            [Values(VRSLGridLayout.Horizontal, VRSLGridLayout.Vertical)] VRSLGridLayout layout)
        {
            var glow = new Dictionary<Realtime, float[]>();
            foreach (var realtime in new[] { Realtime.OnThroughout, Realtime.NeverOn, Realtime.SwitchedOff })
            {
                float[] lit = null, dark = null;
                yield return CaptureShaderDriven(realtime, layout, true,  g => lit  = g);
                yield return CaptureShaderDriven(realtime, layout, false, g => dark = g);
                var difference = new float[lit.Length];
                for (int i = 0; i < lit.Length; i++) difference[i] = lit[i] - dark[i];
                glow[realtime] = difference;
                Debug.Log($"[Z7] {layout}, realtime fixtures {realtime}: glow "
                        + string.Join(", ", difference.Select((d, i) => $"{ShaderDriven[i]} {d:F2}")));
            }

            for (int i = 0; i < ShaderDriven.Length; i++)
            {
                Assert.Greater(glow[Realtime.OnThroughout][i], 2f,
                    $"{ShaderDriven[i]} doesn't glow on a bright {layout} grid with the realtime fixtures on");
                Assert.Greater(glow[Realtime.NeverOn][i], 2f,
                    $"{ShaderDriven[i]} doesn't glow on a bright {layout} grid in a scene with no realtime "
                  + "fixtures, so the row can't tell anything apart");
                Assert.GreaterOrEqual(glow[Realtime.SwitchedOff][i], 0.5f * glow[Realtime.NeverOn][i],
                    $"{ShaderDriven[i]} glows {glow[Realtime.SwitchedOff][i]:F2} on a bright {layout} grid once "
                  + $"every realtime fixture is switched off, against {glow[Realtime.NeverOn][i]:F2} in a scene "
                  + "that never had one: it has stopped reading the grid");
            }
        }
    }
}
#endif
