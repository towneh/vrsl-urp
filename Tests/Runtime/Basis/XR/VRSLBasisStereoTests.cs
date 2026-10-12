#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Unity.XR.MockHMD;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.TestTools;
using UnityEngine.XR;

namespace VRSL.URP.Tests
{
    /// <summary>
    /// Fixture bodies in single-pass instanced stereo on Basis's own pipeline assets,
    /// through Unity's Mock HMD. Row X4 of TESTING.md, automated.
    /// </summary>
    class VRSLBasisStereoTests
    {
        const string QualityFolder = "Assets/Basis/Settings/Quality Settiings/";

        static readonly (string name, string path)[] BasisPipelines =
        {
            ("desktop", QualityFolder + "Modified - Desktop.asset"),
            ("Android", QualityFolder + "Modified - Android.asset"),
        };

        /// <summary>Pixels an eye must give a body before it counts as seen.</summary>
        const int MinPixels = 200;

        const string Shaders = "Packages/town.mr.vrsl-urp/Runtime/Shaders/";

        /// <summary>
        /// The body shaders, a group in frame at a time, each body in its group taking red,
        /// green and blue in turn.
        /// </summary>
        static readonly (string path, bool mover)[][] BodyGroups =
        {
            new[]
            {
                ("AudioLink/StaticLights/VRSL-AudioLink-StaticLight-FixtureMesh.shader",       false),
                ("Basic Surface Shaders/VRSL-StandardSurface-Opaque.shader",                    false),
                ("Basic Surface Shaders/VRSL-StandardSurface-Opaque 12 Channel Bar.shader",     false),
            },
            new[]
            {
                ("MovingLights/VRSL-StandardMover-FixtureMesh.shader",                          true),
                ("MovingLights/VRSL-WashMover-FixtureMesh.shader",                              true),
            },
            new[]
            {
                ("AudioLink/MovingLights/VRSL-AudioLink-StandardMover-FixtureMesh.shader",      true),
                ("AudioLink/MovingLights/VRSL-AudioLink-WashMover-FixtureMesh.shader",          true),
            },
        };

        static readonly Color[] BodyColours = { Color.red, Color.green, Color.blue };

        /// <summary>
        /// One eye of the display's eye texture, read back as linear floats. A copy needs
        /// matching sample counts, so a multisampled eye array is resolved into a
        /// single-sample array first and the eye copied from there.
        /// </summary>
        static Color[] ReadEye(RenderTexture eyes, int slice)
        {
            int samples = Mathf.Max(1, eyes.antiAliasing);
            var eye = new RenderTexture(eyes.width, eyes.height, 0, eyes.graphicsFormat) { name = $"VRSL X4 eye {slice}" };
            eye.Create();
            RenderTexture resolved = null;
            try
            {
                var source = eyes;
                if (samples > 1)
                {
                    resolved = new RenderTexture(eyes.width, eyes.height, 0, eyes.graphicsFormat)
                    {
                        name = "VRSL X4 eyes (resolved)", dimension = eyes.dimension, volumeDepth = eyes.volumeDepth,
                    };
                    resolved.Create();
                    eyes.ResolveAntiAliasedSurface(resolved);
                    source = resolved;
                }
                Graphics.CopyTexture(source, slice, 0, eye, 0, 0);
                var req = AsyncGPUReadback.Request(eye, 0, TextureFormat.RGBAFloat);
                req.WaitForCompletion();
                Assert.IsFalse(req.hasError, $"reading back eye {slice} failed");
                return req.GetData<Color>().ToArray();
            }
            finally
            {
                eye.Release();
                Object.DestroyImmediate(eye);
                if (resolved != null) { resolved.Release(); Object.DestroyImmediate(resolved); }
            }
        }

        /// <summary>The readback copies one slice of the eye array into a 2D texture, which
        /// needs the backend to copy between texture types; without it the rows can't read an
        /// eye at all, and say so rather than failing.</summary>
        static void RequireEyeSliceCopy()
        {
            if ((SystemInfo.copyTextureSupport & CopyTextureSupport.DifferentTypes) == 0)
                Assert.Inconclusive($"this backend can't copy an array slice into a 2D texture (copyTextureSupport "
                                  + $"{SystemInfo.copyTextureSupport}), so the eyes can't be read back");
        }

