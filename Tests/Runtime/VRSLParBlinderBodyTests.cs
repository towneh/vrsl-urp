#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using UnityEngine.TestTools;

namespace VRSL.URP.Tests
{
    /// <summary>
    /// The par and blinder bodies, and the blinders' lens flares, follow the fixture's
    /// DMX; the 5-channel ones cast light from a 5-channel patch.
    ///
    /// Rows J1 to J4 of TESTING.md.
    /// </summary>
    class VRSLParBlinderBodyTests : VRSLDMXTest
    {
        const string Prefabs = "Packages/town.mr.vrsl-urp/Runtime/Prefabs/DMX/";

        static readonly string[] ParsAndBlinders =
        {
            "Horizontal Mode/DMX-13CH-URP-Fixtures/VRSL-DMX-Static-ParLight-H-13CH-URP.prefab",
            "Horizontal Mode/DMX-13CH-URP-Fixtures/VRSL-DMX-Static-Blinder-H-13CH-URP.prefab",
            "Legacy Mode/DMX-13CH-URP-Fixtures/VRSL-DMX-Static-ParLight-L-13CH-URP.prefab",
            "Legacy Mode/DMX-13CH-URP-Fixtures/VRSL-DMX-Static-Blinder-L-13CH-URP.prefab",
            "Vertical Mode/DMX-13CH-URP-Fixtures/VRSL-DMX-Static-ParLight-V-13CH-URP.prefab",
            "Vertical Mode/DMX-13CH-URP-Fixtures/VRSL-DMX-Static-Blinder-V-13CH-URP.prefab",
            "Horizontal Mode/5-Channel Statics/DMX-5CH-URP-Fixtures/VRSL-DMX-Static-ParLight-H-5CH-URP.prefab",
            "Horizontal Mode/5-Channel Statics/DMX-5CH-URP-Fixtures/VRSL-DMX-Static-Blinder-H-5CH-URP.prefab",
            "Vertical Mode/5-Channel Statics/DMX-5CH-URP-Fixtures/VRSL-DMX-Static-ParLight-V-5CH-URP.prefab",
            "Vertical Mode/5-Channel Statics/DMX-5CH-URP-Fixtures/VRSL-DMX-Static-Blinder-V-5CH-URP.prefab",
        };

        static IEnumerable<string> FiveChannel => ParsAndBlinders.Where(p => p.Contains("-5CH-"));

        /// <summary>The brightest linear value J2 and J4 allow on a body or flare at black:
        /// the housing keeps a faint sheen of the rig's reflections, while a lit lens, and a
        /// body glowing whatever the desk sends, run to hundreds.</summary>
        const float DarkPeak = 0.25f;

        /// <summary>Upstream's <c>lightColorTint</c>, which the migration tool carries
        /// into <c>shellEmissionTint</c> for the scenes it brings across.</summary>
        static readonly Color BodyTint = new Color(2f, 2f, 2f, 1f);

        static GameObject Load(string path)
        {
            var prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(Prefabs + path);
            Assert.IsNotNull(prefab, $"{path} is not in the package");
            return prefab;
        }

        /// <summary>
        /// J1. Every renderer on a par or blinder is its body or its flare, so
        /// each is listed for the realtime light to drive, and once the fixture wakes each
        /// carries its address, DMX on and the body tint.
        /// </summary>
        [Test]
        public void J1_every_par_and_blinder_drives_its_body_and_flare()
        {
            foreach (var path in ParsAndBlinders)
            {
                var prefab = Load(path);
                var light  = prefab.GetComponent<VRStageLighting_DMX_RealtimeLight>();
                Assert.IsNotNull(light, $"{path} has no realtime light on its root");
                CollectionAssert.AreEquivalent(prefab.GetComponentsInChildren<MeshRenderer>(true),
                    light.fixtureShellRenderers ?? new MeshRenderer[0],
                    $"{path}: a renderer left off the list glows at full whatever the desk sends");
                Assert.AreEqual(BodyTint, light.shellEmissionTint, $"{path}: the body tint");
                Assert.AreEqual(path.Contains("-5CH-"), light.use5ChannelMode,
                    $"{path}: the cast light must read the patch the fixture is named for");

                var go = Object.Instantiate(prefab);
                try
                {
                    var woken = go.GetComponent<VRStageLighting_DMX_RealtimeLight>();
                    foreach (var r in woken.fixtureShellRenderers)
                    {
                        Assert.IsTrue(r.HasPropertyBlock(), $"{path}: {r.name} was not driven on wake");
                        var block = new MaterialPropertyBlock();
                        r.GetPropertyBlock(block);
                        Assert.AreEqual(1, block.GetInt("_EnableDMX"), $"{path}: {r.name} must follow DMX");
                        Assert.AreEqual(woken.ComputeAbsoluteChannel(), block.GetInt("_DMXChannel"),
                            $"{path}: {r.name} must read the fixture's address");
                        Assert.AreEqual(BodyTint, block.GetColor("_Emission"), $"{path}: {r.name}'s tint");
                    }
                }
                finally { Object.DestroyImmediate(go); }
            }
        }

