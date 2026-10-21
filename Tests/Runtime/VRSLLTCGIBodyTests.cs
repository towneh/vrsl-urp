#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;

namespace VRSL.URP.Tests
{
    /// <summary>
    /// The fixture bodies that LTCGI can light. Rows U2 to U4 of TESTING.md; U2 and U4 need
    /// LTCGI installed and are skipped without it.
    /// </summary>
    class VRSLLTCGIBodyTests : VRSLDMXTest
    {
        const string Keyword = "_VRSL_LTCGI";

        static void RequireLtcgi()
        {
#if !VRSL_LTCGI_PRESENT
            Assert.Ignore("LTCGI (at.pimaker.ltcgi) isn't installed, so the keyword's variants can't compile here");
#endif
        }

        static readonly string[] Bodies =
        {
            "VRSL-URP/Standard Static/Surface Shaders/Opaque",
            "VRSL-URP/Standard Mover/Fixture",
            "VRSL-URP/Wash Mover/Fixture",
        };

        /// <summary>A lookup table every LTCGI fragment samples, whatever its config.</summary>
        const string LtcgiResource = "_Udon_LTCGI_lut1";

        static readonly (ShaderCompilerPlatform platform, BuildTarget target, string[] keywords)[] Targets =
        {
            (ShaderCompilerPlatform.D3D,    BuildTarget.StandaloneWindows64, new string[0]),
            (ShaderCompilerPlatform.D3D,    BuildTarget.StandaloneWindows64, new[] { "STEREO_INSTANCING_ON" }),
            (ShaderCompilerPlatform.Vulkan, BuildTarget.StandaloneLinux64,   new string[0]),
            (ShaderCompilerPlatform.Vulkan, BuildTarget.Android,             new string[0]),
            (ShaderCompilerPlatform.Vulkan, BuildTarget.Android,             new[] { "INSTANCING_ON", "STEREO_MULTIVIEW_ON" }),
        };

        static ShaderData.Pass ForwardPass(Shader shader)
        {
            var sub = ShaderUtil.GetShaderData(shader).GetSubshader(0);
            for (int p = 0; p < sub.PassCount; p++)
            {
                var pass = sub.GetPass(p);
                if (pass.FindTagValue(new ShaderTagId("LightMode")).name == "UniversalForward") return pass;
            }
            return null;
        }

        /// <summary>
        /// U2. Each body's forward pass, keyword on, compiles for every platform the package
        /// supports, and its D3D fragment binds LTCGI's lookup table, so LTCGI's code is in
        /// it; keyword off, the fragment binds nothing of LTCGI's.
        /// </summary>
        [Test]
        public void U2_bodies_compile_with_ltcgi_on_every_platform()
        {
            RequireLtcgi();
            var failures = new List<string>();
            int compiled = 0;
            foreach (var name in Bodies)
            {
                var shader = Shader.Find(name);
                Assert.IsNotNull(shader, $"{name} is not in this project");
                var pass = ForwardPass(shader);
                Assert.IsNotNull(pass, $"{name} has no UniversalForward pass");

                foreach (var (platform, target, set) in Targets)
                {
                    foreach (bool on in new[] { true, false })
                    {
                        var keywords = on ? set.Append(Keyword).ToArray() : set;
                        string where = $"{name}, {platform} {target}, [{string.Join(" ", keywords)}]";
                        foreach (var stage in new[] { ShaderType.Vertex, ShaderType.Fragment })
                        {
                            var info = pass.CompileVariant(stage, keywords, platform, target);
                            compiled++;
                            var errors = info.Messages.Where(m => m.severity == ShaderCompilerMessageSeverity.Error).ToList();
                            if (!info.Success || errors.Count > 0)
                            {
                                failures.Add($"{where} {stage} does not compile: "
                                           + string.Join("; ", errors.Select(m => $"{m.message} ({m.file}:{m.line})")));
                                continue;
                            }
                            // Unity reports no resource bindings for a Vulkan fragment, so only
                            // D3D can show what the fragment reads.
                            if (stage != ShaderType.Fragment || platform != ShaderCompilerPlatform.D3D) continue;
                            bool binds = info.ResourceBindings.Any(b => b.Name == LtcgiResource);
                            if (on && !binds) failures.Add($"{where}: the fragment doesn't bind {LtcgiResource}, so no LTCGI light reaches it "
                                                         + $"(binds {string.Join(", ", info.ResourceBindings.Select(b => b.Name))})");
                            if (!on && info.ResourceBindings.Any(b => b.Name.StartsWith("_Udon_LTCGI")))
                                failures.Add($"{where}: the fragment binds LTCGI's resources with the keyword off");
                        }
                    }
                }
            }
            Debug.Log($"[U2] {compiled} programs compiled");
            Assert.IsEmpty(failures, string.Join("\n", failures));
        }

