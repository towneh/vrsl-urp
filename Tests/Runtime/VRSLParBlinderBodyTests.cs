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
    /// Rows J1 to J4, J6 and J7 of TESTING.md.
    /// </summary>
    class VRSLParBlinderBodyTests : VRSLDMXTest
    {
        const string Prefabs = "Packages/town.mr.vrsl-urp/Runtime/Prefabs/DMX/";

        static readonly string[] ParsAndBlinders =
        {
            "VRSL-DMX-Static-ParLight-13CH.prefab",
            "VRSL-DMX-Static-Blinder-13CH.prefab",
            "Superseded/VRSL-DMX-Static-ParLight-L-13CH-URP.prefab",
            "Superseded/VRSL-DMX-Static-Blinder-L-13CH-URP.prefab",
            "Superseded/VRSL-DMX-Static-ParLight-V-13CH-URP.prefab",
            "Superseded/VRSL-DMX-Static-Blinder-V-13CH-URP.prefab",
            "VRSL-DMX-Static-ParLight-5CH.prefab",
            "VRSL-DMX-Static-Blinder-5CH.prefab",
            "Superseded/VRSL-DMX-Static-ParLight-V-5CH-URP.prefab",
            "Superseded/VRSL-DMX-Static-Blinder-V-5CH-URP.prefab",
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
                var light  = prefab.GetComponent<VRStageLighting_DMX_RealtimeLight>();
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
            string[] bodyShaders =
            {
                "VRSL-URP/AudioLink/Standard Static/Fixture",
                "VRSL-URP/Standard Static/Surface Shaders/Opaque",
                "VRSL-URP/Standard Static/Surface Shaders/Opaque Multi-Channel Bar",
            };
            string[] moverShaders =
            {
                "VRSL-URP/Standard Mover/Fixture",
                "VRSL-URP/Wash Mover/Fixture",
                "VRSL-URP/AudioLink/Standard Mover/Fixture",
                "VRSL-URP/AudioLink/Wash Mover/Fixture",
            };
            var lit = Shader.Find("Universal Render Pipeline/Lit");
            Assert.IsNotNull(lit, "URP Lit is not in the project");

            int cascadesWas = asset.shadowCascadeCount;
            var rig = VRSLDMXRig.Build(withSource: false, targetSize: 512);
            var materials = new List<Material>();
            Mesh moverMesh = null;
            try
            {
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
                for (int i = 0; i < bodyShaders.Length + moverShaders.Length; i++)
                {
                    bool mover = i >= bodyShaders.Length;
                    string name = mover ? moverShaders[i - bodyShaders.Length] : bodyShaders[i];
                    var shader = Shader.Find(name);
                    Assert.IsNotNull(shader, $"{name} did not compile or is not in the project");
                    var material = new Material(shader) { name = $"VRSL J6 body {i}" };
                    materials.Add(material);
                    material.SetColor("_Color", Color.white);
                    material.SetFloat("_Metallic", 0f);
                    material.SetFloat("_Glossiness", 0.2f);
                    material.SetFloat("_FixtureMaxIntensity", 0f);
                    material.SetFloat("_EnableDMX", 0f);
                    if (!mover)
                    {
                        material.SetTexture("_NormalMap", Texture2D.normalTexture);
                        bodies.Add((name, Cube($"VRSL J6 body {i}", 197.75f + 1.5f * i, material)));
                        continue;
                    }
                    if (moverMesh == null)
                    {
                        var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                        moverMesh = Object.Instantiate(cube.GetComponent<MeshFilter>().sharedMesh);
                        Object.DestroyImmediate(cube);
                        moverMesh.name = "VRSL J6 mover cube";
                        moverMesh.colors = Enumerable.Repeat(Color.black, moverMesh.vertexCount).ToArray();
                    }
                    var renderer = Cube($"VRSL J6 body {i}", 197.75f + 1.5f * (i - bodyShaders.Length), material, 2.4f);
                    renderer.GetComponent<MeshFilter>().sharedMesh = moverMesh;
                    bodies.Add((name, renderer));
                }
                // URP's own Lit with the same surface, beside the bodies, so a dark frame
                // separates a body shader from the light and camera.
                var reference = new Material(lit) { name = "VRSL J6 reference" };
                materials.Add(reference);
                reference.SetColor("_BaseColor", Color.white);
                reference.SetFloat("_Metallic", 0f);
                reference.SetFloat("_Smoothness", 0.2f);
                var refRenderer = Cube("VRSL J6 reference", 202.25f, reference);

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
                foreach (var m in materials) if (m != null) Object.DestroyImmediate(m);
                if (moverMesh != null) Object.DestroyImmediate(moverMesh);
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