        /// <summary>
        /// J3. On Ramp every channel holds its own value, so the colour a 5-channel
        /// par or blinder decodes names the channels it read: +1 to +3 of its address, not
        /// the 13-channel layout's +7 to +9.
        /// </summary>
        [UnityTest]
        public IEnumerator J3_five_channel_pars_and_blinders_cast_light_from_a_five_channel_patch()
        {
            var rig = VRSLDMXRig.Build();
            try
            {
                yield return rig.Step(4);
                rig.Source.pattern   = VRSL_SyntheticDMXChannelSource.Pattern.Ramp;
                rig.Source.universes = 1;

                var placed = new List<(string path, VRStageLighting_DMX_RealtimeLight light)>();
                int i = 0;
                foreach (var path in FiveChannel)
                {
                    var go = Object.Instantiate(Load(path), rig.Floor.transform.parent);
                    // Well away from the rig's own fixtures, so each record is told apart
                    // by where its light sits.
                    go.transform.localPosition = new Vector3(100f + 10f * i, 1.5f, 0f);
                    var light = go.GetComponent<VRStageLighting_DMX_RealtimeLight>();
                    light.useLegacySectorMode = true;
                    light.sector = 3 + 3 * i;
                    placed.Add((path, light));
                    i++;
                }
                rig.Manager.RefreshFixtures();
                yield return rig.Step(3);

                var raw = rig.ReadRaw();
                foreach (var (path, light) in placed)
                {
                    var at = light.transform.position;
                    var record = raw.OrderBy(d => Vector3.Distance(at,
                        new Vector3(d.positionAndRange.x, d.positionAndRange.y, d.positionAndRange.z))).First();
                    int abs = light.ComputeAbsoluteChannel();
                    var fiveChannel   = new Vector3(VRSLDMXRig.RampAt(abs + 1), VRSLDMXRig.RampAt(abs + 2),
                                                    VRSLDMXRig.RampAt(abs + 3));
                    var thirteenRead  = new Vector3(VRSLDMXRig.RampAt(VRSLDMXRig.RedChannel(abs)),
                                                    VRSLDMXRig.RampAt(VRSLDMXRig.RedChannel(abs) + 1),
                                                    VRSLDMXRig.RampAt(VRSLDMXRig.RedChannel(abs) + 2));
                    Assert.IsFalse(Near(fiveChannel, thirteenRead),
                        "the two layouts' colour channels must hold different values, or matching one proves nothing");
                    AssertNear(fiveChannel,
                        new Vector3(record.colorAndIntensity.x, record.colorAndIntensity.y, record.colorAndIntensity.z),
                        $"{path} must take its colour from +1 to +3 of its address");
                }
            }
            finally { rig.Dispose(); }
        }

