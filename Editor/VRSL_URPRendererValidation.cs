using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace VRSL.URP.EditorScripts
{
    /// <summary>
    /// Checks the active renderer against what the package needs from it, and says
    /// what an author will see if it does not hold.
    ///
    /// The setting that matters is depth priming. With it on, URP renders a depth
    /// prepass and then draws opaque geometry with an <c>Equal</c> depth test, so any
    /// opaque shader whose depth pass does not reproduce its forward pass exactly is
    /// culled from the frame entirely. A fixture body that vanishes reads as a culling
    /// or LOD fault rather than a depth one, which is why it is worth a command that
    /// says so outright.
    /// </summary>
    public static class VRSL_URPRendererValidation
    {
        const string Menu = "VRSL/URP/Validate Renderer Setup";

        [MenuItem(Menu, false, 102)]
        public static void ValidateFromMenu()
        {
            var report = new StringBuilder();
            int problems = Validate(report);

            string summary = problems == 0
                ? "Nothing to fix. The renderer gives VRSL what it needs."
                : $"{problems} thing(s) worth looking at. Detail in the Console.";

            if (problems > 0) Debug.LogWarning("[VRSL] Renderer setup\n" + report);
            else              Debug.Log("[VRSL] Renderer setup\n" + report);

            EditorUtility.DisplayDialog("VRSL Renderer Setup", summary, "OK");
        }

        /// <summary>Appends findings and returns how many need attention.</summary>
        public static int Validate(StringBuilder report)
        {
            int problems = 0;

            if (GraphicsSettings.currentRenderPipeline is not UniversalRenderPipelineAsset urp)
            {
                report.AppendLine("FAIL  This project is not running the Universal Render "
                                + "Pipeline, so none of the VRSL URP path will render.");
                return 1;
            }

            report.AppendLine($"Pipeline asset: {urp.name}");
            report.AppendLine($"MSAA: {urp.msaaSampleCount}x");
            report.AppendLine($"Depth texture: {(urp.supportsCameraDepthTexture ? "on" : "off")} "
                            + "on the asset. VRSL asks the pipeline for depth per pass, so it "
                            + "does not depend on this being ticked — and it needs no scene "
                            + "light of any kind to get it.");
            report.AppendLine();

            // One traversal for both checks below: each walks every MonoBehaviour in the
            // scene looking for fixtures, and a show scene is not a small one.
            var fixtureRenderers = FixtureRenderers();

            var used = ValidateCameras(urp, report);
            problems += ValidateRenderers(urp, used, fixtureRenderers, report);
            problems += ValidateSceneShaders(fixtureRenderers, report);
            return problems;
        }

        /// <summary>
        /// Which renderer each camera in the scene actually renders through.
        ///
        /// A pipeline asset can carry several renderers with different settings — Basis
        /// ships priming Forced on its desktop renderer and Disabled on its camera one —
        /// and a camera picks exactly one. Listing the renderers without saying which
        /// applies leaves an author to guess, and a camera that overrides nothing takes
        /// the asset's default silently, so the answer is not visible on the camera
        /// either.
        /// </summary>
        static HashSet<int> ValidateCameras(UniversalRenderPipelineAsset urp,
                                           StringBuilder report)
        {
            // -1 for "could not be read". Reporting renderer 1 instead would be a
            // guess presented in the same shape as an answer, on the one question this
            // block exists to settle.
            int defaultIndex = -1;
            var assetSo = new SerializedObject(urp);
            var defaultProperty = assetSo.FindProperty("m_DefaultRendererIndex");
            if (defaultProperty != null) defaultIndex = defaultProperty.intValue;

            var cameras = Object.FindObjectsByType<Camera>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None);

            report.AppendLine("Cameras in this scene:");
            if (cameras.Length == 0)
            {
                report.AppendLine("      NOT CHECKED — the scene has no enabled camera, so "
                                + "which renderer applies could not be worked out. Open a "
                                + "scene with your own camera in it for this to mean anything.");
                report.AppendLine();
                return new HashSet<int>();
            }

            var used = new HashSet<int>();
            foreach (var camera in cameras)
            {
                if (camera == null) continue;
                int index = defaultIndex;
                string how = "the asset's default";

                var data = camera.GetComponent<UniversalAdditionalCameraData>();
                if (data != null)
                {
                    var so = new SerializedObject(data);
                    var property = so.FindProperty("m_RendererIndex");
                    if (property != null && property.intValue >= 0)
                    {
                        index = property.intValue;
                        how = "set on the camera";
                    }
                }

                if (index < 0)
                {
                    report.AppendLine($"      {camera.name}: NOT CHECKED — the renderer "
                                    + "index could not be read from the pipeline asset, so "
                                    + "which renderer this camera uses is unknown.");
                    continue;
                }

                used.Add(index);
                string name = index < urp.rendererDataList.Length
                            && urp.rendererDataList[index] != null
                            ? urp.rendererDataList[index].name
                            : "missing";
                report.AppendLine($"      {camera.name} renders through renderer {index + 1} "
                                + $"({name}), {how}.");
            }

            if (used.Count > 1)
                report.AppendLine("      These cameras do not share a renderer, so the settings "
                                + "below apply to different ones. Read each against the cameras "
                                + "that use it.");

            report.AppendLine();
            return used;
        }

        /// <param name="used">Renderer indices the open scene's enabled cameras resolve
        /// to, empty where that could not be worked out. Used to annotate, not to scope:
        /// a mirror or portal camera created at runtime is in no scene at edit time, and
        /// a renderer nothing here selects is still the one it will pick.</param>
        static int ValidateRenderers(UniversalRenderPipelineAsset urp, HashSet<int> used,
                                     HashSet<Renderer> fixtureRenderers, StringBuilder report)
        {
            int problems = 0;
            var fixtureLayers = FixtureLayers(fixtureRenderers);

            int index = 0;
            bool sawAny = false;
            foreach (var data in urp.rendererDataList)
            {
                index++;
                if (data == null) continue;
                sawAny = true;

                report.AppendLine($"Renderer {index}: {data.name}");
                if (used.Count > 0 && !used.Contains(index - 1))
                    report.AppendLine("      No enabled camera in the open scene renders "
                                    + "through this one. Still worth reading: a mirror or a "
                                    + "portal camera made at runtime is in no scene now, and "
                                    + "it picks a renderer the same way.");

                if (data is not UniversalRendererData universal)
                {
                    report.AppendLine("      Not a Universal Renderer, so its depth priming "
                                    + "and prepass settings could not be read. If VRSL "
                                    + "fixtures render through it, check those by hand.");
                    report.AppendLine();
                    continue;
                }

                report.AppendLine($"      Rendering mode: {universal.renderingMode}");
                report.AppendLine($"      Depth priming:  {universal.depthPrimingMode}");

                bool priming = universal.depthPrimingMode != DepthPrimingMode.Disabled;
                if (priming)
                    report.AppendLine("      With priming on, an opaque shader whose depth pass "
                                    + "draws different geometry from its forward pass is "
                                    + "dropped from the frame — as is one with no depth pass "
                                    + "for the prepass that ran. Every shader this package "
                                    + "ships reproduces its forward vertex stage in both its "
                                    + "depth passes; a custom fixture shader here has to too.");

                problems += ValidateLayerMask(universal, fixtureLayers, priming, report);
                report.AppendLine();
            }

            if (!sawAny)
            {
                report.AppendLine("FAIL  Could not read any renderer from the pipeline asset, so "
                                + "nothing here was checked.");
                problems++;
            }
            return problems;
        }

        /// <summary>
        /// A fixture on a layer the prepass excludes, with priming on, is a fixture that
        /// does not draw at all — the prepass never writes its depth, so the Equal test
        /// in the forward pass rejects every one of its fragments.
        /// </summary>
        static int ValidateLayerMask(UniversalRendererData universal, HashSet<int> fixtureLayers,
                                     bool priming, StringBuilder report)
        {
            var so0 = new SerializedObject(universal);
            var mask0 = so0.FindProperty("m_OpaqueLayerMask");
            if (fixtureLayers.Count == 0)
            {
                // Reported rather than passed over. "Nothing to check" and "checked and
                // fine" are different answers and only one of them is reassurance.
                report.AppendLine("      NOT CHECKED — no VRSL fixtures in the open scene. "
                                + "Opaque layer mask is "
                                + (mask0 != null ? DescribeMask(mask0.intValue) : "unreadable")
                                + ". A fixture on a layer outside that, with priming on, does "
                                + "not draw at all.");
                return 0;
            }

            // Read through SerializedObject: the opaque layer mask is not public API on
            // every URP version, and a missing field should report rather than throw.
            var so = new SerializedObject(universal);
            var maskProperty = so.FindProperty("m_OpaqueLayerMask");
            if (maskProperty == null)
            {
                report.AppendLine("      Could not read this renderer's opaque layer mask, so "
                                + "it was not checked. Confirm by hand that it includes the "
                                + "layers your fixtures sit on.");
                return 0;
            }

            int mask = maskProperty.intValue;
            var excluded = new List<string>();
            foreach (int layer in fixtureLayers)
                if ((mask & (1 << layer)) == 0)
                    excluded.Add($"{LayerMask.LayerToName(layer)} ({layer})");

            if (excluded.Count == 0)
            {
                report.AppendLine("      Opaque layer mask covers every layer the scene's "
                                + "fixtures are on.");
                return 0;
            }

            string layers = string.Join(", ", excluded);
            if (priming)
            {
                report.AppendLine($"FAIL  Fixtures are on {layers}, which this renderer's opaque "
                                + "layer mask excludes, and depth priming is on. Those fixtures "
                                + "will not draw at all. Add the layer to the mask, or turn "
                                + "depth priming off.");
                return 1;
            }

            report.AppendLine($"      Fixtures are on {layers}, which the opaque layer mask "
                            + "excludes. Harmless while depth priming is off, and those "
                            + "fixtures disappear the moment it is turned on.");
            return 0;
        }

        /// <summary>
        /// Shaders in the scene that render opaque and are missing a depth pass.
        ///
        /// The opaque range is where the prepass runs, so a shader below 2500 without
        /// both passes is the failure this milestone is about. Above it, the prepass
        /// never invokes them and their absence means nothing.
        ///
        /// A shader qualifies by being one this package ships, or by being on a renderer
        /// under a VRSL fixture. The second case is the one worth having: a fixture whose
        /// mesh is drawn by a custom shader disappears under priming exactly as a package
        /// one would, and the package cannot fix that shader but can say which it is.
        /// Everything else in the scene is left alone — an avatar with a broken depth pass
        /// is a real fault, but it is not this menu item's to report.
        /// </summary>
        static int ValidateSceneShaders(HashSet<Renderer> onFixtures, StringBuilder report)
        {
            const int OpaqueEnd = 2500;
            var seen    = new HashSet<Shader>();
            var missing = new List<string>();
            int examined = 0;

            foreach (var renderer in Object.FindObjectsByType<Renderer>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (renderer == null) continue;
                bool onFixture = onFixtures.Contains(renderer);
                foreach (var material in renderer.sharedMaterials)
                {
                    var shader = material != null ? material.shader : null;
                    if (shader == null) continue;
                    bool ours = shader.name.StartsWith("VRSL");
                    if (!ours && !onFixture) continue;
                    // -1 means the material takes the shader's queue rather than
                    // overriding it, and comparing that against the opaque ceiling lets
                    // every transparent shader through and reports the queue as -1.
                    int queue = material.renderQueue >= 0 ? material.renderQueue
                                                          : shader.renderQueue;
                    if (queue > OpaqueEnd) continue;
                    // Counted after the filters, not before. Adding every shader in the
                    // scene to the set would make the pass line below quote a number that
                    // has nothing to do with what was examined.
                    if (!seen.Add(shader)) continue;
                    examined++;

                    bool depthOnly    = HasPass(shader, "DepthOnly");
                    // Either tag satisfies the depth-normals prepass: URP draws it with
                    // both, and one of its branches draws DepthNormalsOnly alone. This
                    // package's own VRSLSurfacePrepass matches the same pair.
                    bool depthNormals = HasPass(shader, "DepthNormals")
                                     || HasPass(shader, "DepthNormalsOnly");
                    if (depthOnly && depthNormals) continue;

                    string absent = !depthOnly && !depthNormals ? "DepthOnly and DepthNormals"
                                  : !depthOnly                  ? "DepthOnly"
                                                                : "DepthNormals";
                    missing.Add($"{shader.name} (queue {queue}, no {absent}), "
                              + (ours ? "shipped with this package"
                                      : "on a VRSL fixture in this scene"));
                }
            }

            report.AppendLine("Shaders in this scene:");
            if (examined == 0)
            {
                report.AppendLine("      NOT CHECKED — nothing here draws opaque geometry with "
                                + "a VRSL shader, and no VRSL fixture carries a custom one.");
                return 0;
            }
            if (missing.Count == 0)
            {
                report.AppendLine($"      All {examined} shader(s) examined draw opaque geometry "
                                + "and have both depth passes.");
                return 0;
            }

            foreach (string entry in missing)
                report.AppendLine($"FAIL  {entry}. It draws in the opaque range, so under depth "
                                + "priming it can be culled from the frame. Which prepass "
                                + "priming tests against depends on whether anything in the "
                                + "frame asks URP for a normals texture, so both passes are "
                                + "needed to be safe in any project.");
            return missing.Count;
        }

        /// <summary>Renderers below a VRSL fixture in the open scene.</summary>
        static HashSet<Renderer> FixtureRenderers()
        {
            var found = new HashSet<Renderer>();
            foreach (var fixture in Object.FindObjectsByType<MonoBehaviour>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (fixture == null) continue;
                if (!fixture.GetType().Name.StartsWith("VRStageLighting")) continue;
                foreach (var renderer in fixture.GetComponentsInChildren<Renderer>(true))
                    if (renderer != null) found.Add(renderer);
            }
            return found;
        }

        /// <summary>
        /// Whether a shader declares a pass with the given <c>LightMode</c>.
        ///
        /// <c>FindPassTagValue</c> reads the tag off each sub-shader pass without
        /// depending on renderer internals, which is what makes this survive a URP
        /// version bump.
        /// </summary>
        static bool HasPass(Shader shader, string lightMode)
        {
            var tag = new ShaderTagId("LightMode");
            var wanted = new ShaderTagId(lightMode);
            for (int sub = 0; sub < shader.subshaderCount; sub++)
                for (int pass = 0; pass < shader.GetPassCountInSubshader(sub); pass++)
                    if (shader.FindPassTagValue(sub, pass, tag) == wanted)
                        return true;
            return false;
        }

        /// <summary>The layers a mask covers, named rather than as a bit pattern.</summary>
        static string DescribeMask(int mask)
        {
            if (mask == ~0) return "everything";
            var named = new List<string>();
            for (int layer = 0; layer < 32; layer++)
            {
                if ((mask & (1 << layer)) == 0) continue;
                string name = LayerMask.LayerToName(layer);
                named.Add(string.IsNullOrEmpty(name) ? layer.ToString() : name);
            }
            return named.Count == 0 ? "nothing" : string.Join(", ", named);
        }

        /// <summary>Layers occupied by VRSL fixtures in the open scene.</summary>
        static HashSet<int> FixtureLayers(HashSet<Renderer> fixtureRenderers)
        {
            var layers = new HashSet<int>();
            foreach (var renderer in fixtureRenderers)
                if (renderer != null) layers.Add(renderer.gameObject.layer);
            return layers;
        }
    }
}
