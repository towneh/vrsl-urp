#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.TestTools;

namespace VRSL.URP.Tests
{
    /// <summary>
    /// A stencil a world writes changes no VRSL light. Upstream VRSL hid its light where a
    /// light stencil box wrote 142; this package has no light stencil, so every light path
    /// draws the same with or without one.
    ///
    /// Rows O5 and O6 of TESTING.md.
    /// </summary>
    class VRSLLightStencilTests : VRSLDMXTest
    {
        const string Root = "Packages/town.mr.vrsl-urp/Runtime/";

        /// <summary>Depth priming holds URP's opaque pass at <c>ZTest Equal</c>, which a writer
        /// with no depth pass never meets, so these rows build the rig without it.</summary>
        const DepthPrimingMode Priming = DepthPrimingMode.Disabled;

        /// <summary>
        /// A world's stencil over the whole view: a writer putting 142 into the stencil early
        /// in the opaque queue, colour and depth untouched, and a probe (off until asked for)
        /// that draws green only where the stencil holds 142. Both ride the camera a metre out.
        /// </summary>
        sealed class WorldStencil : System.IDisposable
        {
            readonly List<Object> _owned = new();
            public readonly MeshRenderer Writer, Probe;

            public WorldStencil(Camera camera)
            {
                var shader = Shader.Find("Hidden/VRSL-URP/Tests/StencilProbe");
                Assert.IsNotNull(shader, "the stencil test shader is not in the project");
                Writer = Quad("VRSL world stencil", new Material(shader), camera);
                var probe = new Material(shader) { renderQueue = (int)RenderQueue.Overlay };
                probe.SetFloat("_StencilComp", (float)CompareFunction.Equal);
                probe.SetFloat("_StencilPass", (float)StencilOp.Keep);
                probe.SetFloat("_ColorMask", 15f);
                Probe = Quad("VRSL stencil probe", probe, camera);
                Probe.enabled = false;
            }

            MeshRenderer Quad(string name, Material material, Camera camera)
            {
                material.name = name;
                _owned.Add(material);
                var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
                _owned.Add(quad);
                quad.name = name;
                Object.DestroyImmediate(quad.GetComponent<Collider>());
                quad.transform.SetParent(camera.transform, false);
                // The rig's view is 60 degrees on a square target, 1.15 m across at a metre.
                quad.transform.localPosition = new Vector3(0f, 0f, 1f);
                quad.transform.localScale    = new Vector3(4f, 4f, 1f);
                var r = quad.GetComponent<MeshRenderer>();
                r.sharedMaterial    = material;
                r.shadowCastingMode = ShadowCastingMode.Off;
                return r;
            }

            public void Dispose()
            {
                foreach (var o in _owned) if (o != null) Object.DestroyImmediate(o);
            }
        }

        /// <summary>Whether the probe drew green at the centre of the view, which it does
        /// only where the stencil holds 142.</summary>
        static bool ProbeSeesStencil(Texture2D frame)
        {
            var c = frame.GetPixel(frame.width / 2, frame.height / 2);
            return c.g > 0.9f && c.r < 0.1f && c.b < 0.1f;
        }