        /// <summary>
        /// J2. The Horizontal 13- and 5-channel par and blinder, turned to the
        /// camera: each body and flare is dark with the fixture's channels at 0 and lit red
        /// with its dimmer and red at full, from a video grid and from a channel source.
        /// </summary>
        [UnityTest]
        public IEnumerator J2_bodies_and_flares_are_dark_at_black_and_lit_in_the_fixtures_colour()
        {
            string[] paths = { ParsAndBlinders[0], ParsAndBlinders[1], ParsAndBlinders[6], ParsAndBlinders[7] };
            var rig    = VRSLDMXRig.Build(withSource: false, targetSize: 512);
            var source = new FixedChannels();
            var made   = new List<Object>();
            var grids  = new List<VRSLDMXRig.PaintedGrid>();
            try
            {
                yield return rig.Step(2);
                var placed = new List<(string path, VRStageLighting_DMX_RealtimeLight light)>();
                for (int i = 0; i < paths.Length; i++)
                {
                    var go = Object.Instantiate(Load(paths[i]), rig.Floor.transform.parent);
                    // Upright, a par's lens faces the floor and the camera sees its rim.
                    go.transform.SetPositionAndRotation(new Vector3(100f + 1.6f * i, 1.5f, 0f),
                                                        Quaternion.Euler(90f, 0f, 0f));
                    var light = go.GetComponent<VRStageLighting_DMX_RealtimeLight>();
                    light.useLegacySectorMode = true;
                    light.sector = 2 + 2 * i;
                    // Held on, so lit or dark is the dimmer's alone.
                    light.enableStrobe = false;
                    // No cast light: its beam points at the camera in the same colour and would
                    // light the measured pixels whatever the body did.
                    light.maxIntensity = 0f;
                    light.ApplyAddress();
                    placed.Add((paths[i], light));
                }
                rig.Manager.RefreshFixtures();
                rig.Camera.transform.SetPositionAndRotation(new Vector3(102.4f, 1.5f, -5f), Quaternion.identity);

                foreach (bool fromSource in new[] { false, true })
                    foreach (bool lit in new[] { false, true })
                    {
                        var values = new float[VRSLDMX.UsableSlotsPerUniverse];
                        if (lit)
                            foreach (var (_, light) in placed)
                            {
                                int abs = light.ComputeAbsoluteChannel();
                                bool five = light.use5ChannelMode;
                                values[(five ? abs     : VRSLDMXRig.DimmerChannel(abs)) - 1] = 1f;
                                values[(five ? abs + 1 : VRSLDMXRig.RedChannel(abs))    - 1] = 1f;
                            }

                        if (fromSource)
                        {
                            source.Set(values);
                            rig.Manager.ChannelSource = source;
                            rig.Manager.SetDmxInput((int)VRSLDmxInput.StreamData);
                        }
                        else
                        {
                            var size    = VRSLGridDecodeTests.SizeOf(VRSLGridLayout.Horizontal);
                            var painted = VRSLDMXRig.PaintGrid(values, VRSLGridLayout.Horizontal, size.x, size.y);
                            grids.Add(painted);
                            var white = VRSLGridDecodeTests.Solid(Color.white);
                            var grid  = ScriptableObject.CreateInstance<VRSLGridTextures>();
                            made.Add(white);
                            made.Add(grid);
                            var set = new VRSLGridTextures.Set
                            {
                                main = painted.Texture, movement = painted.Texture, strobe = white,
                                spinTimer = painted.Texture,
                            };
                            grid.horizontal = grid.vertical = grid.legacy = set;
                            rig.Manager.ChannelSource = null;
                            rig.Manager.enabled       = false;
                            rig.Manager.gridTextures  = grid;
                            rig.Manager.dmxInput      = VRSLDmxInput.HorizontalGrid;
                            rig.Manager.enabled       = true;
                            rig.Manager.RefreshFixtures();
                        }
                        yield return rig.Step(12);

                        var frame = CaptureHdr(rig);
                        try
                        {
                            foreach (var (path, light) in placed)
                                foreach (var r in light.GetComponentsInChildren<MeshRenderer>())
                                {
                                    var (mean, peak) = Measure(rig.Camera, frame, r);
                                    string what = $"{path}: {r.name} {(fromSource ? "from a channel source" : "from a video grid")}";
                                    Debug.Log($"[J2] {what}, {(lit ? "lit" : "dark")}: peak {peak}, mean {mean}");
                                    if (lit)
                                        AssertLitRed(mean, peak, what);
                                    else
                                        Assert.LessOrEqual(peak, DarkPeak,
                                            $"{what} must be dark with the fixture's channels at 0 (peak {peak}, mean {mean})");
                                }
                        }
                        finally { Object.DestroyImmediate(frame); }
                    }
            }
            finally
            {
                foreach (var g in grids) g.Dispose();
                rig.Dispose();
                foreach (var o in made)
                {
                    if (o is RenderTexture rt) rt.Release();
                    Object.DestroyImmediate(o);
                }
                source.Dispose();
            }
        }

