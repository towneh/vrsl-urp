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
    /// DMX; the 5-channel ones cast light from a 5-channel patch. The AudioLink laser
    /// follows its colour and amplitude inputs.
    ///
    /// Rows J1 to J4 and J6 to J15 of TESTING.md.
    /// </summary>
    class VRSLParBlinderBodyTests : VRSLDMXTest
    {
        const string Prefabs = "Packages/town.mr.vrsl-urp/Runtime/Prefabs/DMX/";

        /// <summary>J4 takes the first entry as its 13-channel par, and J2 lines all four up
        /// either side of its camera, so a change to the order or the count moves both.</summary>
        static readonly string[] ParsAndBlinders =
        {
            "VRSL-DMX-Static-ParLight-13CH.prefab",
            "VRSL-DMX-Static-Blinder-13CH.prefab",
            "VRSL-DMX-Static-ParLight-5CH.prefab",
            "VRSL-DMX-Static-Blinder-5CH.prefab",
        };

        static bool IsFiveChannel(string path) => path.Contains("-5CH");

        static IEnumerable<string> FiveChannel => ParsAndBlinders.Where(p => IsFiveChannel(p));

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
                var light  = prefab.GetComponent<VRSLDMXRealtimeLight>();
                Assert.IsNotNull(light, $"{path} has no realtime light on its root");
                CollectionAssert.AreEquivalent(prefab.GetComponentsInChildren<MeshRenderer>(true),
                    light.fixtureShellRenderers ?? new MeshRenderer[0],
                    $"{path}: a renderer left off the list glows at full whatever the desk sends");
                Assert.AreEqual(BodyTint, light.shellEmissionTint, $"{path}: the body tint");
                Assert.AreEqual(IsFiveChannel(path), light.use5ChannelMode,
                    $"{path}: the cast light must read the patch the fixture is named for");

                var go = Object.Instantiate(prefab);
                try
                {
                    var woken = go.GetComponent<VRSLDMXRealtimeLight>();
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

                var placed = new List<(string path, VRSLDMXRealtimeLight light)>();
                int i = 0;
                foreach (var path in FiveChannel)
                {
                    var go = Object.Instantiate(Load(path), rig.Floor.transform.parent);
                    // Well away from the rig's own fixtures, so each record is told apart
                    // by where its light sits.
                    go.transform.localPosition = new Vector3(100f + 10f * i, 1.5f, 0f);
                    var light = go.GetComponent<VRSLDMXRealtimeLight>();
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
        /// J2. The 13- and 5-channel par and blinder, turned to the
        /// camera: each body and flare is dark with the fixture's channels at 0 and lit red
        /// with its dimmer and red at full, from a video grid and from a channel source.
        /// </summary>
        [UnityTest]
        public IEnumerator J2_bodies_and_flares_are_dark_at_black_and_lit_in_the_fixtures_colour()
        {
            string[] paths = ParsAndBlinders;
            var rig    = VRSLDMXRig.Build(withSource: false, targetSize: 512);
            var source = new FixedChannels();
            var made   = new List<Object>();
            var grids  = new List<VRSLDMXRig.PaintedGrid>();
            try
            {
                yield return rig.Step(2);
                var placed = new List<(string path, VRSLDMXRealtimeLight light)>();
                for (int i = 0; i < paths.Length; i++)
                {
                    var go = Object.Instantiate(Load(paths[i]), rig.Floor.transform.parent);
                    // Upright, a par's lens faces the floor and the camera sees its rim.
                    go.transform.SetPositionAndRotation(new Vector3(100f + 1.6f * i, 1.5f, 0f),
                                                        Quaternion.Euler(90f, 0f, 0f));
                    var light = go.GetComponent<VRSLDMXRealtimeLight>();
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
                var par = go.GetComponent<VRSLDMXRealtimeLight>();
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
        /// J6. The fixture body shaders are lit by the main light whatever the shadow setup:
        /// the AudioLink static body (par, blinder, flasher, light bar), the DMX surface
        /// bodies (par, blinder, and the multi-channel light bar) and the four mover bodies
        /// (DMX and AudioLink, spot and wash).
        ///
        /// A cube on each body shader, emission off, beside a cube on URP Lit, under a white
        /// directional light that casts shadows, captured with the light off and then with
        /// the pipeline asset at one shadow cascade, at four, and with the light's shadows
        /// off. Each lit capture counts only its gain over the light-off one, and the row
        /// reads back the shadow keyword URP set, so a capture that never reached the
        /// variant it is for is inconclusive rather than a pass. URP sets
        /// <c>_MAIN_LIGHT_SHADOWS</c> only at exactly one cascade and
        /// <c>_MAIN_LIGHT_SHADOWS_CASCADE</c> above it, so a body whose lighting hangs on the
        /// first renders black at four, which is what Basis ships. With no shadow-casting
        /// light URP sets no shadow keyword, or the single-map one over an empty map where
        /// the project strips shadows-off variants; the third capture covers projects of the
        /// first kind. The URP Lit cube is the control for the light and camera, and on a
        /// Forward+ renderer a body dark beside it at one cascade means its shader isn't
        /// built with the cluster light loop, so its main light reads a per-object
        /// attenuation Forward+ leaves at zero. The cubes cast their own shadows so the
        /// shadow pass has something to draw at every count. The mover cubes sit in a row
        /// above the others and carry black vertex colours, which the movers draw in place.
        /// </summary>
        [UnityTest]
        public IEnumerator J6_fixture_bodies_are_lit_at_every_shadow_cascade_count()
        {
            var asset = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline
                            as UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset;
            if (asset == null) Assert.Ignore("No Universal Render Pipeline asset is active.");
            if (!asset.supportsMainLightShadows)
                Assert.Inconclusive("the pipeline asset doesn't support main-light shadows, so no shadow keyword "
                                  + "is ever set and the row can't separate the cases");

            int cascadesWas = asset.shadowCascadeCount;
            var rig = VRSLDMXRig.Build(withSource: false, targetSize: 512);
            var owned = new List<Object>();
            try
            {
                var (bodies, refRenderer) = BodyCubes(rig, "J6", owned);

                var light = new GameObject("VRSL J6 sun").AddComponent<Light>();
                light.transform.SetParent(rig.Floor.transform.parent, false);
                light.type      = LightType.Directional;
                light.color     = Color.white;
                light.intensity = 1f;
                // From above, behind and to the left of the camera, so the faces it sees are lit.
                light.transform.rotation = Quaternion.Euler(30f, 20f, 0f);
                RenderSettings.sun = light;

                var eye = new Vector3(200f, 1f, -6f);
                rig.Camera.transform.SetPositionAndRotation(eye, Quaternion.identity);
                // Beyond the shadow distance URP renders an empty shadow map with the
                // single-map keyword at any cascade count, so the cascade capture would
                // test nothing.
                float reach = Vector3.Distance(eye, refRenderer.bounds.center) + 1f;
                if (asset.shadowDistance < reach)
                    Assert.Inconclusive($"the pipeline asset's shadow distance is {asset.shadowDistance} m, inside the "
                                      + $"{reach:F1} m to the cubes, so no capture renders their shadows");
                yield return rig.Step(2);

                (Vector3[] body, Vector3 control, string keyword) Capture(bool sun, int cascades, LightShadows shadows, string what)
                {
                    asset.shadowCascadeCount = cascades;
                    light.enabled = sun;
                    light.shadows = shadows;
                    var frame = CaptureHdr(rig);
                    try
                    {
                        // URP sets its main-light shadow keyword globally for the frame, so
                        // it can be read back once the camera has rendered.
                        string keyword = Shader.IsKeywordEnabled("_MAIN_LIGHT_SHADOWS_CASCADE") ? "_MAIN_LIGHT_SHADOWS_CASCADE"
                                       : Shader.IsKeywordEnabled("_MAIN_LIGHT_SHADOWS_SCREEN")  ? "_MAIN_LIGHT_SHADOWS_SCREEN"
                                       : Shader.IsKeywordEnabled("_MAIN_LIGHT_SHADOWS")         ? "_MAIN_LIGHT_SHADOWS"
                                       : "none";
                        var means = new Vector3[bodies.Count];
                        for (int i = 0; i < bodies.Count; i++)
                        {
                            var (mean, peak) = Measure(rig.Camera, frame, bodies[i].renderer);
                            means[i] = mean;
                            Debug.Log($"[J6] {what}: {bodies[i].shader} peak {peak}, mean {mean}");
                        }
                        var (refMean, refPeak) = Measure(rig.Camera, frame, refRenderer);
                        Debug.Log($"[J6] {what}: shadow keyword {keyword}; URP Lit peak {refPeak}, mean {refMean}");
                        return (means, refMean, keyword);
                    }
                    finally { Object.DestroyImmediate(frame); }
                }

                // The sun's own contribution is each lit capture over this one: whatever
                // reflection probes or the host leave in the frame is in both.
                var dark        = Capture(false, 4, LightShadows.Hard, "sun off");
                var oneCascade  = Capture(true,  1, LightShadows.Hard, "one cascade");
                var fourCascade = Capture(true,  4, LightShadows.Hard, "four cascades");
                var noShadows   = Capture(true,  4, LightShadows.None, "sun casting no shadows");

                float Gain(Vector3 lit, Vector3 unlit) => (lit - unlit).magnitude;

                foreach (var (c, what) in new[] { (oneCascade, "one cascade"), (fourCascade, "four cascades"),
                                                  (noShadows, "the sun casting no shadows") })
                    if (Gain(c.control, dark.control) < 0.1f)
                        Assert.Inconclusive($"URP Lit beside the bodies gains only {Gain(c.control, dark.control):F3} from "
                                          + $"the sun with {what} ({c.control} against {dark.control} with it off), so "
                                          + "the light or the camera is wrong and the row has nothing to judge");
                if (oneCascade.keyword != "_MAIN_LIGHT_SHADOWS" || fourCascade.keyword != "_MAIN_LIGHT_SHADOWS_CASCADE")
                    Assert.Inconclusive($"the shadow captures ran {oneCascade.keyword} at one cascade and "
                                      + $"{fourCascade.keyword} at four, not _MAIN_LIGHT_SHADOWS and "
                                      + "_MAIN_LIGHT_SHADOWS_CASCADE, so the row didn't exercise the variants it is for");

                var failures = new List<string>();
                for (int i = 0; i < bodies.Count; i++)
                {
                    string name = bodies[i].shader;
                    float oneGain = Gain(oneCascade.body[i], dark.body[i]);
                    if (!(oneGain > 0.1f)) failures.Add(
                        $"{name} gains only {oneGain:F3} from the sun at one cascade ({oneCascade.body[i]} "
                      + $"against {dark.body[i]} with it off) while URP Lit beside it is lit. On a Forward+ "
                      + "renderer a shader built without _CLUSTER_LIGHT_LOOP takes its main light's "
                      + "attenuation from unity_LightData.z, which Forward+ leaves at zero. A shader lit "
                      + "through the Built-in pipeline's light variables gains nothing on any renderer, "
                      + "since URP never sets them");
                    if (Gain(fourCascade.body[i], dark.body[i]) < 0.5f * oneGain) failures.Add(
                        $"{name} reads {fourCascade.body[i]} at four cascades against {oneCascade.body[i]} at "
                      + $"one and {dark.body[i]} with the sun off. At more than one cascade URP sets "
                      + "_MAIN_LIGHT_SHADOWS_CASCADE instead of _MAIN_LIGHT_SHADOWS, so lighting gated on the "
                      + "single-map keyword drops out");
                    if (Gain(noShadows.body[i], dark.body[i]) < 0.5f * oneGain) failures.Add(
                        $"{name} reads {noShadows.body[i]} with the sun casting no shadows against "
                      + $"{oneCascade.body[i]} with shadows and {dark.body[i]} with it off. With no "
                      + "shadow-casting light URP may set no main-light shadow keyword at all, and the "
                      + "body must still be lit");
                }
                if (failures.Count > 0) Assert.Fail(string.Join("\n", failures));
            }
            finally
            {
                asset.shadowCascadeCount = cascadesWas;
                rig.Dispose();
                foreach (var o in owned) if (o != null) Object.DestroyImmediate(o);
            }
        }

        /// <summary>
        /// J7. Scene fog leaves both lens flares as they are. Each flare is a quad on a copy
        /// of its shipped blinder material, lit red with no AudioLink or DMX input, 5 m from
        /// the camera, captured with the flares shown and hidden under no fog and then under
        /// green linear, exponential and exponential squared fog that would take about half
        /// of anything at that distance. A flare adds light, so the difference between the
        /// two captures is all it contributes, and it must not change with fog. A cube on
        /// URP Unlit beside them is the control that the fog was on.
        /// </summary>
        [UnityTest]
        public IEnumerator J7_lens_flares_are_unchanged_by_scene_fog()
        {
            const string Materials = "Packages/town.mr.vrsl-urp/Runtime/Materials/Lights/";
            var flares = new (string name, string material, string enable)[]
            {
                ("AudioLink Flare", "AudioLink/StaticLight-Blinder/VRSL-AudioLink-Blinder-LensFlareMat.mat", "_EnableAudioLink"),
                ("DMX Flare",       "DMX/StaticLight-Blinder/VRSL-Blinder-LensFlareMat-Horizontal.mat",       "_EnableDMX"),
            };
            var unlit = Shader.Find("Universal Render Pipeline/Unlit");
            Assert.IsNotNull(unlit, "URP Unlit is not in the project");

            var fogMode    = RenderSettings.fogMode;
            var fogColor   = RenderSettings.fogColor;
            var fogDensity = RenderSettings.fogDensity;
            var fogStart   = RenderSettings.fogStartDistance;
            var fogEnd     = RenderSettings.fogEndDistance;
            var rig        = VRSLDMXRig.Build(withSource: false, targetSize: 512);
            var materials  = new List<Material>();
            try
            {
                var renderers = new List<MeshRenderer>();
                for (int i = 0; i < flares.Length; i++)
                {
                    var shipped = UnityEditor.AssetDatabase.LoadAssetAtPath<Material>(Materials + flares[i].material);
                    Assert.IsNotNull(shipped, $"{flares[i].material} is not in the package");
                    var material = new Material(shipped) { name = $"VRSL J7 {flares[i].name}" };
                    materials.Add(material);
                    material.SetFloat(flares[i].enable, 0f);
                    material.SetColor("_Emission", new Color(24f, 0f, 0f, 1f));
                    var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
                    quad.name = $"VRSL J7 {flares[i].name}";
                    quad.transform.SetParent(rig.Floor.transform.parent, false);
                    quad.transform.position = new Vector3(300f + 1.5f * i, 1.5f, 0f);
                    var r = quad.GetComponent<MeshRenderer>();
                    r.sharedMaterial = material;
                    renderers.Add(r);
                }
                var control = new Material(unlit) { name = "VRSL J7 control" };
                materials.Add(control);
                control.SetColor("_BaseColor", Color.red);
                var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                cube.name = "VRSL J7 control";
                cube.transform.SetParent(rig.Floor.transform.parent, false);
                cube.transform.position = new Vector3(303.5f, 1.5f, 0f);
                var controlRenderer = cube.GetComponent<MeshRenderer>();
                controlRenderer.sharedMaterial = control;
                rig.Camera.transform.SetPositionAndRotation(new Vector3(301.5f, 1.5f, -5f), Quaternion.identity);
                yield return rig.Step(2);

                (Vector3[] flare, Vector3 control) Capture(bool fog, FogMode mode)
                {
                    RenderSettings.fog              = fog;
                    RenderSettings.fogMode          = mode;
                    RenderSettings.fogColor         = Color.green;
                    RenderSettings.fogStartDistance = 0f;
                    RenderSettings.fogEndDistance   = 10f;
                    RenderSettings.fogDensity       = mode == FogMode.Exponential ? 0.14f : 0.17f;
                    foreach (var r in renderers) r.enabled = true;
                    var shown = CaptureHdr(rig);
                    foreach (var r in renderers) r.enabled = false;
                    var hidden = CaptureHdr(rig);
                    foreach (var r in renderers) r.enabled = true;
                    try
                    {
                        var added = new Vector3[renderers.Count];
                        for (int i = 0; i < renderers.Count; i++)
                            added[i] = Measure(rig.Camera, shown, renderers[i]).mean
                                     - Measure(rig.Camera, hidden, renderers[i]).mean;
                        return (added, Measure(rig.Camera, shown, controlRenderer).mean);
                    }
                    finally
                    {
                        Object.DestroyImmediate(shown);
                        Object.DestroyImmediate(hidden);
                    }
                }

                var clear = Capture(false, FogMode.Linear);
                for (int i = 0; i < flares.Length; i++)
                {
                    Debug.Log($"[J7] {flares[i].name}, no fog: adds {clear.flare[i]:F4}");
                    Assert.Greater(clear.flare[i].x, 0.02f, $"{flares[i].name} must be lit red with no fog (adds {clear.flare[i]:F4})");
                }
                Debug.Log($"[J7] control, no fog: {clear.control}");

                var failures = new List<string>();
                foreach (var mode in new[] { FogMode.Linear, FogMode.Exponential, FogMode.ExponentialSquared })
                {
                    var fogged = Capture(true, mode);
                    Debug.Log($"[J7] control, {mode} fog: {fogged.control}");
                    if (fogged.control.y < 0.25f || fogged.control.x > 0.75f * clear.control.x)
                        Assert.Inconclusive($"URP Unlit beside the flares reads {fogged.control} under {mode} fog against "
                                          + $"{clear.control} without, so the fog never reached the frame and the row "
                                          + "has nothing to judge");
                    for (int i = 0; i < flares.Length; i++)
                    {
                        Debug.Log($"[J7] {flares[i].name}, {mode} fog: adds {fogged.flare[i]:F4}");
                        var drift = fogged.flare[i] - clear.flare[i];
                        if (drift.magnitude > 0.02f * clear.flare[i].magnitude + 0.002f) failures.Add(
                            $"{flares[i].name} adds {fogged.flare[i]:F4} under green {mode} fog against {clear.flare[i]:F4} "
                          + "without. A flare is unfogged, so fog must neither dim it nor tint it; a fog-coloured "
                          + "disc means the pass applies fog from a fog coordinate its vertex never wrote");
                    }
                }
                if (failures.Count > 0) Assert.Fail(string.Join("\n", failures));
            }
            finally
            {
                RenderSettings.fogMode          = fogMode;
                RenderSettings.fogColor         = fogColor;
                RenderSettings.fogDensity       = fogDensity;
                RenderSettings.fogStartDistance = fogStart;
                RenderSettings.fogEndDistance   = fogEnd;
                rig.Dispose();
                foreach (var m in materials) if (m != null) Object.DestroyImmediate(m);
            }
        }

        /// <summary>
        /// J8. A lens flare hides behind whatever blocks its light, on its shipped material and
        /// on one an author makes from the shader. Each flare (AudioLink and DMX, the two
        /// materials each) is a quad lit red with no AudioLink or DMX input, 5 m from the
        /// camera, with a cube on URP Lit set 1 m in front of it on the line to the camera and
        /// then moved aside; the
        /// flares are captured shown and hidden each time. A flare draws over the scene
        /// whatever its depth, so a blocked one adds nothing only because its own depth test
        /// hid it.
        /// </summary>
        [UnityTest]
        public IEnumerator J8_lens_flares_hide_behind_what_blocks_their_light()
        {
            const string Materials = "Packages/town.mr.vrsl-urp/Runtime/Materials/Lights/";
            var flares = new (string name, string material, string enable)[]
            {
                ("AudioLink Flare", "AudioLink/StaticLight-Blinder/VRSL-AudioLink-Blinder-LensFlareMat.mat", "_EnableAudioLink"),
                ("DMX Flare",       "DMX/StaticLight-Blinder/VRSL-Blinder-LensFlareMat-Horizontal.mat",       "_EnableDMX"),
            };
            var lit = Shader.Find("Universal Render Pipeline/Lit");
            Assert.IsNotNull(lit, "URP Lit is not in the project");

            var rig       = VRSLDMXRig.Build(withSource: false, targetSize: 512);
            var materials = new List<Material>();
            try
            {
                var renderers = new List<MeshRenderer>();
                var blockers  = new List<Transform>();
                var wall = new Material(lit) { name = "VRSL J8 blocker" };
                materials.Add(wall);
                wall.SetColor("_BaseColor", Color.grey);
                foreach (var (name, path, enable) in flares)
                {
                    var shipped = UnityEditor.AssetDatabase.LoadAssetAtPath<Material>(Materials + path);
                    Assert.IsNotNull(shipped, $"{path} is not in the package");
                    // The author's own: only what lights it red is set, everything else is the
                    // shader's default.
                    var fresh = new Material(shipped.shader) { name = $"VRSL J8 {name} new material" };
                    fresh.SetTexture("_MainTex", shipped.GetTexture("_MainTex"));
                    var copy = new Material(shipped) { name = $"VRSL J8 {name} shipped material" };
                    foreach (var m in new[] { copy, fresh })
                    {
                        materials.Add(m);
                        m.SetFloat(enable, 0f);
                        m.SetColor("_Emission", new Color(24f, 0f, 0f, 1f));
                        int i = renderers.Count;
                        var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
                        quad.name = m.name;
                        quad.transform.SetParent(rig.Floor.transform.parent, false);
                        quad.transform.position = new Vector3(400f + 1.5f * i, 1.5f, 0f);
                        var r = quad.GetComponent<MeshRenderer>();
                        r.sharedMaterial = m;
                        renderers.Add(r);
                        var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                        cube.name = $"VRSL J8 blocker {i}";
                        cube.transform.SetParent(rig.Floor.transform.parent, false);
                        cube.GetComponent<MeshRenderer>().sharedMaterial = wall;
                        blockers.Add(cube.transform);
                    }
                }
                rig.Camera.transform.SetPositionAndRotation(new Vector3(402.25f, 1.5f, -5f), Quaternion.identity);

                Vector3[] Capture(bool blocked)
                {
                    var eye = rig.Camera.transform.position;
                    for (int i = 0; i < blockers.Count; i++)
                    {
                        var at = renderers[i].transform.position;
                        blockers[i].position = blocked ? Vector3.Lerp(eye, at, 0.8f) : at + new Vector3(0f, -10f, 0f);
                    }
                    foreach (var r in renderers) r.enabled = true;
                    var shown = CaptureHdr(rig);
                    foreach (var r in renderers) r.enabled = false;
                    var hidden = CaptureHdr(rig);
                    foreach (var r in renderers) r.enabled = true;
                    try
                    {
                        var added = new Vector3[renderers.Count];
                        for (int i = 0; i < renderers.Count; i++)
                            added[i] = Measure(rig.Camera, shown, renderers[i]).mean
                                     - Measure(rig.Camera, hidden, renderers[i]).mean;
                        return added;
                    }
                    finally
                    {
                        Object.DestroyImmediate(shown);
                        Object.DestroyImmediate(hidden);
                    }
                }

                yield return rig.Step(2);
                var open = Capture(false);
                yield return rig.Step(2);
                var blocked = Capture(true);

                var failures = new List<string>();
                for (int i = 0; i < renderers.Count; i++)
                {
                    string name = renderers[i].name;
                    Debug.Log($"[J8] {name}: adds {open[i]:F4} in the open, {blocked[i]:F4} blocked");
                    if (!(open[i].x > 0.02f))
                        failures.Add($"{name} adds only {open[i]:F4} with nothing in front of it, so it isn't lit and "
                                   + "its blocked capture proves nothing");
                    else if (blocked[i].magnitude > 0.002f)
                        failures.Add($"{name} adds {blocked[i]:F4} behind a cube against {open[i]:F4} in the open: it "
                                   + "draws through what blocks its light");
                }
                if (failures.Count > 0) Assert.Fail(string.Join("\n", failures));
            }
            finally
            {
                rig.Dispose();
                foreach (var m in materials) if (m != null) Object.DestroyImmediate(m);
            }
        }

        /// <summary>
        /// J9. The fixture body shaders take URP's point and spot lights on every path URP
        /// lights them by: Forward+, Forward per pixel, and Forward per vertex, which is how
        /// Basis's Quest renderer is set. The same cubes as J6 under a white point light in
        /// front of them and no sun, captured with the light off and on for each path; the
        /// row sets every renderer's rendering mode and the asset's additional-light mode for
        /// each capture and puts them back after. Each lit capture counts only its gain over
        /// the light-off one on the same path, and the row reads back the keyword URP set, so
        /// a capture that never reached its path is inconclusive. URP Lit beside them is the
        /// control. Forward+ defines <c>_ADDITIONAL_LIGHTS</c> itself under the cluster light
        /// loop, so a body built without the additional-light keywords is lit there and dark
        /// on plain Forward.
        /// </summary>
        [UnityTest]
        public IEnumerator J9_fixture_bodies_take_urp_point_lights_on_every_lighting_path()
        {
            var asset = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline
                            as UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset;
            if (asset == null) Assert.Ignore("No Universal Render Pipeline asset is active.");
            // URP gives the asset's additional-light mode no public setter, so the row writes
            // the serialised field the asset reads. That marks the asset dirty, and the host
            // rewrites the active asset's quality settings in memory during a run, so a dirty
            // flag left behind saves the host's values over the project's at exit.
            bool dirtyWas = UnityEditor.EditorUtility.IsDirty(asset);
            var serializedAsset = new UnityEditor.SerializedObject(asset);
            var lightMode = serializedAsset.FindProperty("m_AdditionalLightsRenderingMode");
            if (lightMode == null)
                Assert.Inconclusive("the pipeline asset has no m_AdditionalLightsRenderingMode field, so the row "
                                  + "can't choose per-pixel or per-vertex additional lights");
            int lightModeWas = lightMode.intValue;
            void SetLightMode(int value)
            {
                serializedAsset.Update();
                lightMode.intValue = value;
                serializedAsset.ApplyModifiedPropertiesWithoutUndo();
            }
            var renderers = UniversalRenderers(asset);
            if (renderers.Count == 0) Assert.Ignore("The pipeline asset has no Universal Renderer.");
            var renderingModesWas = renderers.Select(r => r.renderingMode).ToList();

            var rig = VRSLDMXRig.Build(withSource: false, targetSize: 512);
            var owned = new List<Object>();
            try
            {
                var (bodies, refRenderer) = BodyCubes(rig, "J9", owned);

                var light = new GameObject("VRSL J9 point light").AddComponent<Light>();
                light.transform.SetParent(rig.Floor.transform.parent, false);
                light.transform.position = new Vector3(200f, 1.7f, -2.5f);
                light.type      = LightType.Point;
                light.color     = Color.white;
                light.range     = 10f;
                light.intensity = 8f;
                light.shadows   = LightShadows.None;

                rig.Camera.transform.SetPositionAndRotation(new Vector3(200f, 1f, -6f), Quaternion.identity);
                yield return rig.Step(2);

                (Vector3[] body, Vector3 control, string keyword) Capture(
                    UnityEngine.Rendering.Universal.RenderingMode path,
                    UnityEngine.Rendering.Universal.LightRenderingMode perLight, bool on, string what)
                {
                    foreach (var r in renderers)
                        if (r.renderingMode != path) r.renderingMode = path;
                    SetLightMode((int)perLight);
                    if (asset.additionalLightsRenderingMode != perLight)
                        Assert.Inconclusive($"the pipeline asset reads {asset.additionalLightsRenderingMode} additional "
                                          + $"lights after the row set {perLight}");
                    light.enabled = on;
                    var frame = CaptureHdr(rig);
                    try
                    {
                        // URP sets its additional-light keywords globally for the frame, so
                        // they can be read back once the camera has rendered.
                        string keyword = Shader.IsKeywordEnabled("_CLUSTER_LIGHT_LOOP")        ? "_CLUSTER_LIGHT_LOOP"
                                       : Shader.IsKeywordEnabled("_ADDITIONAL_LIGHTS_VERTEX") ? "_ADDITIONAL_LIGHTS_VERTEX"
                                       : Shader.IsKeywordEnabled("_ADDITIONAL_LIGHTS")        ? "_ADDITIONAL_LIGHTS"
                                       : "none";
                        var means = new Vector3[bodies.Count];
                        for (int i = 0; i < bodies.Count; i++)
                        {
                            var (mean, peak) = Measure(rig.Camera, frame, bodies[i].renderer);
                            means[i] = mean;
                            Debug.Log($"[J9] {what}: {bodies[i].shader} peak {peak}, mean {mean}");
                        }
                        var (refMean, refPeak) = Measure(rig.Camera, frame, refRenderer);
                        Debug.Log($"[J9] {what}: keyword {keyword}; URP Lit peak {refPeak}, mean {refMean}");
                        return (means, refMean, keyword);
                    }
                    finally { Object.DestroyImmediate(frame); }
                }

                var paths = new (UnityEngine.Rendering.Universal.RenderingMode path,
                                 UnityEngine.Rendering.Universal.LightRenderingMode perLight,
                                 string keyword, string what)[]
                {
                    (UnityEngine.Rendering.Universal.RenderingMode.ForwardPlus,
                     UnityEngine.Rendering.Universal.LightRenderingMode.PerPixel, "_CLUSTER_LIGHT_LOOP", "Forward+"),
                    (UnityEngine.Rendering.Universal.RenderingMode.Forward,
                     UnityEngine.Rendering.Universal.LightRenderingMode.PerPixel, "_ADDITIONAL_LIGHTS", "Forward per pixel"),
                    (UnityEngine.Rendering.Universal.RenderingMode.Forward,
                     UnityEngine.Rendering.Universal.LightRenderingMode.PerVertex, "_ADDITIONAL_LIGHTS_VERTEX", "Forward per vertex"),
                };

                float Gain(Vector3 lit, Vector3 unlit) => (lit - unlit).magnitude;

                var failures = new List<string>();
                foreach (var (path, perLight, keyword, what) in paths)
                {
                    var dark = Capture(path, perLight, false, $"{what}, light off");
                    var lit  = Capture(path, perLight, true,  $"{what}, light on");
                    if (lit.keyword != keyword)
                        Assert.Inconclusive($"the {what} capture ran {lit.keyword}, not {keyword}, so the row didn't "
                                          + "exercise the path it is for");
                    if (Gain(lit.control, dark.control) < 0.1f)
                        Assert.Inconclusive($"URP Lit beside the bodies gains only {Gain(lit.control, dark.control):F3} "
                                          + $"from the point light on {what} ({lit.control} against {dark.control} with "
                                          + "it off), so the light or the camera is wrong and the row has nothing to judge");
                    for (int i = 0; i < bodies.Count; i++)
                    {
                        float gain = Gain(lit.body[i], dark.body[i]);
                        if (!(gain > 0.1f)) failures.Add(
                            $"{bodies[i].shader} gains only {gain:F3} from the point light on {what} ({lit.body[i]} "
                          + $"against {dark.body[i]} with it off) while URP Lit beside it is lit. A shader without "
                          + "the _ADDITIONAL_LIGHTS_VERTEX and _ADDITIONAL_LIGHTS variants, or one that never hands "
                          + "URP its vertex lighting, takes no point or spot light on plain Forward");
                    }
                }
                if (failures.Count > 0) Assert.Fail(string.Join("\n", failures));
            }
            finally
            {
                for (int i = 0; i < renderers.Count; i++)
                    if (renderers[i] != null && renderers[i].renderingMode != renderingModesWas[i])
                        renderers[i].renderingMode = renderingModesWas[i];
                SetLightMode(lightModeWas);
                if (!dirtyWas) UnityEditor.EditorUtility.ClearDirty(asset);
                rig.Dispose();
                foreach (var o in owned) if (o != null) Object.DestroyImmediate(o);
            }
        }

        /// <summary>
        /// J10. Every fixture body with a metallic-smoothness map reads smoothness from the
        /// map's alpha, as the map's label says, not from its red. A sphere on each such body
        /// shader (the two DMX surface bodies and the four movers, which carry black vertex
        /// colours) and one on URP Lit, under a white sun, smoothness 0.8 and Metallic 0, are
        /// captured with three maps: red 0 alpha 1, red 1 alpha 1, red 0 alpha 0. With Metallic
        /// at 0 the red channel can't change anything, so the first two must look the same,
        /// and the third, a rough surface, must look different. URP Lit, given the alpha as
        /// its smoothness, is the control that the light and camera show smoothness at all.
        /// </summary>
        [UnityTest]
        public IEnumerator J10_fixture_bodies_take_smoothness_from_their_maps_alpha()
        {
            var lit = Shader.Find("Universal Render Pipeline/Lit");
            Assert.IsNotNull(lit, "URP Lit is not in the project");
            var rig = VRSLDMXRig.Build(withSource: false, targetSize: 512);
            var owned = new List<Object>();
            try
            {
                Texture2D Map(float red, float alpha)
                {
                    var t = new Texture2D(2, 2, TextureFormat.RGBA32, false, true) { name = $"VRSL J10 map r{red} a{alpha}" };
                    t.SetPixels(Enumerable.Repeat(new Color(red, 0f, 0f, alpha), 4).ToArray());
                    t.Apply();
                    owned.Add(t);
                    return t;
                }
                var maps = new[] { (map: Map(0f, 1f), alpha: 1f, what: "red 0 alpha 1"),
                                   (map: Map(1f, 1f), alpha: 1f, what: "red 1 alpha 1"),
                                   (map: Map(0f, 0f), alpha: 0f, what: "red 0 alpha 0") };

                var sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                var sphereMesh = sphere.GetComponent<MeshFilter>().sharedMesh;
                var moverMesh = Object.Instantiate(sphereMesh);
                Object.DestroyImmediate(sphere);
                owned.Add(moverMesh);
                moverMesh.name = "VRSL J10 mover sphere";
                moverMesh.colors = Enumerable.Repeat(Color.black, moverMesh.vertexCount).ToArray();

                MeshRenderer Sphere(string name, int slot, Material m, Mesh mesh)
                {
                    var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                    go.name = name;
                    go.transform.SetParent(rig.Floor.transform.parent, false);
                    go.transform.position = new Vector3(197.75f + 1.5f * slot, 1f, 0f);
                    go.GetComponent<MeshFilter>().sharedMesh = mesh;
                    var r = go.GetComponent<MeshRenderer>();
                    r.sharedMaterial = m;
                    return r;
                }

                var shaders = new[] { BodyShaders[1], BodyShaders[2] }.Concat(MoverShaders).ToArray();
                var bodies = new List<(string shader, string mapProperty, Material material, MeshRenderer renderer)>();
                for (int i = 0; i < shaders.Length; i++)
                {
                    var shader = Shader.Find(shaders[i]);
                    Assert.IsNotNull(shader, $"{shaders[i]} did not compile or is not in the project");
                    var material = new Material(shader) { name = $"VRSL J10 body {i}" };
                    owned.Add(material);
                    string mapProperty = material.HasTexture("_MetallicSmoothness") ? "_MetallicSmoothness"
                                       : material.HasTexture("_MetallicGlossMap")    ? "_MetallicGlossMap"
                                       : null;
                    Assert.IsNotNull(mapProperty, $"{shaders[i]} has no metallic-smoothness map");
                    material.SetColor("_Color", Color.white);
                    material.SetFloat("_Metallic", 0f);
                    material.SetFloat("_Glossiness", 0.8f);
                    material.SetFloat("_FixtureMaxIntensity", 0f);
                    material.SetFloat("_EnableDMX", 0f);
                    if (material.HasTexture("_NormalMap")) material.SetTexture("_NormalMap", Texture2D.normalTexture);
                    bool mover = i >= 2;
                    bodies.Add((shaders[i], mapProperty, material,
                                Sphere($"VRSL J10 body {i}", i, material, mover ? moverMesh : sphereMesh)));
                }
                var reference = new Material(lit) { name = "VRSL J10 reference" };
                owned.Add(reference);
                reference.SetColor("_BaseColor", Color.white);
                reference.SetFloat("_Metallic", 0f);
                var refRenderer = Sphere("VRSL J10 reference", shaders.Length, reference, sphereMesh);

                var light = new GameObject("VRSL J10 sun").AddComponent<Light>();
                light.transform.SetParent(rig.Floor.transform.parent, false);
                light.type      = LightType.Directional;
                light.color     = Color.white;
                light.intensity = 1f;
                light.transform.rotation = Quaternion.Euler(30f, 20f, 0f);
                RenderSettings.sun = light;

                rig.Camera.transform.SetPositionAndRotation(new Vector3(197.75f + 0.75f * shaders.Length, 1f, -10f),
                                                            Quaternion.identity);
                yield return rig.Step(2);

                // Each sphere's mean colour and brightest channel: a smooth surface gathers
                // the sun into a small bright highlight, a rough one spreads it.
                Vector4 Look(Vector3 mean, float peak) => new Vector4(mean.x, mean.y, mean.z, peak);
                var looks = new Vector4[maps.Length, bodies.Count];
                var refLooks = new Vector4[maps.Length];
                for (int m = 0; m < maps.Length; m++)
                {
                    foreach (var b in bodies) b.material.SetTexture(b.mapProperty, maps[m].map);
                    reference.SetFloat("_Smoothness", 0.8f * maps[m].alpha);
                    var frame = CaptureHdr(rig);
                    try
                    {
                        for (int i = 0; i < bodies.Count; i++)
                        {
                            var (mean, peak) = Measure(rig.Camera, frame, bodies[i].renderer);
                            looks[m, i] = Look(mean, peak);
                            Debug.Log($"[J10] {maps[m].what}: {bodies[i].shader} peak {peak}, mean {mean}");
                        }
                        var (refMean, refPeak) = Measure(rig.Camera, frame, refRenderer);
                        refLooks[m] = Look(refMean, refPeak);
                        Debug.Log($"[J10] {maps[m].what}: URP Lit peak {refPeak}, mean {refMean}");
                    }
                    finally { Object.DestroyImmediate(frame); }
                }

                float refRough = (refLooks[0] - refLooks[2]).magnitude;
                if (refRough < 0.1f)
                    Assert.Inconclusive($"URP Lit looks nearly the same at smoothness 0.8 and 0 ({refLooks[0]} against "
                                      + $"{refLooks[2]}), so the light or camera shows no highlight and the row has "
                                      + "nothing to judge");

                var failures = new List<string>();
                for (int i = 0; i < bodies.Count; i++)
                {
                    string name = bodies[i].shader;
                    float byRed   = (looks[0, i] - looks[1, i]).magnitude;
                    float byAlpha = (looks[0, i] - looks[2, i]).magnitude;
                    if (!(byAlpha > 0.1f)) failures.Add(
                        $"{name} looks the same with {bodies[i].mapProperty}'s alpha at 1 and at 0 ({looks[0, i]} "
                      + $"against {looks[2, i]}), so its smoothness doesn't follow the map's alpha");
                    if (!(byRed < 0.1f * byAlpha)) failures.Add(
                        $"{name} changes by {byRed:F3} when only {bodies[i].mapProperty}'s red changes, with Metallic "
                      + $"at 0 ({looks[0, i]} against {looks[1, i]}), against {byAlpha:F3} for its alpha, so it "
                      + "reads smoothness from the red channel");
                }
                if (failures.Count > 0) Assert.Fail(string.Join("\n", failures));
            }
            finally
            {
                rig.Dispose();
                foreach (var o in owned) if (o != null) Object.DestroyImmediate(o);
            }
        }

        /// <summary>
        /// J11. The AudioLink laser takes its colour and brightness from each of its inputs.
        /// The laser's mesh on a copy of its shipped material (scroll 0, so the beams hold
        /// still), in front of the camera, fed an AudioLink texture the row writes: band 0 at
        /// a chosen amplitude, the four theme colours red, green, blue and yellow, and a blue
        /// colour chord light. Each capture is the frame with the laser shown less the frame
        /// with it hidden, summed over the frame, on a half-float capture.
        /// </summary>
        [UnityTest]
        public IEnumerator J11_the_audiolink_laser_follows_its_colour_and_amplitude_inputs()
        {
            var shipped = UnityEditor.AssetDatabase.LoadAssetAtPath<Material>(
                "Packages/town.mr.vrsl-urp/Runtime/Materials/Lights/AudioLink/VRSL-BasicLaser-AudioLInk.mat");
            Assert.IsNotNull(shipped, "the AudioLink laser material is not in the package");
            var prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(
                "Packages/town.mr.vrsl-urp/Runtime/Prefabs/AudioLink/VRSL-AudioLink-BasicLaser.prefab");
            Assert.IsNotNull(prefab, "the AudioLink laser prefab is not in the package");

            var audioWas = Shader.GetGlobalTexture("_AudioTexture");
            var rig      = VRSLDMXRig.Build(withSource: false, targetSize: 512);
            var owned    = new List<Object>();
            try
            {
                var audio = new Texture2D(128, 64, TextureFormat.RGBAFloat, false, true)
                    { name = "VRSL J11 AudioLink", filterMode = FilterMode.Point };
                owned.Add(audio);
                // AudioLink's texture layout, as the laser reads it (VRSL-AudioLink-Data.hlsl):
                // ALPASS_AUDIOLINK is one row per band from row 0, 128 delays wide; ALPASS_THEME_COLOR0
                // to 3 are four texels on one row; ALPASS_CCLIGHTS is two rows, 128 wide. Only band 0
                // carries the amplitude, so a laser reading another band reads silence.
                const int Band0Row = 0, ThemeRow = 23, ChordRow = 25, ChordRows = 2;
                void WriteAudio(float amplitude)
                {
                    var pixels = new Color[128 * 64];
                    for (int x = 0; x < 128; x++) pixels[Band0Row * 128 + x] = new Color(amplitude, amplitude, amplitude, 1f);
                    pixels[ThemeRow * 128 + 0] = Color.red;
                    pixels[ThemeRow * 128 + 1] = Color.green;
                    pixels[ThemeRow * 128 + 2] = Color.blue;
                    pixels[ThemeRow * 128 + 3] = new Color(1f, 1f, 0f, 1f);
                    for (int y = ChordRow; y < ChordRow + ChordRows; y++)
                        for (int x = 0; x < 128; x++) pixels[y * 128 + x] = Color.blue;
                    audio.SetPixels(pixels);
                    audio.Apply();
                    Shader.SetGlobalTexture("_AudioTexture", audio);
                }

                var magenta = new Texture2D(2, 2, TextureFormat.RGBA32, false) { name = "VRSL J11 sample" };
                owned.Add(magenta);
                magenta.SetPixels(Enumerable.Repeat(Color.magenta, 4).ToArray());
                magenta.Apply();

                var material = new Material(shipped) { name = "VRSL J11 laser" };
                owned.Add(material);
                var laser = new GameObject("VRSL J11 laser");
                laser.transform.SetParent(rig.Floor.transform.parent, false);
                laser.transform.position   = new Vector3(500f, 3f, 0f);
                laser.transform.localScale = prefab.transform.localScale;
                laser.AddComponent<MeshFilter>().sharedMesh = prefab.GetComponentInChildren<MeshFilter>().sharedMesh;
                var renderer = laser.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = material;
                rig.Camera.transform.SetPositionAndRotation(new Vector3(500f, 1.5f, -8f), Quaternion.identity);
                yield return rig.Step(2);

                Vector3 Adds(float amplitude, string mode, Color emission)
                {
                    WriteAudio(amplitude);
                    material.SetFloat("_Scroll", 0f);
                    material.SetColor("_Emission", emission);
                    material.SetFloat("_EnableAudioLink", mode == "off" ? 0f : 1f);
                    material.SetFloat("_Band", 0f);
                    material.SetFloat("_EnableThemeColorSampling", mode.StartsWith("theme") ? 1f : 0f);
                    material.SetFloat("_ThemeColorTarget", mode == "theme 2" ? 2f : mode == "theme 3" ? 3f : 0f);
                    material.SetFloat("_EnableColorChord", mode == "chord" ? 1f : 0f);
                    material.SetFloat("_EnableColorTextureSample", mode == "texture" ? 1f : 0f);
                    material.SetFloat("_UseTraditionalSampling", 0f);
                    material.SetTexture("_SamplingTexture", magenta);
                    renderer.enabled = true;
                    var shown = CaptureHdr(rig);
                    renderer.enabled = false;
                    var hidden = CaptureHdr(rig);
                    renderer.enabled = true;
                    try
                    {
                        var a = shown.GetPixels();
                        var b = hidden.GetPixels();
                        var sum = Vector3.zero;
                        for (int i = 0; i < a.Length; i++)
                            sum += new Vector3(a[i].r - b[i].r, a[i].g - b[i].g, a[i].b - b[i].b);
                        Debug.Log($"[J11] {mode}, amplitude {amplitude}, emission {emission}: adds {sum.x:F6} {sum.y:F6} {sum.z:F6}");
                        return sum;
                    }
                    finally
                    {
                        Object.DestroyImmediate(shown);
                        Object.DestroyImmediate(hidden);
                    }
                }

                var red    = new Color(1f, 0f, 0f, 1f);
                var white  = Color.white;
                var off    = Adds(0.03f, "off", red);
                var full   = Adds(1f, "band", red);
                var half   = Adds(0.5f, "band", red);
                var above  = Adds(0.051f, "band", red);
                var quiet  = Adds(0.049f, "band", red);
                var theme2 = Adds(1f, "theme 2", white);
                var theme3 = Adds(1f, "theme 3", white);
                var chord  = Adds(1f, "chord", red);
                var sample = Adds(1f, "texture", white);

                var failures = new List<string>();
                if (!(off.x > 1f && off.x > 3f * off.y && off.x > 3f * off.z)) failures.Add(
                    $"with AudioLink off the laser should add its red emission whatever the band reads, and adds {off:F3}");
                if (!(full.x > 1f && full.x > 3f * full.y)) failures.Add(
                    $"at full amplitude the laser should add red, and adds {full:F3}");
                if (!(half.x > 0f && half.x < 0.75f * full.x)) failures.Add(
                    $"at half amplitude the laser should add less than at full ({half.x:F3} against {full.x:F3})");
                // The two captures sit either side of the 0.05 cutoff, so a cutoff moved by more than
                // 0.001 fails one of them. Without a cutoff 0.049 adds most of what 0.051 does,
                // so "nothing" is judged against the capture just above it, not full.
                if (!(above.x > 0f && above.x > 3f * above.y)) failures.Add(
                    $"just above the amplitude threshold the laser should add red, and adds {above:F4}");
                if (above.x > 0f && !(Mathf.Abs(quiet.x) + Mathf.Abs(quiet.y) + Mathf.Abs(quiet.z) < 1e-2f * above.x)) failures.Add(
                    $"below the amplitude threshold the laser should add nothing, and adds {quiet:F4} "
                  + $"against {above:F4} just above it");
                if (!(theme2.y > 1f && theme2.y > 3f * theme2.x && theme2.y > 3f * theme2.z)) failures.Add(
                    $"on theme colour 2 (AudioLink's second, green) the laser should add green, and adds {theme2:F3}");
                if (!(theme3.z > 1f && theme3.z > 3f * theme3.x && theme3.z > 3f * theme3.y)) failures.Add(
                    $"on theme colour 3 (AudioLink's third, blue) the laser should add blue, and adds {theme3:F3}");
                if (!(chord.z > 1f && chord.z > 3f * chord.x)) failures.Add(
                    $"on the colour chord light (blue) the laser should add blue over its red emission, and adds {chord:F3}");
                if (!(sample.x > 1f && sample.z > 1f && sample.x > 3f * sample.y && sample.z > 3f * sample.y)) failures.Add(
                    $"sampling a magenta texture the laser should add magenta, and adds {sample:F3}");
                if (failures.Count > 0) Assert.Fail(string.Join("\n", failures));
            }
            finally
            {
                Shader.SetGlobalTexture("_AudioTexture", audioWas);
                rig.Dispose();
                foreach (var o in owned) if (o != null) Object.DestroyImmediate(o);
            }
        }

        /// <summary>
        /// J12. A lens flare dims by the share of its occlusion test points that something
        /// blocks. Each flare tests a 17 x 17 grid of points around its centre, one
        /// seventeenth of its light source radius apart in view space, against the camera's
        /// depth. Each flare (AudioLink, and DMX Horizontal) is a quad on a copy of its
        /// shipped material with that radius at 1 m, lit red with AudioLink and DMX off, 5 m
        /// from the camera; a cube on URP Lit 1 m in front of it covers the grid's columns
        /// right of column 4 of 17, then right of column 11, then its rows above row 6, its
        /// edge halfway between two columns or rows as seen from the camera. Captured with the
        /// flares shown and then hidden; measured on a half-float capture.
        /// </summary>
        [UnityTest]
        public IEnumerator J12_lens_flares_dim_by_the_share_of_their_light_that_is_blocked()
        {
            const string Materials = "Packages/town.mr.vrsl-urp/Runtime/Materials/Lights/";
            const int    Side      = 17;
            const float  Radius    = 1f;
            const float  Step      = Radius / Side;
            const float  Depth     = 0.8f;
            var flares = new (string name, string material, string enable)[]
            {
                ("AudioLink Flare", "AudioLink/StaticLight-Blinder/VRSL-AudioLink-Blinder-LensFlareMat.mat", "_EnableAudioLink"),
                ("DMX Flare",       "DMX/StaticLight-Blinder/VRSL-Blinder-LensFlareMat-Horizontal.mat",       "_EnableDMX"),
            };
            // Visible columns or rows out of 17, the axis the edge cuts, and the last grid
            // index (-8 to 8) left uncovered.
            var cases = new (string what, bool columns, int lastOpen)[]
            {
                ("columns right of the 4th covered", true,  -5),
                ("columns right of the 11th covered", true,  2),
                ("rows above the 6th covered",        false, -3),
            };
            var lit = Shader.Find("Universal Render Pipeline/Lit");
            Assert.IsNotNull(lit, "URP Lit is not in the project");

            var rig       = VRSLDMXRig.Build(withSource: false, targetSize: 512);
            var materials = new List<Material>();
            try
            {
                var renderers = new List<MeshRenderer>();
                var blockers  = new List<Transform>();
                var wall = new Material(lit) { name = "VRSL J12 blocker" };
                materials.Add(wall);
                wall.SetColor("_BaseColor", Color.grey);
                for (int i = 0; i < flares.Length; i++)
                {
                    var shipped = UnityEditor.AssetDatabase.LoadAssetAtPath<Material>(Materials + flares[i].material);
                    Assert.IsNotNull(shipped, $"{flares[i].material} is not in the package");
                    var m = new Material(shipped) { name = $"VRSL J12 {flares[i].name}" };
                    materials.Add(m);
                    m.SetFloat(flares[i].enable, 0f);
                    m.SetColor("_Emission", new Color(24f, 0f, 0f, 1f));
                    m.SetFloat("_LightSourceViewSpaceRadius", Radius);
                    var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
                    quad.name = m.name;
                    quad.transform.SetParent(rig.Floor.transform.parent, false);
                    quad.transform.position = new Vector3(500f + 3f * i, 1.5f, 0f);
                    var r = quad.GetComponent<MeshRenderer>();
                    r.sharedMaterial = m;
                    renderers.Add(r);
                    var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    cube.name = $"VRSL J12 blocker {i}";
                    cube.transform.SetParent(rig.Floor.transform.parent, false);
                    cube.GetComponent<MeshRenderer>().sharedMaterial = wall;
                    blockers.Add(cube.transform);
                }
                rig.Camera.transform.SetPositionAndRotation(new Vector3(501.5f, 1.5f, -5f), Quaternion.identity);

                // The camera is unrotated, so view space x and y are world x and y. A point a
                // fraction t of the way from the camera to the flare's plane sits t of the way
                // along the line from the eye to the test point.
                void Place(bool open, bool columns, int lastOpen)
                {
                    var eye = rig.Camera.transform.position;
                    for (int i = 0; i < blockers.Count; i++)
                    {
                        var at = renderers[i].transform.position;
                        if (open)
                        {
                            blockers[i].position   = at + new Vector3(0f, -10f, 0f);
                            blockers[i].localScale = Vector3.one;
                            continue;
                        }
                        var centre = Vector3.Lerp(eye, at, Depth);
                        float edge = (lastOpen + 0.5f) * Step * Depth;
                        blockers[i].localScale = columns ? new Vector3(1f, 1.5f, 0.05f) : new Vector3(1.5f, 1f, 0.05f);
                        blockers[i].position   = centre + (columns ? new Vector3(edge + 0.5f, 0f, 0f) : new Vector3(0f, edge + 0.5f, 0f));
                    }
                }

                // Red less green: the flare's white core doesn't scale with what is visible on
                // the DMX flare, its red does on both.
                float[] Capture()
                {
                    foreach (var r in renderers) r.enabled = true;
                    var shown = CaptureHdr(rig);
                    foreach (var r in renderers) r.enabled = false;
                    var hidden = CaptureHdr(rig);
                    foreach (var r in renderers) r.enabled = true;
                    try
                    {
                        var added = new float[renderers.Count];
                        for (int i = 0; i < renderers.Count; i++)
                        {
                            var d = Measure(rig.Camera, shown, renderers[i]).mean - Measure(rig.Camera, hidden, renderers[i]).mean;
                            added[i] = d.x - d.y;
                        }
                        return added;
                    }
                    finally
                    {
                        Object.DestroyImmediate(shown);
                        Object.DestroyImmediate(hidden);
                    }
                }

                Place(true, true, 0);
                yield return rig.Step(2);
                var open = Capture();
                var failures = new List<string>();
                for (int i = 0; i < renderers.Count; i++)
                {
                    Debug.Log($"[J12] {renderers[i].name}: adds {open[i]:F4} red in the open");
                    if (!(open[i] > 0.02f)) failures.Add(
                        $"{renderers[i].name} adds only {open[i]:F4} red with nothing in front of it, so it isn't lit "
                      + "and its blocked captures prove nothing");
                }
                if (failures.Count > 0) Assert.Fail(string.Join("\n", failures));

                foreach (var (what, columns, lastOpen) in cases)
                {
                    Place(false, columns, lastOpen);
                    yield return rig.Step(2);
                    var part = Capture();
                    float expected = (lastOpen + Side / 2 + 1) / (float)Side;
                    for (int i = 0; i < renderers.Count; i++)
                    {
                        float share = part[i] / open[i];
                        Debug.Log($"[J12] {renderers[i].name}, {what}: {share:F4} of its open light, expected {expected:F4}");
                        if (Mathf.Abs(share - expected) > 0.25f / Side) failures.Add(
                            $"{renderers[i].name} with its {what} keeps {share:F4} of its open light against "
                          + $"{expected:F4}, the share of its test points left visible. A flare whose test points "
                          + "sit at the wrong spacing along either axis dims by the wrong amount");
                    }
                }
                if (failures.Count > 0) Assert.Fail(string.Join("\n", failures));
            }
            finally
            {
                rig.Dispose();
                foreach (var m in materials) if (m != null) Object.DestroyImmediate(m);
            }
        }

        static List<UnityEngine.Rendering.Universal.UniversalRendererData> UniversalRenderers(
            UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset asset)
        {
            var renderers = new List<UnityEngine.Rendering.Universal.UniversalRendererData>();
            foreach (var data in asset.rendererDataList)
                if (data is UnityEngine.Rendering.Universal.UniversalRendererData universal) renderers.Add(universal);
            return renderers;
        }

        /// <summary>
        /// J13. A static fixture body marked to contribute to baked lighting bakes its housing
        /// as URP Lit bakes the same surface, and none of its glow. Each body's Meta pass, drawn
        /// as the lightmapper draws it, returns URP Lit's albedo for the same colour, metallic
        /// and smoothness, and black for emission. A body with no Meta pass bakes as white.
        /// </summary>
        [Test]
        public void J13_static_bodies_bake_their_housing_like_urp_lit()
        {
            var colour = new Color(0.8f, 0.3f, 0.1f);
            const float metallic = 0.4f, smoothness = 0.7f;

            bool asyncCompilation = UnityEditor.ShaderUtil.allowAsyncCompilation;
            UnityEditor.ShaderUtil.allowAsyncCompilation = false;
            var reference = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = "VRSL J13 reference" };
            var quad = new Mesh { name = "VRSL J13 quad" };
            var target = new RenderTexture(8, 8, 0, RenderTextureFormat.ARGBHalf) { name = "VRSL J13 meta" };
            var made = new List<Object> { reference, quad, target };
            try
            {
                RenderOnce(target);

                // The lightmapper rasterises from the lightmap UVs, which cover the target. The
                // positions sit off it and the texture UVs cover one corner, so a Meta pass that
                // rasterises from either leaves the centre black.
                var corners = new[] { Vector2.zero, Vector2.right, Vector2.one, Vector2.up };
                quad.vertices = corners.Select(c => new Vector3(10f + c.x, 10f + c.y, 0f)).ToArray();
                quad.uv = corners.Select(c => c * 0.25f).ToArray();
                quad.uv2 = corners;
                quad.triangles = new[] { 0, 1, 2, 0, 2, 3 };
                var holder = new GameObject("VRSL J13 quad");
                made.Add(holder);
                holder.AddComponent<MeshFilter>().sharedMesh = quad;
                var quadRenderer = holder.AddComponent<MeshRenderer>();
                // The bake sets the lightmap scale and offset; outside one it reaches the
                // shader as zero and every vertex lands on one point.
                var lightmapST = new MaterialPropertyBlock();
                lightmapST.SetVector("unity_LightmapST", new Vector4(1, 1, 0, 0));
                quadRenderer.SetPropertyBlock(lightmapST);

                reference.SetColor("_BaseColor", colour);
                reference.SetFloat("_Metallic", metallic);
                reference.SetFloat("_Smoothness", smoothness);
                Color expected = DrawMeta(reference, albedo: true, quadRenderer, target);
                Debug.Log($"[J13] URP Lit albedo {expected}");
                Assert.That(expected.r, Is.GreaterThan(2f * expected.b),
                    $"URP Lit's Meta pass returned {expected} for {colour}, so the draw isn't reaching the pass the way the lightmapper does");

                foreach (var name in BodyShaders)
                {
                    var material = new Material(Shader.Find(name)) { name = $"VRSL J13 {name}" };
                    made.Add(material);
                    material.SetColor("_Color", colour);
                    material.SetFloat("_Metallic", metallic);
                    material.SetFloat("_Glossiness", smoothness);
                    material.SetColor("_Emission", new Color(2f, 1f, 0.5f));
                    Assert.That(material.FindPass("Meta"), Is.GreaterThanOrEqualTo(0),
                        $"{name} has no Meta pass, so a body marked to contribute to baked lighting bakes as white");

                    Color albedo = DrawMeta(material, albedo: true, quadRenderer, target);
                    Color emission = DrawMeta(material, albedo: false, quadRenderer, target);
                    Debug.Log($"[J13] {name}: albedo {albedo}, emission {emission}");
                    for (int c = 0; c < 3; c++)
                        Assert.That(albedo[c], Is.EqualTo(expected[c]).Within(0.004f),
                            $"{name} bakes its housing as {albedo} where URP Lit bakes the same surface as {expected}");
                    Assert.That(emission.maxColorComponent, Is.LessThan(1e-4f),
                        $"{name} bakes emission {emission}; its glow is driven at runtime and mustn't be baked");
                }
            }
            finally
            {
                UnityEditor.ShaderUtil.allowAsyncCompilation = asyncCompilation;
                foreach (var o in made) Object.DestroyImmediate(o);
            }
        }

        static readonly System.Text.RegularExpressions.Regex ProbeVolumePragma = new(
            @"^\s*#pragma\s+multi_compile(?:_vertex|_fragment)?\s.*\bPROBE_VOLUMES_L1\b.*\bPROBE_VOLUMES_L2\b",
            System.Text.RegularExpressions.RegexOptions.Multiline);

        /// <summary>
        /// J14. Every fixture body takes its baked light from Adaptive Probe Volumes where the
        /// project uses them, as URP Lit does: its forward pass declares URP's two probe volume
        /// keywords, and its fragment compiled with either one binds the volumes' index. A body
        /// without them gets only the ambient probe in a world baked with probe volumes, while
        /// URP Lit beside it takes the bounce light. Compiled for Windows, nothing rendered.
        /// </summary>
        [Test]
        public void J14_fixture_bodies_sample_adaptive_probe_volumes()
        {
            var lightMode = new UnityEngine.Rendering.ShaderTagId("LightMode");
            var failures = new List<string>();
            int compiled = 0;
            foreach (var name in BodyShaders.Concat(MoverShaders))
            {
                var shader = Shader.Find(name);
                Assert.IsNotNull(shader, $"{name} is not in this project");
                var data = UnityEditor.ShaderUtil.GetShaderData(shader);
                var forward = Enumerable.Range(0, data.SubshaderCount)
                                        .Select(data.GetSubshader)
                                        .SelectMany(s => Enumerable.Range(0, s.PassCount).Select(s.GetPass))
                                        .Where(p => p.FindTagValue(lightMode).name == "UniversalForward")
                                        .ToList();
                if (forward.Count == 0) failures.Add($"{name} has no UniversalForward pass");
                foreach (var pass in forward)
                {
                    if (!ProbeVolumePragma.IsMatch(pass.SourceCode ?? ""))
                        failures.Add($"{name} '{pass.Name}' doesn't declare PROBE_VOLUMES_L1 and PROBE_VOLUMES_L2, so URP never builds its probe volume variant");
                    foreach (var keyword in new[] { "PROBE_VOLUMES_L1", "PROBE_VOLUMES_L2" })
                    {
                        var info = pass.CompileVariant(UnityEditor.Rendering.ShaderType.Fragment, new[] { keyword },
                                                       UnityEditor.Rendering.ShaderCompilerPlatform.D3D,
                                                       UnityEditor.BuildTarget.StandaloneWindows64);
                        compiled++;
                        if (!info.Success)
                            failures.Add($"{name} '{pass.Name}' [{keyword}] doesn't compile: "
                                       + string.Join("; ", info.Messages.Select(m => m.message)));
                        else if (!info.ResourceBindings.Any(b => b.Name == "_APVResIndex"))
                            failures.Add($"{name} '{pass.Name}' [{keyword}] doesn't sample the probe volumes; its baked light is the ambient probe alone");
                    }
                }
            }
            Debug.Log($"[J14] {compiled} forward fragments compiled, {failures.Count} failure(s)");
            Assert.IsEmpty(failures, string.Join("\n", failures));
        }

        /// <summary>
        /// J15. Every renderer on a package prefab drawn with a fixture body shader blends light
        /// probes. With Light Probes off, URP gives the renderer the ambient probe alone,
        /// probe volumes included, whatever its shader samples.
        /// </summary>
        [Test]
        public void J15_fixture_body_renderers_blend_light_probes()
        {
            var bodies = new HashSet<string>(BodyShaders.Concat(MoverShaders));
            var failures = new List<string>();
            int checkedRenderers = 0;
            foreach (var guid in UnityEditor.AssetDatabase.FindAssets("t:Prefab", new[] { "Packages/town.mr.vrsl-urp/Runtime/Prefabs" }))
            {
                string path = UnityEditor.AssetDatabase.GUIDToAssetPath(guid);
                var prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(path);
                foreach (var renderer in prefab.GetComponentsInChildren<Renderer>(true))
                {
                    if (!renderer.sharedMaterials.Any(m => m != null && bodies.Contains(m.shader.name))) continue;
                    checkedRenderers++;
                    if (renderer.lightProbeUsage != UnityEngine.Rendering.LightProbeUsage.BlendProbes)
                        failures.Add($"{path}: '{renderer.name}' has Light Probes {renderer.lightProbeUsage}, so its body takes no baked light beyond the ambient probe");
                }
            }
            Debug.Log($"[J15] {checkedRenderers} body renderers, {failures.Count} not blending probes");
            Assert.Greater(checkedRenderers, 0, "no prefab renderer uses a fixture body shader, so the row checked nothing");
            Assert.IsEmpty(failures, string.Join("\n", failures));
        }

        /// <summary>A shader picks its URP subshader only once the pipeline has rendered, so a
        /// row that asks a material for a pass renders a camera first.</summary>
        static void RenderOnce(RenderTexture target)
        {
            var camera = new GameObject("VRSL J13 camera").AddComponent<Camera>();
            try
            {
                camera.targetTexture = target;
                camera.Render();
            }
            finally
            {
                Object.DestroyImmediate(camera.gameObject);
            }
        }

        /// <summary>Draws a material's Meta pass with the controls the lightmapper sets
        /// (rasterised from the lightmap UVs, lightmap space onto the target, albedo or emission,
        /// linear, no albedo boost) and reads the centre.</summary>
        static Color DrawMeta(Material material, bool albedo, Renderer quad, RenderTexture target)
        {
            var cmd = new UnityEngine.Rendering.CommandBuffer { name = "VRSL J13 meta" };
            cmd.SetRenderTarget(target);
            cmd.ClearRenderTarget(false, true, Color.clear);
            cmd.SetViewProjectionMatrices(Matrix4x4.identity, Matrix4x4.Ortho(0f, 1f, 0f, 1f, -1f, 1f));
            cmd.SetGlobalVector("unity_MetaVertexControl", new Vector4(1, 0, 0, 0));
            cmd.SetGlobalVector("unity_MetaFragmentControl", albedo ? new Vector4(1, 0, 0, 0) : new Vector4(0, 1, 0, 0));
            cmd.SetGlobalFloat("unity_OneOverOutputBoost", 1f);
            cmd.SetGlobalFloat("unity_MaxOutputValue", 100f);
            cmd.SetGlobalFloat("unity_UseLinearSpace", 1f);
            cmd.DrawRenderer(quad, material, 0, material.FindPass("Meta"));
            Graphics.ExecuteCommandBuffer(cmd);
            cmd.Release();

            var read = new Texture2D(target.width, target.height, TextureFormat.RGBAHalf, false, true);
            var previous = RenderTexture.active;
            try
            {
                RenderTexture.active = target;
                read.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0);
                return read.GetPixel(target.width / 2, target.height / 2);
            }
            finally
            {
                RenderTexture.active = previous;
                Object.DestroyImmediate(read);
            }
        }

        static readonly string[] BodyShaders =
        {
            "VRSL-URP/AudioLink/Standard Static/Fixture",
            "VRSL-URP/Standard Static/Surface Shaders/Opaque",
            "VRSL-URP/Standard Static/Surface Shaders/Opaque Multi-Channel Bar",
        };

        static readonly string[] MoverShaders =
        {
            "VRSL-URP/Standard Mover/Fixture",
            "VRSL-URP/Wash Mover/Fixture",
            "VRSL-URP/AudioLink/Standard Mover/Fixture",
            "VRSL-URP/AudioLink/Wash Mover/Fixture",
        };

        /// <summary>
        /// A cube on each fixture body shader, emission off, white with smoothness 0.2, in a
        /// row at x 197.75 to 200.75, with the four mover bodies in a row above, and a cube on
        /// URP Lit with the same surface at x 202.25, all casting shadows. The mover cubes
        /// carry black vertex colours, which the movers draw in place. Everything the row
        /// must destroy goes in <paramref name="owned"/>.
        /// </summary>
        static (List<(string shader, MeshRenderer renderer)> bodies, MeshRenderer reference) BodyCubes(
            VRSLDMXRig rig, string row, List<Object> owned)
        {
            Mesh moverMesh = null;
            var lit = Shader.Find("Universal Render Pipeline/Lit");
            Assert.IsNotNull(lit, "URP Lit is not in the project");

            MeshRenderer Cube(string name, float x, Material m, float y = 1f)
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                go.name = name;
                go.transform.SetParent(rig.Floor.transform.parent, false);
                go.transform.position = new Vector3(x, y, 0f);
                var r = go.GetComponent<MeshRenderer>();
                r.sharedMaterial    = m;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                return r;
            }

            var bodies = new List<(string shader, MeshRenderer renderer)>();
            for (int i = 0; i < BodyShaders.Length + MoverShaders.Length; i++)
            {
                bool mover = i >= BodyShaders.Length;
                string name = mover ? MoverShaders[i - BodyShaders.Length] : BodyShaders[i];
                var shader = Shader.Find(name);
                Assert.IsNotNull(shader, $"{name} did not compile or is not in the project");
                var material = new Material(shader) { name = $"VRSL {row} body {i}" };
                owned.Add(material);
                material.SetColor("_Color", Color.white);
                material.SetFloat("_Metallic", 0f);
                material.SetFloat("_Glossiness", 0.2f);
                material.SetFloat("_FixtureMaxIntensity", 0f);
                material.SetFloat("_EnableDMX", 0f);
                if (!mover)
                {
                    material.SetTexture("_NormalMap", Texture2D.normalTexture);
                    bodies.Add((name, Cube($"VRSL {row} body {i}", 197.75f + 1.5f * i, material)));
                    continue;
                }
                if (moverMesh == null)
                {
                    var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    moverMesh = Object.Instantiate(cube.GetComponent<MeshFilter>().sharedMesh);
                    Object.DestroyImmediate(cube);
                    owned.Add(moverMesh);
                    moverMesh.name = $"VRSL {row} mover cube";
                    moverMesh.colors = Enumerable.Repeat(Color.black, moverMesh.vertexCount).ToArray();
                }
                var renderer = Cube($"VRSL {row} body {i}", 197.75f + 1.5f * (i - BodyShaders.Length), material, 2.4f);
                renderer.GetComponent<MeshFilter>().sharedMesh = moverMesh;
                bodies.Add((name, renderer));
            }
            // URP's own Lit with the same surface, beside the bodies, so a dark frame
            // separates a body shader from the light and camera.
            var reference = new Material(lit) { name = $"VRSL {row} reference" };
            owned.Add(reference);
            reference.SetColor("_BaseColor", Color.white);
            reference.SetFloat("_Metallic", 0f);
            reference.SetFloat("_Smoothness", 0.2f);
            return (bodies, Cube($"VRSL {row} reference", 202.25f, reference));
        }

        /// <summary>
        /// The rig camera's view rendered into a half-float target. The rig's own target is
        /// 8-bit, and a lit body's emission is far above 1 in every channel, so there it clips
        /// to white whatever colour it is.
        /// </summary>
        internal static Texture2D CaptureHdr(VRSLDMXRig rig)
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