        /// <summary>
        /// O5. The lasers and lens flares draw where a world writes stencil 142. Each (the DMX
        /// and AudioLink laser on their prefab's mesh, the DMX and AudioLink blinder flare on
        /// a quad) on a copy of its shipped material, lit red with no DMX or AudioLink input,
        /// in front of the camera on its own; captured shown and hidden, without the world's
        /// stencil and then with it over the whole view; measured on a half-float capture,
        /// summed over the frame. The probe proves the stencil was in the frame.
        /// </summary>
        [UnityTest]
        public IEnumerator O5_lasers_and_flares_draw_where_a_world_writes_stencil_142()
        {
            var lights = new (string name, string material, string prefab, string enable)[]
            {
                ("DMX Laser",       "Materials/Lights/DMX/Lasers/VRSL-BasicLaser-DMX-Horizontal.mat",
                                    "Prefabs/DMX/VRSL-DMX-Static-Laser-13CH.prefab", "_EnableDMX"),
                ("AudioLink Laser", "Materials/Lights/AudioLink/VRSL-BasicLaser-AudioLInk.mat",
                                    "Prefabs/AudioLink/VRSL-AudioLink-BasicLaser.prefab", "_EnableAudioLink"),
                ("DMX Flare",       "Materials/Lights/DMX/StaticLight-Blinder/VRSL-Blinder-LensFlareMat-Horizontal.mat",
                                    null, "_EnableDMX"),
                ("AudioLink Flare", "Materials/Lights/AudioLink/StaticLight-Blinder/VRSL-AudioLink-Blinder-LensFlareMat.mat",
                                    null, "_EnableAudioLink"),
            };

            // A light first drawn while its variant compiles in the background draws nothing,
            // which would read as hidden under whichever capture came first.
            bool asyncWas        = ShaderUtil.allowAsyncCompilation;
            VRSLDMXRig rig       = null;
            WorldStencil stencil = null;
            var owned            = new List<Object>();
            try
            {
                ShaderUtil.allowAsyncCompilation = false;
                rig     = VRSLDMXRig.Build(withSource: false, targetSize: 512, priming: Priming);
                stencil = new WorldStencil(rig.Camera);
                var renderers = new List<MeshRenderer>();
                for (int i = 0; i < lights.Length; i++)
                {
                    var (name, path, prefabPath, enable) = lights[i];
                    var shipped = AssetDatabase.LoadAssetAtPath<Material>(Root + path);
                    Assert.IsNotNull(shipped, $"{path} is not in the package");
                    var material = new Material(shipped) { name = $"VRSL O5 {name}" };
                    owned.Add(material);
                    material.SetFloat(enable, 0f);

                    GameObject go;
                    if (prefabPath == null)
                    {
                        go = GameObject.CreatePrimitive(PrimitiveType.Quad);
                        Object.DestroyImmediate(go.GetComponent<Collider>());
                        go.transform.position = new Vector3(600f + 20f * i, 1.5f, 0f);
                        material.SetColor("_Emission", new Color(24f, 0f, 0f, 1f));
                    }
                    else
                    {
                        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(Root + prefabPath);
                        Assert.IsNotNull(prefab, $"{prefabPath} is not in the package");
                        var source = prefab.GetComponentsInChildren<MeshRenderer>(true)
                            .FirstOrDefault(r => r.sharedMaterial != null && r.sharedMaterial.shader == shipped.shader);
                        Assert.IsNotNull(source, $"{prefabPath} has no renderer on {shipped.shader.name}");
                        go = new GameObject();
                        go.AddComponent<MeshFilter>().sharedMesh = source.GetComponent<MeshFilter>().sharedMesh;
                        go.AddComponent<MeshRenderer>();
                        go.transform.SetPositionAndRotation(new Vector3(600f + 20f * i, 3f, 0f), source.transform.rotation);
                        go.transform.localScale = source.transform.lossyScale;
                        material.SetColor("_Emission", Color.red);
                        material.SetFloat("_Scroll", 0f);
                    }
                    owned.Add(go);
                    go.name = material.name;
                    go.transform.SetParent(rig.Floor.transform.parent, true);
                    var renderer = go.GetComponent<MeshRenderer>();
                    renderer.sharedMaterial = material;
                    renderers.Add(renderer);
                }
                foreach (var r in renderers)
                {
                    rig.Camera.transform.SetPositionAndRotation(
                        new Vector3(r.transform.position.x, 1.5f, -8f), Quaternion.identity);
                    yield return rig.Step(2);
                }
                foreach (var r in renderers) r.enabled = false;

                Vector3 Adds(MeshRenderer r, bool withStencil)
                {
                    stencil.Writer.enabled = withStencil;
                    r.enabled = true;
                    var shown = VRSLParBlinderBodyTests.CaptureHdr(rig);
                    r.enabled = false;
                    var hidden = VRSLParBlinderBodyTests.CaptureHdr(rig);
                    try
                    {
                        var a = shown.GetPixels();
                        var b = hidden.GetPixels();
                        var sum = Vector3.zero;
                        for (int p = 0; p < a.Length; p++)
                            sum += new Vector3(a[p].r - b[p].r, a[p].g - b[p].g, a[p].b - b[p].b);
                        return sum;
                    }
                    finally
                    {
                        Object.DestroyImmediate(shown);
                        Object.DestroyImmediate(hidden);
                    }
                }

                bool Probe(bool withStencil)
                {
                    stencil.Writer.enabled = withStencil;
                    stencil.Probe.enabled  = true;
                    var frame = VRSLParBlinderBodyTests.CaptureHdr(rig);
                    stencil.Probe.enabled  = false;
                    try { return ProbeSeesStencil(frame); }
                    finally { Object.DestroyImmediate(frame); }
                }

                if (Probe(false))
                    Assert.Inconclusive("the probe drew green with no stencil written, so it doesn't test the stencil");
                if (!Probe(true))
                    Assert.Inconclusive("the probe never saw stencil 142 under the writer, so the world's stencil "
                                      + "never reached the frame and the row has nothing to judge");

                var failures = new List<string>();
                for (int i = 0; i < renderers.Count; i++)
                {
                    var r = renderers[i];
                    var at = r.transform.position;
                    rig.Camera.transform.SetPositionAndRotation(
                        new Vector3(at.x, 1.5f, r.name.Contains("Flare") ? -5f : -8f), Quaternion.identity);
                    var open  = Adds(r, false);
                    var under = Adds(r, true);
                    Debug.Log($"[O5] {lights[i].name}: adds {open:F3} without the stencil, {under:F3} under it");
                    if (!(open.x > 1f && open.x > 3f * open.y))
                        failures.Add($"{lights[i].name} adds only {open:F3} with no stencil, so it isn't lit and the "
                                   + "capture under the stencil proves nothing");
                    else if ((under - open).magnitude > 0.02f * open.magnitude + 0.01f)
                        failures.Add($"{lights[i].name} adds {under:F3} where the world writes stencil 142 against "
                                   + $"{open:F3} without it: it still hides where upstream's light stencil boxes wrote, "
                                   + "which no other VRSL light does");
                }
                if (failures.Count > 0) Assert.Fail(string.Join("\n", failures));
            }
            finally
            {
                stencil?.Dispose();
                rig?.Dispose();
                foreach (var o in owned) if (o != null) Object.DestroyImmediate(o);
                ShaderUtil.allowAsyncCompilation = asyncWas;
            }
        }