        /// <summary>
        /// J4. A par re-addressed through <c>ApplyAddress()</c> moves its body
        /// with its beam. Sector 3 is dark and sector 10 lit red, so the body renders dark
        /// before the call and red after it, its block carries the new channel, and the
        /// beam decodes the new address's red.
        /// </summary>
        [UnityTest]
        public IEnumerator J4_a_re_addressed_par_moves_its_body_with_its_beam()
        {
            var rig    = VRSLDMXRig.Build(withSource: false, targetSize: 512);
            var source = new FixedChannels();
            try
            {
                yield return rig.Step(2);
                var go = Object.Instantiate(Load(ParsAndBlinders[0]), rig.Floor.transform.parent);
                // Turned so its lens faces the camera, as in J2.
                go.transform.SetPositionAndRotation(new Vector3(100f, 1.5f, 0f), Quaternion.Euler(90f, 0f, 0f));
                var par = go.GetComponent<VRStageLighting_DMX_RealtimeLight>();
                par.useLegacySectorMode = true;
                par.sector = 3;
                par.enableStrobe = false;
                // No cast light, as in J2; the decoded colour is still written.
                par.maxIntensity = 0f;
                par.ApplyAddress();
                Assert.IsNotEmpty(par.fixtureShellRenderers ?? new MeshRenderer[0],
                    "the par drives no body, so nothing about it can follow the new address");

                int abs = VRSLDMXRig.ChannelOf(10);
                var values = new float[VRSLDMX.UsableSlotsPerUniverse];
                values[VRSLDMXRig.DimmerChannel(abs) - 1] = 1f;
                values[VRSLDMXRig.RedChannel(abs) - 1]    = 1f;
                source.Set(values);
                rig.Manager.ChannelSource = source;
                rig.Manager.SetDmxInput((int)VRSLDmxInput.StreamData);
                rig.Manager.RefreshFixtures();
                rig.Camera.transform.SetPositionAndRotation(new Vector3(100f, 1.5f, -5f), Quaternion.identity);
                yield return rig.Step(6);

                var body = par.fixtureShellRenderers[0];
                var before = CaptureHdr(rig);
                try
                {
                    var (mean, peak) = Measure(rig.Camera, before, body);
                    Debug.Log($"[J4] at sector 3: peak {peak}, mean {mean}");
                    Assert.LessOrEqual(peak, DarkPeak, $"the body must be dark on dark sector 3 (peak {peak}, mean {mean})");
                }
                finally { Object.DestroyImmediate(before); }

                par.sector = 10;
                par.ApplyAddress();
                yield return rig.Step(3);

                foreach (var r in par.fixtureShellRenderers)
                {
                    var block = new MaterialPropertyBlock();
                    r.GetPropertyBlock(block);
                    Assert.AreEqual(abs, block.GetInt("_DMXChannel"),
                        $"{r.name} must read the channel the beam moved to, or the par looks half-patched");
                }
                var after = CaptureHdr(rig);
                try
                {
                    var (mean, peak) = Measure(rig.Camera, after, body);
                    Debug.Log($"[J4] at sector 10: peak {peak}, mean {mean}");
                    AssertLitRed(mean, peak, "the body re-addressed to sector 10");
                }
                finally { Object.DestroyImmediate(after); }

                var at = par.transform.position;
                var record = rig.ReadRaw().OrderBy(d => Vector3.Distance(at,
                    new Vector3(d.positionAndRange.x, d.positionAndRange.y, d.positionAndRange.z))).First();
                AssertNear(new Vector3(1f, 0f, 0f),
                    new Vector3(record.colorAndIntensity.x, record.colorAndIntensity.y, record.colorAndIntensity.z),
                    "the beam must decode sector 10's red");
            }
            finally
            {
                rig.Dispose();
                source.Dispose();
            }
        }