        /// <summary>
        /// U3. LTCGI's controller lights a renderer when its material's subshader tag "LTCGI"
        /// names a float that is non-zero. Each body's tag names the property that also
        /// switches the keyword, so the two can't disagree when set from the inspector.
        /// </summary>
        [Test]
        public void U3_bodies_name_their_ltcgi_toggle_for_the_controller()
        {
            var failures = new List<string>();
            foreach (var name in Bodies)
            {
                var shader = Shader.Find(name);
                Assert.IsNotNull(shader, $"{name} is not in this project");

                string tag = shader.FindSubshaderTagValue(0, new ShaderTagId("LTCGI")).name;
                if (tag != Keyword)
                {
                    failures.Add($"{name}: its LTCGI tag is '{tag}', not '{Keyword}'");
                    continue;
                }
                int index = shader.FindPropertyIndex(tag);
                if (index < 0)
                {
                    failures.Add($"{name}: its LTCGI tag names {tag}, which it doesn't declare");
                    continue;
                }
                var type = shader.GetPropertyType(index);
                if (type != ShaderPropertyType.Float && type != ShaderPropertyType.Range)
                    failures.Add($"{name}: {tag} is a {type}, and LTCGI reads it with GetFloat");
                if (!shader.GetPropertyAttributes(index).Contains($"Toggle({Keyword})"))
                    failures.Add($"{name}: {tag} doesn't switch the {Keyword} keyword");

                var material = new Material(shader);
                try
                {
                    if (material.GetFloat(tag) != 0)
                        failures.Add($"{name}: {tag} is on by default");
                }
                finally { Object.DestroyImmediate(material); }
            }
            Assert.IsEmpty(failures, string.Join("\n", failures));
        }

        const string Generated = "Assets/LTCGI-Generated";

        static System.Type LtcgiType(string name)
        {
            var type = System.Type.GetType($"pi.LTCGI.{name}, at.pimaker.ltcgi.Editor");
            Assert.IsNotNull(type, $"LTCGI is installed without {name}");
            return type;
        }

        /// <summary>
        /// LTCGI's own controller and one white, double-sided screen behind the camera, as wide
        /// as J6's rows of body cubes and facing them, set up the way a world sets them up. Reached
        /// by reflection, so the test assembly needn't reference LTCGI.
        /// </summary>
        static GameObject[] BuildLtcgi(Transform parent)
        {
            var screenType = LtcgiType("LTCGI_Screen");
            var controllerType = LtcgiType("LTCGI_Controller");

            var screen = GameObject.CreatePrimitive(PrimitiveType.Quad);
            screen.name = "VRSL U4 LTCGI screen";
            screen.transform.SetParent(parent, false);
            screen.transform.SetPositionAndRotation(new Vector3(200.25f, 1.75f, -7f), Quaternion.Euler(0f, 180f, 0f));
            screen.transform.localScale = new Vector3(8.5f, 4.5f, 1f);
            var s = screen.AddComponent(screenType);
            screenType.GetField("Color").SetValue(s, Color.white);
            screenType.GetField("DoubleSided").SetValue(s, true);
            var colorMode = screenType.GetField("ColorMode");
            colorMode.SetValue(s, System.Enum.Parse(colorMode.FieldType, "Static"));
            var rendererMode = screenType.GetField("RendererMode");
            rendererMode.SetValue(s, System.Enum.Parse(rendererMode.FieldType, "All"));

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Packages/at.pimaker.ltcgi/LTCGI Controller.prefab");
            Assert.IsNotNull(prefab, "LTCGI is installed without its controller prefab");
            var controller = Object.Instantiate(prefab, parent);
            var c = controller.GetComponent(controllerType);
            controllerType.GetField("Singleton").SetValue(null, c);
            // The controller lists the renderers whose material turns LTCGI on and hands them,
            // with the screen, to its adapter, which sets the globals and each renderer's block.
            controllerType.GetMethod("UpdateMaterials", new[] { typeof(bool), screenType, typeof(bool) })
                          .Invoke(c, new object[] { false, null, true });
            return new[] { screen, controller };
        }