        /// <summary>
        /// X4's readback on a multisampled eye texture. Mock HMD hands back a single-sample
        /// eye texture even where the pipeline asks for MSAA, so the row itself never takes
        /// the multisampled path; this builds a 2x multisampled two-slice array, clears one
        /// slice red and the other green, and reads each back through the same code.
        /// </summary>
        [Test]
        public void X4_readback_resolves_a_multisampled_eye_texture()
        {
            RequireEyeSliceCopy();
            if (!SystemInfo.supportsMultisampled2DArrayTextures)
                Assert.Inconclusive("this GPU has no multisampled 2D array textures, so there is nothing to resolve");
            var eyes = new RenderTexture(64, 64, 0, UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8A8_SRGB)
            {
                name = "VRSL X4 multisampled eyes", dimension = TextureDimension.Tex2DArray, volumeDepth = 2, antiAliasing = 2,
            };
            var previous = RenderTexture.active;
            try
            {
                Assert.IsTrue(eyes.Create(), "couldn't create the multisampled eye array");
                Assert.AreEqual(2, eyes.antiAliasing, "the eye array didn't keep its sample count");
                Graphics.SetRenderTarget(eyes, 0, CubemapFace.Unknown, 0);
                GL.Clear(false, true, Color.red);
                Graphics.SetRenderTarget(eyes, 0, CubemapFace.Unknown, 1);
                GL.Clear(false, true, Color.green);

                var left  = ReadEye(eyes, 0);
                var right = ReadEye(eyes, 1);
                Assert.AreEqual(64 * 64, left.Length, "the left eye read back the wrong size");
                Assert.AreEqual(64 * 64, CountHue(left, 0),  $"the left slice didn't come back red (first pixel {left[0]})");
                Assert.AreEqual(64 * 64, CountHue(right, 1), $"the right slice didn't come back green (first pixel {right[0]})");
            }
            finally
            {
                RenderTexture.active = previous;
                eyes.Release();
                Object.DestroyImmediate(eyes);
            }
        }

        /// <summary>Pixels where one channel clearly leads: how much of the body of
        /// that colour an eye sees.</summary>
        static int CountHue(Color[] px, int channel)
        {
            int n = 0;
            foreach (var c in px)
            {
                float v  = channel == 0 ? c.r : channel == 1 ? c.g : c.b;
                float o1 = channel == 0 ? c.g : c.r;
                float o2 = channel == 2 ? c.g : c.b;
                if (v > 0.05f && o1 < 0.3f * v && o2 < 0.3f * v) n++;
            }
            return n;
        }

        static int CountWhite(Color[] px)
            => px.Count(c => c.r > 0.1f && c.g > 0.1f && c.b > 0.1f
                          && Mathf.Max(c.r, Mathf.Max(c.g, c.b)) < 1.5f * Mathf.Min(c.r, Mathf.Min(c.g, c.b)));