        /// <summary>
        /// The rig camera's view rendered into a half-float target. The rig's own target is
        /// 8-bit, and a lit body's emission is far above 1 in every channel, so there it clips
        /// to white whatever colour it is.
        /// </summary>
        static Texture2D CaptureHdr(VRSLDMXRig rig)
        {
            var camera   = rig.Camera;
            var target   = camera.targetTexture;
            bool hdrWas  = camera.allowHDR;
            var hdr      = new RenderTexture(target.width, target.height, 24, RenderTextureFormat.ARGBHalf);
            var previous = RenderTexture.active;
            try
            {
                camera.allowHDR      = true;
                camera.targetTexture = hdr;
                rig.Render(camera);
                RenderTexture.active = hdr;
                var frame = new Texture2D(hdr.width, hdr.height, TextureFormat.RGBAFloat, false, true);
                frame.ReadPixels(new Rect(0, 0, hdr.width, hdr.height), 0, 0);
                frame.Apply();
                return frame;
            }
            finally
            {
                RenderTexture.active = previous;
                camera.targetTexture = target;
                camera.allowHDR      = hdrWas;
                hdr.Release();
                Object.DestroyImmediate(hdr);
            }
        }

        /// <summary>Lit, in the red the row sent: bright, with red leading.</summary>
        static void AssertLitRed(Vector3 mean, float peak, string what)
        {
            Assert.Greater(peak, 1f, $"{what} must light with its dimmer up (mean {mean})");
            Assert.Greater(mean.x, 1.5f * Mathf.Max(mean.y, mean.z),
                $"{what} must light in the fixture's colour, red (mean {mean})");
        }

        /// <summary>
        /// The mean linear colour and the brightest channel over the pixels
        /// <paramref name="r"/> covers. A flare is a quad the shader turns to the camera, so
        /// its bounds say nothing about where it lands; it is measured over a square at its
        /// centre.
        /// </summary>
        static (Vector3 mean, float peak) Measure(Camera camera, Texture2D frame, Renderer r)
        {
            var b = r.name.Contains("Flare") ? new Bounds(r.bounds.center, new Vector3(0.5f, 0.5f, 0.5f)) : r.bounds;
            Vector2 lo = new Vector2(float.MaxValue, float.MaxValue), hi = new Vector2(float.MinValue, float.MinValue);
            for (int i = 0; i < 8; i++)
            {
                var corner = b.center + Vector3.Scale(b.extents,
                    new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                var s = camera.WorldToScreenPoint(corner);
                lo = Vector2.Min(lo, s);
                hi = Vector2.Max(hi, s);
            }
            int x0 = Mathf.Clamp((int)lo.x, 0, frame.width - 1),  x1 = Mathf.Clamp((int)hi.x, 0, frame.width - 1);
            int y0 = Mathf.Clamp((int)lo.y, 0, frame.height - 1), y1 = Mathf.Clamp((int)hi.y, 0, frame.height - 1);
            Assert.Greater((x1 - x0) * (y1 - y0), 0, $"{r.name} is not in the frame");
            var pixels = frame.GetPixels();
            Vector3 sum = Vector3.zero;
            float peak = 0f;
            int n = 0;
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    var p = pixels[y * frame.width + x];
                    sum += new Vector3(p.r, p.g, p.b);
                    peak = Mathf.Max(peak, Mathf.Max(p.r, Mathf.Max(p.g, p.b)));
                    n++;
                }
            return (sum / n, peak);
        }

        /// <summary>One universe of values the row chooses, as a desk would send them.</summary>
        sealed class FixedChannels : IVRSLDMXChannelSource, System.IDisposable
        {
            NativeArray<VRSLDMXBlock> _blocks = new(1, Allocator.Persistent);
            NativeArray<byte>         _values = new(VRSLDMX.UsableSlotsPerUniverse, Allocator.Persistent);

            public FixedChannels()
                => _blocks[0] = new VRSLDMXBlock { universe = 0, start = 0, length = VRSLDMX.UsableSlotsPerUniverse };

            public int UniverseCount => 1;

            /// <summary><paramref name="values"/>[0] is channel 1.</summary>
            public void Set(float[] values)
            {
                for (int i = 0; i < _values.Length; i++)
                    _values[i] = (byte)Mathf.RoundToInt(255f * (i < values.Length ? values[i] : 0f));
            }

            public bool TryGetBlocks(out NativeArray<VRSLDMXBlock> blocks, out int blockCount,
                                     out NativeArray<byte> values)
            {
                blocks     = _blocks;
                values     = _values;
                blockCount = 1;
                return true;
            }

            public void Dispose()
            {
                if (_blocks.IsCreated) _blocks.Dispose();
                if (_values.IsCreated) _values.Dispose();
            }
        }
    }
}
#endif