        /// <summary>
        /// U4. A body with the keyword on takes light from an LTCGI screen; with it off, or on
        /// a body shader without the keyword, it takes none. LTCGI's own controller lights J6's
        /// body cubes from one white screen behind the camera, with the rig's lights and
        /// ambient off; the screen is switched off through LTCGI's global enable. LTCGI's own
        /// Lit is the control that the screen lights anything, and URP Lit that nothing else
        /// moved between the captures.
        /// </summary>
        [UnityTest]
        public IEnumerator U4_bodies_with_the_keyword_take_light_from_an_ltcgi_screen()
        {
            RequireLtcgi();
            var rig = VRSLDMXRig.Build(withSource: false, targetSize: 512);
            var owned = new List<Object>();
            var generatedBefore = System.IO.Directory.Exists(Generated)
                ? System.IO.Directory.GetFiles(Generated) : new string[0];
            var controllerType = LtcgiType("LTCGI_Controller");
            bool asyncWas = ShaderUtil.allowAsyncCompilation;
            try
            {
                var (bodies, reference) = VRSLParBlinderBodyTests.BodyCubes(rig, "U4", owned);
                var lit = bodies.Where(b => Bodies.Contains(b.shader)).ToList();
                Assert.AreEqual(Bodies.Length, lit.Count, "J6's cubes don't carry every body that takes the keyword");

                var ltcgiLit = Shader.Find("LTCGI/Lit URP");
                Assert.IsNotNull(ltcgiLit, "LTCGI is installed without its URP Lit shader");
                var ltcgiMaterial = new Material(ltcgiLit) { name = "VRSL U4 LTCGI Lit" };
                owned.Add(ltcgiMaterial);
                ltcgiMaterial.SetColor("_BaseColor", Color.white);
                ltcgiMaterial.SetFloat("_Smoothness", 0.2f);
                var ltcgiCube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                ltcgiCube.name = "VRSL U4 LTCGI Lit";
                ltcgiCube.transform.SetParent(rig.Floor.transform.parent, false);
                ltcgiCube.transform.position = new Vector3(200.75f, 3.9f, 0f);  // above J6's rows
                var ltcgiRenderer = ltcgiCube.GetComponent<MeshRenderer>();
                ltcgiRenderer.sharedMaterial = ltcgiMaterial;

                void Switch(bool on)
                {
                    foreach (var (_, r) in lit)
                    {
                        var m = r.sharedMaterial;
                        m.SetFloat(Keyword, on ? 1f : 0f);
                        if (on) m.EnableKeyword(Keyword); else m.DisableKeyword(Keyword);
                    }
                }

                // On before the controller looks, so it lists the bodies; and drawn once first,
                // since until a shader has drawn Material.GetTag reads its FallBack's tags, and
                // LTCGI's Lit has a FallBack.
                Switch(true);
                rig.Camera.transform.SetPositionAndRotation(new Vector3(200f, 1f, -6f), Quaternion.identity);
                ShaderUtil.allowAsyncCompilation = false;
                yield return rig.Step(2);
                foreach (var go in BuildLtcgi(rig.Floor.transform.parent)) owned.Add(go);
                yield return rig.Step(2);

                (Vector3[] body, Vector3 control, Vector3 ltcgi) Capture(bool screen, bool keyword, string what)
                {
                    Shader.SetGlobalFloat("_Udon_LTCGI_GlobalEnable", screen ? 1f : 0f);
                    Switch(keyword);
                    var frame = VRSLParBlinderBodyTests.CaptureHdr(rig);
                    try
                    {
                        var means = bodies.Select(b => VRSLParBlinderBodyTests.Measure(rig.Camera, frame, b.renderer).mean).ToArray();
                        var control = VRSLParBlinderBodyTests.Measure(rig.Camera, frame, reference).mean;
                        var ltcgi = VRSLParBlinderBodyTests.Measure(rig.Camera, frame, ltcgiRenderer).mean;
                        for (int i = 0; i < bodies.Count; i++) Debug.Log($"[U4] {what}: {bodies[i].shader} mean {means[i]}");
                        Debug.Log($"[U4] {what}: URP Lit mean {control}, LTCGI Lit mean {ltcgi}");
                        return (means, control, ltcgi);
                    }
                    finally { Object.DestroyImmediate(frame); }
                }

                var dark     = Capture(false, true,  "screen off, keyword on");
                var screenOn = Capture(true,  true,  "screen on, keyword on");
                var keyOff   = Capture(true,  false, "screen on, keyword off");

                float Gain(Vector3 a, Vector3 b) => (a - b).magnitude;
                float control = Gain(screenOn.ltcgi, dark.ltcgi);
                if (!(control > 0.05f))
                    Assert.Inconclusive($"LTCGI's own Lit gains only {control:F4} from the screen ({screenOn.ltcgi} "
                                      + $"against {dark.ltcgi}), so the screen lights nothing and the row has nothing to judge");
                var failures = new List<string>();
                if (Gain(screenOn.control, dark.control) > 1e-3f)
                    failures.Add($"URP Lit changed with the screen on ({screenOn.control} against {dark.control}), so "
                               + "something other than LTCGI moved between the captures");
                for (int i = 0; i < bodies.Count; i++)
                {
                    string name = bodies[i].shader;
                    float on = Gain(screenOn.body[i], dark.body[i]);
                    float off = Gain(keyOff.body[i], dark.body[i]);
                    if (Bodies.Contains(name))
                    {
                        if (!(on > 0.5f * control))
                            failures.Add($"{name} with the keyword on gains {on:F4} from the screen ({screenOn.body[i]} "
                                       + $"against {dark.body[i]} with it off), under half of LTCGI's own Lit ({control:F4})");
                        if (off > 1e-3f)
                            failures.Add($"{name} with the keyword off still gains {off:F4} from the screen");
                    }
                    else if (on > 1e-3f)
                        failures.Add($"{name} has no LTCGI keyword but gains {on:F4} from the screen");
                }
                if (failures.Count > 0) Assert.Fail(string.Join("\n", failures));
            }
            finally
            {
                ShaderUtil.allowAsyncCompilation = asyncWas;
                Shader.SetGlobalFloat("_Udon_LTCGI_GlobalEnable", 0f);
                controllerType.GetField("Singleton").SetValue(null, null);
                rig.Dispose();
                foreach (var o in owned) if (o != null) Object.DestroyImmediate(o);
                // The controller bakes its screens into a texture under Assets; leave only what was there.
                if (System.IO.Directory.Exists(Generated))
                {
                    foreach (var f in System.IO.Directory.GetFiles(Generated).Except(generatedBefore))
                        if (!f.EndsWith(".meta")) AssetDatabase.DeleteAsset(f.Replace('\\', '/'));
                    if (generatedBefore.Length == 0) AssetDatabase.DeleteAsset(Generated);
                }
            }
        }
    }
}
#endif