        /// <summary>
        /// X4. The fixture body shaders draw in both eyes on Basis's desktop and Android
        /// pipeline assets (Forward+ and Forward renderers), in single-pass instanced
        /// stereo through Mock HMD.
        ///
        /// A cube per body shader beside a white URP Lit cube, under a directional light
        /// with ambient off, the bodies in three groups framed one at a time: the static
        /// bodies (AudioLink static red, DMX surface green, multi-channel bar blue), the
        /// DMX movers (spot red, wash green) and the AudioLink movers (spot red, wash
        /// green). The movers' cubes carry black vertex colours, which the movers draw
        /// in place. Each pipeline asset is made the active one in turn
        /// and put back afterwards. The display's eye texture, what the headset would be
        /// handed, is read back a slice per eye for each group. Each body has to cover
        /// a fair number of pixels of its colour in each eye, and the two eyes' counts
        /// have to agree within a factor of two, which parallax stays well inside.
        /// Counting colours rather than projecting bounds keeps the row independent of
        /// how the eye texture is oriented. Inconclusive when Mock HMD can't start, the
        /// display didn't hand back a two-slice eye texture, or the URP Lit cube isn't
        /// seen in both eyes.
        ///
        /// Needs the editor window: in batch mode Mock HMD reports a 0x0 eye texture and
        /// nothing renders, so the row ignores itself there. Errors the editor's own views
        /// log while an XR display is up are reported, not failed on.
        /// </summary>
        [UnityTest]
        public IEnumerator X4_fixture_bodies_are_visible_in_both_eyes_on_the_basis_pipelines()
        {
            if (Application.isBatchMode)
                Assert.Ignore("needs the editor window: in batch mode Mock HMD reports a 0x0 eye texture and "
                            + "nothing renders, so run this assembly without -batchmode");
            RequireEyeSliceCopy();
            var pipelines = new List<(string name, UniversalRenderPipelineAsset asset)>();
            foreach (var (name, path) in BasisPipelines)
            {
                var asset = UnityEditor.AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(path);
                if (asset == null) Assert.Ignore($"Basis's {name} pipeline asset isn't at {path}");
                pipelines.Add((name, asset));
            }

            var displays = new List<XRDisplaySubsystem>();
            SubsystemManager.GetSubsystems(displays);
            if (displays.Any(d => d.running))
                Assert.Inconclusive("an XR display is already running, so Mock HMD can't take over");

            bool ignoreFailingWas = LogAssert.ignoreFailingMessages;
            LogAssert.ignoreFailingMessages = true;
            var errors = new List<string>();
            void OnLog(string condition, string stack, LogType type)
            {
                if (type == LogType.Error || type == LogType.Exception) errors.Add(condition);
            }
            Application.logMessageReceived += OnLog;

            var qualityPipelineWas = QualitySettings.renderPipeline;
            var loader = ScriptableObject.CreateInstance<MockHMDLoader>();
            var made   = new List<Object> { loader };
            System.Action<ScriptableRenderContext, Camera> count = null;
            bool started = false;
            var ambientModeWas  = RenderSettings.ambientMode;
            var ambientLightWas = RenderSettings.ambientLight;
            var skyboxWas       = RenderSettings.skybox;
            var sunWas          = RenderSettings.sun;
            var fogWas          = RenderSettings.fog;
            try
            {
                // Its native bool doesn't marshal to a canonical true, so the result isn't
                // asserted; the two-slice check below proves the mode took. Set on both
                // sides of Initialize, which applies the package's own build settings.
                MockHMD.SetRenderMode(MockHMDBuildSettings.RenderMode.SinglePassInstanced);
                Assert.IsTrue(loader.Initialize(), "Mock HMD did not initialise");
                MockHMD.SetRenderMode(MockHMDBuildSettings.RenderMode.SinglePassInstanced);
                Assert.IsTrue(loader.Start(), "Mock HMD did not start");
                started = true;
                SubsystemManager.GetSubsystems(displays);
                var display = displays.FirstOrDefault(d => d.running);
                if (display == null)
                    Assert.Inconclusive("Mock HMD started but no XR display is running, so nothing renders in stereo");

                RenderSettings.ambientMode  = AmbientMode.Flat;
                RenderSettings.ambientLight = Color.black;
                RenderSettings.skybox       = null;
                RenderSettings.fog          = false;

                var cam = new GameObject("VRSL X4 camera").AddComponent<Camera>();
                made.Add(cam.gameObject);
                cam.clearFlags      = CameraClearFlags.SolidColor;
                cam.backgroundColor = Color.black;
                cam.depth           = 100f;
                cam.transform.SetPositionAndRotation(new Vector3(0f, 1f, -6f), Quaternion.identity);

                var light = new GameObject("VRSL X4 sun").AddComponent<Light>();
                made.Add(light.gameObject);
                light.type      = LightType.Directional;
                light.intensity = 1f;
                light.shadows   = LightShadows.Hard;
                // From above, behind and to the left of the camera, so the faces it sees are lit.
                light.transform.rotation = Quaternion.Euler(30f, 20f, 0f);
                RenderSettings.sun = light;

                var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                var moverMesh = Object.Instantiate(cube.GetComponent<MeshFilter>().sharedMesh);
                Object.DestroyImmediate(cube);
                moverMesh.name = "VRSL X4 mover cube";
                moverMesh.colors = Enumerable.Repeat(Color.black, moverMesh.vertexCount).ToArray();
                made.Add(moverMesh);

                var groups = new List<(string path, GameObject go)[]>();
                for (int g = 0; g < BodyGroups.Length; g++)
                {
                    var group = new (string path, GameObject go)[BodyGroups[g].Length];
                    for (int i = 0; i < group.Length; i++)
                    {
                        var (path, mover) = BodyGroups[g][i];
                        var shader = UnityEditor.AssetDatabase.LoadAssetAtPath<Shader>(Shaders + path);
                        Assert.IsNotNull(shader, $"{path} is not in the project");
                        Assert.IsTrue(shader.isSupported, $"{path} did not compile");
                        var colour = BodyColours[i];
                        var m = new Material(shader) { name = $"VRSL X4 body {g}.{i}" };
                        made.Add(m);
                        m.SetColor("_Color", colour);
                        m.SetFloat("_Metallic", 0f);
                        m.SetFloat("_Glossiness", 0.2f);
                        m.SetFloat("_FixtureMaxIntensity", 0f);
                        m.SetFloat("_EnableDMX", 0f);
                        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                        made.Add(go);
                        go.transform.position = new Vector3(-2.25f + 1.5f * i, 1f, 0f);
                        go.GetComponent<MeshRenderer>().sharedMaterial = m;
                        if (mover)
                            go.GetComponent<MeshFilter>().sharedMesh = moverMesh;
                        else
                            m.SetTexture("_NormalMap", Texture2D.normalTexture);
                        go.SetActive(false);
                        group[i] = (path, go);
                    }
                    groups.Add(group);
                }
                var lit = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = "VRSL X4 reference" };
                made.Add(lit);
                lit.SetColor("_BaseColor", Color.white);
                var reference = GameObject.CreatePrimitive(PrimitiveType.Cube);
                made.Add(reference);
                reference.transform.position = new Vector3(2.25f, 1f, 0f);
                reference.GetComponent<MeshRenderer>().sharedMaterial = lit;

                int rendersForCam = 0;
                count = (ctx, c) => { if (c == cam) rendersForCam++; };
                RenderPipelineManager.beginCameraRendering += count;

                var failures = new List<string>();
                foreach (var (name, asset) in pipelines)
                foreach (var group in groups)
                {
                    foreach (var other in groups)
                        foreach (var (_, go) in other) go.SetActive(other == group);
                    rendersForCam = 0;
                    int errorsBefore = errors.Count;
                    for (int i = 0; i < 10; i++)
                    {
                        yield return null;
                        // Held per render: a host can rewrite the quality level's pipeline
                        // at a moment of its own. Written only when it differs, since
                        // assigning it rebuilds the pipeline.
                        if (QualitySettings.renderPipeline != asset) QualitySettings.renderPipeline = asset;
                        cam.Render();
                    }
                    Assert.AreSame(asset, GraphicsSettings.currentRenderPipeline,
                        $"Basis's {name} pipeline asset didn't become the active one");

                    int passes = display.GetRenderPassCount();
                    RenderTexture eyes = passes > 0 ? display.GetRenderTextureForRenderPass(0) : null;
                    int parameters = 0;
                    if (passes > 0)
                    {
                        display.GetRenderPass(0, out var pass);
                        parameters = pass.GetRenderParameterCount();
                    }
                    Debug.Log($"[X4] {name} ({asset.name}): camera rendered {rendersForCam} time(s); display passes "
                            + $"{passes}, eyes per pass {parameters}, eye texture "
                            + (eyes != null ? $"{eyes.dimension} x{eyes.volumeDepth} {eyes.width}x{eyes.height} "
                                            + $"{eyes.graphicsFormat} msaa {eyes.antiAliasing} (asset asks {asset.msaaSampleCount})"
                                          : "none")
                            + $"; errors logged meanwhile {errors.Count - errorsBefore}");
                    if (rendersForCam == 0 || passes != 1 || parameters != 2 || eyes == null
                        || eyes.dimension != TextureDimension.Tex2DArray || eyes.volumeDepth < 2)
                        Assert.Inconclusive($"on Basis's {name} pipeline the display didn't hand back a single-pass "
                                          + $"instanced eye texture ({passes} pass(es), {parameters} eye(s) in the first, "
                                          + $"camera rendered {rendersForCam} time(s))");

                    var left  = ReadEye(eyes, 0);
                    var right = ReadEye(eyes, 1);
                    int refLeft = CountWhite(left), refRight = CountWhite(right);
                    Debug.Log($"[X4] {name}, group {groups.IndexOf(group)}: URP Lit left {refLeft} px, right {refRight} px");
                    if (refLeft < MinPixels || refRight < MinPixels)
                        Assert.Inconclusive($"on Basis's {name} pipeline URP Lit covers {refLeft} px left and "
                                          + $"{refRight} px right, so the scene or the capture is wrong");
                    for (int i = 0; i < group.Length; i++)
                    {
                        int l = CountHue(left, i), r = CountHue(right, i);
                        Debug.Log($"[X4] {name}: {group[i].path} left {l} px, right {r} px");
                        if (l < MinPixels || r < MinPixels)
                            failures.Add($"{name}: {group[i].path} covers {l} px in the left eye and {r} px in the right");
                        else if (Mathf.Max(l, r) > 2f * Mathf.Min(l, r))
                            failures.Add($"{name}: {group[i].path} covers {l} px left against {r} px right, "
                                       + "more than parallax explains");
                    }
                }
                if (errors.Count > 0)
                    Debug.Log($"[X4] {errors.Count} error(s) logged during the row, first: {errors[0]}");
                if (failures.Count > 0) Assert.Fail(string.Join("\n", failures));
            }
            finally
            {
                Application.logMessageReceived -= OnLog;
                LogAssert.ignoreFailingMessages = ignoreFailingWas;
                if (count != null) RenderPipelineManager.beginCameraRendering -= count;
                QualitySettings.renderPipeline = qualityPipelineWas;
                if (started) loader.Stop();
                loader.Deinitialize();
                RenderSettings.ambientMode  = ambientModeWas;
                RenderSettings.ambientLight = ambientLightWas;
                RenderSettings.skybox       = skyboxWas;
                RenderSettings.sun          = sunWas;
                RenderSettings.fog          = fogWas;
                foreach (var o in made) if (o != null) Object.DestroyImmediate(o);
            }
        }
    }
}
#endif