        /// <summary>
        /// O6. The surface light and the beams are the same where a world writes stencil 142.
        /// The rig's frozen frame (every dimmer up, beams aimed down at the floor) at Standard,
        /// without the world's stencil and then with it over the whole view, then the same at
        /// Off (no beams), then with the manager off. With and without the stencil match at
        /// each level; Standard against Off shows the beams were in the frame, Off against
        /// the manager off that the surface light was.
        /// </summary>
        [UnityTest]
        public IEnumerator O6_surface_light_and_beams_are_the_same_where_a_world_writes_stencil_142()
        {
            var rig              = VRSLDMXRig.Build(priming: Priming);
            WorldStencil stencil = null;
            var frames           = new List<Texture2D>();
            try
            {
                stencil = new WorldStencil(rig.Camera);
                rig.Source.pattern = VRSL_SyntheticDMXChannelSource.Pattern.Fixtures;
                rig.Source.speed   = 0f;
                rig.Manager.enabled = false;
                rig.Manager.enabled = true;
                rig.Manager.quality = VRSLQuality.Standard;
                rig.FreezeForImageCapture();
                stencil.Writer.enabled = false;
                // As the image rows: movement damping settles before anything is read.
                yield return rig.Step(120);

                Texture2D Read()
                {
                    var t = VRSLImageCompare.Read(rig.Target);
                    frames.Add(t);
                    return t;
                }

                var standard = Read();
                stencil.Writer.enabled = true;
                yield return rig.Step(2);
                var standardUnder = Read();
                stencil.Probe.enabled = true;
                yield return rig.Step(1);
                var probed = Read();
                stencil.Probe.enabled = false;

                rig.Manager.quality = VRSLQuality.Off;
                yield return rig.Step(4);
                var offUnder = Read();
                stencil.Writer.enabled = false;
                yield return rig.Step(2);
                var off = Read();

                rig.Manager.enabled = false;
                yield return rig.Step(2);
                var dark = Read();

                if (!ProbeSeesStencil(probed))
                    Assert.Inconclusive("the probe never saw stencil 142 under the writer, so the world's stencil "
                                      + "never reached the frame and the row has nothing to judge");

                var beams   = VRSLImageCompare.Compare(standard, off);
                var surface = VRSLImageCompare.Compare(off, dark);
                Debug.Log($"[O6] Standard against Off: {beams}; Off against the manager off: {surface}");
                if (beams.DifferingPercent < 1f)
                    Assert.Inconclusive($"Standard and Off differ on only {beams}, so there were no beams to judge");
                if (surface.DifferingPercent < 1f)
                    Assert.Inconclusive($"Off and the manager off differ on only {surface}, so there was no surface "
                                      + "light to judge");

                var failures = new List<string>();
                foreach (var (level, clear, under) in new[] { ("Standard", standard, standardUnder), ("Off", off, offUnder) })
                {
                    var result = VRSLImageCompare.Compare(clear, under);
                    Debug.Log($"[O6] {level}, without against with the stencil: {result}");
                    if (result.SizeMismatch || result.Max > VRSLImageCompare.Threshold)
                        failures.Add($"at {level} the frame changed where the world writes stencil 142 ({result}); "
                                   + "the surface light and the beams don't test the stencil");
                }
                if (failures.Count > 0) Assert.Fail(string.Join("\n", failures));
            }
            finally
            {
                stencil?.Dispose();
                rig.Dispose();
                foreach (var t in frames) if (t != null) Object.DestroyImmediate(t);
            }
        }
    }
}
#endif
